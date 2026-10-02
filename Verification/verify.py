#!/usr/bin/env python3
"""WUInity/PREACT basic verification cases: synthetic inputs, expected values derived independently of the code
under test, every case run head-less through the product, and a PASS/FAIL table of measured against expected.

Run it through ../verify.sh or ../verify.ps1, which find the tools; or directly:

    python3 Verification/verify.py [--case 1,2,3,4] [--work DIR] [--preact PREACT.dll] [--cli PREACTcli.dll]
                                   [--elmfire EXE] [--gdal BIN] [--sumo-home DIR] [--strict] [--generate-only]

It builds nothing: PREACT and PREACTcli come from build.sh / build.ps1 (PREACT/*/bin/Release/net8.0). Standard
library only, Python 3.8 or newer. Exit code 0 when every check passed or failed as a known discrepancy (XFAIL),
1 when any check failed, errored or (with --strict) failed as a known discrepancy, 2 when a tool is missing.
See docs/verification.md.
"""

import argparse
import datetime
import importlib.util
import os
import sys
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from vlib import report, tools  # noqa: E402
from vlib.context import Context  # noqa: E402

CASES = [
    ("1", "case1_flat_wind", "flat ground, uniform fuel, constant wind: ROS, L/B, ellipse; k-PERIL boundary"),
    ("2", "case2_slope", "slope only, no wind: upslope ROS ratio; slope + upslope wind through k-PERIL"),
    ("3", "case3_evacuation", "evacuation without fire: arrival times; household reaction to a synthetic front"),
    ("4", "case4_campaign", "a tiny trigger campaign: convergence and reproducible per-realization seeds"),
]


def load_case(folder):
    path = os.path.join(HERE, "cases", folder, "case.py")
    spec = importlib.util.spec_from_file_location("case_" + folder, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--case", default="1,2,3,4", help="comma-separated case numbers (default: all)")
    p.add_argument("--work", help="folder for the generated inputs and every run (default: Verification/_runs/<time>)")
    p.add_argument("--preact", help="PREACT.dll (or the PREACT executable beside it)")
    p.add_argument("--cli", help="PREACTcli.dll")
    p.add_argument("--elmfire", help="the ELMFIRE executable")
    p.add_argument("--gdal", help="GDAL's bin folder (gdal_translate)")
    p.add_argument("--sumo-home", help="SUMO's folder (default: $SUMO_HOME)")
    p.add_argument("--strict", action="store_true", help="count known discrepancies (XFAIL) as failures")
    p.add_argument("--list", action="store_true", help="list the cases and exit")
    p.add_argument("--generate-only", action="store_true",
                   help="write every selected case's inputs into --work and stop (to look at them, e.g. in QGIS)")
    return p.parse_args()


def main():
    a = parse()
    if a.list:
        for n, folder, what in CASES:
            print("%s  %-18s %s" % (n, folder, what))
        return 0
    root = tools.repo_root()
    try:
        preact = a.preact or tools.find_build_output(root, "PREACTexecute", "PREACT.dll")
        if preact and not preact.lower().endswith(".dll"):
            preact = os.path.splitext(preact)[0] + ".dll"
        cli = a.cli or tools.find_build_output(root, "PREACTcli", "PREACTcli.dll")
        if not preact or not os.path.isfile(preact):
            raise tools.ToolError("PREACT.dll was not found: run build.sh / build.ps1 first, or pass --preact")
        if not cli or not os.path.isfile(cli):
            raise tools.ToolError("PREACTcli.dll was not found: run build.sh / build.ps1 first, or pass --cli")
        elmfire = tools.find_elmfire(root, a.elmfire)
        gdal_bin = tools.find_gdal_bin(a.gdal)
        sumo_home, netconvert = tools.find_sumo_home(a.sumo_home)
    except tools.ToolError as e:
        print("verify: " + str(e), file=sys.stderr)
        return 2

    work = os.path.abspath(a.work or os.path.join(HERE, "_runs", datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))
    if os.path.exists(work) and os.listdir(work):
        print("verify: %s is not empty; pass an empty or new --work folder" % work, file=sys.stderr)
        return 2
    os.makedirs(work, exist_ok=True)
    if " " in work:
        print("verify: warning: %s has a space in it; ELMFIRE hands paths to GDAL unquoted and the campaign (case 4) "
              "refuses such paths - pass --work with a path without spaces" % work, file=sys.stderr)
    env = dict(os.environ)
    env["SUMO_HOME"] = sumo_home
    note = ""
    if not tools.WINDOWS and os.path.normcase(os.path.abspath(preact)).startswith(os.path.normcase(root) + os.sep):
        note = ("on Linux the committed SUMO C# bindings do not match a locally built libsumocs; build a scratch PREACT "
                "with Verification/tools/linux-sumo-glue.sh and pass it with --preact")
    ctx = Context(root, work, os.path.abspath(preact), os.path.abspath(cli), elmfire, gdal_bin, sumo_home,
                  netconvert, env, note)

    print("WUInity/PREACT verification -> %s" % work)
    print("  PREACT    %s" % ctx.preact_dll)
    print("  PREACTcli %s" % ctx.cli_dll)
    print("  ELMFIRE   %s" % elmfire)
    print("  GDAL      %s" % gdal_bin)
    print("  SUMO      %s" % sumo_home)
    print("  fuel table %s" % ctx.fuel_table)
    if note:
        print("  NOTE: PREACT is the repository's own build; " + note)
    print("")

    wanted = [c.strip() for c in a.case.split(",") if c.strip()]
    checks = []
    for number, folder, what in CASES:
        if number not in wanted:
            continue
        case_dir = os.path.join(work, folder)
        os.makedirs(case_dir)
        start = time.time()
        print("case %s (%s)..." % (number, what), flush=True)
        try:
            module = load_case(folder)
            module.generate(ctx, case_dir)
            if a.generate_only:
                print("  inputs written to " + case_dir)
                continue
            got = module.run(ctx, case_dir)
        except Exception as e:  # a broken case is a result, not the end of the run
            got = [report.Check.error(number, "case %s" % folder, "%s: %s (see %s)" % (type(e).__name__, e,
                                                                                     os.path.join(case_dir, "error.txt")))]
            with open(os.path.join(case_dir, "error.txt"), "w") as f:
                traceback.print_exc(file=f)
        checks += got
        bad = sum(1 for c in got if c.status in (report.FAIL, report.ERROR))
        print("  %d checks, %d failed, %.0f s" % (len(got), bad, time.time() - start), flush=True)

    if a.generate_only:
        return 0
    print("")
    print(report.table(checks))
    extra = report.notes(checks)
    if extra:
        print("")
        print(extra)
    report.write_files(work, checks)
    failed = [c for c in checks if c.status in (report.FAIL, report.ERROR) or (a.strict and c.status == report.XFAIL)]
    counts = {}
    for c in checks:
        counts[c.status] = counts.get(c.status, 0) + 1
    print("")
    print("%s - %s. Results in %s" % ("FAILED" if failed else "OK", ", ".join("%d %s" % (v, k) for k, v in
                                                                              sorted(counts.items())),
                                     os.path.join(work, "results.txt")))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
