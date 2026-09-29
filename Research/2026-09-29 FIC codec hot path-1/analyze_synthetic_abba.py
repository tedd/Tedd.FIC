"""Summarize synthetic A-B-B-A results by operation and fixture."""
import csv
import statistics
from collections import defaultdict
from pathlib import Path

raw = Path(__file__).parent / "raw"
values = defaultdict(lambda: defaultdict(list))
for suffix in ("sa1", "sb1", "sb2", "sa2"):
    with (raw / f"abba-{suffix}.csv").open(encoding="utf-8-sig", newline="") as stream:
        next(stream)
        for row in csv.DictReader(stream):
            key = (row["pattern"], row["effort"], row["operation"])
            values[key]["baseline" if suffix.startswith("sa") else "candidate"].append(float(row["ms"]))
for key, variants in sorted(values.items()):
    a = statistics.median(variants["baseline"])
    b = statistics.median(variants["candidate"])
    print(f"{','.join(key)}: {a:.4f} -> {b:.4f} ms, {100*(b/a-1):+.2f}%, n=18/18, ranges={min(variants['baseline']):.4f}-{max(variants['baseline']):.4f}/{min(variants['candidate']):.4f}-{max(variants['candidate']):.4f}")
