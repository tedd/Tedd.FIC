"""Build the standard scientific report data from the versioned raw evidence."""
import csv
import json
import statistics
from collections import defaultdict
from datetime import datetime
from pathlib import Path

run = Path(__file__).parent
raw = run / "raw"
synthetic = defaultdict(lambda: {"baseline": [], "candidate": []})
for suffix in ("sa1", "sb1", "sb2", "sa2"):
    variant = "baseline" if suffix.startswith("sa") else "candidate"
    with (raw / f"abba-{suffix}.csv").open(encoding="utf-8-sig", newline="") as stream:
        next(stream)
        for row in csv.DictReader(stream):
            synthetic[(row["pattern"], row["effort"], row["operation"])][variant].append(float(row["ms"]))

def stats(samples):
    return {"median": round(statistics.median(samples), 4), "min": round(min(samples), 4),
            "max": round(max(samples), 4), "n": len(samples)}

def pair(key):
    a, b = synthetic[key]["baseline"], synthetic[key]["candidate"]
    left, right = stats(a), stats(b)
    right["changePercent"] = round(100 * (right["median"] / left["median"] - 1), 2)
    return left, right

def corpus_total(suffix):
    with (raw / f"abba-{suffix}.csv").open(encoding="utf-8-sig", newline="") as stream:
        next(stream)
        return sum(float(row["encode_ms"]) for row in csv.DictReader(stream))

corpus_a = [corpus_total("a1"), corpus_total("a2")]
corpus_b = [corpus_total("b1"), corpus_total("b2")]
corpus_before = statistics.mean(corpus_a)
corpus_after = statistics.mean(corpus_b)
noise_before, noise_after = pair(("noise", "Default", "encode"))

def tested(id, claim, mechanism, prediction, falsification, change, scope,
           maintainability, comments, result, state, decision, rationale, evidence, risks=None, parent=None):
    item = {"id": id, "claim": claim, "mechanism": mechanism,
            "prediction": prediction, "falsification": falsification, "change": change,
            "changeScope": scope, "maintainability": maintainability,
            "codeComments": comments, "result": result, "state": state,
            "decision": decision, "decisionRationale": rationale,
            "evidence": evidence, "risks": risks or []}
    if parent:
        item["parentId"] = parent
    return item

