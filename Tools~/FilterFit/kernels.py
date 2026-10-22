"""Python mirror of the runtime coefficient builders (Runtime/Kernels/*.cs).

Function and field names follow the C#, so the two can be read side by side.
Constants are read from the C# itself (csharp.py), never copied.

Uploaded values are rounded to float32 like the C# Vector4s. Gaussian and
halve fetches are (offset, weight); cubic phases are (left, right, left
weight, right weight); wide Gaussian pairs are (first, fraction, weight).
"""
import math
import numpy as np

from csharp import read

SAMPLER_SUBTEXELS = read('BlurMath.cs', 'SamplerSubtexels')


def f32(x):
    return float(np.float32(x))


# ---- BlurMath -------------------------------------------------------------------------

def smooth(x):
    x = min(max(x, 0.), 1.)
    return x*x*(3-2*x)


def snap(x):
    """Rounds to the sampler grid, ties away from zero (odd: snap(-x) == -snap(x))."""
    return math.copysign(math.floor(abs(x)*SAMPLER_SUBTEXELS+0.5)/SAMPLER_SUBTEXELS, x)


def tapered_weight(k, spread, cutoff):
    taper = smooth((cutoff-abs(k)/spread)/0.5)
    return math.exp(-0.5*k*k/(spread*spread))*taper


def reach(spread, cutoff):
    return max(1, int(math.ceil(cutoff*spread)))


def tapered_variance(spread, cutoff):
    if spread < 1e-10:
        return 0.
    total = second = 0.
    for k in range(-reach(spread, cutoff), reach(spread, cutoff)+1):
        w = tapered_weight(k, spread, cutoff)
        total += w
        second += k*k*w
    return second/total


def solve_spread(variance, cutoff):
    lo, hi = 0., max(1., 2*math.sqrt(variance))
    for _ in range(48):
        middle = (lo+hi)*0.5
        if tapered_variance(middle, cutoff) < variance:
            lo = middle
        else:
            hi = middle
    return (lo+hi)*0.5


def gaussian(variance, cutoff):
    """Normalized tapered Gaussian of exactly this variance: {offset: weight}."""
    if variance < 1e-12:
        return {0: 1.}
    spread = solve_spread(variance, cutoff)
    r = reach(spread, cutoff)
    w = {k: tapered_weight(k, spread, cutoff) for k in range(-r, r+1)}
    total = sum(w[k] for k in range(-r, r+1))
    return {k: w[k]/total for k in range(-r, r+1)}


LARGE_SIGMA_THRESHOLD = read('BlurMath.cs', 'LargeSigmaThreshold')
LARGE_SIGMA_VARIANCE_FACTOR = read('BlurMath.cs', 'LargeSigmaVarianceFactor')


def requested_variance(sigma):
    if sigma < 1e-5:
        return 0.
    if sigma < LARGE_SIGMA_THRESHOLD:
        return tapered_variance(sigma, 4)
    return (tapered_variance(LARGE_SIGMA_THRESHOLD, 4) +
            LARGE_SIGMA_VARIANCE_FACTOR*(sigma*sigma-LARGE_SIGMA_THRESHOLD**2))


def cubic_bspline(node, f):
    return [(1-f)**3/6, (3*f**3-6*f*f+4)/6, (-3*f**3+3*f*f+3*f+1)/6, f**3/6][node]


# ---- BilinearPairs -------------------------------------------------------------------

def second_moment(x):
    fraction = x-math.floor(x)
    return x*x+fraction*(1-fraction)


def _moment(j, x):
    """Moment j of one unit fetch at x: total, first, second, even texels' first."""
    if j == 0:
        return 1.
    if j == 1:
        return x
    if j == 2:
        return second_moment(x)
    first = math.floor(x)
    fraction = x-first
    return first*(1-fraction) if first % 2 == 0 else (first+1)*fraction


def _restore(row, positions, weights, even_moment):
    """BilinearPairs.Restore: reweight fetches so the row's moments stay exact."""
    target = [sum(row.values()), sum(k*w for k, w in row.items()), sum(k*k*w for k, w in row.items()),
              sum(k*w for k, w in row.items() if k % 2 == 0)]
    for n in range(min(4 if even_moment else 3, len(positions)), 0, -1):
        g = np.array([[_moment(j, x) for x in positions] for j in range(n)])
        w = np.array(weights)
        matrix = (g*w)@g.T
        residual = np.array(target[:n])-g@w
        try:
            c = np.linalg.solve(matrix, residual)
        except np.linalg.LinAlgError:
            continue
        out = w*(1+c@g)
        if (out >= 0).all():
            return list(out)
    return list(weights)


