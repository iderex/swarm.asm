# What & why

<!-- What does this change do, and why? Link the issue: Closes #NNN -->

## Type of change

- [ ] Kernel / simulation math
- [ ] Performance
- [ ] Platform (window, input, rendering)
- [ ] Bug fix
- [ ] Refactor / code quality
- [ ] Build / CI / supply chain
- [ ] Docs

## Engineering checklist

- [ ] Import allowlist intact: `swarm.exe` still imports only
      kernel32 / user32 / gdi32; no CRT.
- [ ] Kernel purity preserved: no API calls, I/O, or hidden global state in
      `src/kernel/`.
- [ ] Every touched routine's register contract (inputs, outputs, clobbers,
      alignment) is present and truthful.
- [ ] Determinism preserved (same seed → same state), or the break is a
      documented, gated decision.
- [ ] Input parsing stays fail-closed: invalid presets/config are rejected,
      never partially applied.
- [ ] An adversarial review (`/security-review`) was run for changes to kernel
      math, the ABI, the platform boundary, parsing, build/tools, or workflows.

## Performance checklist

Fill in for any change on the hot path.

- [ ] The claim is measured, not reasoned: before/after numbers with hardware,
      CPU features, particle count, and seed.
- [ ] No regression against the recorded baseline (docs/BENCHMARKS.md), or the
      regression is justified below.

## Quality checklist

- [ ] Minimal: the change adds no more code than the problem requires; no
      duplication, no dead code, no unused labels.
- [ ] Self-documenting: intention-revealing labels and small, single-purpose
      routines; comments explain why, not what.
- [ ] Conformance fitness tests pass, and any new structural property this
      change establishes is locked in as a new conformance test.

## Verification

Paste the local gate results (or confirm CI is green).

- [ ] `build.ps1` — assembles clean.
- [ ] Smoke-run — exit code 0.
- [ ] `dotnet test` — green (reference equivalence, determinism goldens,
      conformance tests).
- [ ] Prettier (`**/*.{md,yml,yaml}`) — clean.
- [ ] Docs synced: MASTERPLAN / README / BENCHMARKS reflect any behavior,
      config, or performance change.

## Notes

<!-- Trade-offs, declined review findings and why, follow-ups, or anything
explicitly out of scope. -->
