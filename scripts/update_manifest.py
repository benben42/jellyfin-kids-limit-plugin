#!/usr/bin/env python3
"""Upsert a version entry into manifest.json, the Jellyfin plugin repository index.

Reads plugin metadata (name/guid/overview/description/owner/category/targetAbi)
from build.yaml so the manifest never drifts from it, then inserts or replaces
the entry for --version, sorted newest-first.
"""
import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import yaml


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--build-yaml", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--changelog", default="See the GitHub release notes.")
    args = parser.parse_args()

    build = yaml.safe_load(args.build_yaml.read_text())

    manifest = json.loads(args.manifest.read_text()) if args.manifest.exists() else []

    entry = next((p for p in manifest if p.get("guid") == build["guid"]), None)
    if entry is None:
        entry = {"guid": build["guid"], "versions": []}
        manifest.append(entry)

    entry["name"] = build["name"]
    entry["description"] = build["description"].strip()
    entry["overview"] = build["overview"]
    entry["owner"] = build["owner"]
    entry["category"] = build["category"]
    entry.setdefault("imageUrl", "")

    entry["versions"] = [v for v in entry["versions"] if v.get("version") != args.version]
    entry["versions"].append(
        {
            "version": args.version,
            "changelog": args.changelog,
            "targetAbi": build["targetAbi"],
            "sourceUrl": args.source_url,
            "checksum": args.checksum,
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
    )
    entry["versions"].sort(key=lambda v: [int(p) for p in v["version"].split(".")], reverse=True)

    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
