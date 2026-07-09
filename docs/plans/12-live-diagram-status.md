# Architecture: live-updating `diagram.html` status overlay (issue #219)

Status: DESIGN (design-of-record). Fast-follow, single-milestone. Owner: Guardrails architect.

## What's being asked

While a run is in flight, paint per-node progress **onto the DAG diagram** — a spinner on
each in-flight box, a settled icon (check / X / "?") once it finishes — so the user watches
progress in the mental model the diagram already gave them, instead of context-switching to
the console's ASCII table. Granularity down to individual **task-guardrail** and
**task-preflight** check leaves, plus the **plan-level brackets** ("Full Flight Checks" /
"Terminal Gate"). The issue asserts (correctly) that the mechanism already exists:
`OnTheFlyLogSiteObserver` solves the identical problem for the log site (decorator observer,
one lock, atomic write, best-effort, during-run `meta refresh` vs a final static page).

**Ambiguity named + narrowed.** The issue says "the diagram the user already has open,"
implying we overwrite the plan-root `diagram.html`. That collides with a load-bearing
boundary (below). I narrow it: the live diagram is a **new runtime artifact under
`logs/<runId>/`**, not the plan-root file. The plan-root `diagram.html` stays the canonical,
static, hash-stamped `graph` artifact and is never touched mid-run. Second narrowing:
**per-leaf** live status is available today only for **task guardrails** (they fire
`GuardrailFinished`); task-preflights and plan-level checks have no per-check event, so v1
shows their status at **container** granularity, with per-leaf badges a flagged follow-on.

## Placement

**Harness/CLI (`Guardrails.Cli` observer + `Guardrails.Core` renderer surface) + SSOT §10 +
knowledge skills.** No new schema fields in `guardrails.json`. This is v1 (not a named v2 bet):
it reuses the shipped on-the-fly mechanism and the shipped container-model renderer; it adds
one decorator observer, one pure renderer surface method, and two arguments to an existing
pure renderer.

## Invariants in play

- **#6 Worktree isolation / user checkout is read-only for the run** — *decisive for the
  write-location decision.* The plan-root `diagram.html` is a **tracked, committed-adjacent
  artifact** in the user's checkout; `state/` and `logs/` are **gitignored runtime state** the
  harness owns (see the plan-root `.gitignore` scaffolding, #258). Writing badges into the
  tracked artifact mid-run would dirty the user's tree (and race `graph --check`). The live
  diagram therefore lives under `logs/<runId>/`, exactly like the on-the-fly log site.
- **#4 SSOT is the schema/contract source** — the `HtmlDiagramRenderer.Render` signature and
  the new node-id surface are contracts; §10 gains a "Live status overlay" subsection in the
  same change that adds them.
- **#2 Harness is the single writer of merged state** — unaffected and reinforced: the status
  overlay is pure **chrome** rendered from observer events; it never writes state, never
  changes a verdict, and is best-effort (a render failure is swallowed).
- **Determinism / hash-neutrality (§10, `GraphSourceHashTests`)** — `source-sha256` is a pure
  function of `MermaidRenderer.SemanticContent(plan)`, computed *before* `HtmlDiagramRenderer`
  and passed in as a string. Status is a separate `<script>` blob + JS; it never reaches the
  Mermaid source or `SemanticContent`, so the hash is unchanged **by construction** and
  `graph --check` never spuriously reports stale.

## Design

### D1 — Write location: `logs/<runId>/diagram.html` (NOT the plan-root file)

The during-run and final live diagrams are written to `logs/<runId>/diagram.html`.

Rationale (in priority order):
1. **Respects invariant #6.** `logs/` is gitignored harness runtime state (like the log site,
   `state/`); the plan-root `diagram.html` is a tracked artifact the run must not modify.
2. **Consistent lifecycle.** Cleared by `--fresh` with the rest of `logs/`; excluded from
   `guardrails.baseline`; non-authored — identical treatment to `logs/<runId>/index.html`.
