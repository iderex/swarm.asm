# How swarm.asm works, and what it costs

This is the one document to read top to bottom. It says why the simulation
kernel is hand-written x64 assembly, what the architecture actually is, what
the measurements honestly report, and which popular optimisations were tried
and dropped because a measurement refused them.

Every number below is quoted from [BENCHMARKS.md](BENCHMARKS.md) and names the
section it comes from, so a figure here can always be checked against the run
that produced it. Nothing is claimed that the repository cannot reproduce. Where
a thing is still a goal, it says so.

The design authority is [MASTERPLAN.md](MASTERPLAN.md), which carries the twelve
architecture decisions with their rationale and their rejected alternatives.
This document is the tour, not the specification.

## Why assembly

Particle Life is a small rule set with disproportionate results. Give N species
of particle an N x N matrix of attraction and repulsion coefficients, apply only
pairwise forces inside a radius, and cells, chasers, rings and self-assembling
structures fall out of it. There are dozens of implementations in C++, Java,
JavaScript and Godot.

The premise here is the opposite one. The whole simulation kernel is hand-written
x64 assembly, the whole program is one Windows executable, and the executable
imports nothing but `kernel32`, `user32` and `gdi32`. No CRT, no runtime, no
framework, no GPU. The question the project is asking is what a CPU actually
does when nothing sits between the algorithm and the instruction stream.

That premise only means something if it is enforced, so it is. A conformance
test parses the built executable's import table and fails the build when
anything outside the allowlist appears. Another refuses a CRT
startup. Another walks `src/kernel/` and refuses an API call there. Another
holds the executable to a 64 KB size budget. The constraints table in
[MASTERPLAN.md](MASTERPLAN.md) lists them, and each row is a test rather than a
promise.

The cost of the premise is worth naming too. There is no portability layer, and
a Linux or macOS build would mean writing a second one, which is not an
abstraction the kernel pays for today. There is no scripting surface and no
plugin loader. The engine never touches the network. Those are choices
recorded as non-goals, not gaps waiting to be filled.

## The rules of the world

The world is the unit torus, `[0, 1)` in both axes, and it wraps. Up to 2^20
particles carry a position, a velocity, a species index and an identity. Each
particle feels every other particle inside `rmax`, with a short-range repulsion
below a knee at `beta` and the matrix coefficient above it. Velocity is damped
by a per-step friction factor and clamped per axis, and the step is fixed:
exactly one simulation step per rendered frame, no accumulator and no variable
timestep.

The clamp is not cosmetic. It pins the per-step displacement at or below `rmax`,
which is at or below 0.25, which is under half the torus, and that is what makes
a single wrap correction and the minimum-image convention valid every time. A
few of the decisions in this engine buy no speed at all. They exist to keep an
invariant like that true.

Two rules in the force evaluation are worth reading even if the rest of the
physics is familiar.

The first is how self-interaction is excluded. There is no lane-index compare
and no identity compare anywhere in the kernel. A particle against itself gives
`dx = dy = 0` exactly, so `r2` is exactly `+0`, and the single test `r2 > 0`
removes it along with any genuinely coincident pair. Both contribute zero force
by definition, so one test covers both cases and the hot loop carries no branch
for either.

The second is the order of masking around the divide. The lane validity mask is
computed first, the squared distance of an invalid lane is replaced by 1.0
before the square root, and only then are the root, the reciprocal and the force
computed and masked. Masking after the divide instead would poison every
accumulator on the first frame. Padding elements hold pinned finite zeros for
the same reason: NaN padding would poison the accumulator through the
multiply-add rather than being masked out of it.

Determinism is a contract here. The same seed produces the
same state, bit for bit, on a given code path, and the harness asserts it. The
RNG is splitmix64, owned and seeded in the repository, so nothing about the
initial state depends on a library. MXCSR is pinned to `0x9FC0`, which is
flush-to-zero, denormals-are-zero, all exceptions masked and round-to-nearest,
at every DLL export prologue, at exe init and at every worker thread entry. The
export prologue saves the caller's MXCSR and restores it on the way out, so the
pin does not outlive the call. Poisoning a host runtime's floating-point state
would be a rude thing for a library to do.

