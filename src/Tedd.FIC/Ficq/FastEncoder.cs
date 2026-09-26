using System.Runtime.CompilerServices;

namespace Tedd.FIC;

internal sealed record FastSettings
{
    public static readonly FastSettings Default = new();

    public int Rows { get; init; } = 32;
    public double PenaltyGW { get; init; } = 0.05;
    public int MinStripPixels { get; init; } = Ficq.MinStripPixels;
    public StripCodec? Force { get; init; }
    public bool Oracle { get; init; }
    public bool LegacyLazy { get; init; }
    public double RepT { get; init; } = 0.4;
    public double RepHi { get; init; } = 0.9;
    public double LiteralLo { get; init; } = 1.1;
    public double LiteralHi { get; init; } = 1.5;
    public double PhotoShare { get; init; } = 0.5;
    public bool PhotoRepF { get; init; } = true;
    public double PhotoGwLiteral { get; init; } = 1.0;
    public double PhotoGwF0cRep { get; init; } = 0.6;
    public int PilotMinStrips { get; init; } = 16;
    public int PilotStep { get; init; } = 8;
    public double PilotGain { get; init; } = 0.01;
    public double PhotoGwRep { get; init; } = 0.2;
    public bool GfxEstQ { get; init; } = true;
    public int SampleRows { get; init; } = 2;
    public bool GwAbort { get; init; } = true;
    public bool F0cAbort { get; init; } = true;
    public bool Guarantee { get; init; } = true;
    public bool Simd { get; init; } = Isa.Simd;

    public const int LazyRepStep = 8, LazyEstStep = 16;
}

internal static unsafe class FastEncoder
{
    internal static long[]? Counts;

    private struct StripResult
    {
        public NativePayload Payload;
        public int Slot;
        public uint Crc;
        public double Rep, Est;
        public bool Evidence, GfxEvidence, Done;
        public long GwGain;
        public int NforLen;
    }

    public static byte[] Encode(ReadOnlySpan<byte> pixels, int w, int h, int ch, FastSettings s, int threads)
    {
        var res = EncodeParts(pixels, w, h, ch, s, threads, out var header, out long total, out uint pixCrc);
        try
        {
            var o = GC.AllocateUninitializedArray<byte>((int)total);
            Assemble(header, res, pixCrc, (long)w * h * ch, o);
            return o;
        }
        finally { Free(res); }
    }

    public static bool TryEncode(ReadOnlySpan<byte> pixels, int w, int h, int ch, FastSettings s, int threads, Span<byte> dst, out int written)
    {
        var res = EncodeParts(pixels, w, h, ch, s, threads, out var header, out long total, out uint pixCrc);
        try
        {
            written = 0;
            if (total > dst.Length) return false;
            Assemble(header, res, pixCrc, (long)w * h * ch, dst);
            written = (int)total;
            return true;
        }
        finally { Free(res); }
    }

    private static void Free(StripResult[] res)
    {
        for (int i = 0; i < res.Length; i++) res[i].Payload.Dispose();
    }

    private static void Assemble(byte[] header, StripResult[] res, uint pixCrc, long pixBytes, Span<byte> o)
    {
        header.CopyTo(o);
        int op = header.Length;
        for (int i = 0; i < res.Length; i++) op += Ficq.WriteVarint(o[op..], Ficq.TableEntry(res[i].Payload.Len, res[i].Slot));
        for (int i = 0; i < res.Length; i++) { res[i].Payload.Span.CopyTo(o[op..]); op += res[i].Payload.Len; }
        uint crc = Crc32C.Combine(Crc32C.Append(0, header), pixCrc, pixBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o[op..], crc);
    }

    internal static int StripRows(int w, int h, int rows, int minStripPixels) =>
        Math.Max(Ficq.MinStripRows(w, h), (int)Math.Min(h, Math.Max(rows, ((long)minStripPixels + w - 1) / w)));

