"""Checks and the results table."""

import csv
import os

PASS, FAIL, XFAIL, XPASS, SKIP, ERROR, INFO = "PASS", "FAIL", "XFAIL", "XPASS", "SKIP", "ERROR", "INFO"


class Check(object):
    """One comparison of a measured value against an independently derived expectation.

    `known` names a discrepancy that is reported in docs/verification.md: a check carrying it is expected to fail
    (XFAIL) until the engine or model is fixed, and shows XPASS once it no longer fails - the cue to drop the
    marker. `info` marks a measurement reported for the record, with no pass/fail."""

    def __init__(self, case, name, measured, expected, tolerance, ok, unit="", note="", known=None, info=False):
        self.case = case
        self.name = name
        self.measured = measured
        self.expected = expected
        self.tolerance = tolerance
        self.unit = unit
        self.note = note
        self.known = known
        if info:
            self.status = INFO
        elif ok is None:
            self.status = ERROR
        elif known:
            self.status = XPASS if ok else XFAIL
        else:
            self.status = PASS if ok else FAIL

    @staticmethod
    def skipped(case, name, why):
        c = Check(case, name, None, None, "", True, note=why)
        c.status = SKIP
        return c

    @staticmethod
    def error(case, name, why):
        c = Check(case, name, None, None, "", None, note=why)
        return c


def fmt(v):
    if v is None:
        return "-"
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, float):
        if v == 0:
            return "0"
        a = abs(v)
        if a >= 1000:
            return "%.0f" % v
        if a >= 100:
            return "%.1f" % v
        if a >= 1:
            return "%.3f" % v
        return "%.4g" % v
    return str(v)


def within(measured, expected, rel=None, abs_=None):
    """|measured - expected| within rel * |expected| or abs_, whichever is larger."""
    if measured is None or expected is None:
        return None
    allowed = 0.0
    if rel is not None:
        allowed = max(allowed, rel * abs(expected))
    if abs_ is not None:
        allowed = max(allowed, abs_)
    return abs(measured - expected) <= allowed


def table(checks):
    rows = [("Case", "Check", "Measured", "Expected", "Tolerance", "Result")]
    for c in checks:
        unit = (" " + c.unit) if c.unit and c.measured is not None else ""
        result = c.status + (" (%s)" % c.known if c.known and c.status in (XFAIL, XPASS) else "")
        rows.append((c.case, c.name, fmt(c.measured) + unit, fmt(c.expected) + (unit if c.expected is not None else ""),
                     c.tolerance, result))
    widths = [max(len(r[i]) for r in rows) for i in range(len(rows[0]))]
    widths[1] = min(widths[1], 66)
    lines = []
    for i, r in enumerate(rows):
        cells = []
        for w, v in zip(widths, r):
            v = v if len(v) <= w else v[:w - 1] + "~"
            cells.append(v.ljust(w))
        lines.append("  ".join(cells).rstrip())
        if i == 0:
            lines.append("  ".join("-" * w for w in widths))
    return "\n".join(lines)


def notes(checks):
    out = []
    for c in checks:
        if c.status in (FAIL, ERROR, XPASS, SKIP, INFO) or (c.status == XFAIL and c.note):
            out.append("[%s %s] %s: %s" % (c.case, c.status, c.name, c.note or ""))
    return "\n".join(out)


def write_files(folder, checks):
    with open(os.path.join(folder, "results.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["case", "check", "measured", "expected", "unit", "tolerance", "status", "known", "note"])
        for c in checks:
            w.writerow([c.case, c.name, c.measured, c.expected, c.unit, c.tolerance, c.status, c.known or "", c.note])
    with open(os.path.join(folder, "results.txt"), "w") as f:
        f.write(table(checks) + "\n\n" + notes(checks) + "\n")
