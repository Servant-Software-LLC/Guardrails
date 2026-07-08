---
name: cut-release
description: |
  Cut a new NuGet release of the `ServantSoftware.Guardrails` dotnet tool. Verify the
  tree is fully pushed and green, pick the next version, tag it at master HEAD, and let
  the `release.yml` pipeline publish to NuGet.org via Trusted Publishing (OIDC — no API
  key to handle). Use when the maintainer says "cut a release", "publish a new version",
  "ship a NuGet package", or "release preview.N".

  MAINTAINER-ONLY: this skill is NOT packed into the shipped tool (the csproj bundles only
  plan-breakdown / guardrails-review / guardrails-domain-knowledge). It lives in the repo
  for anyone releasing Guardrails.

  SELF-UPDATING: if the release mechanism changes (trigger, versioning, auth, the
  pipeline jobs), update this skill AND `.github/workflows/release.yml` together.
---

# Cut a release

Cutting a release of the `ServantSoftware.Guardrails` dotnet tool **is one action: push a
`v*` git tag.** Everything else is the `release.yml` pipeline. The version is derived from
the tag (leading `v` stripped): tag `v1.0.0-preview.36` publishes `1.0.0-preview.36`.

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

## Pick the version

```bash
git -C <repo> tag --sort=-creatordate | head -5
```
The scheme is `v1.0.0-preview.N`, monotonically increasing. **Default: bump the preview
number by one** (`preview.35` → `preview.36`). A stable `v1.0.0` or a minor/major bump is a
**deliberate human decision** — if the maintainer hasn't said otherwise, use the next
preview and state which version you're cutting. NuGet won't let you republish, so a typo'd
or reused version wastes a number.

## Cut it

Annotate the tag with the headline changes since the previous tag (helps the release notes
and the git history read well):

```bash
git -C <repo> log v1.0.0-preview.<PREV>..origin/master --oneline    # source the summary
git -C <repo> tag -a v1.0.0-preview.<N> -m "v1.0.0-preview.<N> — <one-line theme>

<short bullet summary of the notable #issue fixes since the last tag>"
git -C <repo> push origin v1.0.0-preview.<N>
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
dotnet tool install --global ServantSoftware.Guardrails --version 1.0.0-preview.<N>
# (or `dotnet tool update --global …` to move an existing install forward)
```

## If it fails

- **A `test` or `packaged-tool-smoke` job fails** → the code/package has a real problem on
  the tagged commit. The version is NOT published (the `publish` job `needs:` both). Fix on
  `master` via the normal PR flow, then cut a **new** tag (`preview.<N+1>`) — you cannot
  re-use `<N>`.
- **Only the `publish` job fails, on NuGet login/auth** → a nuget.org Trusted-Publishing
  policy or `NUGET_USER` secret issue; the code is fine. Escalate to the maintainer (it's
  their nuget.org account config). Re-running just the failed job after they fix it can
  complete the same release without a new tag.
- **You tagged the wrong commit / wrong version and the pipeline hasn't published yet** →
  you can delete the tag locally and on origin (`git -C <repo> tag -d v…; git -C <repo>
  push origin :refs/tags/v…`) to abort, then re-tag correctly. Once the `publish` job has
  pushed to NuGet, the version is permanent — do NOT try to "fix" it by republishing; cut
  the next preview instead.

## Do not

- Do **not** run `dotnet nuget push` by hand or paste a NuGet API key anywhere — OIDC does
  the authenticated publish. Handling the key is both unnecessary and a prohibited
  credential operation.
- Do **not** tag a commit that isn't on `origin/master` or isn't CI-green.
- Do **not** reuse or hand-edit an already-published version to "patch" it.
