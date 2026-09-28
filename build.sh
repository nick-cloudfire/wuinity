#!/usr/bin/env bash
# Builds everything the Unity project and the command line need from PREACT, in Release:
#
#   PREACTcore  -> WUInity/Assets/PREACT/Release/netstandard2.1/   engine DLLs and native wrappers that the
#                                                                  Unity project uses
#   PREACT      -> PREACT/PREACTexecute/bin/Release/net8.0/         head-less scenario runner
#   PREACTcli   -> PREACT/PREACTcli/bin/Release/net8.0/             build-case, converge-trigger, ...
#
# The engine DLLs are not committed; run this once after cloning and again after pulling engine
# changes, before opening the Unity project. Needs the .NET 8 SDK (dotnet) on PATH.
# This is the Linux counterpart of build.ps1. Usage: ./build.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNITY_OUT="$ROOT/WUInity/Assets/PREACT/Release/netstandard2.1"
PREACT_EXE="$ROOT/PREACT/PREACTexecute/bin/Release/net8.0/PREACT"
CLI_EXE="$ROOT/PREACT/PREACTcli/bin/Release/net8.0/PREACTcli"

fail() {
    printf '\nBUILD FAILED: %s\n' "$1" >&2
    exit 1
}

command -v dotnet >/dev/null 2>&1 ||
    fail "dotnet was not found on PATH. Install the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)."

sdk_ok=0
while read -r version _; do
    [[ "${version%%.*}" =~ ^[0-9]+$ ]] && (( ${version%%.*} >= 8 )) && sdk_ok=1
done < <(dotnet --list-sdks)
(( sdk_ok )) || fail "no .NET SDK 8 or newer found ('dotnet --list-sdks'). Install the .NET 8 SDK."

build() {
    printf '\n==> dotnet build %s -c Release\n' "$1"
    dotnet build "$ROOT/$1" -c Release -nologo || fail "$1 did not build (see the errors above)."
}

# PREACTcore first: its Release output path is the Unity project (BaseOutputPath in PREACTcore.csproj).
# The other two reference it and only re-check it.
build PREACT/PREACTcore/PREACTcore.csproj
build PREACT/PREACTexecute/PREACTexecute.csproj
build PREACT/PREACTcli/PREACTcli.csproj

# Every file Unity uses from the engine has a committed .meta beside it, so the metas are the list of
# what the build must have produced.
missing=()
while IFS= read -r -d '' meta; do
    [[ "$meta" == *.pdb.meta ]] && continue
    asset="${meta%.meta}"
    [[ -e "$asset" ]] || missing+=("${asset#"$ROOT"/}")
done < <(find "$UNITY_OUT" -name '*.meta' -print0)
[[ -f "$UNITY_OUT/PREACTcore.dll" ]] || missing+=("${UNITY_OUT#"$ROOT"/}/PREACTcore.dll")
if (( ${#missing[@]} )); then
    printf '  missing: %s\n' "${missing[@]}" >&2
    fail "the Unity engine folder is incomplete: the files above have a .meta but were not built."
fi

# Files without a .meta are new build outputs; Unity writes their .meta on the next import, and that
# .meta should be committed so every checkout gets the same GUID.
while IFS= read -r -d '' f; do
    [[ -e "$f.meta" ]] || printf 'note: %s has no .meta yet; commit the one Unity creates.\n' "${f#"$ROOT"/}"
done < <(find "$UNITY_OUT" -type f ! -name '*.meta' ! -name '*.pdb' -print0)

[[ -f "$PREACT_EXE.dll" ]] || fail "PREACT was built but $PREACT_EXE.dll is not there."
[[ -f "$CLI_EXE.dll" ]] || fail "PREACTcli was built but $CLI_EXE.dll is not there."

cat <<EOF

Build succeeded.
  Unity engine DLLs : $UNITY_OUT
  PREACT            : $PREACT_EXE   (or: dotnet $PREACT_EXE.dll <scenario.wui>)
  PREACTcli         : $CLI_EXE   (or: dotnet $CLI_EXE.dll <command>)
EOF
