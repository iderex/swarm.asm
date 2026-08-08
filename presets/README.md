# Presets

Scene files for `swarm.exe`. A preset named on the command line replaces the
built-in one before anything is sized from it:

```powershell
.\build\swarm.exe presets\headline.txt
.\build\swarm.exe presets\headline.txt -capture
```

The grammar is pinned by `docs/MASTERPLAN.md` decision 10 and parsed by
`src/kernel/parse.inc`. It has no comment syntax, deliberately, which is why
the descriptions live here instead of inside the files. Every key appears
exactly once and all of them are required, so a file is either the whole scene
or refused. Grid mode is not a key: the exe applies `FLAG_GRID` to every
loaded preset, because which spatial structure runs is a platform decision and
not part of the scene.

## The benchmark pair

`docs/MASTERPLAN.md` decision 12 pins two scenes, and every published
performance number is quoted against one of them. Two scenes rather than one so
that a sparse headline cannot be read against a denser rival: the density is
disclosed instead of implied.

| file           | n         | rmax     | g   | k    | candidates walked |
| -------------- | --------- | -------- | --- | ---- | ----------------- |
| `headline.txt` | 1,048,576 | 0.001953 | 512 | 12.6 | 37.0              |
| `dense.txt`    | 1,048,576 | 0.003906 | 256 | 50.3 | 145.0             |

Everything else in the two files is identical, and identical to the preset
compiled into the exe: 4 species, seed `0x9E3779B97F4A7C15`, `beta = 0.3`,
`dt = 0.02`, `friction = 0.71`, `force = 10.0`, and the same 4x4 matrix. Only
`n` and `rmax` move, so a difference between the two rows in
`docs/BENCHMARKS.md` is a difference in density and nothing else.

Neither derived column is written in the file. `g` follows from `rmax` by the
layout rule in `src/kernel/layout.inc`, the largest power of two with
`1/g >= rmax`, then meets the `[4, 512]` clamp. `k` is the mean number of
particles genuinely inside `rmax` on a uniform field, `pi * n * rmax^2`, which
is the density decision 12 names the scenes by. **Candidates walked** is the
mean population of the 3x3 wrapped cell neighbourhood the force loop actually
visits, `9n/g^2 + 1`, so it is the work rather than the physics; the two
numbers in that column are the `g = 512` and `g = 256` rows #148 measured
directly from copied-out positions, 37.01 and 145.00.

**`rmax` is the nearest expressible value to what decision 12 pins, not the
pinned value itself.** The decision names `rmax = 1/512` and `rmax = 1/256`.
The grammar accepts one to six fraction digits and no exponent, so
`0.001953125` and `0.00390625` are both refused, on the `rmax` line:

```
swarm_parse_preset, rmax 0.001953125 -> raw=0x80700005 code=7 line=5
                                        code 7 = PERR_NUM_FRAC
```

`0.001953` and `0.003906` are the six-digit truncations, low by 1.25e-7 and
2.5e-7, a relative 6.4e-5. They resolve to the same `g` as the pinned values,
512 and 256, and to the same `k` to three significant figures, so the scenes
decision 12 describes are reproduced in every derived quantity. What is not
reproduced is the literal `rmax`, and it is not reachable from a preset file at
all until either the grammar admits more fraction digits or the decision is
restated in values the grammar can express. Issue #166 carries that question;
nothing here settles it.

**The headline scene sits one step below where the `g` clamp starts to bind.**
At `rmax = 0.001953` the layout rule itself gives `g = 512`, so the clamp is
not what limits this scene. Below `1/1024` the clamp does bind, and #148
measured what it costs there: about 23% of the frame. A row quoted against
`headline.txt` is therefore a row about the rule and not about the ceiling, and
it would stop describing the same grid if the ceiling moved and this scene's
`rmax` moved with it.

## Adding a preset

Nothing in the suite walks this directory yet, so a file added here is covered
by no gate and a grammar change could break it silently. Issue #168 is the walk
test that closes that, and it replaces this paragraph when it lands.

If a required key is ever added to the grammar, every file here needs it. There
are two today, and `docs/MASTERPLAN.md` decision 10 is where that cost is
argued.