def pack(row, capacity=16, even_moment=False, parity=-1):
    """BilinearPairs.Pack: row is {offset: weight}; (offset, weight) in position order.

    With parity below zero: texel 0 alone, then pairs (1, 2), (3, 4) ... and
    (-2, -1), (-4, -3) .... Otherwise pairs (k, k + 1) whose k has that parity.
    """
    lo, hi = min(row), max(row)
    if parity >= 0:
        start = lo-1 if (lo+parity) % 2 else lo
        pairs = [(k, k+1) for k in range(start, hi+1, 2)]
    else:
        pairs = [(0, 0)]+[(k, k+1) for k in range(1, hi+1, 2)]+[(k, k+1) for k in range(-2, lo-2, -2) if k+1 >= lo]
    positions, weights = [], []
    for first, second in pairs:
        a = row.get(first, 0.)
        b = 0. if first == second else row.get(second, 0.)
        if a+b <= 1e-14:
            continue
        if len(positions) == capacity:
            raise ValueError('too many fetches')
        positions.append(snap(first+b/(a+b)))
        weights.append(a+b)
    if not positions:
        return [(0., 1.)]
    weights = _restore(row, positions, weights, even_moment)
    return sorted((f32(x), f32(w)) for x, w in zip(positions, weights))


# ---- SmallKernel (pyramid part) ------------------------------------------------------------

LEVEL_VARIANCE = read('SmallKernel.cs', 'LevelVariance')
NEXT_LEVEL_FADE_START = read('SmallKernel.cs', 'NextLevelFadeStart')
NEXT_LEVEL_FADE_END = read('SmallKernel.cs', 'NextLevelFadeEnd')
BINOMIAL = read('SmallKernel.cs', 'Binomial')
# Fetches (offset, weight) of the binomial down and the cubic B-spline up.
BINOMIAL_DOWN = [(f32(x), f32(w)) for x, w, _, _ in read('SmallKernel.cs', 'BinomialDown')]
CUBIC_UP = [(f32(x), f32(w)) for x, w, _, _ in read('SmallKernel.cs', 'CubicUp')]


def coarse_variance(variance):
    return (variance-LEVEL_VARIANCE)/4


def outer_upsample_bilinear(sigma):
    """How far the outermost upsample has turned bilinear: 0 below sigma 4.25, 1 from 4.5."""
    start, end = OUTER_BILINEAR_START_SIGMA, OUTER_BILINEAR_END_SIGMA
    return smooth((sigma*sigma-start*start)/(end*end-start*start))


CUBIC_UP_VARIANCE = read('SmallKernel.cs', 'CubicUpVariance')
OUTER_BILINEAR_START_SIGMA = read('SmallKernel.cs', 'OuterBilinearStartSigma')
OUTER_BILINEAR_END_SIGMA = read('SmallKernel.cs', 'OuterBilinearEndSigma')
# SmallKernel.ParityPairingStart: rows pair by output parity from this sigma.
PARITY_PAIRING_START = requested_variance(read('SmallKernel.cs', 'ParityPairingSigma'))


def outer_level_variance(variance, up_variance):
    """The outermost level's budget when its Up adds up_variance (half-res pixels²)."""
    return variance+4*(CUBIC_UP_VARIANCE-up_variance)


def _add_fetch(row, position, weight):
    first = math.floor(position)
    fraction = position-first
    for k, w in ((first, weight*(1-fraction)), (first+1, weight*fraction)):
        if w > 0:
            row[k] = row.get(k, 0.)+w


def outer_upsample(bilinear):
    """SmallKernel.OuterUpsample: ([even fetches, odd fetches], variance in half-res pixels²)."""
    taps, variance = [], 0.
    for phase in range(2):
        row = {}
        for x, w in CUBIC_UP[2*phase:2*phase+2]:
            _add_fetch(row, x, (1-bilinear)*w)
        center = -0.25 if phase == 0 else 0.25
        _add_fetch(row, center, bilinear)
        start = -2 if phase == 0 else -1

        def fraction(first):
            a, b = row.get(first, 0.), row.get(first+1, 0.)
            return snap(b/(a+b)) if a+b > 0 else 0.
        left = start+fraction(start)
        right = start+2+fraction(start+2)
        w = (center-left)/(right-left)
        taps.append([(f32(left), f32(1-w)), (f32(right), f32(w))])
        variance += ((1-w)*second_moment(left)+w*second_moment(right)-center*center)/2
    return taps, variance


