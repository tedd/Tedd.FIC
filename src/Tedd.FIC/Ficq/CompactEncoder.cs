using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

internal sealed record CompactSettings(string Name, QpLayout[] Graphics, double Lambda, double GSlack, int Rows = 64)
{
    public StripCodec? Force { get; init; }

    public int MinStripPixels { get; init; } = Ficq.MinStripPixels;

    public bool Guarantee { get; init; } = true;

    public bool Simd { get; init; } = Isa.Simd;

    public static readonly CompactSettings Default = new("A", [QpLayout.GW], 0.05, double.PositiveInfinity);

    public static readonly CompactSettings Max = new("B", [QpLayout.GW, QpLayout.GM], 0.02, 1.25);

    public const double ByteEqualityGate = 0.95, LiteralBytes = 2.0, Near = 1.3, R0Expensive = 12, HitRate = 0.2;
    public const double AbortF = 1.25, PreGate = 0.75;
    public const long AbortMinPx = 16384;
    public const int SmallPixels = 16384;
}

internal static unsafe class CompactEncoder
{
    public static double TimeNs(StripCodec m, long px, long bytes) => m switch
    {
        StripCodec.R0 => 5.0 * px,
        StripCodec.GW or StripCodec.Literal => 0.6 * px + 4.5 * bytes,
        StripCodec.GM => 0.6 * px + 6.9 * bytes,
        _ => 0.6 * px + 3.5 * bytes,
    };

    private static StripCodec CodecOf(QpLayout l) => l.Med ? StripCodec.GM : StripCodec.GW;

    private struct StripResult
    {
        public NativePayload Payload;
        public StripCodec Mode;
        public double Cost, PalCost;
        public int PalLen;
        public uint Crc;
    }

    public static byte[] Encode(ReadOnlySpan<byte> pixels, int w, int h, int ch, CompactSettings s, int threads)
    {
        var parts = EncodeParts(pixels, w, h, ch, s, threads, out var header, out var results, out long total);
        try
        {
            var o = GC.AllocateUninitializedArray<byte>((int)total);
            Assemble(header, results, parts, o);
            return o;
        }
        finally { Free(results); }
    }

    public static bool TryEncode(ReadOnlySpan<byte> pixels, int w, int h, int ch, CompactSettings s, int threads, Span<byte> dst, out int written)
    {
        var parts = EncodeParts(pixels, w, h, ch, s, threads, out var header, out var results, out long total);
        try
        {
            written = 0;
            if (total > dst.Length) return false;
            Assemble(header, results, parts, dst);
            written = (int)total;
            return true;
        }
        finally { Free(results); }
    }

    private static void Free(StripResult[] results)
    {
        for (int i = 0; i < results.Length; i++) results[i].Payload.Dispose();
    }

    private sealed class Parts
    {
        public int N, Rows;
        public List<(int, int)>[]? Pops;
        public byte[]? Shorts;
        public uint[]? Palette;
        public bool UsePalette;
        public uint PixelCrc;
        public long PixelBytes;
    }

