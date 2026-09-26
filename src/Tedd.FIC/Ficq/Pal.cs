using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

internal static class PalCodec
{
    public const int MaxK = Ficq.MaxPaletteColours, Classes = Ficq.PaletteClasses;
    public static readonly (int Dy, int Dx)[] Cls = [(1, 0), (1, 1), (1, -1), (2, 0), (0, -2), (0, -3), (0, -4), (1, -2), (1, 2)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Px(ReadOnlySpan<byte> p, int i, int ch) =>
        ch == 4 ? MemoryMarshal.Read<uint>(p[(i * 4)..]) : p[i * 3] | (uint)p[i * 3 + 1] << 8 | (uint)p[i * 3 + 2] << 16 | 0xFF000000u;

    public static (uint[] K, int[] C)? Counts(ReadOnlySpan<byte> px, int n, int ch, ref int stop, int max = MaxK)
    {
        Span<uint> key = stackalloc uint[512];
        Span<int> cnt = stackalloc int[512];
        Span<bool> used = stackalloc bool[512];
        cnt.Clear(); used.Clear();
        int distinct = 0;
        uint last = 0; int lastSlot = -1;
        for (int i = 0; i < n; i++)
        {
            if ((i & 4095) == 0 && Volatile.Read(ref stop) != 0) return null;
            uint v = Px(px, i, ch);
            if (lastSlot >= 0 && v == last) { cnt[lastSlot]++; continue; }
            int h = (int)((v * 0x9E3779B1u) >> 23);
            while (used[h] && key[h] != v) h = (h + 1) & 511;
            if (!used[h])
            {
                if (++distinct > max) { Volatile.Write(ref stop, 1); return null; }
                used[h] = true; key[h] = v;
            }
            cnt[h]++;
            last = v; lastSlot = h;
        }
        var k = new uint[distinct]; var c = new int[distinct];
        int j = 0;
        for (int h = 0; h < 512; h++) if (used[h]) { k[j] = key[h]; c[j++] = cnt[h]; }
        return (k, c);
    }

    public static List<(int Cls, int L)> Parse(ReadOnlySpan<byte> px, int w, int rows, int ch, uint[] pal, Dictionary<uint, byte> idx)
    {
        int n = w * rows;
        var u = new uint[n];
        for (int i = 0; i < n; i++) u[i] = Px(px, i, ch);
        Span<int> dist = stackalloc int[9];
        for (int j = 0; j < 9; j++) dist[j] = Cls[j].Dy * w - Cls[j].Dx;
        var ops = new List<(int, int)>(n / 4 + 16);
        uint prev = pal[0];
        int maxL = QpCodec.EncoderRunCap;
        for (int k = 0; k < n;)
        {
            uint p = u[k];
            int bestL = 0, bestC = -1;
            if (p == prev)
            {
                int L = 1;
                while (k + L < n && L < maxL && u[k + L] == prev) L++;
                bestL = L; bestC = 0;
            }
            for (int j = 0; j < 9; j++)
            {
                int d = dist[j];
                if (d < 1 || k < d || u[k - d] != p) continue;
                int L = 1;
                while (k + L < n && L < maxL && u[k + L] == u[k + L - d]) L++;
                if (L > bestL) { bestL = L; bestC = 1 + j; }
            }
            if (bestL == 0) { ops.Add((-1, idx[p])); k++; prev = p; continue; }
            ops.Add((bestC, bestL));
            k += bestL;
            prev = u[k - 1];
        }
        return ops;
    }

    private static int Leb(int v) { int n = 1; while (v >= 0x80) { v >>= 7; n++; } return n; }

    public static byte[]? Shorts(IEnumerable<List<(int Cls, int L)>> strips, int K)
    {
        int budget = 256 - K - Classes;
        if (budget < Classes) return null;
        var hist = new Dictionary<int, long>[Classes];
        for (int c = 0; c < Classes; c++) hist[c] = new();
        foreach (var ops in strips)
            foreach (var (c, L) in ops) if (c >= 0) { hist[c].TryGetValue(L, out long v); hist[c][L] = v + 1; }
        var shorts = new int[Classes];
        for (int c = 0; c < Classes; c++) shorts[c] = 1;
        budget -= Classes;
        long Cost(int c, int s) { long t = 0; foreach (var kv in hist[c]) t += kv.Value * (kv.Key <= s ? 1 : 1 + Leb(kv.Key - s - 1)); return t; }
        var cur = Enumerable.Range(0, Classes).Select(c => Cost(c, shorts[c])).ToArray();
        while (budget > 0)
        {
            int best = -1; long gain = 0;
            for (int c = 0; c < Classes; c++)
            {
                if (shorts[c] >= 255) continue;
                long g = cur[c] - Cost(c, shorts[c] + 1);
                if (g > gain) { gain = g; best = c; }
            }
            if (best < 0) break;
            shorts[best]++; cur[best] = Cost(best, shorts[best]); budget--;
        }
        return shorts.Select(s => (byte)s).ToArray();
    }

    public static int Emit(List<(int Cls, int L)> ops, byte[] shorts, int K, Span<byte> dst)
    {
        Span<int> start = stackalloc int[Classes];
        int code = K;
        for (int c = 0; c < Classes; c++) { start[c] = code; code += shorts[c] + 1; }
        int o = 0;
        foreach (var (c, L) in ops)
        {
            if (c < 0) { dst[o++] = (byte)L; continue; }
            if (L <= shorts[c]) { dst[o++] = (byte)(start[c] + L - 1); continue; }
            dst[o++] = (byte)(start[c] + shorts[c]);
            int v = L - shorts[c] - 1;
            while (v >= 0x80) { dst[o++] = (byte)(v | 0x80); v >>= 7; }
            dst[o++] = (byte)v;
        }
        return o;
    }

    public static int Size(List<(int Cls, int L)> ops, byte[] shorts)
    {
        int o = 0;
        foreach (var (c, L) in ops) o += c < 0 || L <= shorts[c] ? 1 : 1 + Leb(L - shorts[c] - 1);
        return o;
    }

    public static bool Decode(ReadOnlySpan<byte> data, int s0, Span<byte> dst, int w, int h, int ch, PalTables t, bool simd = true) =>
        ch == 3 ? Run<Rgb>(data, s0, dst, w, h, t, Isa.Use(simd)) : Run<Rgba>(data, s0, dst, w, h, t, Isa.Use(simd));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Run<TPx>(ReadOnlySpan<byte> data, int s0, Span<byte> dstSpan, int w, int h, PalTables tb, bool simd) where TPx : struct, IPx
    {
        int C = TPx.Ch;
        if (s0 < 0 || data.Length < s0 + Ficq.Padding || dstSpan.Length < (long)w * h * C) return false;
        Span<nint> dist = stackalloc nint[16];
        for (int j = 0; j < 9; j++) { long d = (long)Cls[j].Dy * w - Cls[j].Dx; dist[1 + j] = d >= 1 ? (nint)(d * C) : nint.MaxValue; }
        ref byte src = ref MemoryMarshal.GetReference(data);
        ref byte dst = ref MemoryMarshal.GetReference(dstSpan);
        ref uint P = ref MemoryMarshal.GetArrayDataReference(tb.Pal);
        ref uint G = ref MemoryMarshal.GetArrayDataReference(tb.Grp);
        ref nint D = ref MemoryMarshal.GetReference(dist);
        uint px = tb.Pal[0];
        nint s = s0, sEnd = data.Length - Ficq.Padding;
        nint o = 0, oEnd = (nint)w * h * C, oFast = oEnd - 64;
        uint k = (uint)tb.K;
        while (o < oEnd)
        {
            while (o < oFast)
            {
                if (s >= sEnd) return false;
                uint t = Unsafe.Add(ref src, s);
                if (t < k)
                {
                    px = Unsafe.Add(ref P, (nint)t);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), px);
                    o += C;
                    s++;
                    continue;
                }
                uint e = Unsafe.Add(ref G, (nint)t);
                nint L = (nint)(e & 0xFFFF);
                nint sn = s + 1;
                if (L == 0)
                {
                    if ((e & PalTables.Valid) == 0) return false;
                    nint val = Unsafe.Add(ref src, sn++);
                    if (val >= 0x80)
                    {
                        nint b1 = Unsafe.Add(ref src, sn++);
                        if ((nuint)(b1 - 1) >= 0x7F) return false; // at most 2 LEB128 bytes, minimal (a second byte of 1..127)
                        val = (val & 0x7F) | b1 << 7;
                    }
                    L = (nint)((e >> 22) & 255) + 1 + val;
                    if (L > Ficq.MaxRun) return false;
                }
                nint bytes = L * C;
                if (bytes > oFast + 32 - o) break;
                uint c2 = (e >> 16) & 15;
                ref byte q = ref Unsafe.Add(ref dst, o);
                if (c2 == 0)
                {
                    var fv = Vector256.Create(px).AsByte();
                    if (C == 4) for (nint i = 0; i < bytes; i += 32) Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, i), fv);
                    else if (!simd) for (nint i = 0; i < bytes; i += 3) Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, i), px);
                    else
                    {
                        var pat = Avx2.Shuffle(fv, Vector256.Create((byte)0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1));
                        for (nint i = 0; i < bytes; i += 30) Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, i), pat);
                    }
                }
                else
                {
                    nint d = Unsafe.Add(ref D, (nint)c2);
                    if (d > o) return false;
                    if (d >= 32)
                        for (nint i = 0; i < bytes; i += 32)
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, i), Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref q, i - d)));
                    else if (d >= 4)
                        for (nint i = 0; i < bytes; i += C)
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref q, i), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref q, i - d)));
                    else break;
                    px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref q, bytes - C - d));
                    if (C == 3) px |= 0xFF000000u;
                }
                s = sn;
                o += bytes;
            }
            if (o >= oEnd) break;
            {
                if (s >= sEnd) return false;
                uint t = data[(int)s];
                if (t < k)
                {
                    px = tb.Pal[(int)t];
                    for (int b = 0; b < C; b++) dstSpan[(int)o + b] = (byte)(px >> (8 * b));
                    o += C; s++;
                    continue;
                }
                uint e = tb.Grp[(int)t];
                if ((e & PalTables.Valid) == 0) return false;
                long L = e & 0xFFFF;
                int si = (int)s + 1;
                if (L == 0)
                {
                    if (si >= sEnd) return false;
                    long val = data[si++];
                    if (val >= 0x80)
                    {
                        if (si >= sEnd) return false;
                        int b1 = data[si++];
                        if (b1 == 0 || b1 >= 0x80) return false; // minimal: a second byte of 1..127
                        val = (val & 0x7F) | (long)b1 << 7;
                    }
                    L = ((e >> 22) & 255) + 1 + val;
                    if (L > Ficq.MaxRun) return false;
                }
                if (L * C > oEnd - o) return false;
                uint c2 = (e >> 16) & 15;
                int b0 = (int)o, total = (int)(L * C);
                if (c2 == 0)
                {
                    for (int b = 0; b < C; b++) dstSpan[b0 + b] = (byte)(px >> (8 * b));
                    for (int done = C; done < total;) { int n = Math.Min(done, total - done); dstSpan.Slice(b0, n).CopyTo(dstSpan.Slice(b0 + done, n)); done += n; }
                    o += total;
                }
                else
                {
                    nint d = dist[(int)c2];
                    if (d > o) return false;
                    for (int k2 = 0; k2 < total; k2 += (int)d) { int n = Math.Min((int)d, total - k2); dstSpan.Slice(b0 + k2 - (int)d, n).CopyTo(dstSpan.Slice(b0 + k2, n)); }
                    o += total;
                    px = dstSpan[(int)o - C] | (uint)dstSpan[(int)o - C + 1] << 8 | (uint)dstSpan[(int)o - C + 2] << 16 | (C == 4 ? (uint)dstSpan[(int)o - 1] << 24 : 0xFF000000u);
                }
                s = si;
            }
        }
        return s == sEnd;
    }
}
