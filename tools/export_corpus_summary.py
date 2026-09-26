"""Export aggregate corpus results for the static benchmark charts."""

import argparse
import csv
import json
from collections import defaultdict
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("run_directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    metadata = dict(
        line.split("=", 1)
        for line in (args.run_directory / "run.txt").read_text(encoding="utf-8").splitlines()
    )
    with (args.run_directory / "summary.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))

    numeric = {
        "images": int,
        "megapixels": float,
        "source_bytes": int,
        "encoded_bytes": int,
        "encode_seconds": float,
        "decode_seconds": float,
    }
    for row in rows:
        for field, convert in numeric.items():
            row[field] = convert(row[field])

    by_folder = defaultdict(list)
    for row in rows:
        by_folder[row["folder"]].append(row)
    expected_count = int(metadata["count-per-folder"])
    if len(by_folder) != 5 or any(len(group) != 6 for group in by_folder.values()):
        raise ValueError("Expected five folders and six codecs per folder")
    for folder, group in by_folder.items():
        if any(row["images"] != expected_count for row in group):
            raise ValueError(f"Incomplete sample: {folder}")
        if len({row["source_bytes"] for row in group}) != 1:
            raise ValueError(f"Codecs did not use the same source images: {folder}")

    data = {
        "sample_per_folder": expected_count,
        "seed": int(metadata["seed"]),
        "workers": int(metadata["workers"]),
        "manifest_sha256": metadata["manifest-sha256"],
        "rows": rows,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {args.output} with {len(rows)} rows from {len(by_folder)} folders")


if __name__ == "__main__":
    main()
