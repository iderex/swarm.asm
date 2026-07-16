# swarm.asm

A Particle Life engine written entirely in hand-written x64 assembly.

**Goal: 1,000,000 interacting particles at 60 fps — no GPU, no dependencies,
one small `.exe`.**

## What

Particle Life is a simple rule set with startlingly lifelike results: N
particle species, an N×N attraction/repulsion matrix, and out of nothing but
pairwise forces emerge cells, swarms, chasers, and self-assembling structures.
Every run with a different matrix is a different ecosystem.

Dozens of Particle Life implementations exist — in C++, Java, JavaScript,
Godot. This one is different in a single way: **the entire simulation kernel
is hand-written x64 assembly** (AVX2 baseline, AVX-512 where the CPU has it),
and the whole program is one small Windows executable that imports nothing but
`kernel32`, `user32`, and `gdi32`. No CRT, no runtime, no framework. The
assembly is the product.

## Status

**Design phase.** The architecture is being fixed in
[docs/MASTERPLAN.md](docs/MASTERPLAN.md) before the first kernel line is
written. Progress is tracked in the issues and milestones:

| Milestone              | Deliverable                                           |
| ---------------------- | ----------------------------------------------------- |
| M0 — Foundation        | Design, pinned toolchain, CI, test harness            |
| M1 — First light (50k) | Brute-force AVX2 kernel + live window, 50k particles  |
| M2 — Scale (500k)      | Spatial grid, 500k particles                          |
| M3 — One million       | Multithreading + AVX-512 path, 1M particles at 60 fps |
| M4 — Launch            | Benchmark suite vs. existing ports, presets, write-up |

## Principles

- **Zero dependencies, verifiably.** A conformance test parses the built
  executable's import table and fails the build if anything beyond
  kernel32/user32/gdi32 appears.
- **Deterministic.** Same seed, same universe — bit-exact per code path.
- **Honest numbers.** Every performance claim ships with hardware, CPU
  features, particle count, and seed. Benchmarks live in the repo.
- **Readable assembly.** Every routine carries a register contract (inputs,
  outputs, clobbers, alignment); comments explain why, not what.

## Building

Windows 10/11 x64. PowerShell:

```powershell
.\build.ps1
```

The build script bootstraps the pinned assembler ([FASM](https://flatassembler.net/))
into `tools/fasm/` on first run — the download is verified against a pinned
SHA-256 before it is unpacked. Output lands in `build/swarm.exe`.

The test harness (from M0 onward) needs the .NET 9 SDK: `dotnet test`.

## Contributing

Issue-driven: every change starts as an issue and lands as a gated PR — see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