## The architecture, in the order it matters

### One arena, no globals

All state lives in a single caller-owned arena. A 512-byte header holds the
magic value, the ABI version, a validated copy of the parameters, the RNG state,
the frame counter, the cached padded count and grid dimension, and the selected
code path. Everything else is the particle arrays. There are no globals in the
kernel at all, which is what makes two arenas stepped in an interleaved order
equivalent to two runs in isolation. A scan enforces the absence, so no reviewer
has to notice it: `KernelSourcePurityScan` walks the kernel sources and refuses
an OS seam, a writable data section, a nondeterministic source such as `rdtsc`
or `rdrand`, the x87 stack, and the approximate-reciprocal instructions.

`swarm_layout_bytes(params)` is a pure size function, so the caller allocates
and the kernel never does. The executable takes one `VirtualAlloc`; the test
harness takes an aligned allocation from .NET. That is also what makes the ABI
checkable, because the layout is a function of the parameters and nothing else.

### Structure of arrays, in two fixed-role banks

Positions, velocities, species and identity are six separate arrays rather than
an array of particle structs. There are two banks of those six. Bank IN is
cell-sorted and read by the step pass; bank OUT is written by the pass and is
the input to the next build. The roles never swap.

Not swapping is the point. A ping-pong layout needs bookkeeping about which bank
is current and a barrier in the middle of a threaded frame; fixed roles delete
both, because the sorted copy is already the double buffer.

Every array is 64-byte aligned, and the count is padded to a multiple of 16 plus
an explicit 16-element tail. The extra tail is not an off-by-one. Rounding to a
multiple of 16 adds no padding at all whenever the count is already a multiple
of 16, and the headline count of 2^20 is exactly that, so "an unmasked load past
the end is always safe" would be false without the tail. Pad elements are
excluded by masks derived from the count, never by sentinel values.

The identity array is the seam that makes the whole test strategy possible. The
assembly kernel and the C# reference oracle differ by a few units in the last
place, so a particle sitting on a cell boundary can bin differently in the two,
the sort permutations then diverge, and an index-wise comparison of the two
worlds misaligns entirely. Carrying the original identity through every
permutation is what lets the oracle re-align and compare the right particle
against the right particle.

### The grid

Brute force is O(n^2) and it does not survive contact with a large count. The
grid replaces it with a uniform spatial partition: `g` is the largest power of
two whose cell size is still at least `rmax`, clamped to `[4, 512]`, so a
smaller radius gives a finer grid and sparser cells. A stable counting sort
reorders the population into cell order, and the force pass then reads only the
3x3 cell neighbourhood around each particle.

Power-of-two is load-bearing. The wrap at the grid edge is an
AND with `g - 1`, and the binning is an exact truncation of the scaled
coordinate; both rely on it.

The sort is stable, which makes the iteration order a pure function of the seed,
the parameters and the step count. Every cross-implementation test in the
harness rests on that, so a faster sort that reordered within a cell would not
be a faster sort, it would be a different engine.

### The fused pass, and why it parallelises for free

Force accumulation and integration are one pass rather than two, so no
per-particle force arrays exist to be written and read back. The pass is a pure
map: each particle's output is a function of the frozen IN bank and the cell
index, and of nothing that the pass itself writes.

That property is what makes threading cheap and honest. Splitting the range in
half and running the halves separately gives exactly the same bits as running
the whole range, which the harness asserts directly, so the parallel version is
not a new numerical regime that needs its own tolerance. The worker pool is
created once, one worker per physical core, with the main thread participating
as worker zero. Simultaneous multithreading is not used, because a divider-bound
loop gains nothing from a second sibling sharing the same divide port. Work is a
static even split of the range with every boundary rounded to a multiple of 16,
which is one 64-byte line of f32, so no output array is falsely shared between
workers.

The result is bit-identical to the serial pass at every worker count, on both
the vector and the scalar path. The harness asserts that, and the
per-thread MXCSR pin is part of why it holds.

### The two force paths