3. **No `graph --check` interaction.** `--check` only ever inspects the **plan-root**
   `diagram.html`; it never looks under `logs/`. The plan-root file keeps its stamped
   `source-sha256` and never shows frozen badges.
4. **Avoids a stale committed-adjacent file.** If we overwrote the plan-root file, after the
   run it would sit there frozen at a during-run snapshot (or need a cleanup write) — a
   footgun. Under `logs/<runId>/` the final settled page IS the post-mortem artifact.

Addressing "the diagram the user already has open": print a clickable `file://` link to
`logs/<runId>/diagram.html` at run start — the exact UX `PrintStaticIndexLink` already uses for
the log index. It is the **same DAG** (same `source-sha256`, same click-throughs via
`RenderInteractive`), so the user learns no new view; the cost is one click. A future
`--live-diagram-in-place` opt-in for serial mode is possible but **YAGNI** now.

**Final settled page keeps the badges.** The end-of-run write to `logs/<runId>/diagram.html`
drops the `meta refresh` and shows every node's settled icon — a durable post-mortem of the
run, mirroring the durable final log site. The plan-root `diagram.html` never gets badges.

### D2 — During-run vs final duality

- **During-run:** rewritten after each forwarded observer event; `<head>` carries
  `<meta http-equiv="refresh" content="2">`; in-flight nodes show an animated spinner. A
  plain `file://` view re-reads itself — no server needed (mirrors the log site exactly).
- **Final:** written once at run end, **no** `meta refresh`, all nodes settled. Wired in
  `RunCommand` right beside the log site's `WriteDurableFinalSite(...)` call in `Finish`
  (`RunCommand.cs` ~line 414) — but sourced from the observer's own in-memory status map
  (strictly more accurate than re-deriving per-guardrail status from the journal, which the
  journal does not retain per-leaf). Concretely: the decorator exposes
  `WriteFinalStatic()`, called once after `ExecuteAsync` returns while the observer is in
  scope, best-effort.
- **Resume correctness:** seed the observer's status map from the journal in its constructor
  (task-level statuses; already-succeeded tasks' guardrail leaves → passed, needs-human tasks'
  failed checks → X). A resume where every task is already `succeeded` drains with no events,
  so without seeding the final page would wrongly read all-pending. (This is a SHOULD; the live
  path is the MUST.)

### D3 — Node-id → status map surface (the core seam)

The overlay JS decorates SVG elements by their DOM id. The C# observer receives **semantic**
events (`task.Id`, `GuardrailResult.Name`) and must translate them to the **exact** SVG node
ids the renderer emitted. That translation is the new surface `MermaidRenderer` must expose —
the direct analogue of the existing `TaskFolderTargets`.

New pure method, sibling to `TaskFolderTargets`:

```csharp
public static DiagramStatusNodes StatusNodes(PlanDefinition plan);

public sealed record DiagramStatusNodes
{
    // task.Id -> "task_<base>"
    public required IReadOnlyDictionary<string, string> TaskContainers { get; init; }
    // (task.Id, guardrail Name) -> "task_<base>_gr_<ordinal>"
    public required IReadOnlyDictionary<(string TaskId, string CheckName), string> TaskGuardrailLeaves { get; init; }
    // (task.Id, preflight Name) -> "task_<base>_pf_<ordinal>"
    public required IReadOnlyDictionary<(string TaskId, string CheckName), string> TaskPreflightLeaves { get; init; }
    // plan-preflight Name -> "plan_preflights_<ordinal>"
    public required IReadOnlyDictionary<string, string> PlanPreflightLeaves { get; init; }
    // plan-guardrail Name -> "plan_guardrails_<ordinal>"
    public required IReadOnlyDictionary<string, string> PlanGuardrailLeaves { get; init; }
    // the two plan-level container ids (for the container-level badge)
    public string PlanPreflightsContainerId => "plan_preflights";
    public string PlanGuardrailsContainerId => "plan_guardrails";
}
```

