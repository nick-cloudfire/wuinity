"""A reference re-computation of k-PERIL's trigger boundary, written from the algorithm as PREACT/kPERILcore
documents and implements it (kperil.cs, perilData.cs), in plain Python.

It is not an independent physical expectation - it reproduces k-PERIL's own modelling choices (its 8-neighbour
travel-time graph, its parametric-angle ellipse breakdown, Anderson's L/B applied to mi/h, its 0.06 x slope
effective wind) - but it is independent of the engine's wiring: which rasters k-PERIL is handed, in which layout and
units. A boundary that differs from this one means the engine fed k-PERIL something other than the fire it ran.
"""

import heapq
import math

# Bearing 45 * d: (d_col, d_row) on a grid whose row 0 is the northernmost.
NEIGHBOURS = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)]


def anderson_lb(u):
    return 0.936 * math.exp(0.2566 * u) + 0.461 * math.exp(-0.1548 * u) - 0.397


def directional_rates(ros, direction_deg, u_eff):
    """k-PERIL's rate towards each of the 8 neighbour bearings for one cell (kperil.cs breakdownRateOfSpread)."""
    lb = anderson_lb(u_eff)
    s = math.sqrt(max(lb * lb - 1.0, 0.0))
    hb = (lb + s) / (lb - s)
    a = ros / (2.0 * lb) * (1.0 + 1.0 / hb)
    b = ros * 0.5 * (1.0 + 1.0 / hb)
    c = ros * 0.5 * (1.0 - 1.0 / hb)
    theta = math.radians(direction_deg)
    out = []
    for d in range(8):
        psi = math.pi * d / 4.0 - theta
        x = a * math.sin(psi)
        y = c + b * math.cos(psi)
        out.append(math.hypot(x, y))
    return out


def effective_wind(wind_mph, wind_from_deg, slope_deg, aspect_deg):
    """perilData.GetEffectiveWindWithSlope, as written (both vectors through cos for x and sin for y)."""
    s = 0.06 * slope_deg
    x2 = wind_mph * math.cos(math.radians(wind_from_deg))
    y2 = wind_mph * math.sin(math.radians(wind_from_deg))
    up = math.radians(aspect_deg + 180.0)
    x1 = s * math.cos(up)
    y1 = s * math.sin(up)
    return math.hypot(x1 + x2, y1 + y2)


def effective_wind_aligned(wind_mph, wind_from_deg, slope_deg, aspect_deg):
    """The effective wind with both vectors pointing the way they push the fire: the wind towards wind_from + 180,
    the slope term upslope (aspect + 180, aspect being the downhill bearing PREACT hands k-PERIL)."""
    s = 0.06 * slope_deg
    w = math.radians(wind_from_deg + 180.0)
    up = math.radians(aspect_deg + 180.0)
    return math.hypot(wind_mph * math.sin(w) + s * math.sin(up), wind_mph * math.cos(w) + s * math.cos(up))


def boundary(ros, direction, wind_mph, wind_from, slope, aspect, wui, rset_min, cell, effective=None):
    """All rasters are Grids on the fire grid (ros in m/min, nodata None); `wui` is a set of (col, row). Returns the
    set of cells (outside the WUI area) from which the fire reaches a WUI perimeter cell within rset_min.
    `effective` replaces k-PERIL's effective-wind rule (default: as perilData implements it)."""
    effective = effective or effective_wind
    ncols, nrows = ros.ncols, ros.nrows
    rates = {}
    for r in range(nrows):
        for c in range(ncols):
            v = ros.values[r][c]
            if v is None or v < 0:
                continue
            u = effective(wind_mph.values[r][c] or 0.0, wind_from.values[r][c] or 0.0,
                               (slope.values[r][c] or 0.0) if slope else 0.0,
                               (aspect.values[r][c] or 0.0) if aspect else 0.0)
            rates[(c, r)] = directional_rates(v, direction.values[r][c] or 0.0, u)

    def travel(c, r, d):
        """Time for the fire to go from (c, r) to its neighbour in direction d (kperil.cs getTravelTime...), with
        kPERILcore's edge rule: the outermost ring copies its inner neighbour's times."""
        cc = min(max(c, 1), ncols - 2)
        rr = min(max(r, 1), nrows - 2)
        here = rates.get((cc, rr))
        if here is None:
            return None
        dc, dr = NEIGHBOURS[d]
        there = rates.get((cc + dc, rr + dr))
        if there is None:
            # kPERILcore reads the neighbour's breakdown, which is 0 for a cell without spread: 2 L / (r + 0).
            there_rate = 0.0
        else:
            there_rate = there[d]
        total = here[d] + there_rate
        if total <= 0:
            return None
        return 2.0 * cell * (1.0 if d % 2 == 0 else 1.4142) / total

    def interior(c, r):
        if not (1 <= c < ncols - 1 and 1 <= r < nrows - 1):
            return False
        return all((c + dc, r + dr) in wui for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)))

    perimeter = [cell_ for cell_ in wui if not interior(*cell_)]
    cost = {}
    heap = []
    for p in perimeter:
        cost[p] = 0.0
        heapq.heappush(heap, (0.0, p))
    while heap:
        t, (c, r) = heapq.heappop(heap)
        if t > cost.get((c, r), float("inf")):
            continue
        for d, (dc, dr) in enumerate(NEIGHBOURS):
            n = (c + dc, r + dr)
            if not (0 <= n[0] < ncols and 0 <= n[1] < nrows) or n in wui:
                continue
            step = travel(n[0], n[1], (d + 4) % 8)
            if step is None or step <= 0 or math.isinf(step) or math.isnan(step):
                continue
            nt = t + step
            if nt < cost.get(n, float("inf")):
                cost[n] = nt
                if nt <= rset_min:
                    heapq.heappush(heap, (nt, n))
    return set(k for k, v in cost.items() if v <= rset_min and k not in wui)
