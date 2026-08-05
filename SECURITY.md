# Security policy

swarm.asm is a local desktop program with no network surface: it opens a
window, reads optional preset files, and simulates particles. The security
posture is correspondingly narrow but taken seriously:

- the executable imports only `kernel32`/`user32`/`gdi32` (no CRT, verified
  by a conformance test),
- preset/config parsing is fail-closed,
- the toolchain bootstrap and all CI actions are pinned to exact hashes,
- third-party code nonetheless reaches this repository by several routes of
  differing strength, and all of them are described below with the line that
  proves each claim.

## Reporting a vulnerability

Please report vulnerabilities (e.g. a crafted preset file causing memory
corruption) privately via
[GitHub private vulnerability reporting](../../security/advisories/new)
rather than a public issue. Reports are usually answered within a week.

## How dependencies reach this repository

The paths below were enumerated from the tree first and this section describes
that enumeration, rather than the enumeration being checked against a
pre-written policy. The list is recorded on issue #174. Every line number here
is `origin/main` at `796eace5727fac0f4452e18c88cce2e4cd2e7086`, and each
citation quotes enough of the line that it survives an unrelated edit to the
same file. The set of files it covers is re-derivable:

```
git ls-tree -r --name-only origin/main -- .github
grep -rn 'uses:' .github/workflows/
grep -rn '^\s*run:' .github/workflows/
grep -rn 'secrets\.' .github/
```

Four workflow files exist and no others: `ci.yml`, `zizmor.yml`,
`pr-hygiene.yml` and `unicode-guard.yml`. Three manifests exist and no others:
`tests/Swarm.Tests/Swarm.Tests.csproj`, `tests/Swarm.Tests/packages.lock.json`
and `tests/Swarm.Bench/Swarm.Bench.csproj`. There is no `global.json`, no
`nuget.config`, and no npm, Python, Go, Rust or submodule manifest anywhere in
the tree. There is no release or publish workflow either, which is issue #130.

Where a path below is watched by Dependabot, what its cooldown does and does
not defend against is argued in `.github/dependabot.yml`'s own header and is
not repeated here.

### What each job can reach

Blast radius here is whatever credentials the job holds, so those are read out
of the workflow files first and the entries below refer back to this.

`grep -rn 'secrets\.' .github/` returns exactly two lines, both in
`zizmor.yml`: `zizmor.yml:123` and `zizmor.yml:146`, each
`GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}`. No other workflow hands a secret to
any step.

The job token scopes, read out of the `permissions:` blocks:

- `ci.yml:24` sets `permissions: {}` at workflow level and `ci.yml:35-36`
  grant the `build` job `contents: read`. That job's `GITHUB_TOKEN` therefore
  exists at contents-read scope even though no step is handed a secret, and
  `ci.yml:45` `persist-credentials: false` keeps checkout from leaving it in
  `.git/config`.
- `zizmor.yml:43` sets `permissions: {}` at workflow level, and the `zizmor`
  job gets `zizmor.yml:58` `security-events: write` plus `zizmor.yml:59`
  `contents: read`. It is the only job in the repository holding a write
  scope.
- `pr-hygiene.yml:37` sets `permissions: {}` at workflow level, and the
  hygiene job gets `pr-hygiene.yml:51` `contents: read` and
  `pr-hygiene.yml:52` `pull-requests: read`. That workflow is advisory and is
  wired into no required check, which it states at `pr-hygiene.yml:10`.
- `unicode-guard.yml:12-13` grant `contents: read` at workflow level, with no
  job-level block and no secret.

### Rung 1: pinned by a hash, checked before the code runs

`actions/checkout`, at three call sites carrying the same 40-hex commit SHA:
`ci.yml:43`, `zizmor.yml:64` and `unicode-guard.yml:28`. All three set
`persist-credentials: false` (`ci.yml:45`, `zizmor.yml:66`,
`unicode-guard.yml:31`). Watched by Dependabot through the `github-actions`
ecosystem at `dependabot.yml:29` `- package-ecosystem: github-actions` with
`dependabot.yml:30` `directory: "/"`, cooldown at `dependabot.yml:38`
`default-days: 7`, minor and patch bumps grouped at `dependabot.yml:40-43`.
Credentials in scope differ per call site and are the job scopes above:
contents-read in `ci.yml`, contents-read plus `security-events: write` in
`zizmor.yml`, contents-read in `unicode-guard.yml`.

`actions/setup-dotnet`, at `ci.yml:48`, a full commit SHA, watched under the
same `github-actions` entry, so the cooldown covers the action. What the
action installs is a separate path and is in rung 3. Credentials in scope: the
`build` job's contents-read token, no secret.

`astral-sh/setup-uv`, at `zizmor.yml:69`, a full commit SHA, watched, cooled.
Credentials in scope: `security-events: write` and contents-read, per
`zizmor.yml:58-59`.