The scalar path is the semantic anchor. It is hand-written x64 that reproduces
the pinned physics exactly, and it is what the vector path is checked against.
It also rejects an out-of-range candidate before the expensive maths, which
matters when reading the benchmark ratios below.

The AVX2 path is the production one, selected automatically when CPUID reports
the feature. It has no early exit: it evaluates the whole force formula for all
eight lanes and masks the out-of-range ones to zero. An AVX-512 path is
designed and its detection exists - `swarm_cpu_paths` reports the feature - but
its body is open work, so `force_path = 2` is refused at init on every host,
one reporting the feature included. That refusal is the honest reading of the
arena's path word: it names the body the pass will run, and until a 16-lane
body exists there is nothing for it to name.

### Rendering and the window

The raster is one 32-bit top-down DIB section and a `BitBlt`. The plot stays
pure and serial, which keeps it golden-testable, and clamping at the framebuffer
edge is treated as a correctness belt.

The window is keyboard-driven and applies every edit at a step boundary, so an
interactive change can never land in the middle of a frame's arithmetic. Space
pauses, R reseeds, M rerolls the matrix, H toggles a view of the species matrix
drawn as coloured cells over the frame, and Esc quits. Because edits commit only
at step boundaries and are carried as frame-numbered events, an interactive
session is by definition a deterministic replay of its own edit log.

Pacing waits on a high-resolution timer to the next frame deadline and skips the
wait when it is already past. There is no vsync, because GDI has none and the
libraries that would provide one are outside the import allowlist, so tearing is
accepted and disclosed here. If the machine falls behind,
the animation slows down and the state sequence does not change: the state after
k frames is a function of the seed, the parameters and k, and of nothing else.

### Input, and what happens when it is wrong

The preset grammar is line-based ASCII with a version line first. Every
parameter is range-validated at parse, and a file that does not validate is
rejected outright rather than partially applied. There is no guessing and no
repair. A parser is the outward-facing surface of a program that otherwise
touches nothing, so it is fuzzed: the pull-request gate runs the entry at 4,000
iterations, and a weekly scheduled job runs the same entry at 5,000,000. Neither
is a second implementation of the fuzz, and a failing run prints the seed that
reproduces the failing iteration where the reader of a red run will see it.

## What the numbers say

The reference machine for every figure below is an AMD Ryzen 9 5950X, Zen 3,
16 cores and 32 threads, on Windows 11. That part reports AVX2 and FMA and no
AVX-512, which is why there is no AVX-512 row anywhere in this repository.
Numbers are per-machine and are not compared across hardware.

### Vectorising bought about 1.85x, not 8x

An 8-wide vector kernel naively ought to be 8x the scalar one. It is not. The
brute-force pass measures 1.72x at 1,024 particles rising to 1.87x at 16,384
(BENCHMARKS, "Baseline"), and the gap is the useful part of the result.

Two things account for it and neither is a memory layout problem. First, the two
paths do not do the same work: at the benchmarked radius only about 0.8% of
candidate pairs are actually in range, and the scalar path skips the rest before
the expensive maths while the vector path computes all of them and masks. The
vector path therefore evaluates the real force roughly a hundred times more
often and still finishes about 1.85x faster. Second, the vector loop is bound by
execution unit throughput rather than by lane width, so widening the lanes is not
where the remaining factor lives.

That second claim was measured rather than reasoned. The force group costs about
3.9 cycles per candidate, roughly 31 cycles per 8-lane group, while the only
loop-carried dependency in it is a 3 to 4 cycle accumulator add (BENCHMARKS,
"The AVX2 force inner loop"). A loop bound by its dependency chain would sit
near that floor; one sitting at eight times the floor is bound by execution unit
throughput. The measurement also puts the cost at about 2.8x the budget line the
masterplan had assumed, which is the kind of thing worth finding before building
on the assumption.

### The grid is the win, by three orders of magnitude

Once the population is cell-sorted, a pass touches the in-range neighbours
instead of every candidate pair. On one core (BENCHMARKS, "The M2 grid"):

- 50,000 particles run the frame in 2.484 ms, about 402 fps, against a projected
  brute-force frame of 1,977 ms. That is roughly 790x.
