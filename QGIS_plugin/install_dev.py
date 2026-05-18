"""
Development install helper.
Run once from the repo root to symlink the plugin into QGIS's plugin directory:

    python QGIS_plugin/install_dev.py

Then enable "WUInity" in QGIS > Plugins > Manage and Install Plugins.
After code changes, reload with the Plugin Reloader plugin or restart QGIS.
"""

import os
import sys

PLUGIN_SRC  = os.path.join(os.path.dirname(__file__), "wuinity_qgis")
PLUGIN_NAME = "wuinity_qgis"

def qgis_plugin_dir():
    if sys.platform == "win32":
        appdata = os.environ.get("APPDATA", "")
        return os.path.join(appdata, "QGIS", "QGIS3", "profiles", "default", "python", "plugins")
    elif sys.platform == "darwin":
        home = os.path.expanduser("~")
        return os.path.join(home, "Library", "Application Support", "QGIS", "QGIS3",
                            "profiles", "default", "python", "plugins")
    else:
        home = os.path.expanduser("~")
        return os.path.join(home, ".local", "share", "QGIS", "QGIS3",
                            "profiles", "default", "python", "plugins")


def main():
    plugin_dir = qgis_plugin_dir()
    os.makedirs(plugin_dir, exist_ok=True)

    link = os.path.join(plugin_dir, PLUGIN_NAME)

    if os.path.islink(link):
        os.unlink(link)
        print(f"Removed existing symlink: {link}")
    elif os.path.exists(link):
        print(f"ERROR: {link} exists and is not a symlink. Remove it manually.")
        sys.exit(1)

    os.symlink(os.path.abspath(PLUGIN_SRC), link)
    print(f"Symlinked:\n  {PLUGIN_SRC}\n  → {link}")
    print("\nNow enable 'WUInity' in QGIS > Plugins > Manage and Install Plugins.")


if __name__ == "__main__":
    main()
