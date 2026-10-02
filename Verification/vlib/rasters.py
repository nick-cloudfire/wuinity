"""Reading rasters without GDAL's Python bindings: ESRI ASCII grids directly, anything else (ELMFIRE's DEFLATE
GeoTIFFs) through the gdal_translate command-line tool, which every ELMFIRE installation has anyway."""

import os
import subprocess


class Grid(object):
    """A single-band raster: `values[row][col]`, row 0 the northernmost, `None` for nodata."""

    def __init__(self, ncols, nrows, xll, yll, cell, values, nodata=None):
        self.ncols, self.nrows = ncols, nrows
        self.xll, self.yll, self.cell = xll, yll, cell
        self.values = values
        self.nodata = nodata

    # Cell (col, row) <-> map coordinates (cell centres).
    def centre(self, col, row):
        return (self.xll + (col + 0.5) * self.cell, self.yll + (self.nrows - row - 0.5) * self.cell)

    def cell_of(self, x, y):
        col = int((x - self.xll) // self.cell)
        row = self.nrows - 1 - int((y - self.yll) // self.cell)
        return col, row

    def get(self, col, row):
        if 0 <= col < self.ncols and 0 <= row < self.nrows:
            return self.values[row][col]
        return None

    def at(self, x, y):
        return self.get(*self.cell_of(x, y))

    def cells(self):
        for r in range(self.nrows):
            row = self.values[r]
            for c in range(self.ncols):
                yield c, r, row[c]

    def same_grid(self, other, tol=1e-3):
        return (self.ncols == other.ncols and self.nrows == other.nrows and abs(self.xll - other.xll) < tol
                and abs(self.yll - other.yll) < tol and abs(self.cell - other.cell) < tol)


def read_asc(path):
    header = {}
    values = []
    with open(path) as f:
        while True:
            pos = f.tell()
            line = f.readline()
            if not line:
                break
            parts = line.split()
            if not parts:
                continue
            key = parts[0].lower()
            if key in ("ncols", "nrows", "xllcorner", "yllcorner", "xllcenter", "yllcenter", "cellsize",
                       "nodata_value", "dx", "dy"):
                header[key] = float(parts[1])
            else:
                f.seek(pos)
                break
        nodata = header.get("nodata_value")
        ncols, nrows = int(header["ncols"]), int(header["nrows"])
        cell = header.get("cellsize", header.get("dx"))
        xll = header.get("xllcorner", header.get("xllcenter", 0.0) - 0.5 * cell)
        yll = header.get("yllcorner", header.get("yllcenter", 0.0) - 0.5 * cell)
        flat = f.read().split()
    if len(flat) < ncols * nrows:
        raise ValueError("%s: %d values for %dx%d cells" % (path, len(flat), ncols, nrows))
    for r in range(nrows):
        row = []
        for v in flat[r * ncols:(r + 1) * ncols]:
            x = float(v)
            row.append(None if nodata is not None and x == nodata else x)
        values.append(row)
    return Grid(ncols, nrows, xll, yll, cell, values, nodata)


def read_any(path, gdal_bin, scratch_dir, band=1):
    """Any raster GDAL reads, as a Grid (via gdal_translate -of AAIGrid into scratch_dir)."""
    if path.lower().endswith(".asc") and band == 1:
        return read_asc(path)
    if not os.path.isdir(scratch_dir):
        os.makedirs(scratch_dir)
    out = os.path.join(scratch_dir, os.path.splitext(os.path.basename(path))[0] + "_b%d.asc" % band)
    exe = os.path.join(gdal_bin, "gdal_translate.exe" if os.name == "nt" else "gdal_translate")
    subprocess.run([exe, "-q", "-of", "AAIGrid", "-b", str(band), path, out], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    return read_asc(out)


def write_asc(path, grid_like, values, nodata=-9999, fmt="%g"):
    with open(path, "w") as f:
        f.write("ncols %d\nnrows %d\nxllcorner %.6f\nyllcorner %.6f\ncellsize %.6f\nNODATA_value %g\n"
                % (grid_like.ncols, grid_like.nrows, grid_like.xll, grid_like.yll, grid_like.cell, nodata))
        for row in values:
            f.write(" ".join(fmt % (nodata if v is None else v) for v in row) + "\n")


def find_output(folder, stem):
    """ELMFIRE's `<stem>_<7-digit case>_<7-digit seconds>.tif` with the latest time."""
    best = None
    for name in os.listdir(folder):
        if name.startswith(stem + "_") and name.endswith(".tif"):
            parts = name[:-4].split("_")
            try:
                t = int(parts[-1])
            except ValueError:
                continue
            if best is None or t > best[0]:
                best = (t, os.path.join(folder, name))
    return best[1] if best else None
