## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/08-implement-lite-validate-subset-and-parity`, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/08-implement-lite-validate-subset-and-parity": { "someKey": "someValue" } }`.
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

Make `LiteValidateSubsetTests` and `LiteValidationParityTests`
(`tests/Guardrails.Integration.Tests/Lite/`) pass. **Fill real logic over the stub
`scripts/lite/validate/Subset.psm1`**, and fix `Load.psm1` / `Graph.psm1` / `validate.ps1` wherever parity
shows Lite disagreeing with the harness. This task publishes no state.

Your write scope is `scripts/lite/validate/`. **Don't edit the tests or their fixtures.** Make them pass
by fixing the implementation. If an authored test is genuinely wrong (for example, a fixture the
HARNESS itself rejects, so an assertion is unreachable), write
`{"needsHuman": {"question": "<which test, why it cannot pass, the evidence>", "kind": "blocked-work"}}`
rather than changing it. An edit to a test file fails the write-scope check and burns a retry.

Read first:
- `docs/plans/guardrails-lite-wave01-design.md`. Its "Kernel script I/O contract", "Lite validate v1 code
  set", "Hash algorithm" and "Cross-task APIs" are binding. `Subset.psm1` exports exactly
  `Invoke-LiteRules -Plan <object>` and returns diagnostic objects
  `[pscustomobject]@{ code; severity; message; path }`.
- Both test files, in full. They are the specification. The fixture table in
  `Fixtures/validate-subset/` shows which `guardrails.json` key maps to which GR2090 severity.
- `docs/plans/guardrails-lite.charter.md`, "What \"Lite\" is". That subset table is the reason each
  severity is what it is.

### 1. `Subset.psm1`: GR2090 (LiteUnsupportedFeature)

Emit one diagnostic per unsupported feature, and name the feature (the JSON key) in the message. Match
the severities the tests pin. Refused features are `error`; inert keys and a missing `"profile": "lite"`
are `warning`. The trigger keys are **camelCase JSON names** the harness binds case-insensitively
(`PlanJson.Options`), so read them case-insensitively from the parsed `guardrails.json`, never with a
case-sensitive property access. `promptRunners` is a map in which `default` is a **string pointer** and
every other member is a runner block. Walk it the way `PlanLoader` does, and don't treat `default` as a
block.

### 2. `Subset.psm1`: GR2025 (review marker missing or stale), warning

Reproduce the harness's decision, not an approximation of it. **Read `ReviewMarker.Evaluate` and
`ReviewMarker.Read`** in `src/Guardrails.Core/Review/ReviewMarker.cs` (grep `public static ReviewEvaluation
Evaluate`) and mirror exactly when it warns: no marker file, an unparseable one, or one whose `planHash`
differs from the current key hash. Compute that hash with `Get-LitePlanDefinitionHash -PlanDir` from
`scripts/lite/lib/Hash.psm1`. That task has already landed, so import it by path and don't re-implement
hashing. If `ReviewMarker.Evaluate` has a case this list misses, follow the C#. A marker written by the
harness's own `ReviewMarker.Write` must produce **no** GR2025; `ReviewMarker_FreshFromCore_EmitsNoGR2025`
holds you to that.

### 3. Parity closure

`Parity_LiteMatchesCore` compares Lite against `Guardrails.Cli.PlanProbe.LoadAndValidate` plus
`PlanValidator.ReviewMarkerDiagnostics`, restricted to the codes Lite implements, over every fixture in
the `validate-load`, `validate-graph` and `validate-subset` groups plus two examples. Where a row
disagrees, the harness is the specification. Read the C# check that emits (or suppresses) the disputed
code and make the Lite module match it. Two known shapes to check for yourself, not assume:
- **Load errors suppress the semantic half in the harness.** Grep `LoadAndValidate` in
  `src/Guardrails.Cli/PlanProbe.cs` and establish exactly when semantic validation and the review nudge
  are skipped. Make `validate.ps1`'s dispatcher agree, including whether `Subset.psm1` runs on a
  load-error plan.
- **`guardrails.json` may carry `//` comments and trailing commas** (both examples do), because the
  harness parses it with `PlanJson.Options`. Lite must parse the same files without error.

### Done when (these match the guardrails)

- `dotnet build tests/Guardrails.Integration.Tests` succeeds.
- `dotnet test tests/Guardrails.Integration.Tests --filter "Category=Lite&(FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidateSubsetTests|FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidationParityTests)"`
  passes, with at least one test executed.
- The earlier Lite classes still pass. You may touch `Load.psm1` and `Graph.psm1`, so run
  `--filter "Category=Lite&FullyQualifiedName~Guardrails.Integration.Tests.Lite.LiteValidate"` before you
  finish. A parity fix that breaks `LiteValidateLoadTests` or `LiteValidateGraphTests` is not a fix.
- Nothing under `scripts/lite/validate/` still prints `"stub": true` or `exit 99`.
- `pwsh` 7 only, and no network calls.