**Load-bearing correctness constraint (DRY):** `StatusNodes` MUST derive ids from the SAME
`AllocateNodeIdBases(tasks)` and the SAME `OrderBy(c => c.Name, Ordinal)` ordinal iteration
that `AppendNodesAndEdges`/`AppendCheckNodes` use — never a second copy of the ordinal math.
Leaf ids are `<containerPrefix>_<ordinal>` where the prefix is `task_<base>_gr` / `task_<base>_pf`
for task checks and `plan_preflights` / `plan_guardrails` for plan-level checks (per
`AppendCheckNodes`). The key that "lines up" the observer with the SVG is therefore
**(task.Id, check Name) → node id**, because `GuardrailResult.Name == GuardrailDefinition.Name`
and Name is what the renderer sorts and draws. A **bijection golden test** (every rendered
container/leaf SVG id ↔ exactly one `StatusNodes` entry, in both directions) guards against
drift — the same discipline `GraphSourceHashTests` applies to `SemanticContent`.

**Event → node-id wiring (what v1 actually drives):**

| Node | Event source (exists today) | v1 granularity |
|------|-----------------------------|----------------|
| Task container `task_<base>` | `TaskStarting` → running; `TaskFinished` → settled | full |
| Task guardrail leaf `..._gr_<n>` | `GuardrailFinished(task, result)` keyed `(task.Id, result.Name)` | **per-leaf** |
| Task preflight leaf `..._pf_<n>` | *no per-check event* (ReVerifier path) | reflected by container |
| Plan brackets `plan_preflights` / `plan_guardrails` | *no observer event* — run in pre-DAG / terminal phases | **container-level** badge |

Task-preflight and plan-level **per-leaf** badges are a **named follow-on** (needs a
per-check event or a small `IReVerifier` progress callback) — flagged, not silently dropped.

### D4 — Badge overlay mechanism: appended SVG in the viewport (not screen-space divs)

Recommendation: **appended SVG elements inside the pan/zoom viewport**, mirroring the existing
title-band overlay and `addEdgeDirectionMarkers` — NOT absolutely-positioned `#legend`-style
divs.

Why: the `#legend` div is screen-fixed because it is deliberately OUTSIDE the diagram (a corner
HUD). Badges must **track their node**. SVG appended into the node's own coordinate space rides
the `svg-pan-zoom` viewport transform **for free** — no pan/zoom callback, no per-frame
recompute, no drift (the exact class of bug the issue flags for divs). For each `StatusNodes`
node id, resolve the SVG element (`g.cluster[id]` for containers, the leaf `g[id]`), read its
`getBBox()`, and append a small badge `<g>` at the upper-right corner. Because the during-run
page reloads via `meta refresh`, badges are re-derived fresh each cycle from the current status
JSON — no incremental patching needed.