- 500,000 particles run the frame in 26.982 ms, about 37 fps, against a
  projected brute-force frame of about 198 seconds. That is roughly 7,300x.

The projections are candidate pairs divided by the measured vector throughput,
because the brute-force frame at those counts is not something anyone runs.

Two later refinements are recorded separately so their contributions stay
attributable. Resolving the 3x3 neighbour run set once per cell instead of once
per particle took about 12% off the 500,000-particle pass at the finer grid
(BENCHMARKS, "Grid pass after resolving the run set once per cell"), and
narrowing the copy from the whole bank to the pad tail took 15% to 31% off the
build (BENCHMARKS, "Grid build after the pad-only copy"). Both are recorded with
whole-arena hash comparisons showing the output did not move.

### Threading closed 500,000 particles

The pass across the pool scales 1.93x, 3.93x and 7.71x at two, four and eight
workers, and 12.61x at sixteen (BENCHMARKS, "The M3 worker pool"). At sixteen
workers the pass is 4.979 ms and the frame, carrying the worst-case build, is
15.487 ms. That is 64.6 fps inside a 16.67 ms budget, so 500,000 particles at
60 fps is a measured result.

Scaling to eight cores is close to ideal and then tapers. The ninth through
sixteenth cores turn 7.71x into 12.61x, because the working set starts crossing
the fabric between the two core complexes and the pass becomes partly
bandwidth-bound. That was flagged as a risk in the design and it behaved as
flagged. It is a scaling effect and not a correctness one: the state stays
bit-identical at every worker count.

### The live window at the M1 count

The only figure in this repository taken from the shipped executable rather than
from a harness is the live work window at 8,192 particles. `swarm.exe -capture`
runs the normal paced loop and records the step, plot and blit of each frame,
never the pacing wait, then writes 3,600 raw samples to a file that carries its
own scene description in the header.

Across six captures the worst p99 is 6.100 ms against the 16.67 ms budget, and
the worst single frame anywhere in the six is 14.244 ms, still inside one frame
period (BENCHMARKS, "The M1 live frame at 8,192"). The claim is stated on the
worst of the six readings, and the host was not quiesced, which is
visible in a factor-of-two spread between runs of an identical binary on an
identical scene.

The more useful reading from that capture is that at 8,192 the window is not
force-bound. Quadrupling the candidate neighbours per particle moves the window
by well under a millisecond, so at least 1.5 ms of it does not depend on the
interaction radius at all. What that fixed part is made of is not decomposed:
the clear, the plot and the blit are one window in this instrument, and taking
them apart needs a finer one. That reading also does not transfer upward, since
at 500,000 and beyond the force pass dominates again.

### One million, honestly

The headline is 1,000,000 particles at 60 fps, and it is still a goal.

