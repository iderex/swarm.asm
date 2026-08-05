using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// What <c>pass_core</c> does with an <c>AH_PATH</c> word outside the resolved
/// set (issue #202).
///
/// The dispatch used to decide by exclusion: anything that was not
/// <c>PATH_SCALAR</c> went to the vector body. That routed <c>PATH_AUTO</c>,
/// and any word that never came from a resolution at all, into the one
/// direction that can execute an instruction the CPU may not have. Nothing
/// crashed, but only because two invariants held elsewhere in the tree: the
/// accept gate in <c>pp_validate_params</c> bounds the field, and
/// <c>cpuid.inc</c> cannot report AVX-512 without AVX2. Neither is stated at
/// the dispatch, and neither is this test's subject.
///
/// The rule now: the vector ids are named and everything else falls to the
/// reference path. These tests write the header word directly, which is the
/// only way to reach the case, because every public entry point resolves the
/// field before it is stored.
/// </summary>
public sealed unsafe class PathDispatchTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    private const int AhPath = 12;          // abi.inc AH_PATH
    private const uint PathScalar = 3;      // abi.inc PATH_SCALAR

    // Every id the field can hold that is not a vector path, plus words no
    // resolution produces. PATH_AUTO is the one that matters most: it is a
    // legal PATH_* value that the header is documented never to hold, so it is
    // exactly the word a future bug would leave behind.
    [Theory]
    [InlineData(0u)]           // PATH_AUTO
    [InlineData(4u)]           // one past the id set
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void UnrecognisedArenaPathRunsTheReferenceBody(uint poked)
    {
        _ = NativeKernel.Handle;
        var p = Params(forcePath: PathScalar);

        var reference = RunSteps(p, poke: null);
        var poked_ = RunSteps(Params(forcePath: 0), poke: poked);

        // Bit-for-bit, not within an epsilon: the same body on the same state
        // has to produce the same bytes, and an epsilon here would hide the
        // vector body being reached instead.
        Assert.Equal(reference.X, poked_.X);
        Assert.Equal(reference.Y, poked_.Y);
        Assert.Equal(reference.Vx, poked_.Vx);
        Assert.Equal(reference.Vy, poked_.Vy);
    }

    private static (float[] X, float[] Y, float[] Vx, float[] Vy) RunSteps(
        SwarmParams p, uint? poke, uint steps = 8)
    {
        ulong size = swarm_layout_bytes(in p);
        Assert.True(size > 0, "params must be valid to size the arena");
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            if (poke is uint word)
            {
                Unsafe.WriteUnaligned((byte*)arena + AhPath, word);
            }

            swarm_step(arena, steps);

            int n = (int)p.N;
            var x = new float[n];
            var y = new float[n];
            var vx = new float[n];
            var vy = new float[n];
            var sp = new uint[n];
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp));
            return (x, y, vx, vy);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    private static SwarmParams Params(uint forcePath)
    {
        var p = new SwarmParams
        {
            Version = 1, N = 512, SpeciesN = 4, Seed = 0x5EED,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = forcePath, Flags = 0,
        };
        // Varied entries in [-1, 1] so the matrix actually steers the result;
        // a uniform matrix would make two different bodies agree too easily.
        for (int i = 0; i < 4 * 4; i++)
        {
            p.Matrix[i] = ((i * 37) % 21 - 10) / 10f;
        }

        return p;
    }
}
