"""1D model of the layouts around sigma 11.5 to 24, sampled the way the shaders sample.

Signals are arrays of shape (texels, impulses) on one grid. Array index n is
the shaders' node n, and positions are in node units: node n sits at
position n (the shaders add 0.5 when they convert to texture coordinates).
Fetches are exact linear interpolation at positions that already sit on the
sampler's 1/256 grid. Only the source clamps at its edges, and the impulses
sit far from them.
"""
import math
import numpy as np

import kernels as k


def sample(signal, positions):
    """Bilinear fetch of `signal` at node positions (clamped to its edges)."""
    positions = np.asarray(positions, float)
    first = np.floor(positions)
    f = (positions-first)[:, None]
    n = signal.shape[0]
    i0 = np.clip(first.astype(int), 0, n-1)
    i1 = np.clip(first.astype(int)+1, 0, n-1)
    return signal[i0]*(1-f)+signal[i1]*f


def _fetches(signal, centers, taps, snap_offsets=False):
    """Sum of weight * sample(centers + offset) over (offset, weight) taps."""
    out = 0
    for offset, weight in taps:
        if snap_offsets:
            offset = k.snap(offset)
        out = out+weight*sample(signal, centers+offset)
    return out


def _by_parity(signal, n, rows, snap_offsets=False):
    """Output texel x applies the fetch row rows[x % 2] around itself."""
    x = np.arange(n)
    out = np.zeros((n, signal.shape[1]))
    for parity in range(2):
        sel = x[parity::2]
        out[parity::2] = _fetches(signal, sel.astype(float), rows[parity], snap_offsets)
    return out


def _anchored(signal, positions_by_tap, weights, anchor_index):
    """The shaders' residual sum: anchor + sum of (sample - anchor) * weight."""
    anchor = sample(signal, positions_by_tap[anchor_index])
    out = anchor.copy()
    for i, (p, w) in enumerate(zip(positions_by_tap, weights)):
        if i != anchor_index:
            out += (sample(signal, p)-anchor)*w
    return out


def _cubic(signal, n, cubic):
    """Cubic pass: output texel i reads input texel i // 4 with phase i % 4's two fetches."""
    i = np.arange(n)
    phase, coarse = i % 4, (i//4).astype(float)
    left = np.array([cubic[p][0] for p in phase])
    right = np.array([cubic[p][1] for p in phase])
    right_weight = np.array([cubic[p][3] for p in phase])[:, None]
    a = sample(signal, coarse+left)
    return a+(sample(signal, coarse+right)-a)*right_weight


# ---- Small pyramid (Layouts/SmallLayout.cs, Shaders/Small.hlsl) ------------------------------
# RecordSmallPass snaps every offset to 1/256.

def pyramid(source, sigma):
    if k.outer_upsample_bilinear(sigma) != 1:
        raise ValueError('model covers the pyramid only where its outer up is bilinear (sigma >= 4.5)')
    return _pyramid_level(source, k.outer_level_variance(k.requested_variance(sigma), k.outer_upsample(1.)[1]), outer=True)


def _pyramid_level(source, variance, outer=False):
    n = source.shape[0]
    if variance < k.NEXT_LEVEL_FADE_END:
        rows = k.gaussian_rows(variance, variance >= k.NEXT_LEVEL_FADE_START)
        return _by_parity(source, n, rows, snap_offsets=True)
    half_n = math.ceil(n/2)
    half = _fetches(source, 2*np.arange(half_n)+0.5, k.BINOMIAL_DOWN, snap_offsets=True)
    half = _pyramid_level(half, k.coarse_variance(variance))
    x = np.arange(n)
    if outer:
        return sample(half, x/2-0.25)  # FragLinearUp
    out = np.zeros((n, source.shape[1]))
    for parity in range(2):
        sel = x[parity::2]
        out[parity::2] = _fetches(half, (sel//2).astype(float), k.CUBIC_UP[2*parity:2*parity+2], snap_offsets=True)
    return out


# ---- Transition (Layouts/TransitionLayout.cs, Shaders/Transition.hlsl) -----------------------

def transition(source, kernel):
    n = source.shape[0]
    quarter = _fetches(source, 4*np.arange(math.ceil(n/4)).astype(float), k.QUARTER)
    eighth = _fetches(quarter, 2*np.arange(math.ceil(n/8)).astype(float), kernel.halve)
    blurred = np.zeros_like(eighth)
    x = np.arange(eighth.shape[0])
    for parity in range(2):
        sel = x[parity::2].astype(float)
        row = kernel.gaussian[parity]
        blurred[parity::2] = _anchored(eighth, [sel+o for o, _ in row], [w for _, w in row], len(row)//2)
    # Up: the pyramid's cubic 2x onto the quarter grid (FragUp).
    pitch = k.RECONSTRUCTION_PITCH
    fine_n = math.ceil(n/pitch)
    fine = np.zeros((fine_n, source.shape[1]))
    x = np.arange(fine_n)
    for parity in range(2):
        sel = x[parity::2]
        fine[parity::2] = _fetches(blurred, (sel//2).astype(float), k.CUBIC_UP[2*parity:2*parity+2], snap_offsets=True)
    # Final: a bilinear magnification by the pitch.
    return sample(fine, (np.arange(n)+0.5)/pitch-0.5)


# ---- Wide (Layouts/WideLayout.cs, Shaders/Wide.hlsl) ------------------------------------------

def wide(source, kernel):
    n = source.shape[0]
    current = _fetches(source, 4*np.arange(math.ceil(n/4)).astype(float), k.QUARTER)
    # FragWideHalve until the grid reaches the pitch: the binomial [1, 3, 3, 1] / 8
    # as two bilinear fetches of 1/2 at +-0.75 around the output texel.
    pitch = 4
    while pitch < kernel.pitch:
        current = _fetches(current, 2*np.arange(math.ceil(current.shape[0]/2))+0.5, [(-0.75, 0.5), (0.75, 0.5)])
        pitch *= 2
    # Gaussian: even texels read one row, odd texels its mirror.
    out = np.zeros_like(current)
    x = np.arange(current.shape[0])
    for parity, taps in enumerate(kernel.gaussian):
        sel = x[parity::2].astype(float)
        out[parity::2] = _anchored(current, [sel+first+fraction for first, fraction, _ in taps],
                                   [mass for _, _, mass in taps], len(taps)//2)
    current = out
    scale = kernel.pitch//4
    fine = _cubic(current, math.ceil(n/scale), kernel.cubic)
    # Final: a bilinear magnification by `scale`.
    return sample(fine, (np.arange(n)+0.5)/scale-0.5)


# ---- Point responses --------------------------------------------------------------------------

LENGTH = 1024
CENTER = 512


def configure(length):
    """Row length for point responses; wide strengths need longer rows."""
    global LENGTH, CENTER
    LENGTH, CENTER = length, length//2
PHASES = 16


def impulses():
    """One impulse per column, at CENTER + phase."""
    source = np.zeros((LENGTH, PHASES))
    for p in range(PHASES):
        source[CENTER+p, p] = 1
    return source


def ideal(sigma):
    """The target: the tapered Gaussian of sigma around each impulse."""
    out = np.zeros((LENGTH, PHASES))
    r = math.ceil(4*sigma)
    offsets = np.arange(-r, r+1)
    w = np.array([k.tapered_weight(int(o), sigma, 4) for o in offsets])
    w /= w.sum()
    for p in range(PHASES):
        out[CENTER+p+offsets, p] = w
    return out
