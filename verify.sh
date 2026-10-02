#!/usr/bin/env bash
# Runs the basic verification cases (Verification/) head-less and prints a PASS/FAIL table of measured against
# independently expected values. Builds nothing: run ./build.sh first. See docs/verification.md.
#
#   ./verify.sh [--case 1,2,3,4] [--work DIR] [--preact PREACT.dll] [--cli PREACTcli.dll]
#               [--elmfire EXE] [--gdal BIN] [--sumo-home DIR] [--strict] [--list]
#
# Needs: Python 3.8+ (standard library only), the .NET 8 runtime, ELMFIRE (default: the submodule's
# build/linux/bin/elmfire, or $ELMFIRE_EXE), GDAL's command-line tools on PATH (or --gdal), and SUMO ($SUMO_HOME,
# for netconvert and libsumocs). On Linux PREACT also needs GDAL 3.10's libgdal.so.36 on LD_LIBRARY_PATH, and a
# PREACT whose SUMO C# bindings match the local libsumocs: the committed bindings are the Windows SUMO's, so build
# a scratch copy with Verification/tools/linux-sumo-glue.sh and pass it with --preact.
#
# Exit code: 0 all checks passed (known discrepancies, XFAIL, allowed), 1 a check failed, 2 a tool is missing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PY=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 &&
        "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' 2>/dev/null; then
        PY="$candidate"
        break
    fi
done
[[ -n "$PY" ]] || { echo "verify.sh: Python 3.8 or newer was not found (python3 or python on PATH)." >&2; exit 2; }

exec "$PY" "$ROOT/Verification/verify.py" "$@"
