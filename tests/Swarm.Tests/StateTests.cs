using System.Runtime.InteropServices;
using Xunit;

namespace Swarm.Tests;

/// <summary>
/// swarm_read_state is the id-ordered copy-out that keeps the arena opaque to
/// consumers (masterplan decision 5). These tests pin it against the seeded
/// init state: right after init id[i] = i, so the copy is the identity, and
/// the returned arrays must equal the same splitmix64 oracle the init test
/// uses. It is also the seam every kernel golden reads through, so getting the
/// scatter and the n-boundary right now is load-bearing.
///
/// The scatter index is also a security boundary: copy_scatter writes
/// dst[id[i]] into the CALLER's arrays, so an id outside [0, n) is an
/// out-of-bounds write across the P/Invoke seam that no caller-side bound can
/// prevent. Two tests below hold that shut from both sides - the guard rejects
/// an out-of-range id (issue #86), and id_out is proven to stay a permutation
/// of [0, n) on every path, which is what keeps the guard unreachable.
///
/// In the pool collection because IdOutStaysAPermutation drives the worker pool
/// on its threaded cases: pool_storage is process-global mutable state, so a
/// pool_init/pool_shutdown here must never overlap ThreadingTests' own. The
/// whole class joins the collection rather than the two cases, because xUnit
/// serialises at collection granularity - and the suite runs in about two
/// seconds, so the lost parallelism costs nothing worth measuring.
/// </summary>
[Collection(PoolCollection.Name)]
public sealed unsafe class StateTests
{
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_init(void* arena, ulong arenaBytes, in SwarmParams p);

    // i32, per the export table in docs/MASTERPLAN.md: 0 = every id was in
    // range, 1 = at least one was rejected and its store dropped.
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_read_state(
        void* arena, float[] x, float[] y, float[] vx, float[] vy, uint[] species);

    // The same export, declared over raw destination pointers. copy_scatter is a
    // dword copy, so typing the four f32 components as u32 is bit-exact and lets
    // the guard tests compare exact canary words instead of float payloads.
    [DllImport("swarm.kernel.dll", EntryPoint = "swarm_read_state")]
    private static extern int swarm_read_state_raw(
        void* arena, uint* x, uint* y, uint* vx, uint* vy, uint* species);

    [DllImport("swarm.kernel.dll")]
    private static extern ulong swarm_layout_bytes(in SwarmParams p);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step(void* arena, uint nSteps);

    // The M3 pool seam, so the permutation invariant is checked on the threaded
    // frame too and not only on the serial one.
    [DllImport("swarm.kernel.dll")]
    private static extern int swarm_pool_init(int requested);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_step_mt(void* arena, uint nSteps);

    [DllImport("swarm.kernel.dll")]
    private static extern void swarm_pool_shutdown();

    private const uint FlagGrid = 1;

    // Arena geometry the white-box tests index by hand (abi.inc): a 512-byte
    // header, then the padded_n-element component arrays of bank OUT.
    private const long ArenaHeaderBytes = 512;
    private const uint IdOutComponent = 5; // AR_ID_OUT

    private static uint PaddedN(uint n) => ((n + 15u) & ~15u) + 16u;

    private static SwarmParams Params(uint n, uint species, ulong seed, float rmax = 0.05f, uint flags = 0)
    {
        var p = new SwarmParams
        {
            Version = 1, N = n, SpeciesN = species, Seed = seed,
            RMax = rmax, Beta = 0.3f, Dt = 0.02f, Friction = 0.71f, ForceScale = 10f,
            ForcePath = 0, Flags = flags,
        };
        for (int i = 0; i < species * species; i++) p.Matrix[i] = 0.1f;
        return p;
    }

