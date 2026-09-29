## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. In this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/10-implement-lite-hashes`, NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/10-implement-lite-hashes": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

**Fill real logic over the three stubs** so the already-authored tests
`tests/Guardrails.Integration.Tests/Lite/LiteHashParityTests.cs` and
`tests/Guardrails.Integration.Tests/Lite/LiteMarkReviewedTests.cs` pass. **Do NOT edit those tests or
their fixtures** (they are outside your `writeScope`; the harness rejects the edit). If a test is
genuinely wrong, write `{"needsHuman": "<which test and why>"}` and stop rather than changing it.
Your files: `scripts/lite/lib/Hash.psm1`, `scripts/lite/plan-hash.ps1`, `scripts/lite/mark-reviewed.ps1`.

This is a **byte-exact port of C#**, so port from the source, not from memory. Read, in
`src/Guardrails.Core`:
- `Hashing/HashText.cs` — `AppendFile` (segment = `label` + U+001F + `NormalizeNewlines(File.ReadAllText(path))`
  + U+001E; an ABSENT file contributes the label and separators with an EMPTY body), `NormalizeNewlines`
  (`\r\n` → `\n`, then lone `\r` → `\n`), `EnumerateFolderFiles` (recursive, every file, labels are
  paths relative to the label root with `\` → `/`, sorted **Ordinal**), `NormalizeRelative`.
- `Journal/PlanDefinitionHash.cs` — `Compute`: `guardrails.json`; then tasks sorted **Ordinal** by id,
  each file from `TaskDefinitionFiles.Enumerate` labelled `task:<id>/<label>`; then the plan-root
  `guardrails/` folder; then the plan-root `preflights/` folder (both labelled relative to the PLAN
  root, e.g. `guardrails/01-gate.ps1`). SHA-256 over the UTF-8 bytes of the whole string →
  `sha256:` + lowercase hex. (The waves loop: Lite refuses waved plans, so you need not port it —
  but do not silently mis-hash one: if any `wave-NN-*` directory exists, throw.)
- `Journal/TaskDefinitionFiles.cs` — `task.json`; `action:<rel>` (the resolved action file, label is its
  path relative to the task dir); the task's `guardrails/**`; the task's `preflights/**` (labels relative
  to the TASK dir). `Journal/TaskDefinitionHash.cs` hashes that same set with NO `task:<id>/` prefix.
- `Journal/PlanHash.cs` — `guardrails.json`, then each task's `task.json` labelled `task:<id>`.
- Action resolution (`PlanLoader`): an explicit `action.path` in `task.json` wins; otherwise the single
  `action.*` file in the task folder.

**Pitfalls that make a port disagree with Core** (each is exercised by a test):
- **Text decoding.** `File.ReadAllText` decodes UTF-8 and STRIPS a BOM; you then hash the UTF-8 bytes of
  the decoded text. Read with `[System.IO.File]::ReadAllText($p)` (it detects/strips the BOM like .NET
  does, because it IS .NET) — do not use `Get-Content`, which splits lines and loses `\r` distinctions.
- **Sorting.** PowerShell `Sort-Object` is culture-aware and case-insensitive. Use ordinal:
  `[System.StringComparer]::Ordinal` with `[System.Collections.Generic.List[string]]` / `[Array]::Sort($a, [StringComparer]::Ordinal)`.
- **String building.** Build one string with `[System.Text.StringBuilder]` and hash
  `[System.Text.Encoding]::UTF8.GetBytes($sb.ToString())` (UTF8 without preamble — `GetBytes` never adds one).
- **Separators.** `[char]0x1F` and `[char]0x1E`.
- **JSON.** `guardrails.json` / `task.json` may contain `//` comments (the examples do). You only need
  `task.json` to find an explicit `action.path`.

**Resolved at breakdown time** (pwsh 7.6.6): piping `{ // a comment` + `"action": { "path": "run-me.ps1" } }`
to `ConvertFrom-Json` parsed and returned `run-me.ps1`; `{ "version": 1, }` (trailing comma) parsed; a
truncated `{ "version": ` threw. `[System.IO.File]::ReadAllText` over `EF BB BF 61 62 63` returned a
3-char string starting `a` (BOM stripped). So use both directly rather than probing for them. **If your
own check disagrees, trust your check**, do what it says, and say so in your summary.

### Script contract (the tests pin it — see `docs/plans/guardrails-lite-wave01-design.md`)
- `Hash.psm1` exports `Get-LitePlanDefinitionHash -PlanDir`, `Get-LiteTaskDefinitionHash -PlanDir -TaskId`,
  `Get-LiteNarrowPlanHash -PlanDir`, each returning `sha256:<64 lowercase hex>`. Task ids = the folder
  names under `tasks/`, sorted ordinal.
- `plan-hash.ps1 <planDir>` → one compressed JSON line
  `{ "script":"plan-hash","ok":true,"planDefinitionHash":…,"planHash":…,"tasks":{ "<id>":… } }`, exit 0;
  a missing `<planDir>` → exit **64** with `ok:false` and a message on stderr.
- `mark-reviewed.ps1 <planDir> [-Reviewer <name>]` → writes `<planDir>/state/guardrails-review.json`
  (create `state/`): `version` 2, `reviewedAt` (ISO-8601 UTC, `[DateTimeOffset]::UtcNow.ToString('o')`),
  `planHash` = the PlanDefinitionHash, `attestation` = `{ "source":"bare", "tool":"guardrails-lite <v>" }`
  plus `"actor"` only when `-Reviewer` is given; `<v>` = `$env:GUARDRAILS_LITE_VERSION` or `0.0.0-dev`.
  Prints `{ "script":"mark-reviewed","ok":true,"planHash":… }`, exit 0; missing dir → exit 64.
- Both scripts `Import-Module (Join-Path $PSScriptRoot 'lib/Hash.psm1')` — the hash logic lives ONCE in
  `Hash.psm1`. Every script keeps `#Requires -Version 7`, `Set-StrictMode -Version Latest`,
  `$ErrorActionPreference = 'Stop'`. **No network calls.** Remove every stub marker
  (`"stub":true`, `exit 99`, `throw 'stub'`).

Verify with:
`dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&(FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteHashParityTests|FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteMarkReviewedTests)"`

### Completion criteria (these match the guardrails)
1. `dotnet build tests/Guardrails.Integration.Tests` succeeds.
2. All 13 pinned tests in `LiteHashParityTests` and `LiteMarkReviewedTests` run and pass.
3. No stub marker remains in the three files.
