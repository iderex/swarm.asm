# Contributing

## Process

Issue-driven, gate-driven:

1. Every change — feature, fix, perf work, docs — starts as a **GitHub issue**
   with a type, `area:` and `priority:` label and a milestone.
2. Work happens on a short-lived branch off `main`:
   `feature/…`, `fix/…`, `perf/…`, `harden/…`, `chore/…`, `refactor/…`.
3. The PR fills the template honestly and references the issue (`Closes #N`).
   PRs merge with a merge commit once CI and review are green.

## Commit messages

Short, imperative subject line (`Add the grid neighbour sweep`, not `feat:
add the grid neighbour sweep`) — no conventional-commit prefix. Explain the
_why_ in the body, not the subject.

**Every commit subject ends with its issue reference in brackets** — `Add the
grid neighbour sweep [#23]`, multiple issues as `[#23][#24]` — so the link
survives `git blame`/`bisect`/`log`, which show only the subject. `Closes #N`
still goes in the body when the commit resolves the issue; the bracket is
additional, not a replacement.

All commits are in English, authored as iderex, and carry no AI-attribution
markers (no `Co-Authored-By`, no "generated with", no emoji, no session
links).

## Sign your work (DCO)

This project uses the [Developer Certificate of Origin](DCO) (DCO 1.1) — a
lightweight, standard way to certify that you wrote or otherwise have the
right to submit the code you contribute, under the project's MIT license. It
is not a copyright-assignment CLA; you keep your copyright.

**Every commit must be signed off.** Add the sign-off automatically with `-s`:

```powershell
git commit -s -m "Add the grid neighbour sweep [#23]"
```

This appends a trailer matching your commit author identity:

```
Signed-off-by: Your Name <your.email@example.com>
```

By adding it you certify the [DCO](DCO). Forgot to sign off? Add it
retroactively across your branch with `git rebase --signoff <base>` and
force-push.

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
