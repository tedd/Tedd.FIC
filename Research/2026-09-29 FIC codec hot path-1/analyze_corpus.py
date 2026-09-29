"""Compare paired per-image median encode times for the deterministic sample."""
import csv
import statistics
from collections import defaultdict
from pathlib import Path

raw = Path(__file__).parent / "raw"
def read(prefix):
    with (raw / f"{prefix}-corpus.csv").open(encoding="utf-8-sig", newline="") as stream:
        manifest = next(stream).strip()
        rows = {(r["folder"], int(r["index"])): r for r in csv.DictReader(stream)}
    return manifest, rows

for base_prefix, prefix in (("baseline", "h002"), ("baseline", "h002b"), ("baselinec", "h002c"), ("baselinec", "h003"), ("baselinec", "h004")):
    baseline_manifest, baseline = read(base_prefix)
    candidate_manifest, candidate = read(prefix)
    assert baseline_manifest == candidate_manifest, "Corpus selection changed"
    assert baseline.keys() == candidate.keys(), "Image keys changed"
    groups = defaultdict(list)
    for key, before in baseline.items():
        after = candidate[key]
        b, a = float(before["encode_ms"]), float(after["encode_ms"])
        groups[key[0]].append((b, a))
    groups["all"] = [v for values in groups.values() for v in values]
    byte_before = sum(int(row["encoded_bytes"]) for row in baseline.values())
    byte_after = sum(int(row["encoded_bytes"]) for row in candidate.values())
    print(base_prefix, "versus", prefix, baseline_manifest)
    print(f"bytes={byte_before}->{byte_after}, change={100*(byte_after/byte_before-1):.2f}%")
    for name, pairs in groups.items():
        bs, a = sum(x for x, _ in pairs), sum(y for _, y in pairs)
        effects = [100 * (y / x - 1) for x, y in pairs]
        print(f"{name}: n={len(pairs)}, summed_ms={bs:.3f}->{a:.3f}, change={100*(a/bs-1):.2f}%, median_paired_change={statistics.median(effects):.2f}%")
    print("Google Photos detail (index, dimensions, before ms, after ms, paired change):")
    for key, before in baseline.items():
        if key[0] != "Google Photos":
            continue
        after = candidate[key]
        b, a = float(before["encode_ms"]), float(after["encode_ms"])
        print(f"{key[1]}, {before['width']}x{before['height']}, {b:.3f}, {a:.3f}, {100*(a/b-1):.2f}%")
