---
name: cut-release
description: |
  Cut a new NuGet release of the `ServantSoftware.Guardrails` dotnet tool. Verify the
  tree is fully pushed and green, pick the next version, tag it at master HEAD, and let
  the `release.yml` pipeline publish to NuGet.org via Trusted Publishing (OIDC — no API
  key to handle). Use when the maintainer says "cut a release", "publish a new version",
  "ship a NuGet package", or "cut v1.2.0".

  MAINTAINER-ONLY: this skill is NOT packed into the shipped tool (the csproj bundles only
  plan-breakdown / guardrails-review / guardrails-domain-knowledge). It lives in the repo
  for anyone releasing Guardrails.

  SELF-UPDATING: if the release mechanism changes (trigger, versioning, auth, the
  pipeline jobs), update this skill AND `.github/workflows/release.yml` together.
---

# Cut a release

Cutting a release of the `ServantSoftware.Guardrails` dotnet tool **is one action: push a
`v*` git tag.** Everything else is the `release.yml` pipeline. The version is derived from
the tag (leading `v` stripped): tag `v1.2.0` publishes `1.2.0`.

**This is outward-facing and effectively irreversible** — NuGet refuses to republish an
existing version, and a published version can be *unlisted* but never truly deleted (it may
already be cached/indexed). So the tree must be *exactly* what you intend to ship **before**
you tag. Never tag a commit that isn't already on `origin/master` and CI-green.

You never handle a NuGet API key: publishing uses **Trusted Publishing (OIDC)**. The
`publish` job mints a short-lived key at push time via the nuget.org policy + the
`NUGET_USER` repo secret. If publish ever fails on *auth/login* (not tests), that's a
nuget.org Trusted-Publishing / `NUGET_USER` config issue for the maintainer — not a code
problem.

## Preconditions — verify ALL before tagging

Use `git -C <repo-root>` for every git command (never `cd … && git …`).

**Run the mechanical checks first — they are scripted for a reason:**

```bash
.github/scripts/release-preflight.sh preflight <sha-you-are-about-to-tag>
```

It resolves the tag target, compares it to `origin/master`, reports the working tree, and refuses
to proceed inside the #747 timing window (see **If it fails**). Every check it performs is one
that was done by hand — and got wrong — during a real cut. The prose below says what it checks
and why; the script is what actually checks it.

1. **The commit you are about to TAG is `origin/master`.** This is a check on the **tag target**,
   not on your working tree. You can be standing anywhere — a stale checkout, a worktree, a
   detached HEAD — and still tag correctly, *provided the sha you name is `origin/master`*. The
   failure that actually happened was tagging a specific sha from a checkout that was behind and
   assuming `HEAD` spoke for it. Resolve **both sides** through `rev-parse` before comparing, or a
   short sha compares unequal to a 40-character one and every check is a false result:
   ```bash
   git -C <repo> fetch origin --tags
   git -C <repo> rev-parse --verify "<sha>^{commit}"          # the tag target, fully resolved
   git -C <repo> rev-parse --verify "origin/master^{commit}"  # MUST be identical
   git -C <repo> log origin/master..HEAD --oneline            # nothing unpushed, if you are on master
   ```
   No open PR you intend to include may still be unmerged.
