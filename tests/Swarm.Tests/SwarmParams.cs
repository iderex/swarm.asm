using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swarm.Tests;

/// <summary>
/// 1:1 mirror of the native SwarmParams seam struct (src/kernel/abi.inc):
/// sequential, Pack=4, 304 bytes. Pack=4 is deliberate — it places the u64
/// seed at offset 12, matching the asm layout exactly; the conformance test
/// asserts the marshaled size so drift fails loudly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SwarmParams
{
    public uint Version;
    public uint N;
    public uint SpeciesN;
    public ulong Seed;
    public float RMax;
    public float Beta;
    public float Dt;
    public float Friction;
    public float ForceScale;
    public uint ForcePath;
    public uint Flags;
    public Matrix64 Matrix;
}

/// <summary>Row-major 8x8 f32 attraction matrix (stride 8 regardless of
/// species_n); blittable by construction.</summary>
[InlineArray(64)]
public struct Matrix64
{
    private float _e0;
}