`github/codeql-action/upload-sarif`, at `zizmor.yml:134`, a full commit SHA,
watched, cooled. It is gated at `zizmor.yml:132` `if: (github.event_name ==`
and `zizmor.yml:133` `continue-on-error: true`, so it runs only on pushes to
`main` and on same-repo non-Dependabot pull requests. This is the step that
consumes the write scope: it runs in the job holding `zizmor.yml:58`
`security-events: write`, and it is why that scope exists.

`actions/github-script`, at `pr-hygiene.yml:61`, a full commit SHA, watched,
cooled. The script it runs is repository-authored inline JavaScript
(`pr-hygiene.yml:63` `script: |`), so the step ingests nothing further.
Credentials in scope: contents-read and pull-requests-read
(`pr-hygiene.yml:51-52`), no secret passed.

That is seven `uses:` call sites across five actions, every one at a full
commit SHA. `grep -rn 'uses:' .github/workflows/` returns nine lines; two of
them, `zizmor.yml:8` and `zizmor.yml:119`, are prose inside comments and not
call sites.

The FASM archive, fetched by `ci.yml:57` `run: ./tools/get-fasm.ps1` and
reached locally through `build.ps1:12`
`& (Join-Path $Root 'tools\get-fasm.ps1')`. The pin is
`tools/get-fasm.ps1:11` `$Version = '1.73.35'`, `:12` the URL and `:13` the
SHA-256. The hash is compared at `:39-40` and `:42` throws before `:45`
`Expand-Archive`, so the archive is verified before anything in it is
unpacked, let alone executed. Two properties of this path are recorded rather
than assumed. The transport falls back to plain HTTP at `:34`
`$fallback = $Url -replace '^https:', 'http:'`, for the reason given at
`:32-33`, so integrity rests on the pinned hash and not on the channel. And
the archive contributes more than the assembler binary: `build.ps1:19`
`$env:INCLUDE = Join-Path $Root 'tools\fasm\INCLUDE'` puts the archive's
include directory on the assembler's search path, so macro text from the
download is assembled into the shipped executable. Dependabot cannot see this
path: there is no manifest, the version is a literal in a PowerShell script,
and no ecosystem covers it, so no cooldown applies and a bump is a human
editing two lines. Credentials in scope: the `build` job's contents-read
token, no secret.

The test harness's NuGet packages. `tests/Swarm.Tests/Swarm.Tests.csproj:28`
`<PackageReference Include="xunit.v3" Version="3.2.2" />` is the only direct
package reference in the tree, and `tests/Swarm.Tests/packages.lock.json`
carries 20 `"resolved"` entries, each with a `contentHash`, starting at `:5-9`.
Restore runs in locked mode at `ci.yml:82` `-p:RestoreLockedMode=true`, so a
package drifting from the lock file fails the build instead of floating
silently. Dependabot watches it at `dependabot.yml:45`
`- package-ecosystem: nuget` with `dependabot.yml:46`
`directory: "/tests/Swarm.Tests"`, cooldowns at `dependabot.yml:55-56`. No
`nuget.config` exists, so the feed is the default `nuget.org` rather than a
repository-chosen one. Credentials in scope: the `build` job's contents-read
token, no secret.

`uv` itself, which `setup-uv` downloads, is pinned by both halves at this sha:
`zizmor.yml:87` `version: "0.11.30"` and `zizmor.yml:96` a `checksum:`, which
is the SHA-256 of the single Linux artifact this `ubuntu-latest` job
(`zizmor.yml:55` `runs-on: ubuntu-latest`) downloads. `zizmor.yml:102`
`prune-cache: true` holds the v9 default flip. So the strength is exact
version plus SHA-256, weaker than a manifest pin only in that it lives in a
workflow input, which is also why Dependabot cannot see it: there is no
`pyproject.toml`, `uv.toml`, `uv.lock`, `.tool-versions` or `requirements.txt`
in the tree, no ecosystem covers it, no cooldown applies, and a bump is a
human editing `zizmor.yml`. Issue #144 is open at this sha: the version and
the checksum are what has landed, and what it still holds is the decision on
whether `uv` should be given a manifest so Dependabot can watch it, and the
decision on whether the two `uvx` steps need `GITHUB_TOKEN` at all.
Credentials in scope: `security-events: write` and contents-read, per
`zizmor.yml:58-59`.

### Rung 2: pinned to an exact version, with no hash

`prettier`, at `ci.yml:95`
`run: npx --yes prettier@3.9.5 --check "**/*.{md,yml,yaml}"`. Exact version,
no integrity hash, and `--yes` means the package is fetched and executed
without a prompt. There is no npm manifest in the tree, so Dependabot sees
nothing and no cooldown applies, which the workflow says of itself at
`ci.yml:92` `# formatting and break the gate with zero repo change, invisible to`.
Where it runs is measured rather than remembered: it is the last step of the
`build` job, and `ci.yml:60` `run: ./build.ps1` earlier in the same job has
already produced `build/swarm.exe` in that workspace, so this step executes
fetched code in a workspace that contains the built artifact. Nothing consumes
or publishes that artifact today, because the tree carries no release or
publish workflow; issue #130 is where that changes. Credentials in scope: the
`build` job's contents-read token, no secret.