hypotheses = [
    tested("H-001", "Skip R0 bitstream stores while fewer than eight bits are complete.",
           "fewer overlapping stores in R0Encoder.Put", "Lower Default and Fast encode latency without output changes.",
           "No repeatable application gain or a regression.", "Guarded the eight-byte store on a nonzero completed-byte count.",
           "local", "Added a branch to a very hot bitstream helper.", "Reverted; no retained comment.",
           "The warmed Default scenarios were within noise; Fast and decode controls also drifted.", "rejected", "Rejected",
           "The extra branch did not produce a controlled caller benefit.", ["raw/epoch-a-h001-1.csv", "raw/h001-1.csv"]),
    tested("H-002", "Reuse the already emitted color-code length for the L2 decision in Qp.Parse2.",
           "remove duplicate color-delta classification on same-alpha literals", "Reduce noisy Default encode time while preserving encoded bytes and corpus latency.",
           "No repeatable gain, altered encoded bytes, or material corpus regression.",
           "Emit the color candidate once, use its length to decide whether a two-byte L2 code replaces it, and publish only the selected length.",
           "local", "A non-obvious speculative write requires an explicit buffer-capacity invariant.",
           "Adjacent H-002 comment states the MaxEncodedSize and published-length invariant.",
           "Synthetic noisy Default encode median 7.9009 to 4.7629 ms (-39.7%); 50-image A-B-B-A sum 472.304 to 466.608 ms (-1.2%), near run variation. Six synthetic output hashes matched.",
           "retained", "Adopted", "The large nonoverlapping noisy-input benefit and byte identity justify the small local edit; the corpus shows no material regression in alternating runs.",
           ["raw/abba-sa1.csv", "raw/abba-sb1.csv", "raw/abba-a1.csv", "raw/abba-b1.csv", "disassembly/h002-parse2.asm"],
           ["Extra speculative writes can cost time where L2 hits are common; bounded by MaxEncodedSize."]),
    tested("H-003", "Compute the color code before writing so L2 hits avoid speculative stores.",
           "separate classification from emission", "Keep the noise gain and improve the real-image corpus over H-002.",
           "A representative corpus regression or excessive code burden.",
           "Added ColourCode and WriteColour helpers; deferred stores until after L2 selection.",
           "moderate", "Expanded and branched the hot helper across all color paths.", "Reverted; no retained comment.",
           "Noisy Default improved against control, but fixed-core corpus sum regressed 6.1% and code became more complex.",
           "rejected", "Rejected", "The representative regression and larger maintenance surface outweighed a content-specific gain.",
           ["raw/h003-corpus.csv", "raw/h003.patch"], parent="H-002"),
    tested("H-004", "Skip R0 zero-group mask detection on photo-like strips.",
           "remove a prepass over groups with few all-zero residuals", "Lower photographic encode time without material size loss.",
           "Slower corpus encoding or a size regression.", "Disabled R0Encoder.Masks as an upper-bound experiment.",
           "local", "Changes the encoded representation and may harm compression.", "Reverted; no retained comment.",
           "The 50-image sample was 12.4% slower and 20.75% larger; gradient output also changed.",
           "rejected", "Rejected", "The compression and speed regressions violate the primary caller and byte-identity constraints.",
           ["raw/h004-corpus.csv", "raw/h004.patch"]),
    {"id": "H-005", "claim": "Special-case all-zero eight-pixel groups in R0.Emit.", "state": "not-applicable", "decision": "Low incidence",
     "mechanism": "bypass vector quotient packing and PEXT", "result": "Only 714–15,407 of 1.18–2.02 million measured groups were all zero (0.04–1.30%). The extra common-path test lacks plausible net benefit.", "evidence": ["profiling/r0-group-counts.txt"]},
    {"id": "H-006", "claim": "Specialize R0.Emit's large-quotient fallback.", "state": "not-applicable", "decision": "Rare path",
     "mechanism": "reduce scalar escape handling", "result": "High-quotient groups were 943–11,383 of 1.18–2.02 million (0.05–0.96%); no material caller headroom.", "evidence": ["profiling/r0-group-counts.txt"]},
    {"id": "H-007", "claim": "Precompute vector Rice thresholds for R0.KRow.", "state": "not-applicable", "decision": "Insufficient headroom",
     "mechanism": "replace five per-row broadcasts with cached vectors", "result": "KRow was 1.65% of all corpus trace samples, including idle runtime threads. Cache loads and a new table would add complexity without evidence of a material application gain."},
    {"id": "H-008", "claim": "Avoid the selected strip's NativePayload copy.", "state": "not-applicable", "decision": "Ownership cost",
     "mechanism": "remove one intermediate buffer transfer", "result": "Memmove was 1.10% of all corpus trace samples; direct ownership transfer would rework scratch reuse and exception cleanup for a low observed ceiling."},
    {"id": "H-009", "claim": "Specialize CRC composition for fixed strip lengths.", "state": "not-applicable", "decision": "Insufficient headroom",
     "mechanism": "avoid polynomial shift calculation", "result": "Crc32C.Compute was 1.15% of corpus trace samples and ShiftFor already caches common lengths; no repeated polynomial setup was observed."},
    {"id": "H-010", "claim": "Elide the literal lower-bound guarantee check.", "state": "not-applicable", "decision": "Violates size contract",
     "mechanism": "remove LiteralStrip.LowerBoundsAvx2 work", "result": "The check preserves the encoder's literal fallback and output-size guarantee. Its 2.17% sampled share does not justify changing the byte/size behavior in this investigation."},
]

rows = []
for label in ("gradient", "graphics", "noise"):
    left, right = pair((label, "Default", "encode"))
    rows.append({"label": label.title(), "detail": "512×512 RGBA, Default effort, no outer compression",
                 "measurements": [left, right]})

