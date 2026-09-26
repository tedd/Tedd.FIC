using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

internal static class Isa
{
    public static readonly bool Hardware = Avx2.IsSupported && Bmi2.X64.IsSupported && Bmi1.X64.IsSupported && Sse41.IsSupported && Ssse3.IsSupported && Popcnt.X64.IsSupported;

    public static readonly bool Simd = Hardware && Environment.GetEnvironmentVariable("FIC_SCALAR") != "1";

    public static bool Use(bool simd) => simd && Hardware;
}