**Self-contained assets (no external image URL — file:// + strict-CSP safe):**
- **Spinner:** inline SVG arc with `<animateTransform type="rotate" .../>` — pure SVG, no
  external GIF, no CSS dependency. (The issue said "animated GIF"; inline SVG is cleaner,
  theme-aware, and needs no `data:` payload.)
- **Settled icons:** inline SVG paths — check (green `passed`), X (red `failed`), "?" (amber
  `needs-human`), a muted dot (`blocked` / `pending`). All inline; zero network requests.

### D5 — `HtmlDiagramRenderer.Render` signature change (hash-neutral)

```csharp
// today:
public static string Render(string mermaidSource, string sourceHash,
    IReadOnlyDictionary<string,string> taskFolderTargets);

// proposed:
public static string Render(string mermaidSource, string sourceHash,
    IReadOnlyDictionary<string,string> taskFolderTargets,
    IReadOnlyDictionary<string,string> statusByNodeId,   // node id -> status token
    bool duringRun);                                     // true => inject meta refresh + active spinner
```

- `statusByNodeId` is embedded as a third `<script type="application/json" id="node-status">`
  blob (same verbatim/`textContent` treatment as `task-folder-targets`), read by the overlay JS.
- `duringRun` toggles the `meta refresh` and spinner animation only.
- **ONE template, status-as-data.** `GraphCommand` (the plan-root writer) calls it with an
  **empty** `statusByNodeId` and `duringRun:false`; the overlay loop then appends no badges, so
  the plan-root file has inert scaffolding but no badges and no refresh. (Keep a 3-arg overload
  delegating to the 5-arg with `(empty, false)` so existing callers/tests need no edit.) One
  renderer, no two-variant drift (DRY).
- **Hash-neutrality — confirmed by construction.** `sourceHash` is computed by
  `GraphSourceHash` over `SemanticContent(plan)` and passed IN; `Render` never recomputes it and
  never feeds `statusByNodeId` into it. Status is pure chrome. `GraphSourceHashTests` stay green
  untouched. Note: the plan-root `diagram.html` **bytes** change once (added inert script tag +
  badge JS) — a one-time regeneration of any committed `diagram.html` fixtures and
  `HtmlDiagramRenderer` snapshot tests; `graph --check` is unaffected because it compares the
  (unchanged) `source-sha256`, not the bytes.

### D6 — Concurrency + atomicity (restated contract)

`OnTheFlyDiagramObserver` mirrors `OnTheFlyLogSiteObserver` exactly:
- One `lock (_gate)` guards **both** the `statusByNodeId` mutation and the projection/write;
  the status snapshot is taken inside the lock so the rendered view is consistent. M4 workers
  call `TaskStarting`/`GuardrailFinished`/`TaskFinished` concurrently.
- **Atomic write** (`AtomicFile.WriteAllText` — temp + rename) so a browser never reads a torn
  `diagram.html`.
- **Best-effort:** swallow `IOException` / `UnauthorizedAccessException`; a render hiccup never
  flips a task outcome, changes the exit code, or aborts the run. The next event re-renders.

### Seams and contracts touched

- `Guardrails.Core/Graph/MermaidRenderer.cs` — **new** `StatusNodes(plan)` + `DiagramStatusNodes`
  record (pure; reuses `AllocateNodeIdBases` + the check-ordinal iteration).
- `Guardrails.Core/Graph/HtmlDiagramRenderer.cs` — `Render` gains `statusByNodeId` + `duringRun`;
  template gains a `node-status` JSON blob, the badge-overlay JS, and a conditional `meta refresh`.
- `Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs` — **new** decorator `IRunObserver` (sibling to
  `OnTheFlyLogSiteObserver`) + `WriteInitialDiagram()` + `WriteFinalStatic()`.
- `Guardrails.Cli/Commands/RunCommand.cs` — wire the decorator in **both** the live and `--no-ui`
  paths (stack it around the existing `OnTheFlyLogSiteObserver`, ~lines 315–325); print the
  live-diagram link at start; call `WriteFinalStatic()` at run end; drive the two plan-level
  **container** badges via concrete `OnTheFlyDiagramObserver` methods called around the Full
  Flight Checks and Terminal Gate phase boundaries (no `IRunObserver` change → keeps the
  interface small, ISP).
- `docs/plans/02-schemas-and-contracts.md` §10 — new "Live status overlay" subsection (verbatim
  edits below).
- Skills: `guardrails-domain-knowledge` (Diagram bullet), `guardrails-dev-knowledge` (observer +
  surface + fixture-regen gotcha).

### Schema changes (`02-schemas-and-contracts.md`)

No `guardrails.json` field changes. §10 gains a subsection (verbatim text in "Proposed
plan-document edits").

## Devil's-advocate self-critique

1. **"Writing under `logs/` defeats the point — the user has the PLAN-ROOT file open; forcing a
   new link IS the context-switch #219 wanted gone."** Strongest counter. Response: writing the
   tracked plan-root artifact mid-run violates invariant #6 (dirties the read-only user checkout,
   races `graph --check`) — a correctness issue, not a preference. The re-orientation cost is a
   single click to the SAME DAG (same `source-sha256`, same click-throughs), printed as a
   clickable link exactly like the log index; and it avoids leaving a frozen committed-adjacent
   file. Honest trade. A serial-mode `--live-diagram-in-place` opt-in remains available later if
   demand is real (YAGNI now).
2. **Under-delivery vs the issue's per-leaf ask for preflights + plan brackets.** Task-preflights
   (ReVerifier path) and plan-level checks (pre-DAG / terminal phases) fire no per-check event, so
   v1 gives them **container-level** badges and flags per-leaf as a named follow-on. Not silently
   dropped; task-guardrail leaves — the densest, most-watched surface — DO get per-leaf badges.
3. **Two stacked decorators → two full HTML re-renders + two atomic writes per event.** Same cost
   profile the log site already accepted; a string render + rename is milliseconds against
   task/prompt execution measured in seconds-to-minutes. Best-effort. Debounce is YAGNI until
   measured.
4. **`meta refresh content=2` re-renders the whole Mermaid SVG every 2s → flicker/CPU on a big
   DAG** (heavier than the log site's plain HTML). Accepted for v1 (matches the issue + the proven
   pattern); the issue's own item-4 server **poll-and-patch** (update only the status JSON + repaint
   badges, no Mermaid re-render) is the flagged enhancement if flicker bites. Consider a slightly
   longer interval (3s) for the diagram.
