"""Fits the virtual levels' upsample fractions (TransitionKernel.cs, WideKernel.cs).

    python3 fit_transition.py            check that no fraction pair beats the package's
    python3 fit_transition.py --write    improve them until none does, then update the C#

Before the transition layout ends at sigma 24, its Gaussian row crossfades
with a virtual level whose 2x up uses two bilinear fetches; so does the wide
layout's row before each doubling of its working pitch. Their fractions (in
1/256) decide how well the layout matches its neighbor at the switch and
how it behaves while crossfading:

    toward wide           sigma 21 to 24         TransitionKernel WideUpFractions
    toward the next pitch the last eighth of     WideKernel PitchUpFractions
                          sigma before 70, 140, 280 and 560 (one entry each;
                          the last serves every later switch)

Every pair on the 257 x 257 lattice is scored on the 1D model in pipeline.py
(which matches the GPU to float precision), over 16 impulse phases:

    switch L2, max  difference from the neighbor's response at the switch
    switch 2D L1    the same for every pair of horizontal and vertical phases
    shape           worst L1 difference from the target Gaussian over the fade range
    centroid        worst point displacement over the fade range
    width           worst phase-averaged relative variance error over the fade range
    motion          worst L1 change between neighboring sigmas of the fade range
    fetches         Gaussian fetch count at every sigma of the fade range

The fade range is sampled every 0.001 sigma (0.01 toward the next pitch). At a few strengths the response
jumps, because a snapped fetch flips to the next 1/256 step; the worst
centroid sits just before such a jump. Each step more than JUMP times the
median step is bisected down to adjacent float sigmas, and both sides get
shape, centroid and width scores. Motion keeps the jump itself, so it is the
larger of the steepest smooth change per 0.001 sigma and the largest jump.

The pitch ends search only pairs within WINDOW of the incumbent: their rows
are long, and the fit refines fractions found by a coarser search.

A pair replaces the incumbent only if it matches the switch better (L2), is
no worse on any other score, and needs no more fetches at any sigma. The
search repeats from the winner until no pair beats it, so quality and cost
can only improve. The check confirms that state for the package's values.
"""
import argparse
import re
import sys
from typing import NamedTuple

import numpy as np

import kernels as k
import pipeline as P
from csharp import KERNELS, PACKAGE, read

ENDS = {
    'wide': dict(source='TransitionKernel.cs', field='WideUpFractions', switch=float(k.END_SIGMA),
                 fade=np.arange(k.WIDE_FADE_SIGMA, k.END_SIGMA+1e-5, 0.001)),
    **{f'pitch {switch}': dict(source='WideKernel.cs', field='PitchUpFractions', index=i, switch=float(switch),
                               fade=np.arange(k.PITCH_FADE_START*switch, 0.9999*switch, switch/7000), length=length)
       for i, (switch, length) in enumerate((k.FIRST_DOUBLING_SIGMA*2**i, 1024*2**i) for i in range(4))},
}
JUMP = 3.
WINDOW = 12
TOLERANCE = 1e-9  # relative; scores this close count as equal
SWITCH = ['switch L2', 'switch max', 'switch 2D L1']
FADE = ['shape', 'centroid', 'width', 'motion']


class Score(NamedTuple):
    metrics: dict      # SWITCH and FADE names -> worst value
    fetches: np.ndarray  # Gaussian fetch count per fade sigma


def worst_l1(difference):
    """L1 norm of a difference of responses, worst over the impulse phases."""
    return float(np.abs(difference).sum(0).max())


def worst_2d_l1(a, b):
    """Worst L1 difference of the 2D responses a x a and b x b, over all pairs of phases."""
    support = (a != 0).any(1) | (b != 0).any(1)
    a, b = a[support], b[support]
    worst = 0.
    for i in range(P.PHASES):
        d = np.abs(a[:, i, None, None]*a[None]-b[:, i, None, None]*b[None]).sum(axis=(0, 1))
        worst = max(worst, float(d.max()))
    return worst