What has been measured is the kernel at 1,048,576 particles (BENCHMARKS, "The 1M
baseline"). On the headline scene, serial on one core, three runs of the harness
report frames of 71.700, 80.814 and 80.575 ms. Across sixteen workers the same
three runs report 13.815, 15.410 and 15.535 ms, against a 16.67 ms budget. On
the denser scene the worst is 30.033 ms, which is roughly 1.8x over. Three whole
runs are printed because the spread between them is wider than
several of the differences a reader would otherwise draw, and every reading is
taken on the worst of the three.

Three things keep that from being the headline claim, and all three are stated
where the numbers are.

Those figures are the build and the pass at the library seam. A frame a person
waits for also plots and blits a million particles, and none of that is in them.
They are also minima over nine rounds, which is the right primitive for
comparing two kernels and the wrong one for a frame-rate claim, because a budget
is met at p99 or it is not met. And 15.535 ms of best-case work against a 16.67
ms budget leaves 1.1 ms for everything the minimum excluded, which is not a
margin to spend in advance.

So the honest statement is that the kernel reaches the budget at the seam on the
headline scene, and that the product claim needs an end-to-end capture that does
not exist yet. The README says goal, and it will keep saying goal until the
capture exists.

## What was measured and declined

Performance work here is gated on measurement in both directions, which means
some of it ends in a refusal. Three cases are worth reading, because the
reasoning is more transferable than the wins.

### The reciprocal square root trick

The obvious lever on a loop carrying a square root and a divide is to replace
both with a fast reciprocal-square-root estimate and a Newton-Raphson
refinement. The design for it existed and was gated in advance on the divide
unit turning out to be more than about 90% of the loop.

The measurement says it is roughly half. About 31 cycles per group against a
published divide-pipe throughput of roughly 11 to 15 for the same pair of
instructions leaves the other half on the ordinary floating-point ports, where
some 33 non-divide operations per group are co-limiting. A transform that
removes divide-pipe pressure while adding four or five operations to the ports
that are already busy is not obviously a win, and the estimate for it was
optimistic at the top of its range. The lever was declined.

The honest caveat travels with it: that split rests on published throughput
tables rather than on a per-port measurement, and isolating it properly would
need hardware counters or a stubbed-out kernel variant. It is recorded as
unconfirmed and pointing optimistic, and nothing here treats it as settled.

### Software-pipelining the force groups

The paired lever was to interleave two force groups by hand to hide the latency
of the square root and divide chain. The rule written down in advance was that
it would be tried if the loop cost more than 14 cycles per candidate.

It costs 3.9. More to the point, software pipelining hides latency, and this
loop is not latency-bound; interleaving two groups cannot lift a ceiling the
execution units have already set, and it would spend registers to do it. Also
declined.

Both of these were declined because a rule written before the measurement said
what the measurement would have to show, and it did not show it. That is the
part worth copying.

### Two risks probed, one triggered

The design recorded a set of risks to check empirically, and two of them have
been probed at a million particles.

The serial grid build was estimated at 8 to 12 cycles per particle on
near-sorted input, with anything materially above 4.5 ms called an erosion of
the frame margin. Measured, it is 7.049 ms and about 32.9 cycles per particle,
which misses in both currencies, and the growth from 500,000 to 1,048,576 is
worse than linear at a fixed grid dimension, so it is the scatter that grows and
not the grid-sized part (BENCHMARKS, "The serial grid build at 1M"). That
risk's contingency, a parallel scatter with per-thread per-bucket cursors, is
therefore authorised by the number that triggered it. Nobody's preference
entered into it.

The neighbouring risk said an energetic scene would degrade the scatter's write
locality, and its probe compared a calm scene, the usual one, and an adversarial
one with every matrix coefficient at full magnitude. The share of velocity
components sitting at the clamp across the three is 0.1%, 64.0% and 97.8%, so
the hostile scene is genuinely hostile. The predicted degradation does not
appear: the worst adversarial build is below the worst build of either other
scene, and every difference between scenes is smaller than the calm scene's own
run-to-run spread (BENCHMARKS, "Scatter locality under an energetic scene").
That contingency is not authorised, and the document says so instead of keeping
it warm.

A hypothesis for why the hostile scene is the steadiest one, offered as a
hypothesis: clustering concentrates the writes into fewer distinct cells, which
is better locality and not worse. The probe did not measure per-cell
occupancy, so that is where the sentence stops.

## Reproducing any of this

The engine builds with the assembler the repository bootstraps and verifies
against a pinned hash, and nothing else:

```powershell
.\build.ps1
```

That produces `build/swarm.exe` and `build/swarm.kernel.dll`. Both are assembled
from the same kernel sources, so the tested kernel is the shipped kernel rather
than a copy of it.

The tests need the .NET 9 SDK, and they load the freshly built DLL through
P/Invoke:

```powershell
dotnet test tests\Swarm.Tests\Swarm.Tests.csproj
```

The benchmark harness is in the same tree and is deliberately dependency-free:

```powershell
dotnet run -c Release --project tests\Swarm.Bench\Swarm.Bench.csproj
```

[BENCHMARKS.md](BENCHMARKS.md) explains why that harness is a hand-rolled
minimum-of-rounds loop rather than a standard benchmarking package, records the
methodology for every table, and carries the machine, the feature path, the
scene, the seed, the commit and the date beside each one. If a number here and a
number there disagree, that document is the authority and this one is wrong.