data = {
    "title": "FIC .NET encoder hot-path investigation",
    "generatedAt": datetime.now().astimezone().isoformat(timespec="seconds"),
    "status": "complete",
    "scope": "Local, byte-identical optimizations of single-threaded .NET FIC encoding on synthetic RGBA and a deterministic real-image sample. Broader SIMD rewrites and changed format/size policy are outside this investigation.",
    "question": "Which measured local encoder cost can be removed while preserving encoded bytes and avoiding material representative-workload regressions?",
    "primaryMetric": "milliseconds per encode",
    "summary": {"baseline": noise_before["median"], "final": noise_after["median"], "unit": "ms/image",
                "changePercent": noise_after["changePercent"],
                "headline": "Noisy Default encoding improved 39.7% with identical bytes; the 50-image aggregate changed -1.2% and is close to run variation."},
    "scorecards": [
        {"title": "Noisy 512×512 Default encode", "value": noise_after["median"], "unit": "ms/image",
         "baseline": noise_before["median"], "wallChangePercent": noise_after["changePercent"],
         "progression": "7.9009 → 4.7629 ms; 18 batched observations per variant in alternating order"},
        {"title": "50-image Default corpus", "value": round(corpus_after, 3), "unit": "summed ms",
         "baseline": round(corpus_before, 3), "wallChangePercent": round(100 * (corpus_after / corpus_before - 1), 2),
         "progression": "Two baseline and two candidate launches, A-B-B-A; per-image medians summed"},
    ],
    "environment": [
        {"label": "Baseline revision", "value": "5993551c14fb976470e3b894dbe0494526fca6d0; untracked research rig only before codec edit"},
        {"label": "Runtime/SDK", "value": ".NET runtime 11.0.0; SDK 11.0.100-rc.1.26425.128; Release net11.0"},
        {"label": "JIT/GC", "value": "TieredPGO enabled; workstation GC; concurrent GC disabled in Directory.Build.props"},
        {"label": "CPU/OS", "value": "AMD Ryzen 9 5950X, 32 logical processors; Windows 10.0.26200; x64 AVX2/BMI2"},
        {"label": "Corpus control", "value": "CPU 4 affinity, AboveNormal priority, 20 warmups per image, seven timed encodes; 10 images from each of five folders; seed 20260926"},
        {"label": "Synthetic control", "value": "100 warmups per scenario; batches of 10 encodes; nine batches per process; two processes per variant in alternating order"},
        {"label": "Lock", "value": "C:\\Users\\tedd\\.codex\\locks\\performance-measurement.lock; all builds, tests, traces and timings via run_with_performance_lock.py"},
        {"label": "Published table", "value": "Independent current-code benchmark: three launches, each 100 warmups and seven timed repetitions per codec operation; not used to estimate H-002 effect"},
    ],
    "hotspots": [
        {"name": "QpCodec.Enc<BaseW>.Parse2", "location": "src/Tedd.FIC/Ficq/Qp.cs:759", "inclusivePercent": 37.79,
         "exclusivePercent": 37.09, "evidence": "EventPipe sample-profile top-N on warmed 512×512 noise; percentages are of process samples, not encode CPU time",
         "limit": "Repeated color-delta classification and code emission on literals"},
        {"name": "R0Encoder.Emit", "location": "src/Tedd.FIC/Ficq/R0.cs:368", "exclusivePercent": 6.64,
         "evidence": "EventPipe sample-profile on a 12 MP corpus photo; process sample denominator includes idle runtime threads",
         "limit": "Bitstream packing; no supported all-zero/escape shortcut at measured incidence"},
    ],
    "areas": [
        {"name": "Qp literal classification", "boundary": "Qp.Enc.Parse2 same-alpha branch",
         "evidence": "37.09% exclusive process samples on noisy Default encoding",
         "catalogue": "CPU C2; Storage S2; Runtime R2", "hypothesisIds": ["H-002", "H-003"],
         "disposition": "H-002 retained; H-003 rejected on corpus and burden"},
        {"name": "R0 bitstream and masks", "boundary": "R0Encoder.Emit and EncodeCore",
         "evidence": "6.64% Emit samples on representative photo; group counts recorded",
         "catalogue": "Memory M7; CPU C1/C3; Storage S1", "hypothesisIds": ["H-001", "H-004", "H-005", "H-006", "H-007"],
         "disposition": "Two rejected, three ruled out by measured incidence/headroom"},
        {"name": "Transfers and checksum", "boundary": "NativePayload and Crc32C",
         "evidence": "1.10% memmove and 1.15% CRC among corpus process samples",
         "catalogue": "Memory M1/M7; CPU C4", "hypothesisIds": ["H-008", "H-009", "H-010"],
         "disposition": "No justified local byte-identical change"},
    ],
    "series": [
        {"title": "Synthetic Default encode", "description": "Batched per-image medians; whiskers span all 18 observations in each variant. Only the noisy result has nonoverlapping ranges.",
         "unit": "ms/image", "lowerIsBetter": True, "rounds": ["Baseline", "H-002"], "rows": rows},
        {"title": "50-image corpus", "description": "Bars are sums of per-image medians, averaged over two A/B launches; whiskers span launch totals. This is not wall-clock duration.",
         "unit": "summed ms", "lowerIsBetter": True, "rounds": ["Baseline", "H-002"],
         "rows": [{"label": "Five folders × 10 images", "detail": "Equal image count per folder; same manifest and 72,986,745 encoded bytes in every variant",
                   "measurements": [{"median": round(corpus_before, 3), "min": round(min(corpus_a), 3), "max": round(max(corpus_a), 3), "n": 2},
                                    {"median": round(corpus_after, 3), "min": round(min(corpus_b), 3), "max": round(max(corpus_b), 3), "n": 2,
                                     "changePercent": round(100 * (corpus_after / corpus_before - 1), 2)}]}]},
    ],
    "effectGroups": [
        {"name": "Control-relative H-002 effects", "control": "A-B-B-A from separate baseline and candidate binaries under one performance lock",
         "items": [
             {"hypothesisId": "H-002", "label": "Noisy Default encode", "effect": round(noise_after["changePercent"] / 100, 4),
              "range": [-0.493, -0.339], "note": "Conservative cross-sample extreme range; 18 observations per variant"},
             {"hypothesisId": "H-002", "label": "50-image corpus", "effect": round(corpus_after / corpus_before - 1, 4),
              "range": [-0.0272, 0.0035], "note": "Range of ordered launch contrasts; crosses zero"},
         ]},
    ],
    "hypotheses": hypotheses,
    "retainedChanges": ["H-002 reuses Qp.Enc.Parse2's emitted color-code length on same-alpha literals; source comment documents the bounded speculative store.",
                        "The synthetic benchmark now warms each codec operation for 100 calls before timing; the site and README contain the current three-launch results."],
    "decisions": ["H-003 was discarded despite a noisy-input gain because the controlled real-image sample regressed and the refactor expanded the hot path.",
                  "H-004 was discarded because disabling R0 masks increased corpus output size by 20.75% and encoding time by 12.4% in its screening epoch.",
                  "The search stopped at ten defensible local hypotheses: sampled incidence or ownership/format constraints ruled out six without adding unsupported variants. Broad SIMD fusion remains a separate investigation."],
    "correctness": ["24 .NET codec tests passed for H-002.",
                    "Six synthetic fixtures (three patterns × Fast/Default) had identical SHA-256 encoded outputs between baseline and H-002.",
                    "Every timed corpus output decoded pixel-for-pixel; all 50 paired encoded lengths matched across A-B-B-A runs.",
                    "The speculative write is bounded by QpCodec.Encode's MaxEncodedSize destination validation; only the selected encoded length is copied into a container."],
    "generatedCode": [{"hypothesisId": "H-002", "summary": "Optimized BaseW Parse2 shrank from 3,280 to 2,736 bytes, and one ColourCost call site disappeared.",
                       "before": "Two ColourCost call sites: same-alpha and alpha-different branches.",
                       "after": "One ColourCost call site remains in the alpha-different branch; same-alpha uses Colour's returned length.",
                       "artifact": "disassembly/h002-parse2.asm"}],
    "validityThreats": ["The EventPipe top-N denominator includes runtime idle/finalizer threads; its percentages rank source locations but are not application CPU fractions or Amdahl ceilings.",
                        "The optimized disassembly used TieredCompilation=0 and ReadyToRun=0 for diagnosis; acceptance timings used deployment settings with TieredPGO enabled.",
                        "The 50-image corpus is a deterministic subset, not the site's historical 5,000-image sample. Its -1.2% result is within launch variability and is not a general throughput claim.",
                        "The synthetic ABBA runs were unpinned; unrelated Fast/decode movements show residual host noise. The noisy Default encode ranges did not overlap.",
                        "The first pilot used only eight warmups and showed tiering drift; those results were retained as epoch A but excluded from the final estimate.",
                        "Acceptance priorities and burden limits were formalized after the first pilot; this limits claims of pre-registered selection."],
    "reproduction": ["From baseline revision 5993551, run the saved abba-h002.ps1 under the fixed shared performance lock to build both variants and reproduce the corpus comparison.",
                     "Run abba-synthetic.ps1 against those two saved binaries under the same lock for the synthetic comparison.",
                     "From the retained source, run site-benchmark.ps1 under the lock to regenerate current public benchmark values.",
                     "Run python analyze_abba.py, python analyze_synthetic_abba.py, and python analyze_site.py for tabular summaries."],
    "artifacts": [
        {"label": "Raw A-B-B-A corpus and synthetic tables", "path": "raw/"},
        {"label": "CPU profiles and compact rankings", "path": "profiling/"},
        {"label": "Baseline Parse2 disassembly", "path": "disassembly/baseline-parse2.asm"},
        {"label": "H-002 Parse2 disassembly", "path": "disassembly/h002-parse2.asm"},
        {"label": "Catalogue coverage ledger", "path": "coverage.md"},
    ],
}

(run / "report-data.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
