"""Fits the 2x down/up pair of sigma 1.46 to 2.40 and writes its C# data.

    python3 fit_down_up.py            fit, then compare with the package's data file
    python3 fit_down_up.py --write    fit, then regenerate the data file

The data file is Runtime/Kernels/FittedDownUp.Generated.cs. FittedDownUp.cs
evaluates it at runtime; read that file for what the pair does.

The fit, per axis, on the periodic 1D model in model.py:
  1. Down. At nine strengths from sigma 1.46 to 1.74, search the one free
     shape coordinate of the six-tap Down (its slide along DOWN_SLIDE); for
     each candidate, solve the best Up exactly (a small QP). A cubic Bezier
     through the best slides becomes the Down curve. From 1.74 to 2.40 the
     runtime eases Down into the pyramid's binomial.
  2. Up. With Down and the half-res Gaussian fixed, fit Up's four weights as
     two Bezier curves (degree 6 to sigma 1.62, then degree 16) by one
     constrained least-squares problem: match the target Gaussian's rows,
     point responses and their motion, plus its variance; keep every weight
     nonnegative, every control at unit mass and centered; keep up0 at zero
     before 1.62; join the curves with matching value and slope; end at the
     pyramid's cubic up, as the sampler runs it, with zero slope.
  3. Inverse table. The chain's actual variance V(p) is close to p but not
     equal. Tabulate p for 401 evenly spaced variances by inverting V. The
     runtime rounds fetch positions and corrects what rounding and the
     table's interpolation leave of the variance.

Every number this file chooses or fits is written to the data file. Numbers
the runtime defines elsewhere (the binomial, a pyramid level's variance) are
read from its C#.
"""
import argparse
import math
import re
import sys

import numpy as np

import kernels as k
import positive_qp
from csharp import KERNELS, PACKAGE, read
from model import (Moments, bernstein, bernstein_derivative, coarse_gaussian_matrix, down_matrix,
                   features, moments, target, up_matrix)

# ---- Design choices (sigma) ---------------------------------------------------------
SIGMA_START = 1.46            # full-res Gaussian starts crossfading into Down then Up
SIGMA_SPLIT = 1.62            # Up's second curve begins; before it, up0 stays zero
SIGMA_HALF_RES_GAUSSIAN = 1.74  # half-res Gaussian fades in; Down starts easing to binomial
SIGMA_HOLD = 1.80             # half-res Gaussian reaches HOLD_SHARE of a pyramid level's ...
HOLD_SHARE = 0.60
SIGMA_RISE = 2.10             # ... then rises to all of it at SIGMA_END
SIGMA_END = 2.40              # the pair equals one pyramid level

DOWN_DEGREE = 3
UP_DEGREES = (6, 16)
VARIANCE_WEIGHT = 30.         # weight of the variance-matching rows in the Up fit
SLOPE_WEIGHT = 0.03           # weight of the d/dp rows, relative to the value rows
SAMPLES = 48                  # Up fit samples over the whole range

# Down's weights (down0, down1, down2) at 0.5, 1.5 and 2.5 pixels from the
# half-res center obey two rules: the six taps sum to one, and a moving point
# stays centered whether it lands on an even or an odd source pixel
# (down1 - down2 = 1/8). DOWN_BASE obeys both; sliding along DOWN_SLIDE
# moves weight from the center taps to the outer ones and keeps both.
DOWN_BASE = np.array([1/6, 11/48, 5/48])
DOWN_SLIDE = np.array([-2, 1, 1])/math.sqrt(6)
BINOMIAL_DOWN = np.array(k.BINOMIAL.weights[2::-1])  # the pyramid's Down: (10, 5, 1) / 32
# Up ends at the pyramid's up as the sampler runs it: SmallKernel.CubicUp's
# two even-phase fetches, as weights of half-res texels J - 2 .. J + 1 for
# even output 2J, which sits at J - 1/4.
def _texels(fetches):
    w = np.zeros(4)
    for x, mass in fetches:
        first = math.floor(x)
        w[first+2] += mass*(1-(x-first))
        if x > first:
            w[first+3] += mass*(x-first)
    return w


