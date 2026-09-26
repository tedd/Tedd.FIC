using System.Runtime.CompilerServices;

namespace Tedd.FIC;

internal interface IPx { static abstract int Ch { get; } }
internal struct Rgb : IPx { public static int Ch => 3; }
internal struct Rgba : IPx { public static int Ch => 4; }

internal static class LiteralOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(uint v)
    {
        ulong w = (v & 0x00FF00FFu) | ((ulong)(v & 0xFF00FF00u) << 24);
        return (int)((w * 0x0003_0007_0005_000BUL) >> 48) & 63;
    }

    public static int HashRef(uint rgba) =>
        (int)(((rgba & 0xff) * 3 + ((rgba >> 8) & 0xff) * 5 + ((rgba >> 16) & 0xff) * 7 + (rgba >> 24) * 11) & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Swar(uint a, uint b) => ((a & 0x7F7F7F7Fu) + (b & 0x7F7F7F7Fu)) ^ ((a ^ b) & 0x80808080u);

    public static readonly uint[] DeltaFirst = BuildDeltaFirst();

    public static readonly uint[] DeltaLuma2 = BuildDeltaLuma2();

    private static uint[] BuildDeltaFirst()
    {
        var d = new uint[256];
        for (int t = 0x40; t < 0x80; t++)
        {
            uint dr = (uint)(((t >> 4) & 3) - 2) & 0xFF, dg = (uint)(((t >> 2) & 3) - 2) & 0xFF, db = (uint)((t & 3) - 2) & 0xFF;
            d[t] = dr | dg << 8 | db << 16;
        }
        for (int t = 0x80; t < 0xC0; t++)
        {
            int g = (t & 63) - 32;
            uint rb = (uint)(g - 8) & 0xFF, gg = (uint)g & 0xFF;
            d[t] = rb | gg << 8 | rb << 16;
        }
        return d;
    }

    private static uint[] BuildDeltaLuma2()
    {
        var d = new uint[256];
        for (int b = 0; b < 256; b++) d[b] = (uint)(b >> 4) | (uint)(b & 15) << 16;
        return d;
    }
}
