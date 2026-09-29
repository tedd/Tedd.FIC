# Catalogue coverage

Scope: single-threaded .NET FIC encoding with explicit `FicCompression.None`, preserving decoded pixels and encoded bytes. The primary measured scenarios were synthetic 512×512 RGBA inputs and 50 deterministic corpus images. Categories without a measured limiting mechanism were closed for this local investigation; this is not a claim that the whole codec has no further optimization potential.

| Category | Inspected location and disposition |
| --- | --- |
| M1 allocation | `EncScratch`, `NativePayload`, `CompactEncoder.EncodeParts`: allocations appear in profiles, but no redundant initialization or ownership-safe reuse was demonstrated. H-008 covers the visible copy path. |
| M2 stack storage | `Qp.Enc.Parse2` already uses bounded `stackalloc` for indices; no small heap scratch in the measured loop. |
| M3 array access | `Qp.Enc.Parse2` and R0 loops use `Unsafe`/native pointers after caller validation; repeated managed range checks were not the measured cost. |
| M4 managed references | `Parse2` already uses managed byrefs and `Unsafe.Add`; raw native pixels are pinned only while encoding. No additional access conversion justified. |
| M5 layout | Qp pixels and masks are contiguous scratch buffers; no hot object graph or sparse layout was observed. |
| M6 addressing | The measured loops traverse sequential pixels and packed masks, without latency-bound indirect reads or a location search. |
| M7 copies and stores | `R0Encoder.Put` H-001 rejected; `NativePayload.Copy` H-008 ruled out within the local ownership scope. |
| C1 dependencies | `R0Encoder.Emit` quotient/remainder bit accumulators are separate chains; H-001 tested removal of incomplete-byte stores without gain. |
| C2 branches | `Qp.Enc.Parse2` literal/L2 selection: H-002 retained; H-003 rejected. |
| C3 SIMD | R0 and Qp already use AVX2 with fallbacks. All-zero and high-quotient vector branches H-005/H-006 have low measured incidence. Threshold caching H-007 lacked material headroom. |
| C4 intrinsics | `BitOperations`, AVX2, BMI2 and hardware CRC are already used in the sampled loops; no missing intrinsic lowering was identified. |
| C5 neighborhood kernels | `R0.ZRows8` uses MED neighbors in a vector loop. Fusing it with planar conversion would be a separate moderate/refactor investigation; no local byte-identical edit was supported by the present profile. |
| S1 bitmaps | Qp class masks and R0 group masks are packed bits; R0 mask bypass H-004 was rejected for speed and size. |
| S2 hashing | The 64-slot color cache and 1024-slot L2 cache in `Parse2` are hot. H-002 reuses literal classification without changing lookup semantics. |
| S3 frozen collections | `PalCodec` dictionaries are built per image and are not the measured hot path; a frozen map would add build cost. |
| S4 query preparation | No query planning or archetype-style preparation exists in this codec. |
| S5 compression/runs | Qp and R0 already encode runs; changing run representation would change the format or emitted bytes. |
| R1 dispatch | Decode opcode dispatch did not appear in the selected encode profile; no dispatch change was tested. |
| R2 specialization | Duplicate color classification and emission in `Parse2` motivated H-002/H-003. |
| R3 feature flags | ISA selection is resolved per call but was not material in sampled CPU stacks; no startup-only flag contract was available. |
| T1 false sharing | Single-threaded measurement has no cross-core write sharing. |
| T2 ownership | Strip scratch and native payloads have explicit per-worker ownership; no shared mutation in the measured path. |
| T3 locks | No codec lock or lock contention appeared in the measured caller. Idle runtime wait frames in EventPipe were not treated as codec contention. |
| T4 scheduling | `threads=1` uses the sequential `EncScratch.ForStrips` path; parallel scheduling would change the workload and result contract. |

Only ten source-supported, materially distinct local hypotheses were identified. More would require broad SIMD fusion, a changed size/byte-identity policy, or a separate concurrency workload; adding syntactic variants would not improve the evidence ledger.