`zizmor`, at `zizmor.yml:109` and `zizmor.yml:140`, both
`run: uvx --no-build "zizmor@${ZIZMOR_VERSION}"`, with the version at
`zizmor.yml:61` `ZIZMOR_VERSION: "1.26.1"`. Exact version, no integrity hash.
`--no-build` restricts the install to a prebuilt wheel, so no
source-distribution build script executes. No Python manifest exists in the
tree, so Dependabot sees nothing and no cooldown applies. This is the
ingestion path with the widest credential exposure in the repository, and both
call sites prove it on their own lines: `zizmor.yml:123` and `zizmor.yml:146`
both read `GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}`, in the job that holds
`zizmor.yml:58` `security-events: write`. The token is there deliberately and
on a measurement, per `zizmor.yml:111`
`# Measured on the runner rather than read from the docs (#189).`; without it
five audits skip.

### Rung 3: not pinned at all

The .NET SDK that `setup-dotnet` downloads, at `ci.yml:50`
`dotnet-version: "9.0.x"`, with no `global.json` anywhere in the tree. The
action is pinned and what it installs is not. The version string is a floating
range rather than a version, so what is installed is whatever is newest in the
9.0 feature band on the day the job runs, and nothing in the repository
records which build that was. It executes in the required `build` job:
`ci.yml:82`
`run: dotnet test tests/Swarm.Tests/Swarm.Tests.csproj -c Release --nologo -p:RestoreLockedMode=true`
compiles and runs the conformance harness, which is the merge gate, so a
compromised SDK is a compromised gate rather than a compromised shipped
binary. Nothing verifies it: no hash, no lock file and no manifest covers this
path. Dependabot does not see it either, because the `github-actions`
ecosystem reads `uses:` refs and not a `with:` input, so `9.0.x` is invisible
to the updater and no cooldown applies. The counter-argument is stated rather
than dropped: the SDK never touches the shipped `swarm.exe`, which is produced
entirely by FASM at `build.ps1:21`
`& $Fasm (Join-Path $Root 'src\swarm.asm') (Join-Path $BuildDir 'swarm.exe')`,
which bounds the blast radius to the gate and to whatever the gate's workspace
holds. It is counted here as a dependency and not as the platform anyway,
because a gate that can be made to pass on a bad tree is the kind of thing
this list exists to describe. Credentials in scope: the `build` job's
contents-read token, no secret.

The runner images, at `ci.yml:31` `runs-on: windows-latest` and at
`zizmor.yml:55`, `pr-hygiene.yml:48` and `unicode-guard.yml:24`, all
`runs-on: ubuntu-latest`. Every path above executes on an image this
repository does not pin, does not hash and cannot see the contents of, and
which supplies `node`, `npx`, `dotnet` and `git` before any step runs. It is
floating by construction, no manifest describes it, Dependabot has no
ecosystem for it, and no cooldown applies. It is listed because a list of
ingestion paths that omits the substrate all of them run on is not a complete
list, and not because pinning it is being proposed here.

### A manifest watched but not locked

`tests/Swarm.Bench/Swarm.Bench.csproj` is a second manifest and it is compiled
inside the required `build` job, at `ci.yml:88`
`run: dotnet build tests/Swarm.Bench/Swarm.Bench.csproj -c Release --nologo`.
It carries no dependency today, which the file states as a deliberate choice at
`:4` `Force-kernel micro-benchmark. Deliberately dependency-free: it drives the`
and which the tree confirms:

```
git show origin/main:tests/Swarm.Bench/Swarm.Bench.csproj | grep -c PackageReference
0
```

Dependabot watches it anyway, on the entry added for #198: a third
`package-ecosystem: nuget` block with `directory: "/tests/Swarm.Bench"`,
carrying the same `default-days: 7` and `semver-major-days: 14` tiers as the
harness entry. The entry finds nothing to do while the project stays
dependency-free, and it exists so that the day a `PackageReference` is added
there, the arrival is not silent.

What it is not is locked. Unlike `/tests/Swarm.Tests`, this project has no
`packages.lock.json` and `ci.yml:88` does not restore in locked mode, so a
package added here would have the cooldown as its only hold and nothing that
fails the build on a resolved version drifting from a committed hash. That is
the residual, and it is a property of the project rather than of the updater
entry.

### What this section does not cover

The claim that `prettier` and `zizmor` have no transitive dependencies was not
measured. It is not asserted anywhere above and no sentence above rests on it.

Nothing above was verified by running CI. Every fact here is read out of the
tree at the sha named at the top of this section, which is both what that
reading supports and its bound: a workflow file says what a job is configured
to do, and never what a runner actually did.