    private static StripResult[] EncodeParts(ReadOnlySpan<byte> pixelsSpan, int w, int h, int ch, FastSettings s, int threads,
        out byte[] header, out long total, out uint pixCrc)
    {
        int rows = StripRows(w, h, s.Rows, s.MinStripPixels);
        int N = (h + rows - 1) / rows;
        bool small = (long)w * h < CompactSettings.SmallPixels;
        double pen = small ? 0 : s.PenaltyGW;
        long stride = (long)w * ch;
        var res = new StripResult[N];
        try
        {
            fixed (byte* pixPtr = pixelsSpan)
            {
                nint pix = (nint)pixPtr;
                int alpha = 0;
                if (ch == 4)
                    EncScratch.ForStrips(N, threads, (i, _) =>
                    {
                        if (Volatile.Read(ref alpha) != 0) return;
                        int r = Math.Min(rows, h - i * rows);
                        var span = new ReadOnlySpan<byte>((byte*)pix + i * rows * stride, (int)(r * stride));
                        for (int j = 3; j < span.Length; j += 4) if (span[j] != 255) { Volatile.Write(ref alpha, 1); break; }
                    });
                int P = alpha != 0 ? 4 : 3;
                var marks = new long[(long)N * (rows + 1)];
                EncScratch.ForStrips(N, threads, (i, sc) =>
                {
                    int r = Math.Min(rows, h - i * rows);
                    fixed (long* mk = &marks[(long)i * (rows + 1)])
                        res[i] = First((byte*)pix + i * rows * stride, w, r, ch, P, s, small, sc, mk);
                });
                long evPx = 0;
                for (int i = 0; i < N; i++) if (res[i].GfxEvidence) evPx += (long)w * Math.Min(rows, h - i * rows);
                bool photo = evPx < s.PhotoShare * ((long)w * h);
                void Pass2(int i, EncScratch sc, bool ph, bool pgf)
                {
                    int r = Math.Min(rows, h - i * rows);
                    byte* sp = (byte*)pix + i * rows * stride;
                    fixed (long* mk = &marks[(long)i * (rows + 1)])
                        Second(ref res[i], sp, w, r, ch, P, s, pen, small, ph, pgf, sc, mk);
                    res[i].Crc = Crc32C.Compute(new ReadOnlySpan<byte>(sp, (int)(r * stride)));
                    res[i].Done = true;
                }
                bool pgfOn = true;
                if (!small && s.Force == null && !s.Oracle && !s.LegacyLazy && Ficq.PlanarWidth(w) && N >= s.PilotMinStrips)
                {
                    int step = s.PilotStep, np = (N - step / 2 + step - 1) / step;
                    bool ph0 = photo;
                    EncScratch.ForStrips(np, threads, (j, sc) => Pass2(j * step + step / 2, sc, ph0, true));
                    long gain = 0, nforBytes = 0;
                    for (int j = 0; j < np; j++) { int i = j * step + step / 2; gain += res[i].GwGain; nforBytes += res[i].NforLen; }
                    if (gain < s.PilotGain * nforBytes) { photo = true; pgfOn = false; }
                }
                if (Counts != null) Interlocked.Increment(ref Counts[photo ? 0 : 1]);
                bool ph1 = photo;
                EncScratch.ForStrips(N, threads, (i, sc) => { if (!res[i].Done) Pass2(i, sc, ph1, pgfOn); });
                var hb = new byte[Ficq.MaxHeaderSize];
                int hl = Ficq.WriteHeader(hb, Ficq.TierFast, w, h, ch, P, rows, null, null);
                header = hb.AsSpan(0, hl).ToArray();
                uint crc = 0;
                for (int i = 0; i < N; i++) crc = Crc32C.Combine(crc, res[i].Crc, Math.Min(rows, h - i * rows) * stride);
                pixCrc = crc;
                total = hl + Ficq.CrcSize;
                for (int i = 0; i < N; i++) total += res[i].Payload.Len + Ficq.VarintLength(Ficq.TableEntry(res[i].Payload.Len, res[i].Slot));
            }
            return res;
        }
        catch
        {
            Free(res);
            throw;
        }
    }

    private static long MaxStrip(int w, int r) =>
        Math.Max(Math.Max(BlkCodec.MaxBytes(w, r, 4), QpCodec.MaxEncodedSize(w, r)), LiteralStrip.MaxEncodedSize((long)w * r)) + 64;

    private static StripResult First(byte* sp, int w, int r, int ch, int P, FastSettings s, bool small, EncScratch sc, long* marks)
    {
        var res = new StripResult { Slot = -1 };
        if (s.Force != null || !Ficq.PlanarWidth(w)) return res;
        long stride = (long)w * ch;
        byte* buf = sc.Get(EncScratch.CandA, MaxStrip(w, r));
        int nfor = BlkCodec.Encode(StripCodec.NFor, sp, stride, w, r, ch, P, buf, sc, marks, null, s.Simd);
        res.Payload = NativePayload.Copy(buf, nfor);
        res.Slot = 0;
        res.NforLen = nfor;
        if (small || s.Oracle || s.LegacyLazy) return res;
        res.Rep = RepRate(sp, w, r, ch, FastSettings.LazyRepStep);
        if (res.Rep >= s.RepHi) { res.Est = 0; res.Evidence = res.GfxEvidence = true; return res; }
        int y0 = Math.Max(0, (r - s.SampleRows) / 2), ns = Math.Min(s.SampleRows, r);
        res.Est = (double)LiteralStrip.Size(sp + y0 * stride, w, ns, ch, s.Simd) * r / ns;
        res.Evidence = res.Est < s.LiteralLo * nfor || (res.Rep >= s.RepT && res.Est < s.LiteralHi * nfor);
        res.GfxEvidence = res.Est < s.PhotoGwLiteral * nfor || (res.Rep >= s.PhotoGwRep && res.Est < s.LiteralLo * nfor) || (res.Rep >= s.RepT && res.Est < s.LiteralHi * nfor);
        return res;
    }