    private static Parts EncodeParts(ReadOnlySpan<byte> pixelsSpan, int w, int h, int ch, CompactSettings s, int threads,
        out byte[] header, out StripResult[] results, out long total)
    {
        int rows = FastEncoder.StripRows(w, h, s.Rows, s.MinStripPixels);
        double lambda = (long)w * h >= CompactSettings.SmallPixels ? s.Lambda : 0;
        int N = (h + rows - 1) / rows;
        long stride = (long)w * ch;
        var res = new StripResult[N];
        try
        {
            fixed (byte* pixPtr = pixelsSpan)
            {
                nint pix = (nint)pixPtr;
                int alpha = 0, stop = 0;
                var counts = new (uint[] K, int[] C)?[N];
                EncScratch.ForStrips(N, threads, (i, _) =>
                {
                    int r = Math.Min(rows, h - i * rows);
                    var span = new ReadOnlySpan<byte>((byte*)pix + i * rows * stride, (int)(r * stride));
                    if (ch == 4 && Volatile.Read(ref alpha) == 0)
                        for (int j = 3; j < span.Length; j += 4) if (span[j] != 255) { Volatile.Write(ref alpha, 1); break; }
                    counts[i] = PalCodec.Counts(span, w * r, ch, ref stop);
                });
                int P = alpha != 0 ? 4 : 3;
                var parts = new Parts { N = N, Rows = rows };
                if (stop == 0)
                {
                    var all = new Dictionary<uint, int>();
                    foreach (var c in counts)
                        for (int j = 0; j < c!.Value.K.Length; j++) all[c.Value.K[j]] = all.GetValueOrDefault(c.Value.K[j]) + c.Value.C[j];
                    if (all.Count <= PalCodec.MaxK) parts.Palette = all.OrderByDescending(t => t.Value).ThenBy(t => t.Key).Select(t => t.Key).ToArray();
                }
                if (parts.Palette != null)
                {
                    var idx = new Dictionary<uint, byte>();
                    for (int i = 0; i < parts.Palette.Length; i++) idx[parts.Palette[i]] = (byte)i;
                    var pops = new List<(int, int)>[N];
                    var pal = parts.Palette;
                    EncScratch.ForStrips(N, threads, (i, _) =>
                    {
                        int r = Math.Min(rows, h - i * rows);
                        pops[i] = PalCodec.Parse(new ReadOnlySpan<byte>((byte*)pix + i * rows * stride, (int)(r * stride)), w, r, ch, pal, idx);
                    });
                    parts.Shorts = PalCodec.Shorts(pops, parts.Palette.Length);
                    if (parts.Shorts != null) parts.Pops = pops;
                    else parts.Palette = null;
                }
                var pops2 = parts.Pops;
                var shorts = parts.Shorts;
                EncScratch.ForStrips(N, threads, (i, sc) =>
                {
                    int r = Math.Min(rows, h - i * rows);
                    byte* sp = (byte*)pix + i * rows * stride;
                    res[i] = StripBest(sp, w, r, ch, P, s, lambda, pops2?[i], shorts, sc);
                    res[i].Crc = Crc32C.Compute(new ReadOnlySpan<byte>(sp, (int)(r * stride)));
                });
                if (parts.Palette != null && (s.Force == null || s.Force == StripCodec.Pal))
                {
                    var palOk = new bool[N];
                    EncScratch.ForStrips(N, threads, (i, _) =>
                    {
                        if (!(res[i].PalCost < res[i].Cost)) return;
                        if (s.Guarantee && res[i].PalLen > res[i].Payload.Len &&
                            LiteralStrip.SizeBelow((byte*)pix + i * rows * stride, w, Math.Min(rows, h - i * rows), ch, res[i].PalLen, s.Simd) < res[i].PalLen) return;
                        palOk[i] = true;
                    });
                    double gain = 0;
                    long byteGain = 0;
                    for (int i = 0; i < N; i++)
                        if (palOk[i]) { gain += res[i].Cost - res[i].PalCost; byteGain += res[i].Payload.Len - res[i].PalLen; }
                    double headerCost = 1 + parts.Palette.Length * ch + Ficq.PaletteClasses;
                    parts.UsePalette = s.Force == StripCodec.Pal || (gain > headerCost && byteGain > headerCost);
                    if (parts.UsePalette)
                    {
                        var palette = parts.Palette;
                        EncScratch.ForStrips(N, threads, (i, _) =>
                        {
                            if (!(s.Force == StripCodec.Pal || palOk[i])) return;
                            var buf = (byte*)NativeMemory.Alloc((nuint)(res[i].PalLen + 16));
                            try
                            {
                                int n = PalCodec.Emit(pops2![i], shorts!, palette.Length, new Span<byte>(buf, res[i].PalLen + 16));
                                res[i].Payload.Dispose();
                                res[i].Payload = NativePayload.Copy(buf, n);
                            }
                            finally { NativeMemory.Free(buf); }
                            res[i].Mode = StripCodec.Pal;
                            res[i].Cost = res[i].PalCost;
                        });
                    }
                }
                var hb = new byte[Ficq.MaxHeaderSize];
                int hl = Ficq.WriteHeader(hb, Ficq.TierCompact, w, h, ch, P, rows, parts.UsePalette ? parts.Palette : null, parts.UsePalette ? shorts : null);
                header = hb.AsSpan(0, hl).ToArray();
                uint crc = 0;
                for (int i = 0; i < N; i++) crc = Crc32C.Combine(crc, res[i].Crc, Math.Min(rows, h - i * rows) * stride);
                parts.PixelCrc = crc;
                parts.PixelBytes = (long)h * stride;
                total = hl + Ficq.CrcSize;
                for (int i = 0; i < N; i++)
                    total += res[i].Payload.Len + Ficq.VarintLength(Ficq.TableEntry(res[i].Payload.Len, Ficq.CodecSlot(Ficq.TierCompact, res[i].Mode)));
                results = res;
                return parts;
            }
        }
        catch
        {
            Free(res);
            throw;
        }
    }

