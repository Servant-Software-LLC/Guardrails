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

1. **Everything merged and pushed.** No open PR you intend to include is still unmerged.
   ```bash
   git -C <repo> fetch origin
   git -C <repo> status --short                 # only expected untracked drafts, nothing staged
   git -C <repo> log origin/master..HEAD --oneline   # MUST be empty (nothing unpushed)
   git -C <repo> rev-parse HEAD origin/master        # the two SHAs MUST match
   ```
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
   `docs/plans/` that were never part of the release.
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

Annotate the tag with the headline changes since the previous tag (helps the release notes
and the git history read well):

```bash
# v1.2.0 below is a WORKED EXAMPLE — substitute the version you picked above.
# <PREV-TAG> is the previous tag verbatim, whatever its scheme — for the first stable cut
# that is still v1.0.0-preview.49.
git -C <repo> log <PREV-TAG>..origin/master --oneline    # source the summary
git -C <repo> tag -a v1.2.0 -m "v1.2.0 — <one-line theme>

<short bullet summary of the notable #issue fixes since the last tag>"
git -C <repo> push origin v1.2.0
```

The tag push is the trigger. **Do not** create a GitHub "Release" object by hand — this
repo releases by tag; `gh release list` is normally empty.

## Watch the pipeline to completion

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

Success = `conclusion: success` and all five jobs green:
`test (windows-latest)`, `test (ubuntu-latest)`, `test (macos-latest)`,
`packaged-tool-smoke (ubuntu)`, and **`pack and publish to NuGet.org`**.

The pipeline gate (`release.yml`): the 3-OS test matrix **and** the packaged-tool-smoke
(pack the tag version → install to an isolated tool-path → assert the `skills/` payload
shipped and is version-stamped, #171) must pass; only then does `publish` pack and push.
A build-green-but-package-broken state (e.g. #169) fails the smoke *before* it can publish.

## Confirm live

The publish job succeeding means `dotnet nuget push` was accepted. **NuGet indexing lags a
couple of minutes**, so an immediate `dotnet tool install` may not resolve yet. Tell the
consumer:

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

## Do not

- Do **not** run `dotnet nuget push` by hand or paste a NuGet API key anywhere — OIDC does
  the authenticated publish. Handling the key is both unnecessary and a prohibited
  credential operation.
- Do **not** tag a commit that isn't on `origin/master` or isn't CI-green.
- Do **not** reuse or hand-edit an already-published version to "patch" it.
- Do **not** re-open the `1.0.0-preview.N` line, add a prerelease suffix to a release tag, or
  renumber down to `0.x` — all three are SemVer *downgrades* against the published
  `1.0.0-preview.49` and are irreversible once pushed (see **Pick the version**).
