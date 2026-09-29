"""Report medians across three settled seven-repetition site benchmark launches."""
import csv
import statistics
from collections import defaultdict
from pathlib import Path

raw = Path(__file__).parent / "raw"
values = defaultdict(list)
for index in (1, 2, 3):
    with (raw / f"site-benchmark-{index}.csv").open(encoding="utf-8-sig", newline="") as stream:
        banner = next(stream).strip()
        for row in csv.DictReader(stream):
            values[(row["Image"], row["Codec"])].append(row)
print(banner)
for key, rows in values.items():
    assert len({r["Bytes"] for r in rows}) == 1
    e = [float(r["EncodeMs"]) for r in rows]
    d = [float(r["DecodeMs"]) for r in rows]
    print(f"{key[0]},{key[1]},{rows[0]['Bytes']},{statistics.median(e):.3f},{statistics.median(d):.3f},encode-range={min(e):.3f}-{max(e):.3f},decode-range={min(d):.3f}-{max(d):.3f}")
