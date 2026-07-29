#!/usr/bin/env python3
"""Build USugar.v{version}.unitypackage without Unity.

A .unitypackage is a gzipped tar holding one directory per asset GUID, each with
`asset`, `asset.meta` and `pathname`. GUIDs are read from the committed .meta files
and never regenerated: USugar patches UdonSharp at domain load, so two versions must
never coexist in one project and an upgrade has to overwrite the previous install.
"""

from __future__ import annotations

import re
import shutil
import sys
import tarfile
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INSTALL_ROOT = "Assets/USugar"
NOTES = ROOT / ".github" / "RELEASE_NOTES.md"

# Assets/USugar.meta lives outside this repository, so the install root's folder asset is
# reproduced here. The guid must stay fixed or every upgrade re-creates the folder.
INSTALL_ROOT_GUID = "1100d99290ca53b4283a530c092bb44b"
INSTALL_ROOT_META = (
    "fileFormatVersion: 2\n"
    f"guid: {INSTALL_ROOT_GUID}\n"
    "folderAsset: yes\n"
    "DefaultImporter:\n"
    "  externalObjects: {}\n"
    "  userData: \n"
    "  assetBundleName: \n"
    "  assetBundleVariant: \n"
)

EXCLUDE = [
    r"^\.git(/|$)",
    r"^\.github(/|$)",
    r"^\.claude(/|$)",
    r"^\.agents(/|$)",
    r"^docs(/|$)",
    r"^Library~(/|$)",
    r"^Editor~(/|$)",
    r"^CLAUDE\.md",
    r"^\.gitignore$",
    r"^\.gitattributes$",
    r"\.unitypackage$",
]

GUID = re.compile(r"guid:\s*([a-f0-9]{32})")


def excluded(rel: str) -> bool:
    return any(re.search(pattern, rel) for pattern in EXCLUDE)


def entries():
    for path in sorted(ROOT.rglob("*")):
        rel = path.relative_to(ROOT).as_posix()
        if excluded(rel) or path.suffix == ".meta":
            continue
        meta = Path(f"{path}.meta")
        if not meta.exists():
            raise SystemExit(f"{rel} has no .meta; Unity would not import it")
        found = GUID.search(meta.read_text(encoding="utf-8"))
        if not found:
            raise SystemExit(f"{rel}.meta carries no guid")
        yield path, meta, rel, found.group(1)


def check_notes(version: str) -> None:
    if not NOTES.exists():
        raise SystemExit(".github/RELEASE_NOTES.md is missing")
    heading = f"## v{version}"
    lines = NOTES.read_text(encoding="utf-8").splitlines()
    if not any(line.rstrip() == heading for line in lines):
        raise SystemExit(
            f".github/RELEASE_NOTES.md has no '{heading}' heading; "
            "it still describes the previous release"
        )


def build(version: str) -> Path:
    out = ROOT / f"USugar.v{version}.unitypackage"
    seen: dict[str, str] = {}
    with tempfile.TemporaryDirectory() as tmp:
        stage = Path(tmp)
        root_entry = stage / INSTALL_ROOT_GUID
        root_entry.mkdir()
        (root_entry / "asset.meta").write_bytes(INSTALL_ROOT_META.encode("utf-8"))
        (root_entry / "pathname").write_bytes(INSTALL_ROOT.encode("utf-8"))
        seen[INSTALL_ROOT_GUID] = INSTALL_ROOT
        for path, meta, rel, guid in entries():
            if guid in seen:
                raise SystemExit(f"{rel} shares guid {guid} with {seen[guid]}")
            seen[guid] = rel
            entry = stage / guid
            entry.mkdir()
            if path.is_file():
                shutil.copy2(path, entry / "asset")
            shutil.copy2(meta, entry / "asset.meta")
            (entry / "pathname").write_text(f"{INSTALL_ROOT}/{rel}", encoding="utf-8")
        with tarfile.open(out, "w:gz", format=tarfile.GNU_FORMAT) as tar:
            for entry in sorted(stage.iterdir()):
                tar.add(entry, arcname=entry.name)
    print(f"{out.name}: {len(seen)} entries, {out.stat().st_size / 1024:.1f} KB")
    return out


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: release.py <tag>")
    tag_version = sys.argv[1].lstrip("v")
    check_notes(tag_version)
    build(tag_version)
