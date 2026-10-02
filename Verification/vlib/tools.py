"""Finding the programs a verification run drives, and running them with their output logged."""

import os
import shutil
import subprocess
import sys
import time

WINDOWS = os.name == "nt"


class ToolError(Exception):
    pass


def exe_name(name):
    return name + ".exe" if WINDOWS else name


def repo_root():
    return os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))


def first_existing(paths):
    for p in paths:
        if p and os.path.isfile(p):
            return os.path.abspath(p)
    return None


def find_on_path(name):
    return shutil.which(exe_name(name)) or shutil.which(name)


def find_gdal_bin(explicit=None):
    """The folder holding gdal_translate: the one given, then PATH, then the places the engine looks on Windows
    (QGIS, OSGeo4W)."""
    if explicit:
        if os.path.isfile(os.path.join(explicit, exe_name("gdal_translate"))):
            return os.path.abspath(explicit)
        raise ToolError("no %s in --gdal %s" % (exe_name("gdal_translate"), explicit))
    found = find_on_path("gdal_translate")
    if found:
        return os.path.dirname(os.path.abspath(found))
    if WINDOWS:
        roots = []
        for base in (os.environ.get("ProgramFiles", r"C:\Program Files"), r"C:\OSGeo4W", r"C:\OSGeo4W64"):
            if os.path.isdir(base):
                roots.append(base)
        for base in roots:
            candidates = [os.path.join(base, "bin")]
            try:
                for d in sorted(os.listdir(base), reverse=True):
                    if d.upper().startswith("QGIS"):
                        candidates.append(os.path.join(base, d, "bin"))
            except OSError:
                pass
            for c in candidates:
                if os.path.isfile(os.path.join(c, "gdal_translate.exe")):
                    return c
    raise ToolError("gdal_translate was not found: put GDAL's bin folder on PATH or pass --gdal <folder>")


def find_build_output(root, project, name):
    """A PREACT build output (PREACT.dll, PREACTcli.dll): Release first, as build.sh/build.ps1 make it."""
    return first_existing([os.path.join(root, "PREACT", project, "bin", cfg, "net8.0", name)
                           for cfg in ("Release", "Debug")])


def find_elmfire(root, explicit=None):
    if explicit:
        if os.path.isfile(explicit):
            return os.path.abspath(explicit)
        raise ToolError("--elmfire %s is not a file" % explicit)
    env = os.environ.get("ELMFIRE_EXE")
    sub = os.path.join(root, "WUInity", "Assets", "ThirdParty", "elmfire", "build")
    found = first_existing([env,
                            os.path.join(sub, "windows", "bin", "elmfire.exe") if WINDOWS else None,
                            os.path.join(sub, "linux", "bin", "elmfire")])
    if not found:
        raise ToolError("ELMFIRE was not found (looked at $ELMFIRE_EXE and %s): build it or pass --elmfire" % sub)
    return found


def find_sumo_home(explicit=None):
    home = explicit or os.environ.get("SUMO_HOME")
    if not home or not os.path.isdir(home):
        raise ToolError("SUMO_HOME is not set (or not a folder); set it or pass --sumo-home")
    netconvert = first_existing([os.path.join(home, "bin", exe_name("netconvert"))])
    if not netconvert:
        raise ToolError("no netconvert in %s" % os.path.join(home, "bin"))
    return os.path.abspath(home), netconvert


class Runner(object):
    """Runs a command with its output in a log file, and remembers what it ran."""

    def __init__(self, env):
        self.env = env
        self.commands = []

    def run(self, args, cwd, log_path, timeout=1800, stdin_text=None):
        start = time.time()
        with open(log_path, "w") as log:
            log.write("$ " + " ".join('"%s"' % a if " " in a else a for a in args) + "\n")
            log.write("# in " + cwd + "\n")
            log.flush()
            try:
                p = subprocess.run(args, cwd=cwd, env=self.env, stdout=log, stderr=subprocess.STDOUT,
                                   input=stdin_text.encode() if stdin_text is not None else None,
                                   stdin=None if stdin_text is not None else subprocess.DEVNULL,
                                   timeout=timeout)
                code = p.returncode
            except subprocess.TimeoutExpired:
                code = -999
                log.write("\n# TIMED OUT after %d s\n" % timeout)
        seconds = time.time() - start
        self.commands.append((args, cwd, code, seconds, log_path))
        with open(log_path, "a") as log:
            log.write("\n# exit %d after %.1f s\n" % (code, seconds))
        return code, read_text(log_path), seconds


def read_text(path):
    with open(path, "r", errors="replace") as f:
        return f.read()


def dotnet():
    d = find_on_path("dotnet")
    if not d:
        raise ToolError("dotnet was not found on PATH (the .NET 8 SDK or runtime runs PREACT and PREACTcli)")
    return d


def python():
    return sys.executable