_CUBIC_UP_RIGHT_WEIGHT = read('SmallKernel.cs', 'CubicUpRightWeight')
CUBIC_UP = _texels([(read('SmallKernel.cs', 'CubicUpLeft'), 1-_CUBIC_UP_RIGHT_WEIGHT),
                    (read('SmallKernel.cs', 'CubicUpRight'), _CUBIC_UP_RIGHT_WEIGHT)])

# The same points as requested variances, the unit the curves run in.
START_VARIANCE = k.requested_variance(SIGMA_START)
SPLIT_VARIANCE = k.requested_variance(SIGMA_SPLIT)
HALF_RES_START_VARIANCE = k.requested_variance(SIGMA_HALF_RES_GAUSSIAN)
HOLD_VARIANCE = k.requested_variance(SIGMA_HOLD)
RISE_VARIANCE = k.requested_variance(SIGMA_RISE)
END_VARIANCE = k.requested_variance(SIGMA_END)


# ---- The pair at curve position p (mirrors FittedDownUp.cs) -------------------------

def down_weights(p, slide_controls):
    """(down0, down1, down2) at curve position p."""
    slide = bernstein(p, DOWN_DEGREE, START_VARIANCE, HALF_RES_START_VARIANCE)@slide_controls
    to_binomial = k.smooth((p-HALF_RES_START_VARIANCE)/(END_VARIANCE-HALF_RES_START_VARIANCE))
    return np.maximum((DOWN_BASE+DOWN_SLIDE*slide)*(1-to_binomial)+to_binomial*BINOMIAL_DOWN, 0.)


def half_res_gaussian_variance(p):
    """Variance of the Gaussian between Down and Up, in half-res pixels²."""
    coarse_variance = max(k.coarse_variance(p), 0.)
    fade_in = k.smooth((p-HALF_RES_START_VARIANCE)/(HOLD_VARIANCE-HALF_RES_START_VARIANCE))
    share = HOLD_SHARE+(1-HOLD_SHARE)*k.smooth((p-RISE_VARIANCE)/(END_VARIANCE-RISE_VARIANCE))
    return coarse_variance*fade_in*share


def up_weights(p, before_split, after_split):
    """(up0, up1, up2, up3) at curve position p."""
    if p < SPLIT_VARIANCE:
        return bernstein(p, UP_DEGREES[0], START_VARIANCE, SPLIT_VARIANCE)@before_split
    return bernstein(p, UP_DEGREES[1], SPLIT_VARIANCE, END_VARIANCE)@after_split


def tap_chains(p, slide_controls):
    """Down, then the Gaussian, then Up tap m alone: one N x N chain per Up tap.

    The whole chain is linear in Up's weights, so the chain for any Up is
    the weighted sum of these four.
    """
    a = coarse_gaussian_matrix(half_res_gaussian_variance(p))@down_matrix(down_weights(p, slide_controls))
    return [up_matrix(u)@a for u in np.eye(4)]


# ---- Step 1: Down -------------------------------------------------------------------

def best_up_error(down, variance, target_features):
    """How close the best Up (by a small QP) brings Down to the target."""
    chains = [up_matrix(u)@down_matrix(down) for u in np.eye(4)]
    f = np.array([features(c) for c in chains]).T
    m = np.array([moments(c) for c in chains]).T
    goal = np.array(Moments(even_row_mass=1, even_row_mean=0, even_row_variance=variance,
                            odd_row_mass=1, odd_row_mean=0, odd_row_variance=variance,
                            even_column_mass=1, even_column_mean=0, odd_column_mass=1, odd_column_mean=0))
    keep = (np.linalg.norm(m, axis=1) > 1e-8) | (goal != 0)  # drop rules that hold for any Up
    up, _ = positive_qp.solve(f, target_features, m[keep], goal[keep])
    if up is None:
        return np.inf
    chain = up_matrix(up)@down_matrix(down)
    return float(np.linalg.norm(features(chain)-target_features))


