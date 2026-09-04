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
[GitHub private vulnerability reporting](https://github.com/iderex/swarm.asm/security/advisories/new)
rather than a public issue. Reports are usually answered within a week.

## What analyses this repository, and what nothing analyses

CodeQL's default setup analysed this repository until 2026-09-04 and does not
now:

```
gh api repos/iderex/swarm.asm/code-scanning/default-setup --jq '{state,languages}'
{"languages":["actions","csharp"],"state":"not-configured"}
```

It was turned off because it could not read what this repository is. There is no
C or C++ in the tree:

```
git ls-tree -r --name-only origin/main | grep -icE '\.(c|cc|cpp|cxx|h|hh|hpp)$'
0
```

The `.inc` files under `src/` are FASM assembler includes, and the language
autodetect read them as C/C++ headers. The extractor then refused the tree it
had been pointed at, in its own words `found 0 source files, 13 header files`,
so the job failed for the shape of the tree rather than for anything it found. A
job that cannot succeed by construction is not a control, and a status that is
always red teaches a reader to stop reading statuses, which costs more than the
analysis it was standing in for.

CODEQL NEVER READ THIS PROJECT'S SUBSTANCE, AND THAT DID NOT CHANGE WHEN IT WAS
TURNED OFF. There is no CodeQL extractor for x64 assembly, so everything under
`src/` was outside its reach while it ran and is outside it now. What the
shutdown removed is a result over the workflow files and the C# harness. What it
did not remove is coverage of the engine, because there was none to remove - and
a green tick that a reader takes for a statement about the assembly is the same
defect as a red one that means nothing.

What goes on reading this tree is the workflow set the section below derives,
together with the conformance suite that judges the shipped image - the import
allowlist, the absence of a CRT, the kernel-purity scans - which runs under
`dotnet test` on every pull request.

WHAT WOULD BRING CODEQL BACK. A C or C++ harness landing in this tree, analysed
by an advanced setup whose committed workflow names `languages: c-cpp` with
`paths` scoped to that harness, so that it reads what exists instead of guessing
from a file extension. Until then the correct configuration is the absent one.

WHAT THIS SECTION CANNOT HOLD ON ITS OWN. Default setup is a repository setting
and not a tracked byte, so nothing here refuses it being switched back on, and
these sentences would go stale in silence if it were. The reading at the top is
the command that falsifies them, and it is the only thing standing behind them.
The decision and the readings that led to it are on issue #325.

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

The workflow files and the manifests are not written out here, and the reason
is that the paragraph which used to write them out went wrong exactly as this
one would. It wrote out both sets by name and by number, and by the time
anybody read it the tree held more of each, with nothing anywhere comparing
the sentence against the set it described. What replaces it is the derivation,
in the same shape this section already hands the reader above:

```
git ls-tree --name-only origin/main .github/workflows/
git ls-tree -r --name-only origin/main | grep -E 'csproj$|packages.lock.json$'
```

The negatives are the half a derivation cannot state on its own, so they are
written out instead, each naming the command that would falsify it. There is
no `global.json` and no `nuget.config`, and no npm, Python, Go, Rust or
submodule manifest anywhere in the tree:

```
git ls-tree -r --name-only origin/main \
  | grep -E 'global.json$|nuget.config$|package.json$|pyproject.toml$|uv.lock$|requirements.txt$|go.mod$|Cargo.toml$|.gitmodules$'
```

returns nothing. THERE IS A RELEASE WORKFLOW NOW AND THIS PARAGRAPH SAID THERE
WAS NONE. `release.yml` landed on #181: a `v*` tag push assembles, runs the
whole pull-request gate, refuses a tag that disagrees with `CHANGELOG.md` or
with the ABI version the built DLL reports, and attests the artifact's digest.
It publishes no GitHub Release object, because `docs/RELEASE-POLICY.md` refuses
one. The first command above is what re-derives the set. #130 stays open for
the rest of the release pipeline; what it no longer covers is the workflow's
absence.

Where a path below is watched by Dependabot, what its cooldown does and does
not defend against is argued in `.github/dependabot.yml`'s own header and is
not repeated here.

### What each job can reach

Blast radius here is whatever credentials the job holds, so those are read out
of the workflow files first and the entries below refer back to this.

THIS SUBSECTION IS TAKEN AT A LATER COMMIT THAN THE REST OF THE SECTION, and
the difference is the whole reason it was rewritten. Every line cited from here
to the end of this subsection is `origin/main` at
`0dbd94217c420a51cf5b3ae6e2c66f604ab57b72`; the rung analysis below it is still
at the sha named at the top of this section, and that gap is stated at the end
rather than left for a reader to discover. What stood here covered the
workflows that existed when it was written and went on describing the tree in
the present tense after more arrived, which is issue #312.

Every workflow the derivation above returns has an entry here, and the check
that they agree is to run the derivation and compare the names:

```
git ls-tree --name-only origin/main .github/workflows/
```

Secrets first, because a job holding none is bounded by its `permissions:`
block alone:

```
grep -rn 'secrets\.' .github/workflows/
```

Two of the lines it returns are prose inside comments (`zizmor.yml:35` and
`zizmor.yml:69`); the rest are `zizmor.yml:138` and `zizmor.yml:162`, each
`GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}` and both inside the `zizmor` audit job,
which holds `contents: read` and nothing else. No other workflow hands a secret
to any step.

The job token scopes, read out of the `permissions:` blocks. Every workflow
sets `permissions: {}` at workflow level and grants per job, except
`unicode-guard.yml`, which grants read at workflow level and has no job block:

- `ci.yml:24` `permissions: {}`; the `build` job (`ci.yml:27`) gets
  `ci.yml:36` `contents: read`. That job's `GITHUB_TOKEN` therefore exists at
  contents-read scope even though no step is handed a secret, and
  `ci.yml:45` `persist-credentials: false` keeps checkout from leaving it in
  `.git/config`.
- `dco.yml:43` `permissions: {}`; the `dco` job (`dco.yml:46`) gets
  `dco.yml:53` `contents: read`, with `dco.yml:62` `persist-credentials: false`
  and no secret.
- `mutation.yml:39` `permissions: {}`; the `mutate` job (`mutation.yml:48`)
  gets `mutation.yml:107` `contents: read`, with `mutation.yml:117`
  `persist-credentials: false` and no secret. It is one of the two scheduled
  jobs (`mutation.yml:34` `- cron: "43 4 * * 2"`).
- `parser-fuzz.yml:42` `permissions: {}`; the `fuzz` job
  (`parser-fuzz.yml:51`) gets `parser-fuzz.yml:58` `contents: read`, with
  `parser-fuzz.yml:71` `persist-credentials: false` and no secret. It is the
  other scheduled job (`parser-fuzz.yml:28` `- cron: "17 3 * * 1"`).
- `pr-hygiene.yml:37` `permissions: {}`; the `hygiene` job
  (`pr-hygiene.yml:46`) gets `pr-hygiene.yml:51` `contents: read` and
  `pr-hygiene.yml:52` `pull-requests: read`. That workflow is advisory and is
  wired into no required check, which it states at `pr-hygiene.yml:10`.
- `release.yml:50` `permissions: {}`, and its two jobs hold different scopes.
  The `gate` job (`release.yml:59`) gets `release.yml:65` `contents: read` and
  nothing else, and it is the job that runs every ingesting step in the
  workflow - `npx prettier`, the NuGet restore, the FASM bootstrap - with
  `release.yml:74` `persist-credentials: false` on its checkout. The `attest`
  job (`release.yml:185`) gets `release.yml:193` `contents: read`,
  `release.yml:198` `id-token: write` and `release.yml:199`
  `attestations: write`, checks nothing out, and runs `sha256sum` plus two
  SHA-pinned actions and nothing else. Neither job is handed a secret. It is
  the only workflow started by a tag push (`release.yml:44-46`), so no pull
  request reaches it. THESE LINE CITATIONS ARE TAKEN AT THE COMMIT THAT ADDS
  THE FILE (#181), not at the sha this subsection names above, because the
  file does not exist at that sha.
- `scorecard.yml:57` `permissions: {}`, and its two jobs hold different
  scopes. The `analysis` job (`scorecard.yml:66`) gets
  `scorecard.yml:73` `id-token: write` and `scorecard.yml:74` `contents: read`.
  The `id-token: write` is an OIDC token for the OpenSSF API and grants nothing
  in this repository, which the file argues on its own lines at
  `scorecard.yml:71-72`. The `upload` job (`scorecard.yml:98`) gets
  `scorecard.yml:104` `security-events: write` and `scorecard.yml:105`
  `contents: read`. Both set `persist-credentials: false`
  (`scorecard.yml:79`, `scorecard.yml:110`).
- `unicode-guard.yml:12-13` grant `contents: read` at workflow level, with no
  job-level block and no secret. The `bidi` job is at `unicode-guard.yml:22`.
- `zizmor.yml:53` `permissions: {}`. The `zizmor` audit job
  (`zizmor.yml:63`) gets `zizmor.yml:71` `contents: read` ONLY - the write
  scope was deliberately taken off this job because it is the one that
  downloads and executes a third-party wheel, argued at `zizmor.yml:68-70` inside the block itself.
  The `upload` job (`zizmor.yml:164`) gets `zizmor.yml:184`
  `security-events: write` and `zizmor.yml:185` `contents: read`, and runs no
  third-party code beyond the pinned upload action.

So the write scopes in this repository are `security-events: write` in
`zizmor.yml`'s `upload` job and in `scorecard.yml`'s `upload` job,
`id-token: write` in `scorecard.yml`'s `analysis` job, and `id-token: write`
with `attestations: write` in `release.yml`'s `attest` job. No job that holds a
write scope also holds a secret or executes an unpinned third-party program.
That is the property to re-check when a workflow is added, and it is READ IN
TWO PLACES AND NOT IN THE THIRD: `ZizmorJobPermissionTests` refuses the zizmor
half, `ReleaseGateTests` refuses a `release.yml` job that holds a write scope
and also runs `npx`, `dotnet` or a repository script, and nothing reads the
scorecard half or any workflow added tomorrow. The release half was built by
splitting a single job in two - #193's finding, met a second time - because one
job would have handed a token carrying `attestations: write` to `npx prettier`
and made the sentence above false.

WHAT THIS SUBSECTION DOES NOT REACH. The rung analysis below is still the
reading taken at the sha at the top of this section, so the ingestion paths
that arrived with `dco.yml`, `mutation.yml`, `parser-fuzz.yml` and
`scorecard.yml` are not placed on a rung: the actions they call, the
`.config/dotnet-tools.json` manifest that `mutation.yml:131`
`run: dotnet tool restore` consumes, and the `tests/Swarm.Oracle` project and
its lock file. Their credentials are above and their pins are not analysed
here. That is a gap in the rung analysis, it is stated rather than implied, and
it is what issue #312 leaves open for whoever re-takes the enumeration.

### Rung 1: pinned by a hash, checked before the code runs

`actions/checkout`, at the call sites `ci.yml:43`, `zizmor.yml:64` and
`unicode-guard.yml:28`, each carrying the same 40-hex commit SHA and each
setting `persist-credentials: false` (`ci.yml:45`, `zizmor.yml:66`,
`unicode-guard.yml:31`). The live set of call sites is derived rather than
counted here, and the citations above are the ones that existed at the sha at
the top of this section:

```
grep -rn 'uses: actions/checkout' .github/workflows/
```

Watched by Dependabot through the `github-actions`
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

Every `uses:` call site named above is at a full commit SHA. The set is not
written out as a total, because the total moves with the workflows and nothing
compares a written total against them:

```
grep -rn 'uses:' .github/workflows/
```

Not every line that returns is a call site: some are prose inside comments,
which is why the entries above name their lines individually rather than
resting on the size of that output.

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
`<PackageReference Include="xunit.v3" Version="3.2.2" />` and
`tests/Swarm.Tests/Swarm.Tests.csproj:29`
`<PackageReference Include="YamlDotNet" Version="18.1.0" />` are the direct
package references in the tree, and `tests/Swarm.Tests/packages.lock.json`
carries a `"resolved"` entry per transitive package, each with a
`contentHash`, starting at `:5-9`. Neither set is written out as a total here,
because both move with the harness:

```
grep -rn 'PackageReference' -- tests/*/*.csproj
grep -c '"resolved"' tests/Swarm.Tests/packages.lock.json
```

`YamlDotNet` is a test-only reader for `.github/dependabot.yml`: the harness
asserts that file's cooldown policy against a loader rather than against a line
scanner, because indentation is not scope in YAML. It is never referenced by
`swarm.exe`, which links no managed code at all.
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

`tests/Swarm.Bench/Swarm.Bench.csproj` is a manifest that the derivation at the
top of this section returns, and it is compiled inside the required `build`
job, at `ci.yml:88`
`run: dotnet build tests/Swarm.Bench/Swarm.Bench.csproj -c Release --nologo`.
It carries no dependency today, which the file states as a deliberate choice at
`:4` `Force-kernel micro-benchmark. Deliberately dependency-free: it drives the`
and which the tree confirms:

```
git show origin/main:tests/Swarm.Bench/Swarm.Bench.csproj | grep -c PackageReference
0
```

Dependabot watches it anyway, on the entry added for #198: a
`package-ecosystem: nuget` block with `directory: "/tests/Swarm.Bench"`,
carrying the same `default-days: 7` and `semver-major-days: 14` tiers as the
harness entry. The entry finds nothing to do while the project stays
dependency-free, and it exists so that the day a `PackageReference` is added
there, the arrival is not silent.

WHAT STOOD HERE SAID THIS PROJECT HAS NO `packages.lock.json`, AND IT HAS ONE
NOW. Corrected while doing issue #312, and found by re-running the derivation
this section's own opening now hands the reader rather than by anything
noticing:

```
git ls-tree -r --name-only origin/main tests/Swarm.Bench/
```

returns `tests/Swarm.Bench/packages.lock.json` at `origin/main`
`0dbd94217c420a51cf5b3ae6e2c66f604ab57b72`. The file exists and holds no
`"resolved"` entry, which is what a lock file for a dependency-free project
looks like:

```
git show origin/main:tests/Swarm.Bench/packages.lock.json | grep -c '"resolved"'
0
```

The half of the old sentence that survives is the load-bearing half. The build
step for this project still does not restore in locked mode - at that sha it is
`ci.yml:130` `run: dotnet build tests/Swarm.Bench/Swarm.Bench.csproj -c Release --nologo`,
with no `-p:RestoreLockedMode=true`, unlike the harness step. So the lock file
is present and is not enforced, and a package added here would still have the
cooldown as its only hold. That is the residual, it is a property of the build
step rather than of the updater entry, and the correction makes it narrower
rather than softer: what is missing is one flag, not one file.

### What this section does not cover

The claim that `prettier` and `zizmor` have no transitive dependencies was not
measured. It is not asserted anywhere above and no sentence above rests on it.

Nothing above was verified by running CI. Every fact here is read out of the
tree at the sha named at the top of this section, which is both what that
reading supports and its bound: a workflow file says what a job is configured
to do, and never what a runner actually did.