def float_bits(x):
    return int(np.float32(x).view(np.int32))


def from_bits(i):
    return float(np.int32(i).view(np.float32))


class End:
    """One end of a fade: its neighbor's response at the switch and the fade range."""

    def __init__(self, name):
        spec = ENDS[name]
        self.name = name
        self.source = spec['source']
        self.field = spec['field']
        self.index = spec.get('index')
        P.configure(spec.get('length', 1024))
        self.switch = spec['switch']
        self.impulses = P.impulses()
        self.offsets = np.arange(P.LENGTH)[:, None]-(P.CENTER+np.arange(P.PHASES))[None, :]
        self.sigmas = [float(s) for s in np.float32(spec['fade'])]  # the runtime receives floats
        self.ideal = {s: P.ideal(s) for s in self.sigmas}
        below = float(np.nextafter(np.float32(self.switch), np.float32(0)))
        if name == 'wide':
            self.neighbor = P.wide(self.impulses, k.Wide(24.0))
        else:
            # The fading side is below the switch; the next pitch starts at it.
            self.neighbor = P.wide(self.impulses, k.Wide(self.switch))
            self.switch = below

    def fractions(self):
        value = read(self.source, self.field)
        return value if self.index is None else value[self.index]

    def window(self, incumbent):
        if self.index is None:
            return range(257), range(257)
        a, b = incumbent
        return (range(max(0, a-WINDOW), min(256, a+WINDOW)+1),
                range(max(0, b-WINDOW), min(256, b+WINDOW)+1))

    def write(self, text, fractions):
        if self.index is None:
            return re.sub(self.field+r' = \(\d+, \d+\);', f'{self.field} = {fractions};', text)
        match = re.search(self.field+r' = \{([^}]*)\};', text)
        entries = re.findall(r'\(\d+, \d+\)', match.group(1))
        entries[self.index] = str(tuple(fractions))
        return text[:match.start(1)]+' '+', '.join(entries)+' '+text[match.end(1):]

    def response(self, sigma, fractions):
        """The fading layout's point responses at sigma, and its Gaussian fetch count."""
        if self.index is not None:
            kernel = k.Wide(sigma, pitch_up=fractions)
            return P.wide(self.impulses, kernel), len(kernel.gaussian[0])
        kernel = k.Transition(sigma, wide_up=fractions)
        return P.transition(self.impulses, kernel), len(kernel.gaussian[0])

    def switch_response(self, fractions):
        return self.response(self.switch, fractions)[0]

    def switch_scores(self, response, with_2d=True):
        """SWITCH scores of a switch response; the 2D L1 is the slow one."""
        d = response-self.neighbor
        scores = {'switch L2': float((d**2).sum()), 'switch max': float(np.abs(d).max())}
        if with_2d:
            scores['switch 2D L1'] = worst_2d_l1(response, self.neighbor)
        return scores

    def fade_scores(self, fractions):
        """Worst FADE scores over the fade range, and the fetch count at each sigma."""
        responses, fetches = zip(*(self.response(s, fractions) for s in self.sigmas))
        worst = dict(shape=0., centroid=0., width=0.)

        def include(sigma, response):
            for name, value in self._quality(sigma, response).items():
                worst[name] = max(worst[name], value)

        for s, r in zip(self.sigmas, responses):
            include(s, r)
        steps = [worst_l1(b-a) for a, b in zip(responses, responses[1:])]
        limit = JUMP*np.median(steps)
        for i, step in enumerate(steps):
            if step > limit:
                for s, r in self._bisect_jump(i, responses, fractions):
                    include(s, r)
        worst['motion'] = max(steps)
        return worst, np.array(fetches)

    def score(self, fractions):
        fade, fetches = self.fade_scores(fractions)
        return Score({**self.switch_scores(self.switch_response(fractions)), **fade}, fetches)

    def _quality(self, sigma, response):
        ideal = self.ideal[sigma] if sigma in self.ideal else P.ideal(sigma)
        mass = response.sum(0)
        center = (response*self.offsets).sum(0)/mass
        variance = ((response*self.offsets**2).sum(0)/mass-center**2).mean()
        return dict(shape=worst_l1(response-ideal), centroid=float(np.abs(center).max()),
                    width=abs(variance/k.requested_variance(sigma)-1))

    def _bisect_jump(self, i, responses, fractions):
        """Narrows the jump between sigmas[i] and [i + 1] to adjacent floats; returns both sides."""
        lo, hi = float_bits(self.sigmas[i]), float_bits(self.sigmas[i+1])
        r_lo, r_hi = responses[i], responses[i+1]
        while hi-lo > 1:
            middle = (lo+hi)//2
            r, _ = self.response(from_bits(middle), fractions)
            if worst_l1(r-r_lo) > worst_l1(r_hi-r):
                hi, r_hi = middle, r  # the jump is below the middle
            else:
                lo, r_lo = middle, r
        return [(from_bits(lo), r_lo), (from_bits(hi), r_hi)]