2. **The integrated HEAD is green.** Every merged PR was CI-green individually, and the
   release pipeline re-runs the full 3-OS matrix on the tagged commit anyway — but run a
   final local gate on the actual release artifact for confidence:
   ```bash
   git -C <repo> checkout master && git -C <repo> pull --ff-only    # if not already there
   cd <repo> && dotnet build Guardrails.sln -c Release && dotnet test Guardrails.sln -c Release
   ```
   Expect 0 warnings/0 errors and a green suite (the two `RealClaude*` tests skip without a
   live key — that's normal).
3. **Working tree clean** apart from any long-lived untracked plan-folder drafts under
   `docs/plans/` that were never part of the release. This one is **advisory**: a tag names a
   *commit*, so uncommitted files cannot leak into the release. Check it to catch work you
   *meant* to ship and didn't — not as a gate on the tag.
4. **Nothing to edit before tagging.** The shipped version comes from the TAG
   (`-p:Version=${GITHUB_REF_NAME#v}`), not from a file. `src/Guardrails.Cli/Guardrails.Cli.csproj`'s
   `<Version>` is only the default a *locally built* tool reports (and stamps into installed skills);
   keeping it roughly current is hygiene, but bumping it is **not** a step of cutting a release —
   do not open a PR for it and do not block the tag on it.

## Pick the version

```bash
git -C <repo> tag --sort=-creatordate | head -5
```

The scheme is **`vX.Y.0` — a STABLE version with NO prerelease suffix**, monotonically
increasing. **Default: bump the MINOR by one and leave the patch at `0`**
(`v1.1.0` → `v1.2.0` → `v1.3.0`). Exactly one component moves per release:

| Component | When it moves |
|---|---|
| **minor** `Y` | **every release** — this is the default; no permission needed |
| **major** `X` | only when the maintainer explicitly says "cut a major" *in words* |
| **patch** (third) | **never** — it stays `0` under this scheme |

A **major** bump is a deliberate maintainer call, stated out loud. Do NOT infer one from the
changelog ("this change looks breaking"), and do NOT invent a patch release for a small fix —
a small fix is simply the next minor. If the maintainer hasn't said otherwise, cut the next
minor and **state which version you're cutting** before you tag.

**The `1.0.0-preview.N` line is CLOSED — do not continue it.** `git tag --sort=-creatordate`
still lists those preview tags (they remain the chronologically newest until `v1.1.0` exists),
so do not pattern-match them into a `preview.50`. **If the newest tag you see is a `preview.*`,
the version to cut is `v1.1.0`** — the first release under this scheme.

**Why the line starts at 1.1.0, and why you must not "fix" it back to 0.x.** The published
history ends at `1.0.0-preview.49`; `1.0.0` itself was never formally cut and never will be.
The intuitive successor for a pre-1.0 product — restarting at `0.13.0` — is **wrong, and
unfixable once published**: under SemVer `0.13.0 < 1.0.0-preview.49` (major 0 sorts below
major 1), so NuGet would read the "new" release as a DOWNGRADE. Two concrete breakages:
`dotnet tool update` would refuse to move an existing `1.0.0-preview.*` install forward onto
`0.13.0`; and `--prerelease` would resolve `1.0.0-preview.49` — an OLDER build — while a plain
install resolved the newer `0.13.0`. Starting the stable line at **`1.1.0`** puts every future release
strictly above every published preview, which is the whole point. A future maintainer who
wants 0.x cannot have it without abandoning the package id — this paragraph is the answer,
not a bug to file.

NuGet won't let you republish, so a typo'd or reused version wastes a number permanently.

## Cut it

Annotate the tag with the headline changes since the previous tag. **This annotation matters more
than it looks.** The `create-release` job publishes the GitHub Release with `--generate-notes`,
which emits a raw list of merged commits and PR titles — accurate, and useless to someone deciding
whether to upgrade. Your tag annotation and the notes you write over the generated ones are the
**only** places a human-readable upgrade warning can live. If this release carries a breaking
change, a migration step, or a reason to upgrade promptly, it has to be said here or it is not
said at all.

```bash
# v1.2.0 below is a WORKED EXAMPLE — substitute the version you picked above.
# <PREV-TAG> is the previous tag verbatim, whatever its scheme — for the first stable cut
# that is still v1.0.0-preview.49.
git -C <repo> log <PREV-TAG>..origin/master --oneline    # source the summary
git -C <repo> tag -a v1.2.0 -m "v1.2.0 — <one-line theme>

<short bullet summary of the notable #issue fixes since the last tag>"
git -C <repo> push origin v1.2.0
```

The tag push is the trigger.

**The pipeline creates the GitHub Release itself.** The `create-release` job runs
`gh release create "$TAG" --title "$TAG" --generate-notes` (adding `--prerelease` for any tag
carrying a suffix, which is what keeps throwaway `-ci.` dry-run tags out of "latest"). It is
idempotent — it reuses an existing Release rather than failing — and the five `binaries` jobs then
upload their archives as assets to it.

So `gh release list` is **not** empty; every `vX.Y.0` is there. Do not create the Release object by
hand — the pipeline owns it, and a hand-made one races the job. **Your job is the notes**, not the
object. Where the release warrants it, replace the generated commit list with something a consumer
can act on:

```bash
gh release view v1.2.0 --repo Servant-Software-LLC/Guardrails          # read what --generate-notes produced
gh release edit v1.2.0 --repo Servant-Software-LLC/Guardrails --notes-file notes.md
```

A routine release can keep the generated notes. One with an upgrade note or a breaking change
cannot — see the annotation guidance above.

## Watch the pipeline to completion

```bash
.github/scripts/release-preflight.sh ci <sha>   # find the run, poll it, print every job's conclusion
```

That polls correctly and refuses to build a run URL from an empty id (a hand-written `--jq` filter
that yielded nothing produced a malformed URL and a 404 during a real cut). By hand:

```bash
gh run list --repo Servant-Software-LLC/Guardrails --workflow release.yml --limit 3   # find the run id
```

Poll the **top-level run status**, then check the conclusion + every job:

```bash
# GOTCHA: poll `.status` of the RUN, not a nested job. `--json jobs` will show the
# packaged-tool-smoke job "completed" while the run is still in_progress — don't mistake
# that for the whole run finishing.
gh run view <run-id> --repo Servant-Software-LLC/Guardrails --json status --jq '.status'
# once "completed":
gh run view <run-id> --repo Servant-Software-LLC/Guardrails \
  --json conclusion,jobs --jq '{conclusion, jobs:[.jobs[]|{name,conclusion}]}'
```

Success = `conclusion: success` and **all eleven jobs** green:

| Job | What it is |
|---|---|
| `test (windows-latest)` | the 3-OS matrix: restore, build, Core, Integration, the whole solution |
| `test (ubuntu-latest)` | concurrently (#566), and every example's diagrams fresh (#636) |
| `test (macos-latest)` | |
| `packaged-tool-smoke (ubuntu)` | pack the tag version → install to an isolated tool-path → assert the `skills/` payload shipped and is version-stamped (#171) |
| `pack and publish to NuGet.org` | needs both of the above; the only job that touches NuGet |
| `create GitHub Release` | needs both of the above; creates the Release with `--generate-notes` |
| `binary (osx-arm64)` | the five self-contained single-file binaries. All need `create-release`. |
| `binary (osx-x64)` | The `osx-*` legs run on a macOS runner because `codesign` is macOS-only: |
| `binary (linux-x64)` | each is ad-hoc signed (required to exec at all on Apple Silicon, #415) and |
| `binary (linux-arm64)` | Developer-ID-signed + notarized when the signing secrets exist. Each |
| `binary (win-x64)` | uploads its archive + `.sha256` to the Release. |

Two independent branches hang off the same gate: `publish` (NuGet) and `create-release` →
`binaries` (the GitHub Release and its assets). A build-green-but-package-broken state (e.g. #169)
fails the smoke *before* either branch can run. Note `publish` is skipped for `-ci.` dry-run tags,
which still exercise the test, smoke, release and binary jobs — so on such a tag ten green jobs and
one skipped is the expected result, not a failure.

**Check the test counts, don't just read the tick.** A green run that quietly stopped running some
tests looks exactly like a green run:

```bash
.github/scripts/release-preflight.sh verify <run-id>   # counts vs the recorded baseline
```

Totals must be identical across all three OS; skips legitimately differ per platform, which is why
the baseline records them. A changed **total** is expected when tests were added. A changed
**skipped** under an unchanged total means a test stopped running somewhere — invisible in the
tick, and the shape that cost v1.15.0. Re-record with `baseline <run-id>` once you have explained
the difference, and commit the file.

## Confirm live

The publish job succeeding means `dotnet nuget push` was accepted. **NuGet indexing lags a
couple of minutes**, so an immediate `dotnet tool install` may not resolve yet. Confirm the
version is actually on the feed before telling anyone it shipped:

```bash
.github/scripts/release-preflight.sh published 1.2.0
```

Then tell the consumer:

```bash
dotnet tool install --global ServantSoftware.Guardrails          # newest stable
dotnet tool update  --global ServantSoftware.Guardrails          # move an existing install forward
dotnet tool install --global ServantSoftware.Guardrails --version 1.2.0   # pin exactly
```

**No `--prerelease` anywhere.** Releases carry no prerelease suffix now, so a plain
`install`/`update` resolves the newest release. If you find `--prerelease` in an instruction,
a README, or an install script, it is stale — drop it (leaving it in is not fatal, but it
teaches users a flag that no longer means anything for this package).

## If it fails

- **A `test` or `packaged-tool-smoke` job fails** → the code/package has a real problem on
  the tagged commit. The version is NOT published (the `publish` job `needs:` both). Fix on
  `master` via the normal PR flow, then cut a **new** tag — the next minor (`v1.2.0` failed →
  cut `v1.3.0`). You cannot re-use a tagged version, and you do not "retry" it as a patch.
- **Only the `publish` job fails, on NuGet login/auth** → a nuget.org Trusted-Publishing
  policy or `NUGET_USER` secret issue; the code is fine. Escalate to the maintainer (it's
  their nuget.org account config). Re-running just the failed job after they fix it can
  complete the same release without a new tag.
- **You tagged the wrong commit / wrong version and the pipeline hasn't published yet** →
  you can delete the tag locally and on origin (`git -C <repo> tag -d v…; git -C <repo>
  push origin :refs/tags/v…`) to abort, then re-tag correctly. Once the `publish` job has
  pushed to NuGet, the version is permanent — do NOT try to "fix" it by republishing; cut
  the next minor instead.
- **`Transient_PausesAndResumes_WithoutConsumingRetryBudget` failed, and only that** → check the
  clock before you believe it. While **#747** is open, that test fails *deterministically* in the
  **10:50–11:20 window** on the clock of whatever machine ran it (UTC on a CI runner). The fixture
  hardcodes `resetHint: "11:20am"` and asserts the wait equals the 30-minute probe interval + 4s;
  inside that window `min(probeInterval, timeUntilReset)` correctly picks the smaller remainder and
  the equality fails. It is arithmetic, not flake and not load — so **do not "just re-run" it**, and
  do not burn a version number on it. `preflight` refuses to start a release inside that window for
  exactly this reason. Wait until after 11:20, or fix #747.

## Do not

- Do **not** run `dotnet nuget push` by hand or paste a NuGet API key anywhere — OIDC does
  the authenticated publish. Handling the key is both unnecessary and a prohibited
  credential operation.
- Do **not** tag a commit that isn't on `origin/master` or isn't CI-green.
- Do **not** reuse or hand-edit an already-published version to "patch" it.
- Do **not** re-open the `1.0.0-preview.N` line, add a prerelease suffix to a release tag, or
  renumber down to `0.x` — all three are SemVer *downgrades* against the published
  `1.0.0-preview.49` and are irreversible once pushed (see **Pick the version**).
