#!/usr/bin/env bash
# Linux test bench only - not part of the product, and never run on Windows.
#
# The committed SUMO C# bindings (PREACT/PREACTcore/Runtimes/Managed/Eclipse.Sumo.Libsumo/*.cs) are the files SWIG
# generated for the Windows SUMO 1.22 that WUInity ships against. A Linux libsumocs built from source exports other
# entry points, so with the committed bindings every car fails to enter SUMO ("Unable to find an entry point named
# '?' in shared library 'libsumocs'") and the run stops. This script makes a SCRATCH copy of PREACT outside the
# repository, swaps in the bindings SWIG generated for the local SUMO build, and builds PREACT there. The repository
# is not touched. Pass the printed path to verify.sh with --preact.
#
#   Verification/tools/linux-sumo-glue.sh <scratch dir> [<SWIG cs dir>]
#
# <SWIG cs dir> defaults to $SUMO_HOME/build/cmake-build/src/libsumo/cs (where a CMake build of SUMO puts them).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRATCH="${1:?usage: linux-sumo-glue.sh <scratch dir> [<SWIG cs dir>]}"
GLUE="${2:-${SUMO_HOME:-}/build/cmake-build/src/libsumo/cs}"

[[ -d "$GLUE" ]] && ls "$GLUE"/*.cs >/dev/null 2>&1 ||
    { echo "No SWIG-generated C# bindings in '$GLUE' (pass the folder as the second argument)." >&2; exit 1; }
case "$(cd "$(dirname "$SCRATCH")" && pwd)/$(basename "$SCRATCH")" in
    "$ROOT"|"$ROOT"/*) echo "The scratch copy must be outside the repository ($ROOT)." >&2; exit 1 ;;
esac

mkdir -p "$SCRATCH/WUInity/Assets/PREACT"
# The PREACT sources without build output. PREACTcore's Release output path is ../../WUInity/Assets/PREACT,
# hence the empty WUInity folder beside it.
tar -C "$ROOT" --exclude='bin' --exclude='obj' -cf - PREACT | tar -C "$SCRATCH" -xf -

BINDINGS="$SCRATCH/PREACT/PREACTcore/Runtimes/Managed/Eclipse.Sumo.Libsumo"
find "$BINDINGS" -maxdepth 1 -name '*.cs' -delete
cp "$GLUE"/*.cs "$BINDINGS/"
echo "Bindings: $(ls "$BINDINGS"/*.cs | wc -l) files from $GLUE" > "$SCRATCH/SUMO_GLUE.txt"
git -C "$ROOT" rev-parse HEAD >> "$SCRATCH/SUMO_GLUE.txt" 2>/dev/null || true

dotnet build "$SCRATCH/PREACT/PREACTexecute/PREACTexecute.csproj" -c Release -nologo -v quiet

PREACT="$SCRATCH/PREACT/PREACTexecute/bin/Release/net8.0/PREACT"
[[ -f "$PREACT.dll" ]] || { echo "The build produced no $PREACT.dll." >&2; exit 1; }
echo
echo "PREACT with this machine's SUMO bindings: $PREACT"
echo "Run the verification with: ./verify.sh --preact $PREACT"