5. **Surface drift: `StatusNodes` computing ids separately from the emitter.** Mitigated by
   mandating reuse of `AllocateNodeIdBases` + the shared ordinal iteration and a **bijection golden
   test** — the exact discipline that already keeps `TaskFolderTargets` correct.
6. **Hash leak?** No — `sourceHash` is computed upstream over `SemanticContent` and passed in;
   status never enters it. `GraphSourceHashTests` remain the guard.

## Implementation handoff

Sequenced; each step build+test-green before the next.

1. **`guardrails-harness-developer`** — `MermaidRenderer.StatusNodes` + `DiagramStatusNodes`
   (reuse `AllocateNodeIdBases`; no duplicated ordinal math).
   *filesTouched:* `src/Guardrails.Core/Graph/MermaidRenderer.cs`,
   `tests/**` (bijection test — hand to test author or co-locate).
2. **`guardrails-harness-developer`** — `HtmlDiagramRenderer.Render` 5-arg + 3-arg overload;
   `node-status` blob + inline-SVG badge overlay JS + conditional `meta refresh`.
   *filesTouched:* `src/Guardrails.Core/Graph/HtmlDiagramRenderer.cs`.
3. **`guardrails-harness-developer`** — `OnTheFlyDiagramObserver` (decorator, lock/atomic/
   best-effort, journal-seeded map, `WriteInitialDiagram`/`WriteFinalStatic`) + `RunCommand`
   wiring (both paths, link print, plan-level container-badge calls).
   *filesTouched:* `src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs`,
   `src/Guardrails.Cli/Commands/RunCommand.cs`.
