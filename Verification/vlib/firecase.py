"""Synthetic ELMFIRE cases: a DEM and a fuel raster on a UTM lattice, a scenario that names them, and build-case."""

import math
import os

from . import geo, rasters, scenario, tiff

ZONE = 10                      # WGS 84 / UTM zone 10N (EPSG:32610), the Auburn area of California
EPSG = 32600 + ZONE
ORIGIN = (670000.0, 4316000.0)  # south-west corner of every case's evacuation domain, UTM metres


class FireCase(object):
    """The geometry of one synthetic case. Positions are UTM metres; the case grid's cell edges fall on multiples
    of `cell` (the builder snaps the grid outward to the cell lattice), so a point at (k + 0.5) * cell is a cell
    centre."""

    def __init__(self, name, width, height, cell=10.0, padding=200.0, margin=600.0):
        self.name = name
        self.width, self.height = width, height
        self.cell = cell
        self.padding = padding
        self.x0, self.y0 = ORIGIN
        # The source rasters reach `margin` beyond the domain on every side, which covers the padded domain.
        self.src_x0 = self.x0 - margin
        self.src_y0 = self.y0 - margin
        self.src_cols = int(round((width + 2 * margin) / cell))
        self.src_rows = int(round((height + 2 * margin) / cell))

    def centre_of(self, x, y):
        """The centre of the lattice cell holding (x, y)."""
        return ((math.floor(x / self.cell) + 0.5) * self.cell, (math.floor(y / self.cell) + 0.5) * self.cell)

    def latlon(self, x, y):
        return geo.to_latlon(x, y, ZONE)

    def lower_left(self):
        return self.latlon(self.x0, self.y0)

    def write_rasters(self, folder, elevation, fuel_model, fuel_dtype="int16"):
        """dem.tif from elevation(x, y) (Float32) and fuel.tif with `fuel_model` everywhere, typed `fuel_dtype`."""
        dem_rows = []
        for r in range(self.src_rows):
            y = self.src_y0 + (self.src_rows - r - 0.5) * self.cell
            dem_rows.append([elevation(self.src_x0 + (c + 0.5) * self.cell, y) for c in range(self.src_cols)])
        north = self.src_y0 + self.src_rows * self.cell
        dem = os.path.join(folder, "dem.tif")
        fuel = os.path.join(folder, "fuel_%s.tif" % fuel_dtype)
        tiff.write_geotiff(dem, dem_rows, self.src_x0, north, self.cell, EPSG, "float32", -9999)
        fuel_rows = [[fuel_model] * self.src_cols for _ in range(self.src_rows)]
        tiff.write_geotiff(fuel, fuel_rows, self.src_x0, north, self.cell, EPSG, fuel_dtype, -9999)
        return dem, fuel

    def sections(self, hours, fuel_file, ignition_xy, case_dir="case", namelist=None, start="2026-07-15T12:00:00",
                 end="2026-07-15T14:00:00", extra=None, elmfire_exe=None, standard="FBFM13"):
        """The scenario's sections for an ELMFIRE fire (more can be appended by the case)."""
        s = [
            ("Simulation", [("Name", self.name), ("LowerLeftLatLon", scenario.latlon(self.lower_left())),
                            ("DomainSize", "%g,%g" % (self.width, self.height)), ("DeltaTime", "1"),
                            ("StartDateTime", start), ("EndDateTime", end), ("StopWhenEvacuated", "true"),
                            ("RandomSeed", "1")]),
            ("WildfireModule", [("Enabled", "true"), ("Module", "ELMFIRE")]),
        ]
        if ignition_xy is not None:
            s.append(("IgnitionPoint", [("LatLon", scenario.latlon(self.latlon(*ignition_xy)))]))
        elm = [("CaseDirectory", case_dir), ("SimulationTstopHours", "%g" % hours), ("CellSizeMetres", "%g" % self.cell),
               ("PaddingMetres", "%g" % self.padding), ("FuelModelFile", fuel_file), ("FuelModelStandard", standard)]
        if elmfire_exe:
            elm.append(("ElmfireExe", elmfire_exe))
        s.append(("ELMFIRE", elm))
        nl = [("ENABLE_SPOTTING", "false")]
        if namelist:
            nl += namelist
        s.append(("ElmfireNamelist", nl))
        if extra:
            s += extra
        return s


def case_grid(ctx, case_dir):
    """The built case's grid of record, inputs/dem.tif."""
    return ctx.read(os.path.join(case_dir, "inputs", "dem.tif"), os.path.join(case_dir, "_asc"))


def box_cells(grid, x_min, x_max, y_min, y_max):
    """The cells whose centres lie in the box."""
    out = set()
    for r in range(grid.nrows):
        for c in range(grid.ncols):
            x, y = grid.centre(c, r)
            if x_min <= x <= x_max and y_min <= y <= y_max:
                out.add((c, r))
    return out


def uniform_weather_args(wind_ms, wind_from, m1, m10, m100):
    return ["--no-climatology", "--wind", "%g" % wind_ms, "--wind-dir", "%g" % wind_from,
            "--m1", "%g" % m1, "--m10", "%g" % m10, "--m100", "%g" % m100]


def read_boundary(path):
    """A k-PERIL boundary .asc: the set of cells with a value >= 1 (1 boundary, 2-3 the WUI area itself)."""
    g = rasters.read_asc(path)
    return g, set((c, r) for c, r, v in g.cells() if v is not None and v >= 1)
