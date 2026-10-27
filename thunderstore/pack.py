"""Build PotionDuration and pack it as a Thunderstore package: dist/PotionDuration-<version>.zip.

Usage (from anywhere): python -I thunderstore/pack.py [--interop interop-client]

The zip holds, at its root: manifest.json, README.md, CHANGELOG.md, icon.png, PotionDuration.dll.
One DLL serves server and client; it is built against a game build's interop (default: the client copy).
"""
import argparse
import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent
DESCRIPTION = ("Potions, brews and elixirs last as long as you choose (default 2 hours instead of 60 minutes). "
               "Install on the server. Install on the client so tooltips show the new time, otherwise they will show the default 60 minutes.")
DEPENDENCIES = ["BepInEx-BepInExPack_V_Rising-1.733.2"]


def version():
    text = (PROJECT / "PotionDuration.csproj").read_text(encoding="utf-8")
    return re.search(r"<Version>([^<]+)</Version>", text).group(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--interop", default="interop-client", help="InteropDir, relative to the repo root")
    args = parser.parse_args()

    ver = version()
    out = PROJECT / "bin" / "thunderstore"
    subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", f"-p:InteropDir={args.interop}",
                    "-o", str(out), "--no-incremental"], cwd=PROJECT, check=True)

    manifest = {
        "name": "PotionDuration",
        "version_number": ver,
        "website_url": "https://github.com/jnxldr/VRising-PotionDuration",
        "description": DESCRIPTION,
        "dependencies": DEPENDENCIES,
    }
    assert len(DESCRIPTION) <= 250, len(DESCRIPTION)

    dist = PROJECT / "dist"
    dist.mkdir(exist_ok=True)
    zip_path = dist / f"PotionDuration-{ver}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        z.write(PROJECT / "README.md", "README.md")
        z.write(PROJECT / "CHANGELOG.md", "CHANGELOG.md")
        z.write(HERE / "icon.png", "icon.png")
        z.write(out / "PotionDuration.dll", "PotionDuration.dll")
    print(zip_path)


if __name__ == "__main__":
    sys.exit(main())