4. **`guardrails-test-author`** — bijection test; `OnTheFlyDiagramObserver` concurrency/atomicity
   test (mirror the log-site observer's tests); regenerate committed `diagram.html` fixtures +
   `HtmlDiagramRenderer` snapshots; assert `graph --check` still green (hash-neutrality).
   *filesTouched:* `tests/**`, committed `diagram.html` fixtures.
5. **`guardrails-skill-author`** — SSOT §10 subsection (verbatim below); `guardrails-domain-knowledge`
   Diagram bullet; `guardrails-dev-knowledge` observer/surface/fixture-regen note.
   *filesTouched:* `docs/plans/02-schemas-and-contracts.md`, the two skill `SKILL.md` files.

**#106 draft-PR loop:** this design-of-record changes a contract (the `Render` signature) and adds
a public surface, so it SHOULD go out as a **draft PR** (`docs/plans/12-live-diagram-status.md` +
the §10 edits) for inline human review before the harness milestones start. It is borderline
(single-milestone fast-follow); the user may waive the ceremony. Architect proposes; user decides.

## Proposed plan-document edits

### 1. `docs/plans/02-schemas-and-contracts.md` — plan-folder tree (near lines 23–24)

Add under the `logs/` line (or annotate it): a note that during a run the harness also writes
`logs/<runId>/diagram.html` — the live status companion (§10), non-authored, `--fresh`-cleared.

### 2. `docs/plans/02-schemas-and-contracts.md` §10 — append this subsection

```markdown
### 10.1 Live status overlay (`logs/<runId>/diagram.html`, issue #219)

During a run the harness writes a live status companion to the DAG at
`logs/<runId>/diagram.html` — NOT the plan-root `diagram.html` (a tracked artifact the run
must not modify; the user's checkout is read-only for the run, §5). It is gitignored runtime
state, `--fresh`-cleared, excluded from `guardrails.baseline`, and never inspected by
`graph --check`. The plan-root `diagram.html` stays the canonical static `graph` artifact and
never carries badges.

- **Mechanism.** A decorator `IRunObserver` (`OnTheFlyDiagramObserver`, sibling to the log
  site's `OnTheFlyLogSiteObserver`) forwards every event and, under one lock, re-renders the
  page from an in-memory node-id → status map. Atomic write; best-effort (a render failure
  never flips an outcome or aborts the run). Wired in both the live and `--no-ui` paths.
- **During-run vs final.** The during-run page carries `<meta http-equiv="refresh" content="2">`
  (a plain `file://` view refreshes itself — no server); the final page, written once at run
  end, drops the refresh and shows every node settled.
- **Node-id surface.** `MermaidRenderer.StatusNodes(plan)` (sibling to `TaskFolderTargets`)
  maps each status-bearing element to its SVG node id: task containers `task_<base>`, task
  guardrail leaves `task_<base>_gr_<ordinal>`, task preflight leaves `task_<base>_pf_<ordinal>`,
  and the plan-level bracket leaves under `plan_preflights` / `plan_guardrails`. Ids are derived
  from the SAME `AllocateNodeIdBases` + `OrderBy(Name)` ordinal logic the renderer emits, so the
  keys line up with the SVG exactly (a bijection test guards drift). The observer keys events by
  `(task.Id, check Name)` since `GuardrailResult.Name == GuardrailDefinition.Name`.
- **Badges.** Appended inline-SVG elements inside the `svg-pan-zoom` viewport (like the
  title-band and edge-direction overlays), so they ride the pan/zoom transform without a
  callback: an animated inline-SVG spinner while in-flight, a settled inline-SVG icon
  (check / X / "?") once finished. No external image URL (file:// + strict-CSP safe).
- **Hash-neutral.** Status is chrome: `HtmlDiagramRenderer.Render(..., statusByNodeId,
  duringRun)` embeds it as a separate `<script id="node-status">` blob; it never touches the
  Mermaid source or `SemanticContent`, so `source-sha256` is unchanged by construction and
  `graph --check` never reports stale.
- **v1 granularity.** Task containers + task guardrail leaves are per-leaf live; task-preflight
  and plan-level checks show container-level status (no per-check event yet — per-leaf badges
  are a follow-on).
```

### 3. `guardrails-domain-knowledge` (SKILL.md, "Diagram" bullet)

Add a third companion: during a run, `logs/<runId>/diagram.html` is the live status overlay
(spinner / settled icons per node), gitignored runtime state, separate from the static
plan-root `diagram.html`. Same `source-sha256`; status is hash-neutral chrome.

### 4. `guardrails-dev-knowledge` (SKILL.md)

Note `OnTheFlyDiagramObserver` (decorator wired in `RunCommand`, both paths), the
`MermaidRenderer.StatusNodes` surface, and the one-time gotcha: adding badge scaffolding changes
plan-root `diagram.html` bytes (regenerate fixtures) but NOT `source-sha256` (`graph --check`
stays green).
```
