# UI verification — two levels (#41, #78)

How `plan-breakdown` verifies **browser-rendered UI**. This is the cross-stack methodology
reference paired with SKILL.md `Step 4b / 5c — Two-level UI verification`. It is doctrine, not a
concrete driver stack file: the per-driver Playwright/Cypress invocation idioms are **v2** (see
"The v2 boundary" below). Read this when a plan is UI-facing.

## Why two levels exist

Three checks already verify a UI plan, each more than the last — **none of them DRIVE the UI in a
real browser**:

| Check | Owned by | Proves | Mechanism | Gap |
|---|---|---|---|---|
| Entry-point wiring + smoke-test (#64) | SKILL.md Step 4/5, `stacks/dotnet.md §7–§8` | the exe *starts and serves* | start binary → poll route → assert HTTP 200 → `finally` teardown | answers with *anything*, incl. JSON |
| UI-presence / served-markup (#66) | SKILL.md Step 4/5, `stacks/dotnet.md §9` | the described UI is *built and served* | one `GET`; body contains a known UI marker | one request; never clicks or navigates |
| **Two-level UI verification (#41/#78)** | **this reference + SKILL.md Step 4b/5c** | the page *mounts error-free* (A) and the *flow works* (B) | a real **headless browser driver** (`$e2eStack`) | needs an external driver (v2 for Level B) |

A wizard whose step 2 is broken, whose Next is unwired, or that loses state across steps **passes
#64 and #66 green** — the first page renders, contains its marker, and the binary serves it; nothing
ever exercised the flow. That residual gap is what Level A (partly) and Level B (fully) close.

## Level A — liveness smoke guardrail (v1 doctrine)

**Category:** the browser-driver generalization of catalogue archetype #7 ("probe the running
artifact": service → curl; web UI → headless-browser probe). Default for any UI-producing task
**when a driver is detected**.

**Asserts liveness only, never behavior:**
- the page mounts in a headless browser;
- **no console errors / unhandled promise rejections** on load;
- a **structural selector derived from the plan** (a heading, region, or `data-testid` the plan
  names) is present.

**No anti-tautology scaffolding needed.** The tautology surface is minimal — you cannot make a
broken page emit zero console errors — so Level A does not carry the TDD chain. It is build-time
proof the artifact stands up once, exactly like #7's service-liveness check.

**What Level A does NOT catch:** behavior. A Back button that wipes the form, an unwired Next, a
wrong computed total all pass Level A — the page still mounts cleanly. That is Level B.

**Determinism:** deterministic when a driver is detected (scripted mount + console-error assertion +
selector presence). It is **never** a prompt-judge — "does this look like a good UI" is forbidden,
same stance as #66.

## Level B — behavioral interaction-flow spec (v2 — #78)

**Category:** a new **interaction-flow** archetype (v2). Drives the served UI through the multi-step
flow the plan describes (headless) and asserts the terminal observable:

> fill step 1 → click *Next* → assert step 2 visible → click *Back* → assert step-1 input retained →
> … → submit → assert confirmation.

**It is a real authored test** carrying the **full TDD anti-tautology chain** — `tests-fail-on-current-code`
plus the `writeScope` test-exclusion (SKILL.md Step 5's TDD pair: the test-author task owns the spec
file; the implementation task's `writeScope` EXCLUDES it). It:
- reuses #64's §8 process lifecycle (start the binary on a deterministic port, `finally` teardown) —
  it does **not** re-implement process management;
- launches the driver **headless**, navigates the UI route, performs the **scripted interaction**;
- asserts on **stable selectors** (`id` / `data-testid` / role), **never screenshots or pixels**;
- prints **one actionable failure line** naming the failing step ("interaction-flow: step 2
  'destination' not shown after clicking Next on step 1").

Deterministic — scripted selectors + assertions, not a visual prompt-judge.

### The trigger (the load-bearing decision rule)

Insert the author-spec + run-spec chain when the deliverable carries **regression-bearing logic
reachable only through the artifact** — **NOT** when the plan prose happens to name an "E2E suite"
(plans under-specify tests; that is the whole reason this skill INSERTS unit-test tasks the plan
never mentioned). Decide per exit criterion:

| Criterion shape | Level |
|---|---|
| Does it mount, does the button wire up, is the marker served (UI **glue**) | A (+ §9 served-markup) |
| A computed total, a validation rule rendered in-page, state carried across steps, "complete the wizard" (**logic** behind the UI) | B (v2) |

### E2E anti-tautology note (carry into the v2 spec)

A blank spec "fails on current code" **trivially** because no server = no page — that satisfies
`tests-fail-on-current-code` via *infrastructure*, not behavior. The spec must fail **against a
running app with the feature absent** (assert the specific element/text), not against a dead port.
The `writeScope` test-exclusion leg ports unchanged; only the failure-cause leg needs this
E2E-specific guidance.

### Durability

A Level-B spec is a real file at the CI-globbed path (`tests/e2e/*.spec.ts`) that CI re-runs
forever. The guardrail is build-time-only; the spec is permanent coverage. Land it where CI globs
it — an authoring constraint, not a reason to avoid the chain.

## Detection — `$e2eStack` (Step 0, second dimension)

E2E tooling is **independent of the build stack** — a .NET repo can have Playwright. Record one
probe value `$e2eStack` ∈ { `playwright` | `cypress` | `none` }:

| Signal | `$e2eStack` |
|---|---|
| `Microsoft.Playwright` PackageReference, or `@playwright/test`/`playwright` in `package.json` devDependencies, or `playwright.config.{ts,js}` | `playwright` |
| `cypress` in `package.json`, or `cypress.config.{ts,js}` | `cypress` |
| none of the above | `none` |

Resolve a needed driver with the **same priority ladder** as the test-framework choice
(SKILL.md Step 5): **detected in repo → named in plan → ask the human (interactive) → honest-halt +
report (unattended); never silently scaffold.**

**No driver detected → NO guardrail.** Emit a `needsHuman` placeholder and flag it in the breakdown
report: "Tasks NN produce browser-rendered output; no E2E driver detected (checked
playwright/cypress) — install one or accept the coverage gap here." An honest gap beats a fake
green. **The LLM-prompt "does this look right" fallback is explicitly rejected** — it fails the
catalogue demotion gate (deterministic-property, never-alone, echo-judge) and is strictly worse than
no guardrail.

## The v2 boundary (what is deferred, and to where)

**Decided: v2, not v1** (roadmap v2 bet #5, `docs/plans/03-roadmap.md`). The external browser-driver
dependency (Playwright/Cypress, `playwright install`) and the flakiest guardrail archetype are not
wanted in v1.

| Piece | Status | Owner / where it lands |
|---|---|---|
| `$e2eStack` detection rule (this reference + SKILL.md Step 0) | **documented now** | this skill |
| Two-level decision doctrine (A vs B; the trigger; self-review) | **documented now** | this skill |
| Catalogue note generalizing archetype #7 to "probe the running artifact (service → curl; web UI → headless-browser probe)" | **flag-for-lead** | sibling unit owns `guardrail-catalogue.md` this batch |
| Level-A headless-probe idiom + Level-B interaction-flow archetype (scripted steps, stable selectors, deterministic waits, `finally` teardown, one failure line, no visual judge) | **flag-for-lead** | sibling unit owns `stacks/dotnet.md` this batch |
| Concrete driver invocation (`references/e2e/<driver>.md`: headless launch, readiness-probe loop, `try/finally` server stop, per-tool idioms) | **deferred — v2** | new `references/e2e/<driver>.md` when a SECOND real web-UI plan exists (exactly one does today) |
| `$e2eStack` harness/CI support (`playwright install`, 3-OS CI matrix cost, `guardrails validate` probe step) | **deferred — v2** | `src/**` (harness), `guardrails-harness-developer` |

**Why defer the concrete stack file.** Exactly one web-UI plan exists today
(`0003-commander-desktop-pivot`); a full `references/e2e/playwright.md` (readiness-probe loop,
per-tool invocation) is premature generalization until a second one surfaces — same drop-in routing
as the build-stack files (author the file later; detection already routes to it).

### Readiness-probe idiom (for the deferred v2 stack file — do NOT use a fixed sleep)

When the v2 driver stack file is authored, replace any `Start-Sleep -Seconds N` with a deadline poll
(a fixed sleep is the hidden-state / flaky anti-pattern):

```powershell
$deadline = [DateTime]::UtcNow.AddSeconds(30); $ready = $false
while ([DateTime]::UtcNow -lt $deadline) {
  try {
    if ((Invoke-WebRequest "http://localhost:$port/health" -TimeoutSec 2 -ErrorAction Stop).StatusCode -eq 200) {
      $ready = $true; break
    }
  } catch {}
  Start-Sleep -Milliseconds 500
}
if (-not $ready) {
  Write-Output "server did not become ready on port $port within 30 s"
  exit 1
}
```

Mitigations are mandatory, not optional, for the interaction-flow archetype (it is the flakiest by
far): web-first / auto-waiting assertions or explicit waits (never fixed sleeps), bounded retry on
*navigation*, headless determinism, a single deterministic port. Speed/retry-cheapness warrants
scoping guidance: **one flow per task; do not fold five flows into one guardrail.**

## Step 7 self-review (the interaction dimension)

Extend the Step 7.0 UI exit-criteria self-review:
- An exit criterion phrased as a **multi-step interaction** ("complete the wizard", "next/back
  navigation", "state carried across steps", "submit and see the confirmation") covered by **only** a
  served-markup guardrail (no Level-B task) is an **under-coverage flag** — §66 proves the first
  screen renders; "complete the wizard" needs the flow driven. Surface it the same way §66 surfaces
  "promised a frontend, built zero UI."
- An **unspecified flow** (the plan names the outcome but not the concrete steps/selectors) is a
  human decision, surfaced in the report — never an invented interaction script.

## Where truth lives

- Schema / contract details: `docs/plans/02-schemas-and-contracts.md` (SSOT).
- Roadmap v2 bet #5 (E2E web-UI verification): `docs/plans/03-roadmap.md`.
- The #64/#66 checks this builds on: SKILL.md Step 4/5; `stacks/dotnet.md §7–§9`.
- The decision tree and demotion gate: `references/guardrail-catalogue.md`.