def gaussian_rows(variance, into_next_level):
    """SmallKernel.GaussianRows for Crossfade.None / IntoNextLevel: [even fetches, odd fetches]."""
    g = gaussian(variance, 4)
    fade = 0.
    if into_next_level:
        fade = smooth((variance-NEXT_LEVEL_FADE_START)/(NEXT_LEVEL_FADE_END-NEXT_LEVEL_FADE_START))
        if fade > 0:
            coarse = gaussian_rows(coarse_variance(variance), False)
    rows = []
    for x in range(2):
        row = {}
        for k, w in g.items():
            if (1-fade)*w > 0:
                row[k] = row.get(k, 0.)+(1-fade)*w
        if fade > 0:
            # The next level as its passes run: cubic up fetches, the coarse
            # Gaussian's fetches for each coarse texel's parity, binomial down.
            for ux, uw in CUBIC_UP[2*x:2*x+2]:
                uf = math.floor(ux)
                for c, cw in ((uf, uw*(1-(ux-uf))), (uf+1, uw*(ux-uf))):
                    if cw == 0:
                        continue
                    for gx, gw in coarse[c % 2]:
                        position = c+gx
                        gf = math.floor(position)
                        for b, bw in ((gf, gw*(1-(position-gf))), (gf+1, gw*(position-gf))):
                            if bw == 0:
                                continue
                            for h in range(6):
                                v = fade*cw*bw*BINOMIAL[h]
                                if v > 0:
                                    row[2*b+h-2-x] = row.get(2*b+h-2-x, 0.)+v
        rows.append(pack(row, parity=x if variance >= PARITY_PAIRING_START else -1))
    return rows


# ---- TransitionKernel -----------------------------------------------------------------------

def positive_gaussian(variance, cutoff):
    """TransitionKernel.BuildGaussian: {offset: weight}."""
    if not variance > 0:
        raise ValueError('working variance must be positive')
    return gaussian(variance, cutoff)


def _transition(name):
    return read('TransitionKernel.cs', name)


START_SIGMA = _transition('StartSigma')
SETTLED_SIGMA = _transition('SettledSigma')
WIDE_FADE_SIGMA = _transition('WideFadeSigma')
END_SIGMA = _transition('EndSigma')
# The pyramid's 2x down as the sampler runs it (TransitionKernel uses SmallKernel's).
PYRAMID_OUTER_MASS = read('SmallKernel.cs', 'DownOuterMass')
PYRAMID_OUTER_FAR_FRACTION = read('SmallKernel.cs', 'DownOuterFarFraction')
BINOMIAL_OUTER_MASS = _transition('BinomialOuterMass')
QUARTER_VARIANCE = _transition('QuarterVariance')
QUARTER = [(x, w) for x, w, _, _ in _transition('Quarter')]
RECONSTRUCTION_PITCH = _transition('ReconstructionPitch')
PHASE_CAPACITY = _transition('PhaseCapacity')


def virtual_up(fractions):
    """Virtual level's 2x up weights on coarse texels -2..+1 for fetch fractions in 1/256."""
    f0, f1 = fractions[0]/256, fractions[1]/256
    m = (f1+0.25)/(2+f1-f0)
    return [m*(1-f0), m*f0, (1-m)*(1-f1), (1-m)*f1]


class Transition:
    """TransitionKernel at one sigma, with the virtual level's up fractions as input."""
    def __init__(self, sigma, wide_up):
        squared = sigma*sigma
        self.settled = smooth((squared-START_SIGMA**2)/(SETTLED_SIGMA**2-START_SIGMA**2))
        blended = PYRAMID_OUTER_MASS*(1-self.settled)+BINOMIAL_OUTER_MASS*self.settled
        far = PYRAMID_OUTER_MASS*(1-self.settled)*PYRAMID_OUTER_FAR_FRACTION
        inner = snap(1-far/blended)
        outer = 0.125/(2*inner-1)
        left = -2+inner
        self.halve = [(f32(left), f32(outer)), (0.5, f32(1-2*outer)), (f32(1-left), f32(outer))]
        halve_variance = outer*(second_moment(left)+second_moment(1-left))+(1-2*outer)*0.5-0.25
        self.gaussian_variance = (requested_variance(sigma)-(QUARTER_VARIANCE+16*halve_variance) -
                                  reconstruction_variance())/64
        row = positive_gaussian(self.gaussian_variance, 4+self.settled)
        weight = smooth((squared-WIDE_FADE_SIGMA**2)/(END_SIGMA**2-WIDE_FADE_SIGMA**2))
        eighth = {}
        if weight > 0:
            halve = [0., BINOMIAL_OUTER_MASS, 0.5-BINOMIAL_OUTER_MASS]
            add_virtual_level(eighth, weight, self.gaussian_variance, halve+halve[::-1], virtual_up(wide_up),
                              4+self.settled)
        for k, w in row.items():
            eighth[k] = eighth.get(k, 0.)+w*(1-weight)
        even = pack(eighth, PHASE_CAPACITY, even_moment=True)
        self.gaussian = [even, [(-x, w) for x, w in even]]


