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

## Building & testing

```powershell
.\build.ps1        # bootstraps the pinned FASM on first run, assembles to build/
dotnet test        # reference equivalence + conformance fitness tests
npx --yes prettier@3.9.5 --check "**/*.{md,yml,yaml}"   # docs formatting gate
```

CI additionally reports the binary size budget (`swarm.exe` ≤ 64 KiB) and
restores NuGet in locked mode — if you bump a package, commit the regenerated
`tests/Swarm.Tests/packages.lock.json` with it.

All repo artifacts — code, comments, commits, PRs, issues — are written in
English.
