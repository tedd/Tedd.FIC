"""Summarize raw probe runs; first line is the environment banner."""
import csv
import glob
import statistics
from collections import defaultdict
from pathlib import Path

raw = Path(__file__).parent / "raw"
for prefix in ("baseline", "h001", "h002", "baseline2", "h002b", "h003", "h004"):
    values = defaultdict(list)
    for filename in glob.glob(str(raw / f"{prefix}-[0-9].csv")):
        with open(filename, newline="", encoding="utf-8-sig") as stream:
            next(stream)
            for row in csv.DictReader(stream):
                values[(row["pattern"], row["effort"], row["operation"])].append(float(row["ms"]))
    print(prefix)
    for key, samples in sorted(values.items()):
        print(",".join(key), f"median={statistics.median(samples):.4f} min={min(samples):.4f} max={max(samples):.4f} n={len(samples)}")
