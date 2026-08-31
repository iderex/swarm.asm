# Release policy - three classes, three cadences

swarm.asm has not tagged a release yet - it is still in the milestone build
order (M0–M4, see [MASTERPLAN.md](MASTERPLAN.md)). This document fixes the
release contract before the first tag ships, so M4 does not need to invent
one under pressure.

Every release falls into exactly one class. The class decides **which digit
is bumped** and **how often it may ship**.

Version numbers are three-part `X.Y.Z`. swarm.asm ships a single Windows
executable - there is no plugin manifest, no update channel, and no host
generation to encode; the git tag `X.Y.Z` is the release, full stop.

| Digit | Meaning                                                                   | Example           |
| ----- | ------------------------------------------------------------------------- | ----------------- |
| `X`   | Breaking change (kernel ABI, the P/Invoke seam, the preset/config format) | `1.x` → `2.0.0`   |
| `Y`   | Feature release                                                           | `1.0.x` → `1.1.0` |
| `Z`   | Bug-fix or security patch                                                 | `1.1.0` → `1.1.1` |

Security and bug-fix both bump `Z`; they differ by **cadence**, not by digit.

## 1. Security release - immediate, no rate limit

Any fix for a vulnerability (for example a crafted preset causing memory
corruption - see [SECURITY.md](../SECURITY.md)). Ships as fast as it is
green - **never batched, never rate-limited**, no matter how many happen in
one day.

- **Version:** patch on the released line, bump `Z`, tag `X.Y.Z`.
- **Still required:** a green CI gate and the adversarial-review pass -
  security is exactly what that gate exists for. I give the final go and
  push the tag; security is never delayed by cadence.

## 2. Bug-fix release - at most once per day

Non-security fixes and small robustness or correctness corrections.

- **Version:** bump `Z`, tag `X.Y.Z`.
- **Cadence:** **≤ 1 bug-fix release per calendar day.** If one already went
  out today, further fixes collect for tomorrow. (A same-day security
  release does not count against this - different class.)

## 3. Feature release - at most once per month

A new capability - a new kernel path, a new preset option, a new milestone
deliverable.

- **Version:** bump `Y`, tag `X.Y.Z`.
- **Cadence:** **≤ 1 feature release per calendar month.** Further features
  fold into the next one.

## 4. Breaking release - no fixed cadence, always deliberate

A change to the kernel ABI, the P/Invoke seam the test harness relies on, or
the on-disk preset/config format, in a way that is not backward compatible.

- **Version:** bump `X`, reset `Y` and `Z` to `0`, tag `X.Y.Z`.
- **Cadence:** none fixed - a breaking release happens only when the change
  genuinely requires it, and only by my explicit decision.

## How a release ships

1. **Classify** the pending release (security | bug-fix | feature |
   breaking) from what changed.
2. **Check the cadence gate** against the recent tags: security → proceed;
   bug-fix → stop if one shipped today; feature → stop if one shipped this
   month; breaking → my decision.
3. **Bump the version and update the changelog.** Move the relevant
   `Unreleased` entries in [CHANGELOG.md](../CHANGELOG.md) under the new
   version heading with today's date - the changelog bump is part of the
   release ritual, not an afterthought.
4. **Run the full build/test gate** - assemble, smoke-run, the test suite,
   the size budget, and the formatting check, all green.
5. **Push the version tag.** Tag-push is the **only** publish trigger - there
   is no `gh release create` step and no separate publish action. Pushing a
   version tag to this repository is **gated on me**: an unreviewed or
   accidental tag push is blocked by a local safety check before it reaches
   the remote, and only I can clear it.

There is no separate GitHub Release object to manage: the tag is the
release. GitHub releases are immutable once created and permanently burn a
tag if the release is later deleted, so `gh release create` and equivalent
manual release flows are never used here - the tag push is the whole
publish step.
