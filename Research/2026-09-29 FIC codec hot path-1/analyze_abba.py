"""Analyze fixed-affinity A-B-B-A corpus runs from separate binaries."""
import csv
import statistics
from collections import defaultdict
from pathlib import Path

raw = Path(__file__).parent / "raw"
runs = {}
for suffix in ("a1", "b1", "b2", "a2"):
    with (raw / f"abba-{suffix}.csv").open(encoding="utf-8-sig", newline="") as stream:
        manifest = next(stream).strip()
        rows = {(r["folder"], int(r["index"])): r for r in csv.DictReader(stream)}
    runs[suffix] = (manifest, rows)
assert len({manifest for manifest, _ in runs.values()}) == 1
assert len({frozenset(rows) for _, rows in runs.values()}) == 1
print(runs["a1"][0])
for suffix, (_, rows) in runs.items():
    print(f"{suffix}: summed per-image medians={sum(float(r['encode_ms']) for r in rows.values()):.3f} ms")
groups = defaultdict(list)
for key in runs["a1"][1]:
    rows = [runs[s][1][key] for s in ("a1", "b1", "b2", "a2")]
    assert len({row["encoded_bytes"] for row in rows}) == 1, f"Output size changed: {key}"
    a = statistics.mean(float(rows[i]["encode_ms"]) for i in (0, 3))
    b = statistics.mean(float(rows[i]["encode_ms"]) for i in (1, 2))
    groups[key[0]].append((a, b))
groups["all"] = [pair for name, pairs in groups.items() if name != "all" for pair in pairs]
for name, pairs in groups.items():
    a = sum(x for x, _ in pairs)
    b = sum(y for _, y in pairs)
    effects = [100 * (y / x - 1) for x, y in pairs]
    print(f"{name}: n={len(pairs)}, baseline={a:.3f} ms, candidate={b:.3f} ms, aggregate={100*(b/a-1):.2f}%, median paired={statistics.median(effects):.2f}%")
