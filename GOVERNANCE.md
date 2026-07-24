# Governance

How this project is run, who holds access, and how decisions get made. The
guiding principle is honesty: this document describes the governance that
actually exists for a single-developer project, not the governance a larger
project would perform.

## Roles and access

swarm.asm is a **single-person project**. I, **@iderex**, hold admin access to
the repository, write access to every branch, and control of the release
pipeline (pushing version tags). No one else has commit, review, or release
authority.

Development is **AI-assisted** (see the README's "AI-assisted, human-owned"
callout): an AI assistant executes process steps under my direction —
generating and analyzing assembly, drafting documentation, running review
passes — but it never hands over finished, unreviewed work. Every step is a
proposal; I review, understand, edit where needed, and sign off on every
change that ships. There is no second human reviewer. The gates that stand in
for a review team are CI (assemble, smoke-run, the test suite, conformance
fitness tests, the binary size budget, formatting checks) and an adversarial
multi-lens review pass — correctness, performance, robustness, and
integration, plus a SIMD/assembly-specific pass whenever kernel or ABI code is
touched — run on every change to the kernel math, the internal ABI, the
platform boundary, input parsing, or the build tooling (see
[docs/DEV-PROCESS.md](docs/DEV-PROCESS.md)). No external review, quality, or
analysis service is used or trusted; those gates are the entire
independent-review layer, and a conformance test keeps such services locked
out of the repository.

## Decision-making

- **Changes** follow the gated flow in [CONTRIBUTING.md](CONTRIBUTING.md) and
  [docs/DEV-PROCESS.md](docs/DEV-PROCESS.md): issue → branch → implementation
  → adversarial review → PR → CI-green merge.
- **Scope and milestones** I decide, guided by the roadmap in
  [docs/MASTERPLAN.md](docs/MASTERPLAN.md); releases follow
  [docs/RELEASE-POLICY.md](docs/RELEASE-POLICY.md).
- **Correctness and measured performance outrank feature work.** Anything
  touching the kernel math, the internal ABI, the platform boundary, input
  parsing, or the build tooling passes the adversarial-review gate before
  merge.
- Community input is welcome through issues; the final call is mine.

## Granting elevated access

No one but me has standing elevated access today. If that ever changes, the
bar is: a track record of high-quality contributions here, a direct
conversation with me, least-privilege scoping (write access before admin,
release/tag authority granted separately and last), and a public update to
this document in the same PR that grants the access. Elevated permissions are
never granted implicitly or in bulk.

## Continuity (bus factor)

A one-person project carries an honest bus factor of one. Mitigations in
place:

- Everything needed to build, test, and run the engine is **in the
  repository** — a pinned, reproducible toolchain bootstrap
  (`tools/get-fasm.ps1`), a scripted build (`build.ps1`), and CI that runs the
  same steps on clean infrastructure. No private build secrets beyond
  standard GitHub tokens.
- The [MIT license](LICENSE) guarantees anyone can fork and continue the
  project at any time, with no copyleft obligation back to this repository.
- If I can no longer maintain it, the intent is to archive the repository
  with a clear notice rather than leave it silently stale.
