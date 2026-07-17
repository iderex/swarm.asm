# Contributing

## Process

Issue-driven, gate-driven:

1. Every change — feature, fix, perf work, docs — starts as a **GitHub issue**
   with a type, `area:` and `priority:` label and a milestone.
2. Work happens on a short-lived branch off `main`:
   `feature/…`, `fix/…`, `perf/…`, `harden/…`, `chore/…`, `refactor/…`.
3. The PR fills the template honestly and references the issue (`Closes #N`).
   PRs merge with a merge commit once CI and review are green.

## Code standard

- **Assembly is readable.** Every routine carries a register contract
  (inputs, outputs, clobbers, alignment) that is kept truthful.
  Intention-revealing labels; comments explain _why_, never _what_.
- **Kernel purity**: no API calls, I/O, or hidden state in `src/kernel/`.
- **Zero dependencies**: the import allowlist (kernel32/user32/gdi32, no CRT)
  is enforced by a conformance test — do not weaken it.
- **Determinism**: same seed → same state. Changes that break bit-exactness
  need a documented decision.
- **Performance claims are measured**, never reasoned: before/after numbers
  with hardware, CPU features, particle count, and seed.

## Prerequisites

Windows 10/11 x64, PowerShell, the .NET 9 SDK. FASM is bootstrapped
automatically by `build.ps1`. Node.js (for `npx`/Prettier) is needed only for
the docs formatting gate below — not for building or running the engine.

## Building & testing

`build.ps1` must run first: it assembles `build/swarm.exe` and
`build/swarm.kernel.dll`, and `dotnet test` loads that DLL via P/Invoke.

```powershell
.\build.ps1                                            # bootstraps the pinned FASM on first run, assembles to build/
dotnet test tests\Swarm.Tests\Swarm.Tests.csproj       # reference equivalence + conformance fitness tests
npx --yes prettier@3.9.5 --check "**/*.{md,yml,yaml}"  # docs formatting gate
```

A non-zero skipped-test count means Smart App Control / Device Guard blocked
the freshly built `swarm.kernel.dll` from loading (`0x800711C7`) — a known
quirk on this class of machine, not a real gap in coverage. Set
`SWARM_REQUIRE_NATIVE=1` (as CI does) to turn the skip into a hard failure and
confirm the native path actually ran.

CI additionally reports the binary size budget (`swarm.exe` ≤ 64 KiB) and
restores NuGet in locked mode — if you bump a package, commit the regenerated
`tests/Swarm.Tests/packages.lock.json` with it.

All repo artifacts — code, comments, commits, PRs, issues — are written in
English.
