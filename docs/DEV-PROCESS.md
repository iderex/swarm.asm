# Development process

The engineering discipline behind every change in this repository - how a
single item of work travels from an idea to a merged commit. swarm.asm is a
hand-written x64 assembly engine: correctness and measured performance come
before features, and every change earns its merge through the same gated
loop described here.

Work is **issue-driven** (every change traces to a GitHub issue), branches
off `main`, is **PR-only**, and I merge it myself on a green gate stack.
Design intent lives in [MASTERPLAN.md](MASTERPLAN.md); the milestone
issues (M0–M4) are the source of truth for _what_ to build. This document is
the source of truth for _how_.

## Standing principles

- **Correctness and measured performance before features.** Fail closed:
  malformed preset/config input is rejected, never guessed at; a performance
  claim without a measurement is not a claim.
- **Minimal, self-documenting assembly.** The least code that does the job,
  with intention-revealing labels and a truthful register-contract header
  (inputs, outputs, clobbers, alignment) on every routine; comments explain
  _why_, never _what_.
- **Issue-driven, fully linked.** Every change has an issue; branches,
  commits, and the PR reference it (`Closes #N` / `Refs #N`).
- **Conformance is enforced, not aspired.** Every new structural property is
  locked in as a fitness test in the test harness, so it cannot silently
  regress.
- **Solo governance, human final say** (see [GOVERNANCE.md](../GOVERNANCE.md)).
  Review is run in-house; no external review, quality, or analysis service is
  used or trusted with this repository. The conformance test behind that rule
  refuses a named list of four root config files and three workflow
  substrings, not the class, so a service outside the list is caught by the
  review and by nothing else. [GOVERNANCE.md](../GOVERNANCE.md) names the
  list.

## The six-phase loop

Every item of work runs the same six phases in order. Each phase has an exit
criterion; the item does not advance until it is met.

| #   | Phase                  | Output                                                                                                                | Exit criterion                                                                                                               |
| --- | ---------------------- | --------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| 1   | **Plan**               | A written plan on the issue: scope, design, fail-closed behavior, the test list, and the conformance test it will add | Plan checked against MASTERPLAN.md; ambiguity resolved before any code                                                       |
| 2   | **Implement**          | A minimal assembly (or harness) change on a work branch off `main`, tests first where the harness can express them    | `build.ps1` assembles clean; the test suite (reference equivalence, determinism goldens, conformance fitness tests) is green |
| 3   | **Adversarial review** | Independent, refute-by-default verdicts on the change                                                                 | Every reviewer returns PASS; every finding is fixed and re-reviewed                                                          |
| 4   | **PR**                 | A PR against `main` following the template, linked to the issue                                                       | PR opened, CI green, description complete                                                                                    |
| 5   | **CI**                 | The full gate stack re-run on clean infrastructure                                                                    | Assemble, smoke-run, the test suite, the size budget, and the formatting check all green                                     |
| 6   | **Merge**              | A merge commit on `main`; the work branch deleted                                                                     | Every finding dispositioned; final sign-off given                                                                            |

The loop is the same whether the change is one instruction or one kernel
slice - the cost of a phase scales with the change, the _existence_ of the
phase does not.

## Phase 3 in detail: the adversarial review

Any change touching the kernel math, the internal ABI/register contracts, the
platform boundary, input parsing, or the build tooling passes a
refute-by-default review before merge: four lenses - **correctness**,
**performance**, **robustness**, and **integration** - each read the full
touched files, not just the diff, plus a fifth pass focused on the
SIMD/assembly domain whenever kernel or ABI code is changed. Each reviewer
returns free text ending in an explicit verdict; the burden is on the change
to survive review, not on the reviewer to find fault. Runtime behavior is
verified **empirically** - a failing test, a probe, a measurement - never
accepted from authoritative-sounding reasoning alone. Every real finding is
fixed in code or declined with a written reason on the PR; nothing is left
unaddressed.

This is the entire independent-review layer for the project: no external
review, quality, or analysis service is used or trusted (see
[GOVERNANCE.md](../GOVERNANCE.md)). A conformance test keeps such services
locked out of the tree.

## Conformance fitness tests (the ratchet)

Structural and safety invariants are enforced as code, not convention. They
live in the test harness and run on every PR. The rule: **every new
structural property locks in a conformance fitness test in the same PR that
establishes it** - the suite only ever grows, a one-way ratchet against
regression. Examples already in place: the import allowlist
(`kernel32`/`user32`/`gdi32`, no CRT), kernel purity (no API calls or I/O in
the simulation kernel), register-contract-header presence and truthfulness,
and the binary size budget.

When a review finds a class of defect, the fix includes a fitness test that
makes that class impossible to reintroduce - an error becomes a mandatory,
durable process adaptation, not a one-off patch.

## Governance and escalation

- **Branching:** work branches off freshly-fetched `main`; PR-only;
  merge-commit; the branch is deleted after merge.
- **Releases and tags are human-gated** - see
  [RELEASE-POLICY.md](RELEASE-POLICY.md). The pipeline never tags or
  publishes on its own.
- **Documentation is part of every change.** A change that alters behavior,
  configuration, or measured performance updates the affected docs in the
  same PR.
- **Every item in a PR is explained** - a code comment where non-obvious, a
  full PR body covering every change, and a fix-or-reasoned-decline reply on
  every review comment. Nothing lands unexplained.
