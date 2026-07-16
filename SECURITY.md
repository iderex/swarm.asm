# Security policy

swarm.asm is a local desktop program with no network surface: it opens a
window, reads optional preset files, and simulates particles. The security
posture is correspondingly narrow but taken seriously:

- the executable imports only `kernel32`/`user32`/`gdi32` (no CRT, verified
  by a conformance test),
- preset/config parsing is fail-closed,
- the toolchain bootstrap and all CI actions are pinned to exact hashes.

## Reporting a vulnerability

Please report vulnerabilities (e.g. a crafted preset file causing memory
corruption) privately via
[GitHub private vulnerability reporting](../../security/advisories/new)
rather than a public issue. Reports are usually answered within a week.