def add_virtual_level(row, weight, fine_variance, halve, up, cutoff):
    """TransitionKernel.AddVirtualLevel: weight x (halve, Gaussian, 2x up) on the fine grid."""
    up_variance = sum(w*(i-1.75)**2 for i, w in enumerate(up))
    halve_variance = sum(w*(i-2.5)**2 for i, w in enumerate(halve))
    g = positive_gaussian((fine_variance-halve_variance-4*up_variance)/4, cutoff)
    for u in range(4):
        for k, w in g.items():
            a = up[u]*w*weight
            if a == 0:
                continue
            for d in range(6):
                mass = a*halve[d]
                if mass:
                    node = 2*(u-2+k)+d-2
                    row[node] = row.get(node, 0.)+mass


def magnification(scale):
    """WideKernel.Magnification: variance of a bilinear magnification by scale, in its output texels²."""
    return 0. if scale == 1 else (scale*scale-1)/6+0.25


def reconstruction_variance():
    """TransitionKernel.ReconstructionVariance at full resolution: the cubic Up, then Final."""
    return 64*CUBIC_UP_VARIANCE+magnification(RECONSTRUCTION_PITCH)


# ---- WideKernel ---------------------------------------------------------------------------

def wide_cubic():
    out, variance = [], 0.
    for phase in range(4):
        position = (phase+0.5)/4-0.5
        first = math.floor(position)-1
        t = position-math.floor(position)
        a, b, c, d = (cubic_bspline(n, t) for n in range(4))
        left = first+snap(b/(a+b))
        right = first+2+snap(d/(c+d))
        w = (position-left)/(right-left)
        out.append((f32(left), f32(right), f32(1-w), f32(w)))
        variance += (1-w)*second_moment(left)+w*second_moment(right)-position*position
    return out, variance/4


BASE_PITCH = read('WideKernel.cs', 'BasePitch')
FIRST_DOUBLING_SIGMA = read('WideKernel.cs', 'FirstDoublingSigma')
WIDE_GAUSSIAN_CUTOFF = read('WideKernel.cs', 'GaussianCutoff')
PITCH_FADE_START = read('WideKernel.cs', 'PitchFadeStart')
WIDE_PHASE_CAPACITY = read('WideKernel.cs', 'PhaseCapacity')
BINOMIAL_HALVE = read('WideKernel.cs', 'BinomialHalve')
PITCH_UP_FRACTIONS = read('WideKernel.cs', 'PitchUpFractions')


class Wide:
    """WideKernel at one sigma; pitch_up overrides the pitch fade's up fractions."""
    def __init__(self, sigma, pitch_up=None):
        self.pitch = BASE_PITCH
        doubling = FIRST_DOUBLING_SIGMA
        doublings = 0
        while sigma >= doubling:
            self.pitch *= 2
            doubling *= 2
            doublings += 1
        table = PITCH_UP_FRACTIONS
        pitch_up = pitch_up or table[min(doublings, len(table)-1)]
        self.cubic, cubic_variance = wide_cubic()
        reduction = QUARTER_VARIANCE
        p = 4
        while p < self.pitch:
            reduction += 0.75*p*p
            p *= 2
        scale = self.pitch//4
        reconstruction = self.pitch*self.pitch*cubic_variance+magnification(scale)
        self.gaussian_variance = (requested_variance(sigma)-reduction-reconstruction)/(self.pitch*self.pitch)
        if self.gaussian_variance <= 0:
            raise ValueError('wide Gaussian variance must be positive')
        start = PITCH_FADE_START*doubling
        weight = smooth((sigma*sigma-start*start)/(doubling*doubling-start*start))
        row = {k: w*(1-weight) for k, w in gaussian(self.gaussian_variance, WIDE_GAUSSIAN_CUTOFF).items()}
        if weight > 0:
            add_virtual_level(row, weight, self.gaussian_variance, BINOMIAL_HALVE, virtual_up(pitch_up),
                              WIDE_GAUSSIAN_CUTOFF)
        even = pack(row, WIDE_PHASE_CAPACITY, even_moment=True)

        def fetch(x, w):
            first = math.floor(x)
            return (first, f32(x-first), w)
        # Even texels' row, then its mirror for odd texels: (first, fraction, weight).
        self.gaussian = [[fetch(x, w) for x, w in even], [fetch(f32(-x), w) for x, w in even]]
