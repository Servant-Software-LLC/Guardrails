# Deploying Guardrails to another machine

This is the runbook for installing Guardrails somewhere other than the dev box —
e.g. a work laptop — so you can run the full loop (`/plan-breakdown` → review →
`/guardrails-review` → `guardrails run`) against **your own** plan and codebase.

Deployment has **two parts** — but the tool now carries the skills, so a single
bootstrap does both:

1. **The harness** — the `guardrails` CLI (a .NET tool).
2. **The skills** — `plan-breakdown` and `guardrails-review`, which live in Claude
   Code's skill directory. These are **bundled inside the tool package** and copied
   into place by `guardrails skills install`.

You need both. The harness *runs* a task folder; the skills *generate and review*
that folder from your plan. Your plan and the code it operates on never leave your
machine and are never published anywhere.

---

## Quickest path — the one-command bootstrap

On Windows, from PowerShell:

```powershell
irm https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.ps1 | iex
```

This verifies `dotnet`, installs (or updates) the `guardrails` tool, and runs
`guardrails skills install` for you. macOS/Linux have an `install.sh` twin (mirrors
`install.ps1`; not yet tested on the maintainer's box):

```bash
curl -fsSL https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.sh | bash
```

Prefer the explicit two-liner? It does exactly the same thing:

```bash
dotnet tool install --global ServantSoftware.Guardrails --prerelease
guardrails skills install
```

`guardrails skills install` copies the bundled skills into `~/.claude/skills`
(`--target <dir>` to override, `--force` to overwrite existing skill folders).
Restart Claude Code afterwards so `/plan-breakdown` and `/guardrails-review` appear.

The sections below explain the moving parts and the local-pack path for when the
package is not yet on NuGet.org.

---

## Upgrading

Re-run the **same bootstrap** — it detects the installed tool and *updates* it, then
refreshes the deployed skills with `--force`:

```powershell
irm https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.ps1 | iex
```

Doing it by hand is two steps, and the second needs `--force` — a plain `skills install`
**skips** folders that already exist, so without it your `~/.claude/skills` copies stay
stale after a tool update:

```bash
dotnet tool update --global ServantSoftware.Guardrails --prerelease   # harness + bundled skills
guardrails skills install --force                                      # refresh the deployed skill copies
```

`dotnet tool update` cleanly replaces the harness binary and the skills bundled *inside*
the package; only the copies you previously installed into `~/.claude/skills` need the
`--force` refresh. Restart Claude Code afterwards.

---

## Prerequisites

| Requirement | Why | Windows | macOS / Linux |
|---|---|---|---|
| **.NET 8+ SDK** | Build/install the tool; `RollForward LatestMajor` runs it on 8/9/10 | `winget install Microsoft.DotNet.SDK.8` | `brew install dotnet` / distro package |
| **git** | Clone the public repo | `winget install Git.Git` | usually present |
| **Claude Code CLI**, authenticated | Prompt **actions/guardrails** shell out to `claude -p`. Deterministic-only plans don't need it. | [claude.com/claude-code](https://claude.com/claude-code) | same |
| **PowerShell** | Only if your plan uses `.ps1` guardrails | built-in (`powershell.exe`) | **install `pwsh` (PowerShell 7)** — `.ps1` resolves to `pwsh` off-Windows |

> **Corporate gotcha — check this first.** The most likely Monday-morning failure is
> Claude Code not being authenticated (or blocked by proxy/policy) on the work
> machine. Verify `claude -p "say hi"` works there *before* you rely on it in a demo.

---

## Part 1 — Install the harness

### Option A — Quickest, self-contained (no public release, no secrets)

Clone the public repo, pack locally, install as a global tool. ~2 minutes; works
identically on Windows/macOS/Linux.

```bash
git clone https://github.com/Servant-Software-LLC/Guardrails.git
cd Guardrails
dotnet pack src/Guardrails.Cli -c Release -o nupkg
./install.ps1 -Source ./nupkg          # Windows: installs the tool + the skills
# or, explicitly / on macOS-Linux:
dotnet tool install --global --add-source ./nupkg ServantSoftware.Guardrails --prerelease
guardrails skills install
```

`guardrails` is now on PATH and the skills are installed. Verify:

```bash
guardrails validate examples/hello-guardrails/hello-guardrails   # → "OK: plan is valid."
```

To update later: `dotnet tool update --global --add-source ./nupkg ServantSoftware.Guardrails --prerelease`
(then re-run `guardrails skills install --force` to refresh the skills).

### Option B — Public NuGet (the clean one-liner, ~15 min one-time setup)

Once the package is on NuGet.org, **every machine** installs with a single command —
no clone, no pack. This is the best long-term / team / cross-OS story and is the
nicest thing to show in a Lunch & Learn ("installing is one line").

One-time setup uses **Trusted Publishing** (OIDC) — no long-lived API key to create,
store, or rotate. Done once, by whoever owns the Servant Software NuGet account:

1. On **nuget.org** → your username → **Trusted Publishing** → add a policy:
   - Repository Owner = `Servant-Software-LLC`, Repository = `Guardrails`
   - Workflow File = `release.yml` (file name only), Environment = *(empty)*
   - Policy owner = the account/org that will own the `ServantSoftware.Guardrails` package
2. Add **one** repo secret `NUGET_USER` = your nuget.org **profile name** (not email).
   It is not sensitive — it only tells the workflow which account to mint a key for.
3. Push the release tag: `git tag v1.0.0-preview.1 && git push origin v1.0.0-preview.1`.
   The workflow runs the 3-OS test matrix, then (in the publish job) mints a GitHub OIDC
   token, exchanges it for a short-lived NuGet key via the policy, and pushes.

> The package version comes from the **tag** (`v1.2.3` → `1.2.3`), so each subsequent
> release is just `git tag vX.Y.Z && git push origin vX.Y.Z` — no key, no csproj edit.
> (Public repos activate the policy immediately; a *private* repo's policy is provisional
> for 7 days and locks in on the first successful publish.)

Then, on any machine:

```bash
dotnet tool install --global ServantSoftware.Guardrails --prerelease
guardrails skills install
```

**Recommendation:** Use **Option A this week** to unblock dogfooding now — it depends
on nothing and no one. Do **Option B in parallel** when you want the one-line install
for the demo and for eventual Mac/Linux boxes. It is *not* much extra work (one secret
+ one tag), and it does not change a single line of code — the tool is already
`net8.0` and CI already proves it green on windows/ubuntu/macos.

---

## Part 2 — Install the skills

The skills are **bundled inside the tool package** (under `skills/` next to the entry
assembly). The bootstrap and `guardrails skills install` put them in your **user**
skills directory (`~/.claude/skills/`) so `/plan-breakdown` is available in *any* repo
on the machine — including your work repo, where your plan and code live.

```bash
guardrails skills install            # → ~/.claude/skills (user-level: every repo)
guardrails skills install --project  # → ./.claude/skills (this repo only; created if missing)
guardrails skills install --target /custom/skills/dir   # explicit destination
guardrails skills install --force    # overwrite skill folders that already exist
```

`guardrails install skills` is a hidden alias for the same thing, if that word order
is the one in your fingers. User-level (`~/.claude/skills`, the default) is usually what
you want — it makes `/plan-breakdown` available in *every* repo, including your work repo.
Use `--project` when you'd rather scope the skills to one repo (e.g. to commit them with it).

The three bundled skills:

- `plan-breakdown` (includes its `references/` — the guardrail catalogue, schema
  excerpt, and worked example; the folder is self-contained) — **required**
- `guardrails-review` — **required**
- `guardrails-domain-knowledge` — recommended (gives the agent the domain model,
  improving breakdown quality)

Without `--force`, a skill folder that already exists in the target is left untouched
and reported as skipped. Restart Claude Code (or start a new session) and confirm
`/plan-breakdown` appears.

> **Last resort — manual copy.** If you only have the source checkout (no installed
> tool), copy the folders by hand from the repo root:
>
> ```powershell
> # Windows
> $dst = "$HOME\.claude\skills"; New-Item -ItemType Directory -Force $dst | Out-Null
> Copy-Item -Recurse -Force .\.claude\skills\plan-breakdown            $dst
> Copy-Item -Recurse -Force .\.claude\skills\guardrails-review         $dst
> Copy-Item -Recurse -Force .\.claude\skills\guardrails-domain-knowledge $dst
> ```
>
> ```bash
> # macOS / Linux
> mkdir -p ~/.claude/skills
> cp -r .claude/skills/plan-breakdown ~/.claude/skills/
> cp -r .claude/skills/guardrails-review ~/.claude/skills/
> cp -r .claude/skills/guardrails-domain-knowledge ~/.claude/skills/
> ```

> `guardrails-dev-knowledge`, `uber-report`, and the `.claude/agents/` are for working
> on the Guardrails harness *itself* — you don't need them just to *use* Guardrails on
> a work plan.

---

## Part 3 — Smoke-test the install (optional, ~$1 of tokens)

Prove the end-to-end prompt pipeline works on the new machine before trusting it live:

```bash
guardrails run examples/hello-guardrails/hello-guardrails --fresh --no-ui   # exit 0 = green
```

This fires real `claude -p` calls. If you'd rather not spend tokens, at least run
`guardrails plan examples/hello-guardrails/hello-guardrails` (executes nothing).

---

## Part 4 — The dogfood loop (your plan, your machine, private)

In your **work repo** (so `workspace` can point at the code the plan operates on):

```text
1. Keep your plan as a markdown file — anything from a one-shot prompt to a full design.
2. /plan-breakdown path/to/your-plan.md
      → generates path/to/your-plan/  (tasks, dependsOn DAG, guardrails)
      → presents it as a DRAFT and self-runs `guardrails validate`
3. Review the draft. Edit guardrails where the check is weak or wrong.
   /guardrails-review path/to/your-plan     → "what wrong implementation passes these?"
4. guardrails run path/to/your-plan         → to green, or an honest needs-human halt
```

Nothing about your plan is sent anywhere — the skills run in your local Claude Code,
the harness is a local process, and prompt tasks shell out to your own authenticated
`claude` CLI.

---

## Dogfooding-safety note

When you are **using** Guardrails on a work plan, the installed global `guardrails`
command is exactly right.

When you are **developing the Guardrails harness itself** (e.g. running the
`04-dogfood-cost-cap` plan that edits this repo), do **not** use the installed tool to
run a plan that rebuilds that same tool — run from source instead
(`dotnet run --project src/Guardrails.Cli -- run <plan>`) so the binary under test is
never the binary executing the run.
