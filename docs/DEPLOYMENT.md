# Deploying Guardrails to another machine

This is the runbook for installing Guardrails somewhere other than the dev box —
e.g. a work laptop — so you can run the full loop (`/plan-breakdown` → review →
`/guardrail-review` → `guardrails run`) against **your own** plan and codebase.

Deployment has **two parts** that people often miss:

1. **The harness** — the `guardrails` CLI (a .NET tool).
2. **The skills** — `plan-breakdown` and `guardrail-review`, which live in Claude
   Code's skill directory, not in the .NET tool.

You need both. The harness *runs* a task folder; the skills *generate and review*
that folder from your plan. Your plan and the code it operates on never leave your
machine and are never published anywhere.

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
dotnet tool install --global --add-source ./nupkg ServantSoftware.Guardrails --prerelease
```

`guardrails` is now on PATH. Verify:

```bash
guardrails validate examples/hello-guardrails/hello-guardrails   # → "OK: plan is valid."
```

To update later: `dotnet tool update --global --add-source ./nupkg ServantSoftware.Guardrails --prerelease`.

### Option B — Public NuGet (the clean one-liner, ~15 min one-time setup)

Once the package is on NuGet.org, **every machine** installs with a single command —
no clone, no pack. This is the best long-term / team / cross-OS story and is the
nicest thing to show in a Lunch & Learn ("installing is one line").

One-time setup (done once, by whoever owns the Servant Software NuGet account):

1. Create a NuGet.org API key scoped to push `ServantSoftware.Guardrails`.
2. Add it as the `NUGET_API_KEY` secret on the GitHub repo
   (Settings → Secrets and variables → Actions → New repository secret).
3. Push the release tag: `git tag v1.0.0-preview.1 && git push origin v1.0.0-preview.1`.
   The release workflow runs the 3-OS test matrix, then packs and pushes to NuGet.

Then, on any machine:

```bash
dotnet tool install --global ServantSoftware.Guardrails --prerelease
```

**Recommendation:** Use **Option A this week** to unblock dogfooding now — it depends
on nothing and no one. Do **Option B in parallel** when you want the one-line install
for the demo and for eventual Mac/Linux boxes. It is *not* much extra work (one secret
+ one tag), and it does not change a single line of code — the tool is already
`net8.0` and CI already proves it green on windows/ubuntu/macos.

---

## Part 2 — Install the skills

The skills are folders. Copy the ones you need into your **user** skills directory
(`~/.claude/skills/`) so `/plan-breakdown` is available in *any* repo on the machine —
including your work repo, which is where your plan and code live.

**Required** for the loop:

- `plan-breakdown` (includes its `references/` — the guardrail catalogue, schema
  excerpt, and worked example; the folder is self-contained)
- `guardrail-review`

**Recommended** (improves breakdown quality — gives the agent the domain model):

- `guardrails-domain-knowledge`

Windows (PowerShell), from the cloned repo root:

```powershell
$dst = "$HOME\.claude\skills"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item -Recurse -Force .\.claude\skills\plan-breakdown           $dst
Copy-Item -Recurse -Force .\.claude\skills\guardrail-review         $dst
Copy-Item -Recurse -Force .\.claude\skills\guardrails-domain-knowledge $dst
```

macOS / Linux:

```bash
mkdir -p ~/.claude/skills
cp -r .claude/skills/plan-breakdown            ~/.claude/skills/
cp -r .claude/skills/guardrail-review          ~/.claude/skills/
cp -r .claude/skills/guardrails-domain-knowledge ~/.claude/skills/
```

Restart Claude Code (or start a new session) and confirm `/plan-breakdown` appears.

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
   /guardrail-review path/to/your-plan      → "what wrong implementation passes these?"
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
