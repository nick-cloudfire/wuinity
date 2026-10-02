"""Measurements on fire and boundary rasters: spread rates along an axis, the burned region's shape at a time, its
mirror symmetry, and a comparison against an ideal Huygens ellipse."""

import math


def axis_cells(grid, start_col, start_row, d_col, d_row, count):
    """[(distance m, col, row)] stepping from a cell along a grid axis."""
    out = []
    for k in range(count + 1):
        c, r = start_col + k * d_col, start_row + k * d_row
        if not (0 <= c < grid.ncols and 0 <= r < grid.nrows):
            break
        out.append((k * grid.cell, c, r))
    return out


def mean_along(grid, cells):
    vals = [grid.get(c, r) for _, c, r in cells]
    vals = [v for v in vals if v is not None and v >= 0]
    return sum(vals) / len(vals) if vals else None


def mean_direction(grid, cells):
    """Circular mean of compass directions (degrees, in [0, 360)) along a line of cells."""
    sx = sy = 0.0
    n = 0
    for _, c, r in cells:
        v = grid.get(c, r)
        if v is None or v < 0:
            continue
        sx += math.sin(math.radians(v))
        sy += math.cos(math.radians(v))
        n += 1
    if n == 0:
        return None
    return (math.degrees(math.atan2(sx, sy)) + 360.0) % 360.0


def toa_slope(toa, cells):
    """Least-squares spread rate (m/min) of the arrival times along a line of cells: distance over time."""
    pts = [(d, toa.get(c, r)) for d, c, r in cells]
    pts = [(d, t) for d, t in pts if t is not None and t >= 0]
    if len(pts) < 3:
        return None
    n = float(len(pts))
    mt = sum(t for _, t in pts) / n
    md = sum(d for d, _ in pts) / n
    stt = sum((t - mt) ** 2 for _, t in pts)
    sdt = sum((t - mt) * (d - md) for d, t in pts)
    return sdt / stt * 60.0 if stt > 0 else None


def burned_at(toa, t):
    """The set of (col, row) cells with an arrival time at or before t."""
    return set((c, r) for c, r, v in toa.cells() if v is not None and 0 <= v <= t)


def extent_along(cells_set, start_col, start_row, d_col, d_row, max_steps=100000):
    """How many consecutive cells from (start + 1 step) along the direction are in the set."""
    n = 0
    c, r = start_col + d_col, start_row + d_row
    while (c, r) in cells_set and n < max_steps:
        n += 1
        c += d_col
        r += d_row
    return n


def mirror_jaccard(cells_set, axis_row=None, axis_col=None):
    """Jaccard index of a set of cells and its mirror image about a row (north-south mirror) or a column."""
    if not cells_set:
        return None
    if axis_row is not None:
        mirrored = set((c, 2 * axis_row - r) for c, r in cells_set)
    else:
        mirrored = set((2 * axis_col - c, r) for c, r in cells_set)
    return len(cells_set & mirrored) / float(len(cells_set | mirrored))


def jaccard(a, b):
    if not a and not b:
        return 1.0
    return len(a & b) / float(len(a | b))


def ellipse_cells(grid, ign_col, ign_row, heading_deg, head, back, lb):
    """The cells whose centres lie inside a Huygens fire ellipse with its ignition at the rear focus: `head` and
    `back` the distances from the ignition to the head and the back (m), `lb` its length-to-breadth ratio,
    `heading_deg` the compass direction of the head."""
    b = 0.5 * (head + back)      # semi-major
    a = b / lb                   # semi-minor
    centre_offset = 0.5 * (head - back)
    h = math.radians(heading_deg)
    ux, uy = math.sin(h), math.cos(h)          # along the head direction
    vx, vy = math.cos(h), -math.sin(h)         # across it
    ix, iy = grid.centre(ign_col, ign_row)
    cx, cy = ix + centre_offset * ux, iy + centre_offset * uy
    reach = int(b / grid.cell) + 3
    inside = set()
    for r in range(max(0, ign_row - reach - int(centre_offset / grid.cell)),
                   min(grid.nrows, ign_row + reach + int(centre_offset / grid.cell) + 1)):
        for c in range(max(0, ign_col - reach - int(centre_offset / grid.cell)),
                       min(grid.ncols, ign_col + reach + int(centre_offset / grid.cell) + 1)):
            x, y = grid.centre(c, r)
            p = (x - cx) * ux + (y - cy) * uy
            q = (x - cx) * vx + (y - cy) * vy
            if (p / b) ** 2 + (q / a) ** 2 <= 1.0:
                inside.add((c, r))
    return inside


def centroid(grid, cells_set):
    if not cells_set:
        return None
    xs = ys = 0.0
    for c, r in cells_set:
        x, y = grid.centre(c, r)
        xs += x
        ys += y
    n = float(len(cells_set))
    return xs / n, ys / n


def bearing(from_xy, to_xy):
    dx, dy = to_xy[0] - from_xy[0], to_xy[1] - from_xy[1]
    return (math.degrees(math.atan2(dx, dy)) + 360.0) % 360.0