    private static void Second(ref StripResult res, byte* sp, int w, int r, int ch, int P, FastSettings s, double pen, bool small, bool photo, bool pgf, EncScratch sc, long* marks)
    {
        long stride = (long)w * ch;
        long px = (long)w * r;
        long maxStrip = MaxStrip(w, r);
        byte* cur = sc.Get(EncScratch.CandA, maxStrip), free = sc.Get(EncScratch.CandB, maxStrip);
        bool simd = s.Simd;
        bool planar = Ficq.PlanarWidth(w);
        int best = -1, bestSlot = 0;
        long bestCost = long.MaxValue;
        bool fromCur = false; // the best payload lives in "cur" (else in res.Payload)
        void Offer(int n, int slot)
        {
            long cost = (long)(n * (1 + (slot == 2 ? pen : 0)) * 1024);
            if (cost < bestCost) { bestCost = cost; best = n; bestSlot = slot; fromCur = true; byte* t = cur; cur = free; free = t; }
        }
        int Blk(StripCodec k, long* abortAt = null) => BlkCodec.Encode(k, sp, stride, w, r, ch, P, free, sc, null, abortAt, simd);
        int Gw(long limit = long.MaxValue) => QpCodec.Encode(QpLayout.GW, new ReadOnlySpan<byte>(sp, (int)(r * stride)), w, r, ch, new Span<byte>(free, (int)maxStrip), limit, sc, simd);
        int Literal() => LiteralStrip.Encode(sp, w, r, ch, free);

        if (s.Force is { } f)
        {
            if (f == StripCodec.NFor && planar) Offer(Blk(StripCodec.NFor), 0);
            else if (f == StripCodec.F0C && planar) Offer(Blk(StripCodec.F0C), 1);
            else if (f == StripCodec.Literal) Offer(Literal(), 3);
            else Offer(Gw(), 2);
        }
        else if (!planar) Offer(Gw(), 2);
        else
        {
            best = res.Payload.Len; bestSlot = 0; bestCost = (long)best * 1024;
            int nfor = best;
            bool tryF, tryG;
            if (s.Oracle || small) tryF = tryG = true;
            else if (s.LegacyLazy) tryF = tryG = RepRate(sp, w, r, ch, FastSettings.LazyRepStep) >= 0.4 || EstQ(sp, w, r, ch, FastSettings.LazyEstStep) < nfor;
            else if (photo)
            {
                tryF = res.Evidence || (s.PhotoRepF && res.Rep >= s.RepT);
                tryG = res.Rep >= s.RepHi || res.Est < s.PhotoGwLiteral * nfor ||
                       (res.Est < s.LiteralLo * nfor && (res.Rep >= s.PhotoGwRep || EstQ(sp, w, r, ch, FastSettings.LazyEstStep) < nfor));
            }
            else tryF = tryG = res.Evidence || res.Rep >= s.RepT || (s.GfxEstQ && EstQ(sp, w, r, ch, FastSettings.LazyEstStep) < nfor);
            if (Counts != null) { Interlocked.Increment(ref Counts[2]); if (tryF) Interlocked.Increment(ref Counts[3]); }
            if (tryF)
            {
                int f0 = Blk(StripCodec.F0C, photo && s.F0cAbort && !small && !s.Oracle ? marks : null);
                if (f0 >= 0) Offer(f0, 1);
                if (photo && pgf && !tryG && res.Rep >= s.PhotoGwF0cRep && (f0 < 0 || f0 >= nfor)) tryG = true;
            }
            if (tryG)
            {
                if (Counts != null) Interlocked.Increment(ref Counts[4]);
                int g = Gw(photo && s.GwAbort && !small && !s.Oracle ? (long)(bestCost / 1024 / (1 + pen)) : long.MaxValue);
                long before = best;
                if (g >= 0) Offer(g, 2);
                if (bestSlot == 2) res.GwGain = before - g;
            }
        }
        if (best > 5 * px && bestSlot != 2)
        {
            int n = Gw();
            if (n < best) { best = n; bestSlot = 2; fromCur = true; byte* t = cur; cur = free; free = t; }
        }
        if (s.Guarantee && bestSlot != 3 && s.Force == null && LiteralStrip.SizeBelow(sp, w, r, ch, best, simd) < best)
        {
            int n = Literal();
            if (Counts != null) Interlocked.Increment(ref Counts[5]);
            best = n; bestSlot = 3; fromCur = true; byte* t = cur; cur = free; free = t;
        }
        if (fromCur)
        {
            res.Payload.Dispose();
            res.Payload = NativePayload.Copy(cur, best);
        }
        res.Slot = bestSlot;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double RepRate(byte* px, int w, int rows, int ch, int step)
    {
        long rs = (long)w * ch;
        long hit = 0, tot = 0;
        uint mask = ch == 3 ? 0xFFFFFFu : 0xFFFFFFFFu;
        for (int y = 1; y < rows; y += step)
        {
            byte* row = px + y * rs;
            byte* up = row - rs;
            uint prev = ch == 3 ? (uint)(row[0] | row[1] << 8 | row[2] << 16) : *(uint*)row;
            uint u0 = ch == 3 ? (uint)(up[0] | up[1] << 8 | up[2] << 16) : *(uint*)up;
            hit += prev == u0 ? 1 : 0;
            int x = 1;
            int fastEnd = ch == 3 ? w - 1 : w;
            for (; x < fastEnd; x++)
            {
                uint v = *(uint*)(row + x * ch) & mask;
                uint u = *(uint*)(up + x * ch) & mask;
                hit += ((ulong)(v ^ prev) * (v ^ u)) == 0 ? 1 : 0;
                prev = v;
            }
            for (; x < w; x++)
            {
                byte* q = row + x * ch, qu = up + x * ch;
                uint v = (uint)(q[0] | q[1] << 8 | q[2] << 16), u = (uint)(qu[0] | qu[1] << 8 | qu[2] << 16);
                hit += (v == prev) | (v == u) ? 1 : 0;
                prev = v;
            }
            tot += w;
        }
        return tot == 0 ? 1.0 : (double)hit / tot;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double EstQ(byte* px, int w, int rows, int ch, int step)
    {
        long rs = (long)w * ch;
        uint* cache = stackalloc uint[64];
        for (int i = 0; i < 64; i++) cache[i] = 0;
        long cost = 0;
        int nrows = 0;
        uint am = ch == 3 ? 0xFF000000u : 0u;
        uint mask = ch == 3 ? 0x00FFFFFFu : 0xFFFFFFFFu;
        for (int y = 0; y < rows; y += step)
        {
            nrows++;
            byte* row = px + y * rs;
            byte* up = y > 0 ? row - rs : row; // row 0: "up" = the row itself (a copy then equals a run: never cheaper)
            uint prev = (ch == 3 ? (uint)(up[0] | up[1] << 8 | up[2] << 16) | am : *(uint*)up);
            int state = 0;
            int yUp = y > 0 ? 1 : 0;
            int xe = ch == 3 ? w - 1 : w; // 4-byte loads stay inside the row
            for (int x = 0; x < w; x++)
            {
                byte* q = row + x * ch, qu = up + x * ch;
                uint v, u;
                if (x < xe) { v = (*(uint*)q & mask) | am; u = (*(uint*)qu & mask) | am; }
                else { v = (uint)(q[0] | q[1] << 8 | q[2] << 16) | am; u = (uint)(qu[0] | qu[1] << 8 | qu[2] << 16) | am; }
                int isRun = v == prev ? 1 : 0;
                int isCopy = (v == u ? 1 : 0) & (isRun ^ 1) & yUp;
                int lit = 1 - isRun - isCopy;
                int ns = isRun | (isCopy << 1);
                cost += (1 - lit) & (ns != state ? 1 : 0);
                state = ns;
                int hh = LiteralOps.Hash(v);
                int hit = cache[hh] == v ? 1 : 0;
                cache[hh] = v;
                int dr = (sbyte)(byte)(v - prev), dg = (sbyte)(byte)((v >> 8) - (prev >> 8)), db = (sbyte)(byte)((v >> 16) - (prev >> 16));
                int drg = dr - dg, dbg = db - dg;
                int diff = ((uint)(dr + 2) | (uint)(dg + 2) | (uint)(db + 2)) < 4u ? 1 : 0;
                int luma = (((uint)(dg + 32) >> 6) | ((uint)(drg + 8) >> 4) | ((uint)(dbg + 8) >> 4)) == 0 ? 1 : 0;
                int w3 = (((uint)(dg + 64) >> 7) | ((uint)(drg + 32) >> 6) | ((uint)(dbg + 32) >> 6)) == 0 ? 1 : 0;
                int sum = w3 + luma + diff;
                int achg = ((v ^ prev) >> 24) != 0 ? 1 : 0;
                int col = 4 - sum + achg * (1 + sum);
                cost += lit * (col + hit * (1 - col));
                prev = v;
            }
        }
        return nrows == 0 ? 0 : (double)cost * rows / nrows;
    }
}
