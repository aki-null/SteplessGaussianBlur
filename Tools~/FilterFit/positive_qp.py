"""Convex least squares with nonnegative variables and linear equalities.

    minimize |A x - b|²  subject to  C x = d,  x >= 0

Infeasible-start primal-dual interior-point method (Mehrotra predictor-
corrector), NumPy only. Constraints can leave and re-enter the active set,
unlike greedy column removal.
"""
import numpy as np


def solve(a, b, c, d, max_iterations=120, tolerance=1e-9):
    """Returns (x, diagnostics). x is None when no solution was found."""
    a = np.asarray(a, float)
    b = np.asarray(b, float)
    c = np.asarray(c, float)
    d = np.asarray(d, float)
    n = a.shape[1]

    # Normalize the equalities and keep an independent subset (via SVD).
    scales = np.maximum(np.linalg.norm(c, axis=1), 1e-30)
    cn = c/scales[:, None]
    dn = d/scales
    u, s, vt = np.linalg.svd(cn, full_matrices=False)
    rank = int(np.sum(s > max(float(s[0]) if len(s) else 0, 1.)*1e-11))
    ci = vt[:rank]
    di = (u[:, :rank].T@dn)/s[:rank]
    if np.max(abs(cn@ci.T@di-dn), initial=0) > 1e-8:
        return None, dict(reason='inconsistent equalities')

    h = a.T@a
    g = a.T@b
    scale = max(float(np.max(np.diag(h))), 1e-20)
    h /= scale
    g /= scale
    x = np.full(n, max(float(np.linalg.norm(di))/np.sqrt(n), .01))
    z = np.ones(n)
    y = np.zeros(rank)

    def step(v, dv):
        keep = dv < 0
        return min(1., float(np.min(-v[keep]/dv[keep]))) if np.any(keep) else 1.

    for iteration in range(max_iterations):
        rd = h@x-g+ci.T@y-z
        rp = ci@x-di
        mu = float(x@z/n)
        error = max(float(np.max(abs(rd), initial=0)), float(np.max(abs(rp), initial=0)), mu)
        if error < tolerance:
            if np.max(abs(c@x-d), initial=0) < 1e-7:
                return x, dict(reason='converged', iterations=iteration, error=error, complementarity=mu)
        matrix = np.block([[h+np.diag(z/x), ci.T], [ci, np.zeros((rank, rank))]])

        def direction(rc):
            rhs = np.r_[-rd-rc/x, -rp]
            try:
                delta = np.linalg.solve(matrix, rhs)
            except np.linalg.LinAlgError:
                delta = np.linalg.lstsq(matrix, rhs, rcond=1e-13)[0]
            dx = delta[:n]
            dy = delta[n:]
            dz = (-rc-z*dx)/x
            return dx, dy, dz

        # Predictor (affine) step, then the centered corrector.
        dx, dy, dz = direction(x*z)
        ap = step(x, dx)
        ad = step(z, dz)
        affine = float((x+ap*dx)@(z+ad*dz)/n)
        sigma = min(1., max(0., affine/max(mu, 1e-30))**3)
        dx, dy, dz = direction(x*z+dx*dz-sigma*mu)
        ap = .995*step(x, dx)
        ad = .995*step(z, dz)
        x += ap*dx
        y += ad*dy
        z += ad*dz
        if not np.isfinite(x).all() or min(float(x.min()), float(z.min())) <= 0:
            return None, dict(reason='numerical failure', iterations=iteration)
        if np.linalg.norm(y) > 1e14 or np.linalg.norm(z) > 1e14:
            return None, dict(reason='infeasible or ill-conditioned', iterations=iteration)
    return None, dict(reason='iteration limit', error=error, primal_error=float(np.max(abs(rp), initial=0)))