def fit_slide_at(sigma, grid=41):
    """Best slide of Down at this sigma: a grid over [-0.6, 0.6], then a pattern search."""
    variance = k.requested_variance(sigma)
    target_features = features(target(variance))

    def error(slide):
        down = DOWN_BASE+DOWN_SLIDE*slide
        if down.min() < -1e-12:
            return np.inf
        return best_up_error(np.maximum(down, 0), variance, target_features)

    best_error, best = min((error(s), s) for s in np.linspace(-.6, .6, grid))
    step = 1.2/grid
    while step > 1e-6:
        improved = False
        for direction in [1, -1]:
            trial = best+direction*step
            e = error(trial)
            if e < best_error-1e-13:
                best_error, best, improved = e, trial, True
        if not improved:
            step *= .5
    return best


def fit_down_curve():
    """Cubic Bezier controls of Down's slide over [START_VARIANCE, HALF_RES_START_VARIANCE]."""
    sigmas = np.linspace(SIGMA_START, SIGMA_HALF_RES_GAUSSIAN, 9)
    slides = [fit_slide_at(s) for s in sigmas]
    basis = np.array([bernstein(k.requested_variance(s), DOWN_DEGREE, START_VARIANCE, HALF_RES_START_VARIANCE)
                      for s in sigmas])
    return np.linalg.lstsq(basis, slides, rcond=None)[0]


# ---- Step 2: Up -------------------------------------------------------------------------
# The unknowns are both curves' control points, four weights each, in one
# vector: the before-split curve's controls, then the after-split curve's.

SEGMENTS = [(START_VARIANCE, SPLIT_VARIANCE, UP_DEGREES[0]), (SPLIT_VARIANCE, END_VARIANCE, UP_DEGREES[1])]
UNKNOWNS = sum((degree+1)*4 for _, _, degree in SEGMENTS)


def unknown(segment, control, tap):
    """Index of one control point's weight in the unknown vector."""
    first = 0 if segment == 0 else (SEGMENTS[0][2]+1)*4
    return first+control*4+tap


def _row(segment, control, weights):
    """A row of the unknown vector carrying weights[tap] at one control point's taps."""
    r = np.zeros(UNKNOWNS)
    r[unknown(segment, control, 0):unknown(segment, control, 4)] = weights
    return r