    private static void Assemble(byte[] header, StripResult[] res, Parts parts, Span<byte> o)
    {
        header.CopyTo(o);
        int op = header.Length;
        for (int i = 0; i < res.Length; i++)
            op += Ficq.WriteVarint(o[op..], Ficq.TableEntry(res[i].Payload.Len, Ficq.CodecSlot(Ficq.TierCompact, res[i].Mode)));
        for (int i = 0; i < res.Length; i++) { res[i].Payload.Span.CopyTo(o[op..]); op += res[i].Payload.Len; }
        uint crc = Crc32C.Combine(Crc32C.Append(0, header), parts.PixelCrc, parts.PixelBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o[op..], crc);
    }


    private static StripResult StripBest(byte* sp, int w, int r, int ch, int P, CompactSettings s, double lambda, List<(int, int)>? pops, byte[]? shorts,
        EncScratch sc)
    {
        long stride = (long)w * ch;
        long px = (long)w * r;
        var gm = s.Graphics;
        long maxStrip = Math.Max(Math.Max(R0Encoder.MaxPayload(w, r, P), QpCodec.MaxEncodedSize(w, r)), LiteralStrip.MaxEncodedSize(px)) + 64;
        var res = new StripResult { PalCost = double.PositiveInfinity };
        bool planar = Ficq.PlanarWidth(w);
        double f = Probe(sp, w, r, ch);
        bool r0First = planar && f <= CompactSettings.ByteEqualityGate;
        byte* cur = sc.Get(EncScratch.CandA, maxStrip), free = sc.Get(EncScratch.CandB, maxStrip);
        int best = -1; double bestCost = double.MaxValue; StripCodec bm = StripCodec.R0;
        var r0 = R0Encoder.Local;

        void Offer(int n, StripCodec m)
        {
            double cost = n + lambda * TimeNs(m, px, n);
            if (cost < bestCost) { bestCost = cost; best = n; bm = m; byte* t = cur; cur = free; free = t; }
        }
        int Qp(QpLayout l, int rows) => QpCodec.Encode(l, new ReadOnlySpan<byte>(sp, (int)(rows * stride)), w, rows, ch, new Span<byte>(free, (int)maxStrip), long.MaxValue, sc, s.Simd);

        if (s.Force is { } force && force != StripCodec.Pal)
        {
            if (force == StripCodec.Literal) Offer(LiteralStrip.Encode(sp, w, r, ch, free), StripCodec.Literal);
            else if (force == StripCodec.R0 && planar) Offer(r0.Encode(sp, w, r, ch, P, free, sc, s.Simd), StripCodec.R0);
            else
            {
                var fl = force == StripCodec.GM ? QpLayout.GM : QpLayout.GW;
                Offer(Qp(fl, r), CodecOf(fl));
            }
            goto Chosen;
        }

        bool useG;
        double fhit = -1, gEst = double.MaxValue;
        if (r0First)
        {
            Offer(r0.Encode(sp, w, r, ch, P, free, sc, s.Simd), StripCodec.R0);
            double fpx = ProbePx(sp, w, r, ch, s.Simd);
            double ge = (1 - fpx) * CompactSettings.LiteralBytes * px;
            var g0 = CodecOf(gm[0]);
            double r0Cost = bestCost;
            useG = ge + lambda * TimeNs(g0, px, (long)ge) <= r0Cost;
            gEst = ge;
            if (!useG && ge <= CompactSettings.Near * best)
            {
                fhit = ProbeHits(sp, w, r, ch);
                double ge2 = (1 - fpx) * (CompactSettings.LiteralBytes - fhit) * px;
                useG = ge2 + lambda * TimeNs(g0, px, (long)ge2) <= r0Cost;
                gEst = ge2;
            }
            if (px < CompactSettings.SmallPixels) useG = true;
            if (!useG && best * 8.0 / px >= CompactSettings.R0Expensive)
                useG = (fhit >= 0 ? fhit : ProbeHits(sp, w, r, ch)) >= CompactSettings.HitRate;
        }
        else useG = true;

        if (useG)
        {
            double prevG = double.MaxValue;
            for (int c = 0; c < gm.Length; c++)
            {
                if (c >= 1 && prevG > s.GSlack * bestCost) continue;
                var m = CodecOf(gm[c]);
                if (best >= 0 && c == 0)
                {
                    double t0 = TimeNs(m, px, 0), slope = TimeNs(m, px, 1) - t0;
                    double be = (bestCost - lambda * t0) / (1 + lambda * slope);
                    if (be < 1) { prevG = double.MaxValue; continue; }
                    if (px >= CompactSettings.AbortMinPx && r >= 8 && gEst >= CompactSettings.PreGate * be)
                    {
                        int rq = r >> 2;
                        int nq = Qp(gm[c], rq);
                        if ((double)nq * r / rq > CompactSettings.AbortF * be) { prevG = double.MaxValue; continue; }
                    }
                }
                int n = Qp(gm[c], r);
                prevG = n + lambda * TimeNs(m, px, n);
                Offer(n, m);
            }
        }
        if (!r0First && planar)
        {
            long lb = Ficq.MinPayload(StripCodec.R0, w, r, P);
            if (best < 0 || bestCost > lb + lambda * TimeNs(StripCodec.R0, px, lb))
                Offer(r0.Encode(sp, w, r, ch, P, free, sc, s.Simd), StripCodec.R0);
        }
    Chosen:
        if (best > 5 * px && bm == StripCodec.R0)
        {
            int n = Qp(QpLayout.GW, r);
            if (n < best) { best = n; bm = StripCodec.GW; bestCost = n + lambda * TimeNs(StripCodec.GW, px, n); byte* t = cur; cur = free; free = t; }
        }
        if (s.Guarantee && s.Force == null && LiteralStrip.SizeBelow(sp, w, r, ch, best, s.Simd) < best)
        {
            int n = LiteralStrip.Encode(sp, w, r, ch, free);
            best = n; bm = StripCodec.Literal; bestCost = n + lambda * TimeNs(StripCodec.Literal, px, n);
            byte* t = cur; cur = free; free = t;
        }
        res.Payload = NativePayload.Copy(cur, best); res.Mode = bm; res.Cost = bestCost;
        if (pops != null)
        {
            int n = PalCodec.Size(pops, shorts!);
            res.PalLen = n;
            res.PalCost = n + lambda * TimeNs(StripCodec.Pal, px, n);
        }
        return res;
    }


    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Probe(byte* s, int w, int rows, int ch)
    {
        long stride = (long)w * ch;
        long eq = 0, tot = 0;
        int y0 = rows > 1 ? 1 : 0;
        for (int y = y0; y < rows; y += 4)
        {
            byte* row = s + y * stride;
            long x = ch, n = stride;
            if (y > 0)
                for (; x + 32 <= n; x += 32)
                {
                    var c = Vector256.Load(row + x);
                    var m = Vector256.Equals(c, Vector256.Load(row + x - ch)) | Vector256.Equals(c, Vector256.Load(row + x - stride));
                    eq += BitOperations.PopCount(m.ExtractMostSignificantBits());
                }
            else
                for (; x + 32 <= n; x += 32)
                {
                    var c = Vector256.Load(row + x);
                    eq += BitOperations.PopCount(Vector256.Equals(c, Vector256.Load(row + x - ch)).ExtractMostSignificantBits());
                }
            for (; x < n; x++) if (row[x] == row[x - ch] || (y > 0 && row[x] == row[x - stride])) eq++;
            tot += n - ch;
        }
        return tot > 0 ? (double)eq / tot : 1.0;
    }

