using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// The determinism half of live per-cell matrix editing (#180). The exe writes
/// an edited coefficient into the arena's validated params copy between two
/// steps and never during one, so an edited session is a replay of its edit
/// log: the state after any frame is a function of the seed and of which step
/// boundaries the edits landed on, and of nothing else.
///
/// The claim is made here rather than in the exe because this is where it can
/// be executed. These cases drive the same kernel through the DLL seam and
/// perform the same write at offset AhParams + SpMatrix that
/// <c>ui_apply_matrix_edits</c> performs, at chosen step boundaries. What they
/// cannot reach is the message handler; that the handler writes no matrix byte
/// of its own is <see cref="MatrixEditBoundaryTests"/>'s subject, and the two
/// together are the argument.
///
/// Every leg is EXACT equality or exact inequality, never epsilon. The same
/// edits at the same boundaries are the same arithmetic in the same order, so
/// a drift here is a real bug rather than a reordering.
///
/// THE BOUND, stated rather than left to be discovered. The exe's own
/// <c>ui_apply_matrix_edits</c> is not executed by anything here: it lives in
/// swarm.exe, which exports nothing, so these cases re-implement its arena
/// write instead of calling it. What they prove is that a matrix edit
/// committed between steps is deterministic and does reach the force pass.
/// That the exe's routine performs that same write - both copies, clamped -
/// is read, not run.
/// </summary>
public sealed unsafe class MatrixEditReplayTests
{
    private const uint FlagGrid = 1;

    // Arena header offsets (abi.inc AH_*, SP_*). The matrix the force pass
    // reads lives inside the arena's params copy, not in the caller's struct.
    private const int AhParams = 208, SpMatrix = 48;

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    private const uint N = 512, Species = 4, Steps = 24;
    private const ulong Seed = 0x9E3779B97F4A7C15;

    /// <summary>One entry of an edit log: the step boundary it commits at, the
    /// cell it moves, and by how many whole steps of <c>edit_step</c>.</summary>
    private readonly record struct Edit(uint AfterStep, int Cell, int Steps);

    // The exe's step per wheel notch (src/swarm.asm, edit_step).
    private const float EditStep = 0.02f;

    private static readonly Edit[] Log =
    [
        new(3, 0 * 8 + 1, +7),    // row 0, column 1: attraction up
        new(3, 2 * 8 + 3, -5),    // two edits committing at the same boundary
        new(11, 1 * 8 + 1, -12),  // a diagonal cell, later in the run
        new(17, 3 * 8 + 0, +4),
    ];

    private static SwarmParams Scene()
    {
        var p = new SwarmParams
        {
            Version = 1, N = N, SpeciesN = Species, Seed = Seed,
            RMax = 0.05f, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f,
            ForceScale = 10f, ForcePath = 0, Flags = FlagGrid,
        };
        // A scene with structure rather than a uniform matrix, so an edit to
        // one cell is not indistinguishable from an edit to another.
        for (uint i = 0; i < Species; i++)
        {
            for (uint j = 0; j < Species; j++)
            {
                p.Matrix[(int)(i * 8 + j)] = i == j ? 0.4f : -0.25f + 0.15f * j;
            }
        }
        return p;
    }

    /// <summary>
    /// The arena-side half of <c>ui_apply_matrix_edits</c>: add
    /// <c>steps * edit_step</c> to one cell, clamp into [-1, 1], store. The
    /// clamp is part of the operation, not a guard around it - a log that
    /// drives a cell past the range must land on the range edge in both runs
    /// or the replay claim would only hold for logs that stay inside it.
    /// </summary>
    private static void ApplyEdit(void* arena, int cell, int steps)
    {
        float* m = (float*)((byte*)arena + AhParams + SpMatrix);
        float v = m[cell] + steps * EditStep;
        m[cell] = MathF.Min(1f, MathF.Max(-1f, v));
    }

    /// <summary>Runs the scene for <paramref name="totalSteps"/> steps,
    /// committing each edit between the steps its entry names, and returns the
    /// final state as one flat array.</summary>
    private static float[] Run(IEnumerable<Edit> log, uint totalSteps = Steps)
    {
        var p = Scene();
        ulong size = swarm_layout_bytes(in p);
        Assert.True(size > 0, "the scene must be valid to size an arena");
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            var pending = log.OrderBy(e => e.AfterStep).ToArray();
            int next = 0;
            for (uint s = 0; s < totalSteps; s++)
            {
                swarm_step(arena, 1);
                // The boundary: the step has returned, the next has not begun.
                while (next < pending.Length && pending[next].AfterStep == s)
                {
                    ApplyEdit(arena, pending[next].Cell, pending[next].Steps);
                    next++;
                }
            }
            Assert.Equal(pending.Length, next); // no entry silently unapplied

            var x = new float[N]; var y = new float[N];
            var vx = new float[N]; var vy = new float[N];
            var sp = new uint[N];
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp));
            return [.. x, .. y, .. vx, .. vy];
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    /// <summary>
    /// The claim: same seed, same log, bit-identical state. Run twice from
    /// scratch, in separate arenas, and compare every float exactly.
    /// </summary>
    [Fact]
    public void SameEditLogReplaysBitIdentically()
    {
        var first = Run(Log);
        var second = Run(Log);

        Assert.Equal(first.Length, second.Length);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(first[i]),
                BitConverter.SingleToUInt32Bits(second[i]));
        }
    }

    /// <summary>
    /// The non-vacuity leg, and the reason the boundary matters. The same
    /// edits moved to different boundaries must produce a DIFFERENT state -
    /// otherwise the equality above would hold for a kernel that ignored the
    /// matrix entirely and would prove nothing about when an edit commits.
    /// </summary>
    [Fact]
    public void TheBoundaryAnEditCommitsAtChangesTheState()
    {
        var baseline = Run(Log);
        var shifted = Run([.. Log.Select(e => e with { AfterStep = e.AfterStep + 2 })]);

        Assert.Equal(baseline.Length, shifted.Length);
        Assert.NotEqual(baseline, shifted);
    }

    /// <summary>
    /// The other non-vacuity leg: an edit must reach the simulation at all.
    /// An empty log against the same seed must differ from the edited one, so
    /// a build in which the arena write went nowhere fails here rather than
    /// passing every equality above.
    /// </summary>
    [Fact]
    public void AnEditReachesTheSimulation()
    {
        var edited = Run(Log);
        var untouched = Run([]);

        Assert.Equal(edited.Length, untouched.Length);
        Assert.NotEqual(edited, untouched);
    }

    /// <summary>
    /// The clamp is the params contract, not a nicety: init validates every
    /// coefficient into [-1, 1], so an edit log that would drive a cell past
    /// the edge has to land ON the edge. Driving one cell far past +1 and the
    /// same cell far past -1 must produce exactly those two values.
    /// </summary>
    [Fact]
    public void EditsClampToTheParamsRange()
    {
        var p = Scene();
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            float* m = (float*)((byte*)arena + AhParams + SpMatrix);

            ApplyEdit(arena, 5, +500);
            Assert.Equal(1f, m[5]);
            ApplyEdit(arena, 5, -5000);
            Assert.Equal(-1f, m[5]);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }
}
