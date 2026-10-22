"""The 1D model the down/up fit works on: the target Gaussian and the down/Gaussian/up chain.

Every filter here is one axis of the separable 2D filter. Filters are N x N
(or coarse) matrices on a periodic line of N pixels, so there are no borders:
row i of a matrix is the filter that output pixel i applies.

    chain = Up @ Gaussian @ Down          (N x N)
    Down  : N   -> N/2   six symmetric taps around 2j + 0.5
    Gauss : N/2 -> N/2   tapered Gaussian on the half-res grid
    Up    : N/2 -> N     four taps per output, mirrored for odd pixels

The Gaussians are the runtime's (kernels.py mirrors BlurMath).
"""
import math
from typing import NamedTuple

import numpy as np

from kernels import gaussian

N = 64          # periodic line length; wide enough for every fitted width
REFERENCE = N//2  # even output pixel used to read rows, columns and moments
MOTION_WEIGHT = 2.  # weight of the point-response motion in features(), relative to rows and columns


def bernstein(v, degree, low, high):
    """Bernstein (Bezier) basis of the given degree at v, mapped from [low, high]."""
    x = np.clip((v-low)/(high-low), 0, 1)
    return np.array([math.comb(degree, i)*x**i*(1-x)**(degree-i) for i in range(degree+1)])


def bernstein_derivative(v, degree, low, high):
    """d/dv of bernstein(v, degree, low, high)."""
    lower = bernstein(v, degree-1, low, high)
    return degree/(high-low)*(np.r_[0., lower]-np.r_[lower, 0.])


# ---- Filters as matrices --------------------------------------------------------

def target(variance):
    """N x N matrix of the target: the tapered Gaussian with this variance."""
    return _circulant(N, gaussian(variance, 4))


def coarse_gaussian_matrix(variance):
    """(N/2 x N/2) tapered Gaussian on the half-res grid."""
    return _circulant(N//2, gaussian(variance, 4))


def _circulant(n, row):
    m = np.zeros((n, n))
    for i in range(n):
        for offset, w in row.items():
            m[i, (i+offset) % n] += w
    return m


def down_matrix(half_weights):
    """(N/2 x N) Down from (down0, down1, down2), its weights 0.5, 1.5 and 2.5 pixels from center.

    Half-res texel j reads source pixels 2j - 2 .. 2j + 3, so its center
    sits between source pixels 2j and 2j + 1.
    """
    down0, down1, down2 = half_weights
    taps = [down2, down1, down0, down0, down1, down2]
    m = np.zeros((N//2, N))
    for j in range(N//2):
        for h, w in enumerate(taps):
            m[j, (2*j+h-2) % N] += w
    return m


def up_matrix(weights):
    """(N x N/2) Up from its four even-pixel weights.

    Even output 2J reads half-res texels J - 2 .. J + 1 with weights 0..3;
    odd output 2J + 1 is the mirror image and reads J + 2 .. J - 1.
    """
    m = np.zeros((N, N//2))
    for J in range(N//2):
        for i, w in enumerate(weights):
            m[2*J, (J+i-2) % (N//2)] += w
            m[2*J+1, (J+2-i) % (N//2)] += w
    return m


# ---- What the fit compares --------------------------------------------------------

def features(t):
    """What the fit matches against the target, for both output parities.

    Rows: the filter each output pixel applies. Columns: the point response
    (how a bright source pixel spreads). Column differences: how that
    response changes as the point moves by one pixel, which controls shimmer.
    """
    p = REFERENCE
    rows = [t[p], t[p+1]]
    cols = [t[:, p], t[:, p+1]]
    motion = [MOTION_WEIGHT*(t[:, p+1]-t[:, p]), MOTION_WEIGHT*(t[:, p+2]-t[:, p+1])]
    return np.concatenate([np.roll(r, -p-i) for i, r in enumerate(rows)] +
                          [np.roll(c, -p-i) for i, c in enumerate(cols)] +
                          [np.roll(d, -p-i) for i, d in enumerate(motion)])


class Moments(NamedTuple):
    """Moments about the even and odd reference pixels, of the filter they apply
    (row) and of the point response they emit (column). The row's second moment
    is its variance once the row is centered."""
    even_row_mass: float
    even_row_mean: float
    even_row_variance: float
    odd_row_mass: float
    odd_row_mean: float
    odd_row_variance: float
    even_column_mass: float
    even_column_mean: float
    odd_column_mass: float
    odd_column_mean: float


def moments(t):
    x = (np.arange(N)+N//2) % N-N//2  # signed distance on the periodic line
    out = []
    for i in [REFERENCE, REFERENCE+1]:
        r = np.roll(t[i], -i)
        out += [r.sum(), (r*x).sum(), (r*x*x).sum()]
    for i in [REFERENCE, REFERENCE+1]:
        c = np.roll(t[:, i], -i)
        out += [c.sum(), (c*x).sum()]
    return Moments(*out)
