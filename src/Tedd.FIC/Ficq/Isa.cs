using System.Runtime.Intrinsics.X86;

namespace Tedd.FIC;

/// <summary>Hardware and environment gates for optional SIMD codec paths.</summary>
internal static class Isa
{
    public static readonly bool Hardware = Avx2.IsSupported && Bmi2.X64.IsSupported && Bmi1.X64.IsSupported && Sse41.IsSupported && Ssse3.IsSupported && Popcnt.X64.IsSupported;

    public static readonly bool Simd = Hardware && Environment.GetEnvironmentVariable("FIC_SCALAR") != "1";

    /// <summary>Enables a requested SIMD path only when its required CPU features are present.</summary>
    public static bool Use(bool simd) => simd && Hardware;
}
