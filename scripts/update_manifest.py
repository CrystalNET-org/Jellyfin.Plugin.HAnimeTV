#!/usr/bin/env python3
"""Add a release to manifest.json (the Jellyfin plugin repository file)."""
import argparse
import json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="four-part version, e.g. 0.2.0.0")
    parser.add_argument("--target-abi", required=True, help="minimum Jellyfin version, e.g. 10.11.0.0")
    parser.add_argument("--url", required=True, help="download URL of the release zip")
    parser.add_argument("--checksum", required=True, help="MD5 of the release zip")
    parser.add_argument("--timestamp", required=True, help="release time, ISO 8601")
    parser.add_argument("--changelog", default="")
    parser.add_argument("--manifest", default="manifest.json")
    args = parser.parse_args()

    with open(args.manifest, encoding="utf-8") as f:
        manifest = json.load(f)

    versions = [v for v in manifest[0]["versions"] if v["version"] != args.version]
    versions.insert(0, {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.url,
        "checksum": args.checksum,
        "timestamp": args.timestamp,
    })
    manifest[0]["versions"] = versions

    with open(args.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