def no_worse(candidate, incumbent, names):
    return all(candidate[n] <= incumbent[n]*(1+TOLERANCE) for n in names)


def better_switch(candidate, incumbent):
    return candidate['switch L2'] < incumbent['switch L2']*(1-TOLERANCE)


def beats(candidate, incumbent):
    return (better_switch(candidate.metrics, incumbent.metrics) and
            no_worse(candidate.metrics, incumbent.metrics, SWITCH+FADE) and
            bool(np.all(candidate.fetches <= incumbent.fetches)))


def challengers(end, incumbent, fractions):
    """Lattice pairs that beat the incumbent Score, best switch first."""
    found = []
    first, second = end.window(fractions)
    for a in first:
        for b in second:
            try:
                # Cheapest first: the 1D switch must improve, then the 2D
                # switch must not worsen; only then pay for the fade range.
                response = end.switch_response((a, b))
                switch = end.switch_scores(response, with_2d=False)
                if not (better_switch(switch, incumbent.metrics) and
                        no_worse(switch, incumbent.metrics, switch.keys())):
                    continue
                switch = end.switch_scores(response)
                if not no_worse(switch, incumbent.metrics, SWITCH):
                    continue
                fade, fetches = end.fade_scores((a, b))
            except ValueError:
                continue  # the kernel cannot be built from this pair
            candidate = Score({**switch, **fade}, fetches)
            if beats(candidate, incumbent):
                found.append(((a, b), candidate))
    found.sort(key=lambda item: item[1].metrics['switch L2'])
    return found


def describe(score):
    return ', '.join(f'{n} {v:.4g}' for n, v in score.metrics.items())+f', max fetches {score.fetches.max()}'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--write', action='store_true', help='improve the fractions and update the C#')
    args = parser.parse_args()
    np.seterr(all='ignore')  # Accelerate's matmul raises spurious FP flags on macOS

    ok = True
    texts = {}
    for name in ENDS:
        end = End(name)
        texts.setdefault(end.source, (KERNELS/end.source).read_text())
        fractions = end.fractions()
        print(f'{name}: package {fractions}')
        while True:
            incumbent = end.score(fractions)
            print(f'  {fractions}: {describe(incumbent)}')
            found = challengers(end, incumbent, fractions)
            if not found:
                print(f'  no lattice pair beats {fractions}')
                break
            print(f'  {len(found)} beat it; best {found[0][0]}: {describe(found[0][1])}')
            if not args.write:
                ok = False
                break
            fractions = found[0][0]
        texts[end.source] = end.write(texts[end.source], fractions)

    if args.write:
        for source, text in texts.items():
            (KERNELS/source).write_text(text)
            print('wrote', (KERNELS/source).relative_to(PACKAGE))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