    private static readonly Vector128<byte> Rgb3to4P = Vector128.Create((byte)0, 1, 2, 128, 3, 4, 5, 128, 6, 7, 8, 128, 9, 10, 11, 128);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double ProbePx(byte* s, int w, int rows, int ch, bool simd = true)
    {
        simd = Isa.Use(simd);
        long stride = (long)w * ch;
        long eq = 0, tot = 0;
        var sh = Rgb3to4P;
        for (int y = rows > 1 ? 1 : 0; y < rows; y += 4)
        {
            byte* row = s + y * stride;
            int x = 1;
            if (y > 0)
            {
                if (!simd) { }
                else if (ch == 4)
                    for (; x + 8 <= w; x += 8)
                    {
                        var c = Vector256.Load((uint*)row + x);
                        var m = Vector256.Equals(c, Vector256.Load((uint*)row + x - 1)) | Vector256.Equals(c, Vector256.Load((uint*)(row - stride) + x));
                        eq += BitOperations.PopCount(m.ExtractMostSignificantBits());
                    }
                else
                    for (; x + 10 <= w; x += 8)
                    {
                        byte* p = row + x * 3;
                        var c = Vector256.Create(Ssse3.Shuffle(Vector128.Load(p), sh), Ssse3.Shuffle(Vector128.Load(p + 12), sh)).AsUInt32();
                        var l = Vector256.Create(Ssse3.Shuffle(Vector128.Load(p - 3), sh), Ssse3.Shuffle(Vector128.Load(p + 9), sh)).AsUInt32();
                        var u = Vector256.Create(Ssse3.Shuffle(Vector128.Load(p - stride), sh), Ssse3.Shuffle(Vector128.Load(p - stride + 12), sh)).AsUInt32();
                        var m = Vector256.Equals(c, l) | Vector256.Equals(c, u);
                        eq += BitOperations.PopCount(m.ExtractMostSignificantBits());
                    }
            }
            for (; x < w; x++)
            {
                byte* p = row + x * ch;
                bool left = p[0] == p[-ch] && p[1] == p[1 - ch] && p[2] == p[2 - ch] && (ch == 3 || p[3] == p[3 - ch]);
                bool up = y > 0 && p[0] == p[-stride] && p[1] == p[1 - stride] && p[2] == p[2 - stride] && (ch == 3 || p[3] == p[3 - stride]);
                if (left || up) eq++;
            }
            tot += w - 1;
        }
        return tot > 0 ? (double)eq / tot : 1.0;
    }

    public static double ProbeHits(byte* s, int w, int rows, int ch)
    {
        long stride = (long)w * ch;
        uint* cache = stackalloc uint[64];
        for (int i = 0; i < 64; i++) cache[i] = 0;
        long hits = 0, n = 0;
        for (int y = 0; y < rows; y += 16)
        {
            byte* row = s + y * stride;
            uint prev = 0xFFFFFFFFu;
            for (int x = 0; x < w; x++)
            {
                uint v = ch == 4 ? *(uint*)(row + x * 4) : (uint)(row[x * 3] | row[x * 3 + 1] << 8 | row[x * 3 + 2] << 16) | 0xFF000000u;
                if (v == prev) continue;
                prev = v;
                int hh = LiteralOps.Hash(v);
                if (cache[hh] == v) hits++;
                cache[hh] = v;
                n++;
            }
        }
        return n > 0 ? (double)hits / n : 0;
    }
}