    [Theory]
    [InlineData(1u, 1u, 7UL)]
    [InlineData(100u, 3u, 0xABCDUL)]
    [InlineData(4096u, 8u, 0xDEADBEEFUL)]
    public unsafe void ReadStateReturnsSeededStateInIdOrder(uint n, uint species, ulong seed)
    {
        _ = NativeKernel.Handle;
        var p = Params(n, species, seed);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            var x = new float[n];
            var y = new float[n];
            var vx = new float[n];
            var vy = new float[n];
            var sp = new uint[n];
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp)); // every id in range

            var rng = new TestOracle.SplitMix64(seed);
            for (uint i = 0; i < n; i++)
            {
                var (ex, ey, es) = TestOracle.DrawParticle(rng, species);
                // id[i] = i right after init, so the copy-out is the identity.
                Assert.Equal(ex, x[i]);
                Assert.Equal(ey, y[i]);
                Assert.Equal(es, sp[i]);
                Assert.Equal(0f, vx[i]);
                Assert.Equal(0f, vy[i]);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public unsafe void ReadStateHonorsIdPermutation()
    {
        // After init id[i] = i, so the copy is the identity and a scatter that
        // ignored id would still pass. Write a non-identity permutation into
        // id_out (white-box) so the indirection is actually exercised now,
        // before sorts (M2) produce real permutations.
        _ = NativeKernel.Handle;
        const uint n = 8, species = 2;
        var p = Params(n, species, 0x55);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            uint padded = PaddedN(n);
            long stride = padded * 4L;
            var xOut = (float*)((byte*)arena + 512);
            var yOut = (float*)((byte*)arena + 512 + stride);
            var vxOut = (float*)((byte*)arena + 512 + 2 * stride);
            var vyOut = (float*)((byte*)arena + 512 + 3 * stride);
            var spOut = (uint*)((byte*)arena + 512 + 4 * stride);
            var idOut = (uint*)((byte*)arena + 512 + 5 * stride);

            // Reverse permutation, and a DISTINCT marker per component so a bug
            // that swapped destination pointers (e.g. vx vs vy) is caught too.
            for (uint i = 0; i < n; i++)
            {
                idOut[i] = n - 1 - i;
                xOut[i] = 1000f + i;
                yOut[i] = 2000f + i;
                vxOut[i] = 3000f + i;
                vyOut[i] = 4000f + i;
                spOut[i] = 5000u + i;
            }

            var x = new float[n];
            var y = new float[n];
            var vx = new float[n];
            var vy = new float[n];
            var sp = new uint[n];
            // A reverse permutation is entirely in range, so the status is 0:
            // the bound rejects nothing legal, however far it moves an element.
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp));

            // slot i's markers land at caller index id[i] = n-1-i, each in its
            // own destination array.
            for (uint i = 0; i < n; i++)
            {
                uint dst = n - 1 - i;
                Assert.Equal(1000f + i, x[dst]);
                Assert.Equal(2000f + i, y[dst]);
                Assert.Equal(3000f + i, vx[dst]);
                Assert.Equal(4000f + i, vy[dst]);
                Assert.Equal(5000u + i, sp[dst]);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    [Fact]
    public unsafe void ReadStateWritesExactlyNElements()
    {
        _ = NativeKernel.Handle;
        var p = Params(100, 3, 0x1234);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));

            // One guard slot past n on each array: read_state must not touch it.
            const int n = 100;
            var x = new float[n + 1];
            var y = new float[n + 1];
            var vx = new float[n + 1];
            var vy = new float[n + 1];
            var sp = new uint[n + 1];
            x[n] = y[n] = vx[n] = vy[n] = float.NaN;
            sp[n] = 0xDEADBEEF;
            Assert.Equal(0, swarm_read_state(arena, x, y, vx, vy, sp));

            Assert.True(float.IsNaN(x[n]), "read_state wrote past n (x)");
            Assert.True(float.IsNaN(y[n]));
            Assert.True(float.IsNaN(vx[n]));
            Assert.True(float.IsNaN(vy[n]));
            Assert.Equal(0xDEADBEEFu, sp[n]);
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    // --- the id bound (issue #86) --------------------------------------------

    private const uint GuardN = 8;          // particles in the guard tests
    private const uint GuardSlack = 64;     // owned dwords past n on each destination
    private const uint Canary = 0xFEEDFACE; // must never be overwritten past n

    // A per-component, per-slot marker, distinct from the canary, so a scatter
    // that swapped destinations or slots is caught alongside the bound itself.
    private static uint Marker(uint component, uint slot) => 0x00010000u * (component + 1) + slot;

    /// <summary>
    /// The negative test for the fail-closed branch: an id_out entry outside
    /// [0, n) must not be stored. Unguarded, copy_scatter computes
    /// dst + id*4 unconditionally, so the write lands past the end of the
    /// caller's array - a silent heap corruption across the P/Invoke seam
    /// (issue #86). The out-of-range ids here are chosen just past n so that,
    /// against an unguarded build, the errant store lands inside slack this test
    /// owns: the pre-fix failure is observable AND contained.
    ///
    /// Slot 0 carries the bad id; slots 1..n-1 keep the identity, so the same
    /// run also pins that the guard rejects nothing legal - including id = n-1,
    /// the last in-range value, which sits immediately below the fence.
    /// </summary>
    [Theory]
    [InlineData(GuardN)]                  // the fence itself: the first id past the end
    [InlineData(GuardN + 1)]
    [InlineData(GuardN + GuardSlack - 1)] // the far end of the owned slack
    public void ReadStateRejectsIdOutsideRange(uint badId) => AssertBadIdIsRejected(badId);

    /// <summary>
    /// The bound is an UNSIGNED comparison, so an id with the high bit set - or
    /// the all-ones word - is rejected by the same branch rather than being
    /// treated as a small negative displacement. These two values are asserted
    /// against the guarded build only: running them unguarded is a multi-gigabyte
    /// wild store, so the red-before-green evidence for this branch is
    /// <see cref="ReadStateRejectsIdOutsideRange"/>, which drives the identical
    /// compare with a containable operand.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void ReadStateRejectsIdWithHighBitSet(uint badId) => AssertBadIdIsRejected(badId);

    private void AssertBadIdIsRejected(uint badId)
    {
        _ = NativeKernel.Handle;
        const uint n = GuardN, components = 5, span = GuardN + GuardSlack;
        var p = Params(n, species: 2, seed: 0x99);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        // Five destination buffers of n + slack dwords, laid out back to back and
        // filled with the canary. read_state may write only [0, n) of each.
        uint* dst = (uint*)NativeMemory.Alloc(components * span * sizeof(uint));
        try
        {
            for (uint i = 0; i < components * span; i++)
            {
                dst[i] = Canary;
            }

            Assert.Equal(0, swarm_init(arena, size, in p));

            uint padded = PaddedN(n);
            var outBase = (uint*)((byte*)arena + ArenaHeaderBytes);
            uint* idOut = outBase + IdOutComponent * padded;

            // The geometry above is white-box knowledge duplicated from abi.inc.
            // If a layout change ever moved id_out, this poke would land in some
            // other component and the whole test would pass vacuously - it would
            // no longer be driving an out-of-range id at all. Prove the pointer
            // really is id_out first: init seeds it with the identity over
            // [0, n) and continues n, n+1, ... through the pads.
            for (uint i = 0; i < n; i++)
            {
                Assert.True(idOut[i] == i, $"id_out is not where abi.inc says it is: id_out[{i}] = {idOut[i]}");
            }
            Assert.Equal(n, idOut[n]); // the pad slot, which is exactly an out-of-range id

            for (uint i = 0; i < n; i++)
            {
                for (uint c = 0; c < components; c++)
                {
                    outBase[c * padded + i] = Marker(c, i);
                }
            }
            idOut[0] = badId;

            int status = swarm_read_state_raw(arena, dst, dst + span, dst + 2 * span, dst + 3 * span, dst + 4 * span);

            // Reported, not silent: a dropped store the caller cannot see is the
            // same failure mode this guard exists to remove, moved up a layer.
            Assert.True(status != 0, $"read_state returned {status}: id {badId} was rejected but never reported");

            for (uint c = 0; c < components; c++)
            {
                uint* d = dst + c * span;
                for (uint j = n; j < span; j++)
                {
                    Assert.True(
                        Canary == d[j],
                        $"component {c}: read_state wrote {d[j]:X8} at caller index {j} (n = {n}) " +
                        $"- id {badId} escaped the bound");
                }

                // Nothing maps to caller index 0 once slot 0's id is rejected, so
                // it stays canary: the guard drops the store, it does not relocate it.
                Assert.Equal(Canary, d[0]);

                // Every legal id still lands, id = n-1 included.
                for (uint i = 1; i < n; i++)
                {
                    Assert.Equal(Marker(c, i), d[i]);
                }
            }
        }
        finally
        {
            NativeMemory.Free(dst);
            NativeMemory.AlignedFree(arena);
        }
    }

    /// <summary>
    /// The invariant copy_scatter's guard backstops: id_out[0..n) is a
    /// permutation of [0, n) - every value in range, none repeated - after init
    /// and after stepping, on the brute path and on the grid path whose counting
    /// sort actually permutes it. This is the property that keeps the guard
    /// unreachable rather than merely survivable, so a future sort that leaks a
    /// pad slot (init fills id[n..padded_n) with n, n+1, ...) or drops a real one
    /// fails here instead of degrading read_state's output (issue #86).
    ///
    /// The threaded frame is swept too (threads &gt; 0): the pool partitions
    /// [0, n) across workers that each copy id IN -&gt; OUT for their own range, so
    /// a partition bug that dropped or duplicated a range would break the
    /// permutation. That path is not otherwise covered here - the existing
    /// threading gate builds serially in both arms, so it compares the parallel
    /// pass against the serial pass rather than exercising the MT id path
    /// independently.
    /// </summary>
    [Theory]
    [InlineData(1u, 1u, 7UL, 0u, 0)]
    [InlineData(100u, 3u, 0xABCDUL, 0u, 0)]
    [InlineData(4096u, 8u, 0xDEADBEEFUL, 0u, 0)]
    [InlineData(1u, 1u, 7UL, FlagGrid, 0)]
    [InlineData(100u, 3u, 0xABCDUL, FlagGrid, 0)]
    [InlineData(4096u, 8u, 0xDEADBEEFUL, FlagGrid, 0)]
    [InlineData(4096u, 8u, 0xDEADBEEFUL, 0u, 4)]        // threaded, brute
    [InlineData(4096u, 8u, 0xDEADBEEFUL, FlagGrid, 4)]  // threaded, grid sort
    public void IdOutStaysAPermutation(uint n, uint species, ulong seed, uint flags, int threads)
    {
        _ = NativeKernel.Handle;
        var p = Params(n, species, seed, flags: flags);
        ulong size = swarm_layout_bytes(in p);
        void* arena = NativeMemory.AlignedAlloc((nuint)size, 64);
        try
        {
            Assert.Equal(0, swarm_init(arena, size, in p));
            uint* idOut = (uint*)((byte*)arena + ArenaHeaderBytes) + IdOutComponent * PaddedN(n);

            AssertPermutation(idOut, n, "after init");
            if (threads > 0)
            {
                Assert.True(swarm_pool_init(threads) > 0);
                try { swarm_step_mt(arena, 4); }
                finally { swarm_pool_shutdown(); }
            }
            else
            {
                swarm_step(arena, 4);
            }

            AssertPermutation(idOut, n, "after 4 steps");

            // Non-vacuity: on the grid path the counting sort must actually have
            // reordered the population, or this case would pin the invariant
            // against an untouched identity and pass for the wrong reason. One
            // differing element would clear a bar this weak, so require that the
            // sort moved most of the population: with g*g cells over a uniform
            // random seeding, cell order and seed order are unrelated, and a real
            // sort leaves only a handful of accidental fixed points. The bar
            // applies from n = 100 up because a single particle cannot permute -
            // the n = 1 grid case is here for the degenerate path, not for
            // reordering, and widening the check to it would fail correctly-
            // behaving code.
            if (flags == FlagGrid && n >= 100)
            {
                uint fixedPoints = 0;
                for (uint i = 0; i < n; i++)
                {
                    if (idOut[i] == i) fixedPoints++;
                }

                Assert.True(
                    fixedPoints < n / 2,
                    $"the grid sort left {fixedPoints} of {n} ids in place - this case barely exercised a permutation");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(arena);
        }
    }

    private static void AssertPermutation(uint* id, uint n, string when)
    {
        var seen = new bool[n];
        for (uint i = 0; i < n; i++)
        {
            uint v = id[i];
            Assert.True(v < n, $"{when}: id[{i}] = {v} is outside [0, {n}) - copy_scatter would write past the caller's array");
            Assert.False(seen[v], $"{when}: id value {v} occurs more than once (id[{i}])");
            seen[v] = true;
        }
    }
}