def _objective(slide_controls):
    """Least-squares rows: match the target, its change with p, and its variance."""
    rows, targets = [], []
    for segment, (lo, hi, degree) in enumerate(SEGMENTS):
        span = slice(unknown(segment, 0, 0), unknown(segment, degree+1, 0))
        for p in np.linspace(lo, hi, SAMPLES//2+1):
            chains = tap_chains(p, slide_controls)
            per_tap = np.array([features(c) for c in chains]).T
            goal = features(target(p))
            # Match the target ...
            r = np.zeros((len(goal), UNKNOWNS))
            r[:, span] = np.kron(bernstein(p, degree, lo, hi), per_tap)
            rows.append(r)
            targets.append(goal)
            # ... and how it changes with p, so the blur animates smoothly.
            slope = (features(target(p+1e-4))-features(target(p-1e-4)))/2e-4
            scale = SLOPE_WEIGHT*np.linalg.norm(goal)/np.linalg.norm(slope)
            r = np.zeros((len(goal), UNKNOWNS))
            r[:, span] = scale*np.kron(bernstein_derivative(p, degree, lo, hi), per_tap)
            rows.append(r)
            targets.append(scale*slope)
            # The chain's variance is linear in Up's weights: pull it to p.
            variances = np.array([moments(c).even_row_variance for c in chains])
            r = np.zeros((1, UNKNOWNS))
            r[0, span] = VARIANCE_WEIGHT*np.kron(bernstein(p, degree, lo, hi), variances)
            rows.append(r)
            targets.append([VARIANCE_WEIGHT*p])
    return np.vstack(rows), np.concatenate(targets)


def _control_constraints(slide_controls):
    """Every control point on its own: unit mass, a centered row and a centered point response.

    (The odd pixel mirrors the even one, so the even pixel's moments suffice.)
    """
    rows, values = [], []
    for segment, (lo, hi, degree) in enumerate(SEGMENTS):
        for control, p in enumerate(np.linspace(lo, hi, degree+1)):
            if segment == 1 and control >= degree-1:
                continue  # the last two are pinned to the cubic in _join_and_end_constraints
            m = [moments(c) for c in tap_chains(p, slide_controls)]
            for name, value in [('even_row_mass', 1), ('even_row_mean', 0), ('even_column_mean', 0)]:
                weights = np.array([getattr(tap, name) for tap in m])
                if np.linalg.norm(weights) < 1e-8 and value == 0:
                    continue  # already holds for any Up
                rows.append(_row(segment, control, weights))
                values.append(value)
    return rows, values


def _join_and_end_constraints():
    """The curves join with equal value and slope; the second ends at the cubic B-spline, flat."""
    (lo1, hi1, d1), (lo2, hi2, d2) = SEGMENTS
    rows, values = [], []
    for tap in range(4):
        # Value: the first curve's last control equals the second's first.
        r = np.zeros(UNKNOWNS)
        r[unknown(0, d1, tap)] = 1
        r[unknown(1, 0, tap)] = -1
        rows.append(r)
        values.append(0.)
        # Slope: a Bezier's end slope is degree / span times its last control step.
        r = np.zeros(UNKNOWNS)
        r[unknown(0, d1, tap)] += d1/(hi1-lo1)
        r[unknown(0, d1-1, tap)] -= d1/(hi1-lo1)
        r[unknown(1, 1, tap)] -= d2/(hi2-lo2)
        r[unknown(1, 0, tap)] += d2/(hi2-lo2)
        rows.append(r)
        values.append(0.)
    # Two equal last controls: the curve ends at that value with zero slope.
    for control in [d2-1, d2]:
        for tap in range(4):
            r = np.zeros(UNKNOWNS)
            r[unknown(1, control, tap)] = 1
            rows.append(r)
            values.append(CUBIC_UP[tap])
    return rows, values


def fit_up_curves(slide_controls):
    """Bezier controls of Up's weights, one (degree + 1) x 4 array per segment."""
    a, b = _objective(slide_controls)
    rows, values = _control_constraints(slide_controls)
    join_rows, join_values = _join_and_end_constraints()
    c, d = np.array(rows+join_rows), np.array(values+join_values)
    # up0 is zero before the split and starts from zero after it. Those
    # unknowns are removed rather than constrained: equality-pinned unknowns
    # on the positivity bound make the interior-point solve degenerate.
    d1 = SEGMENTS[0][2]
    zero = {unknown(0, i, 0) for i in range(d1+1)} | {unknown(1, i, 0) for i in [0, 1]}
    free = np.array([i for i in range(UNKNOWNS) if i not in zero])
    c = c[:, free]
    keep = np.linalg.norm(c, axis=1) > 1e-12  # rows that only held removed unknowns
    solution, diagnostics = positive_qp.solve(a[:, free], b, c[keep], d[keep], max_iterations=400)
    if solution is None:
        raise RuntimeError(diagnostics)
    w = np.zeros(UNKNOWNS)
    w[free] = solution
    first = unknown(1, 0, 0)
    return w[:first].reshape(-1, 4), w[first:].reshape(-1, 4), diagnostics



# ---- Step 3: inverse table --------------------------------------------------------------

class Fit:
    def __init__(self):
        self.slide_controls = fit_down_curve()
        self.before_split, self.after_split, self.diagnostics = fit_up_curves(self.slide_controls)
        self._ps = np.linspace(START_VARIANCE, END_VARIANCE, 801)
        self._vs = np.array([self.variance(p) for p in self._ps])

    def variance(self, p):
        """Actual variance of the chain at curve position p, in source pixels²."""
        down = down_matrix(down_weights(p, self.slide_controls))
        up = up_matrix(up_weights(p, self.before_split, self.after_split))
        return moments(up@coarse_gaussian_matrix(half_res_gaussian_variance(p))@down).even_row_variance

    def curve_position(self, variance):
        """The p whose chain has this variance: table guess, then Newton steps."""
        p = float(np.interp(variance, self._vs, self._ps))
        for _ in range(4):
            if p <= START_VARIANCE or p >= END_VARIANCE:
                break
            hi, lo = min(p+1e-6, END_VARIANCE), max(p-1e-6, START_VARIANCE)
            slope = (self.variance(hi)-self.variance(lo))/(hi-lo)
            p = min(max(p-(self.variance(p)-variance)/slope, START_VARIANCE), END_VARIANCE)
        return p

    def inverse_table(self):
        targets = np.linspace(START_VARIANCE, END_VARIANCE, 401)
        table = [self.curve_position(float(v)) for v in targets]
        # V(p) runs backward briefly, so a lookup could land on another branch.
        # The runtime interpolates the table, which must therefore rise
        # monotonically and solve V(p) = v at every entry.
        if not np.all(np.diff(table) > 0):
            raise RuntimeError('inverse table is not increasing')
        residual = max(abs(self.variance(p)-v) for p, v in zip(table, targets))
        if residual > 1e-8:
            raise RuntimeError(f'inverse table misses its variance by {residual:.3g}')
        return table

    def report(self):
        weights = np.r_[self.before_split.ravel(), self.after_split.ravel()]
        return dict(up_fit=self.diagnostics['reason'], min_up_control=float(weights.min()),
                    variance_runs_backward=bool(np.any(np.diff(self._vs) <= 0)),
                    max_variance_minus_p=float(abs(self._vs-self._ps).max()))


# ---- C# export ----------------------------------------------------------------------------

OUTPUT = KERNELS/'FittedDownUp.Generated.cs'


def _number(x):
    return repr(float(x))


def _rows(matrix, indent):
    return (',\n'+indent).join('{ '+', '.join(_number(x) for x in row)+' }' for row in matrix)


def _list(values, indent, per_line):
    lines = [', '.join(_number(x) for x in values[i:i+per_line]) for i in range(0, len(values), per_line)]
    return (',\n'+indent).join(lines)


def sigma_for_variance(variance):
    lo, hi = 0., 10.
    for _ in range(80):
        mid = (lo+hi)/2
        lo, hi = (mid, hi) if k.requested_variance(mid) < variance else (lo, mid)
    return (lo+hi)/2


def runtime_curve_position(table, variance):
    """FittedDownUp.CurvePosition: Catmull-Rom interpolation of the inverse table."""
    last = len(table)-1
    x = max(0., min(last, (variance-START_VARIANCE)/(END_VARIANCE-START_VARIANCE)*last))
    i = min(int(x), last-1)
    f = x-i
    m0 = table[1]-table[0] if i == 0 else (table[i+1]-table[i-1])*0.5
    m1 = table[last]-table[last-1] if i+1 == last else (table[i+2]-table[i])*0.5
    return ((2*f**3-3*f*f+1)*table[i]+(f**3-2*f*f+f)*m0+(-2*f**3+3*f*f)*table[i+1]+(f**3-f*f)*m1)


def split_variance(table):
    """The variance at which the runtime's curve position reaches the split.

    Below it Up keeps up0 at zero, so the full-res row that crossfades into
    Down then Up fits five fetches up to the switch.
    """
    lo, hi = START_VARIANCE, END_VARIANCE
    for _ in range(100):
        mid = (lo+hi)/2
        lo, hi = (mid, hi) if runtime_curve_position(table, mid) < SPLIT_VARIANCE else (lo, mid)
    return lo


def csharp(fit):
    i3 = ' '*12
    table = fit.inverse_table()
    split = split_variance(table)
    return f"""// Generated by Tools~/FilterFit/fit_down_up.py. Do not edit; rerun the script.
// Variances are in source pixels²; the comments give the sigma each one is
// the requested variance of. FittedDownUp.cs explains how they are used.
namespace SteplessGaussianBlur
{{
    internal readonly partial struct FittedDownUp
    {{
        internal const double Start = {_number(START_VARIANCE)}; // sigma {SIGMA_START:.2f}
        internal const double End = {_number(END_VARIANCE)}; // sigma {SIGMA_END:.2f}
        const double UpsampleSplit = {_number(SPLIT_VARIANCE)}; // sigma {SIGMA_SPLIT:.2f}
        const double HalfResGaussianStart = {_number(HALF_RES_START_VARIANCE)}; // sigma {SIGMA_HALF_RES_GAUSSIAN:.2f}
        const double HalfResGaussianHold = {_number(HOLD_VARIANCE)}; // sigma {SIGMA_HOLD:.2f}
        const double HalfResGaussianRise = {_number(RISE_VARIANCE)}; // sigma {SIGMA_RISE:.2f}
        const double HalfResGaussianHoldShare = {_number(HOLD_SHARE)};

        /// <summary>The variance whose curve position is UpsampleSplit (sigma {sigma_for_variance(split):.4f}).</summary>
        internal const double SplitVariance = {_number(split)};

        // Down's (down0, down1, down2) = DownBase + slide * DownSlide.
        static readonly double[] DownBase = {{ {_list(DOWN_BASE, i3, 3)} }};
        static readonly double[] DownSlide = {{ {_list(DOWN_SLIDE, i3, 3)} }};

        // Cubic Bezier controls of Down's slide, on [Start, HalfResGaussianStart].
        static readonly double[] DownSlideControls =
        {{
            {_list(fit.slide_controls, i3, 2)}
        }};

        // Bezier controls of Up's weights (columns up0..up3), on [Start, UpsampleSplit].
        static readonly double[,] UpControlsBeforeSplit =
        {{
            {_rows(fit.before_split, i3)}
        }};

        // Bezier controls of Up's weights (columns up0..up3), on [UpsampleSplit, End].
        static readonly double[,] UpControlsAfterSplit =
        {{
            {_rows(fit.after_split, i3)}
        }};

        // p for variance Start + i / 400 * (End - Start).
        internal static readonly double[] InverseTable =
        {{
            {_list(table, i3, 4)}
        }};
    }}
}}
"""


def _values(source):
    """The numbers of generated C#, ignoring comments."""
    code = '\n'.join(line.split('//')[0] for line in source.splitlines())
    return [float(x) for x in re.findall(r'(?<![\w.])-?\d+\.\d*(?:[eE][-+]?\d+)?|(?<![\w.])-?\d+[eE][-+]?\d+', code)]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--write', action='store_true', help='regenerate '+str(OUTPUT.relative_to(PACKAGE)))
    args = parser.parse_args()
    np.seterr(all='ignore')  # Accelerate's matmul raises spurious FP flags on macOS

    fit = Fit()
    print('fit:', fit.report())
    text = csharp(fit)
    if args.write:
        OUTPUT.write_text(text)
        print('wrote', OUTPUT.relative_to(PACKAGE))
        return 0
    fresh, shipped = _values(text), _values(OUTPUT.read_text())
    if len(fresh) != len(shipped):
        print(f'value count differs: fit {len(fresh)}, package {len(shipped)}; run with --write')
        return 1
    # Another LAPACK build moves the QP's near-zero weights by about 1e-10,
    # so compare against max(1, |value|) rather than purely relatively.
    worst = max(abs(a-b)/max(1., abs(b)) for a, b in zip(fresh, shipped))
    print(f'{len(fresh)} values; largest difference {worst:.3g} (relative above 1)')
    return 0 if worst < 1e-7 else 1


if __name__ == '__main__':
    sys.exit(main())
