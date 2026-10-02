"""What every case needs: the tools, the run folder, and the few product invocations they share."""

import os
import re

from . import rasters, tools


class Context(object):
    def __init__(self, root, work, preact_dll, cli_dll, elmfire, gdal_bin, sumo_home, netconvert, env,
                 preact_note=""):
        self.root = root
        self.work = work
        self.preact_dll = preact_dll
        self.cli_dll = cli_dll
        self.elmfire = elmfire
        self.gdal_bin = gdal_bin
        self.sumo_home = sumo_home
        self.netconvert = netconvert
        self.env = env
        self.preact_note = preact_note
        self.runner = tools.Runner(env)
        self.dotnet = tools.dotnet()
        self.fuel_table = self._fuel_table()

    def _fuel_table(self):
        # ELMFIRE's own table, next to the executable as in the repository (build/<os>/bin/elmfire -> build/source)
        # or beside a distributed elmfire.exe; the expectation is computed from the same row ELMFIRE reads.
        here = os.path.dirname(self.elmfire)
        for p in (os.path.join(here, "..", "..", "source", "fuel_models.csv"), os.path.join(here, "fuel_models.csv")):
            if os.path.isfile(p):
                return os.path.normpath(p)
        raise tools.ToolError("ELMFIRE's fuel_models.csv was not found beside " + self.elmfire)

    def preact_exe(self):
        """The PREACT executable a campaign starts (PREACT.exe on Windows, the apphost on Linux)."""
        base = os.path.splitext(self.preact_dll)[0]
        return base + (".exe" if tools.WINDOWS else "")

    def cli(self, args, cwd, log, timeout=3600, stdin_text=None):
        return self.runner.run([self.dotnet, self.cli_dll] + args, cwd, log, timeout, stdin_text)

    def preact(self, wui, cwd, log, timeout=3600):
        return self.runner.run([self.dotnet, self.preact_dll, wui], cwd, log, timeout)

    def build_case(self, folder, wui, extra, log):
        args = ["build-case", "--wui", wui, "--gdal", self.gdal_bin, "--windninja",
                os.path.join(folder, "no-windninja-on-purpose")] + extra
        return self.cli(args, folder, log, timeout=1200)

    def read(self, path, scratch=None, band=1):
        return rasters.read_any(path, self.gdal_bin, scratch or os.path.join(os.path.dirname(path), "_asc"), band)


def grep_float(text, pattern):
    m = re.search(pattern, text)
    return float(m.group(1)) if m else None


def elmfire_outputs(case_dir):
    out = os.path.join(case_dir, "outputs")
    return {stem: rasters.find_output(out, stem) for stem in ("time_of_arrival", "vs", "spread_dir", "mfws", "flin")}


def elmfire_acres(case_dir):
    """The fire area ELMFIRE reported for its last run, from fire_size_stats.csv."""
    path = os.path.join(case_dir, "outputs", "fire_size_stats.csv")
    if not os.path.isfile(path):
        return None
    with open(path) as f:
        rows = [r.split(",") for r in f.read().strip().splitlines()]
    if len(rows) < 2:
        return None
    header = [h.strip() for h in rows[0]]
    try:
        return float(rows[-1][header.index("Total fire area (ac)")])
    except (ValueError, IndexError):
        return None
