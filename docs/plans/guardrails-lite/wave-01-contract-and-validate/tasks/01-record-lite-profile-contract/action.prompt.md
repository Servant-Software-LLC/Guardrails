## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. In this WAVED plan that is the wave-qualified id
  `wave-01-contract-and-validate/01-record-lite-profile-contract`, NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "wave-01-contract-and-validate/01-record-lite-profile-contract": { "someKey": "someValue" } }`.
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

**Context.** Guardrails Lite (issue #823; design of record `docs/plans/guardrails-lite.charter.md`,
decision `lite-plan-marker`) marks a Lite plan with an optional top-level `"profile": "lite"` key in
`guardrails.json`. The full harness must ACCEPT and IGNORE it — and it already does: `RawRunConfig`
(`src/Guardrails.Core/Loading/RawManifests.cs`) carries no extension-data member, so an unknown top-level
key is silently dropped by `PlanLoader`. **Do NOT change any loader or validator code.** This task
records the contract and pins the existing behaviour so a future loader change cannot break Lite plans
unnoticed. Lite's own validator (a later task, `scripts/lite/validate.ps1`) emits a Lite-only code for a
feature Lite does not implement; that code is **GR2090 `LiteUnsupportedFeature`**, and it is RESERVED BY
NAME here because the harness itself never emits it.

You write exactly three files (this is the whole `writeScope`; anything else fails the harness's
write-scope check):

### 1. `docs/plans/02-schemas-and-contracts.md` — the SSOT

a. **§2 (`guardrails.json`)** — in the first `jsonc` block of `## 2. \`guardrails.json\` (run
   configuration)`, add a line for the key, in the block's own house style (key, value, `//` comment),
   directly after the `"version": 1,` line. It must read (after indentation) `"profile": "lite",` followed
   by its `//` comment, and the comment must state: OPTIONAL; the only recognised value is `"lite"`, which marks a Guardrails Lite plan
   (#823); the full harness ACCEPTS AND IGNORES it (no diagnostic, no behaviour change — only Lite's
   `validate.ps1` reads it); absent means a normal plan. Keep it on ONE line like its neighbours.

b. **The diagnostic-code table** that lists `GR2083` as `RESERVED BY NAME` and ends with the `GR2089`
   row. Find it with `Select-String -Path docs/plans/02-schemas-and-contracts.md -Pattern '^\| `GR2089`'`
   — do not trust any line number. Add a row directly after the `GR2089` row, in the same shape as the
   `GR2083` reserved row (`| code | — | text |`), reading in substance:
   `` | `GR2090` | — | RESERVED BY NAME for Guardrails Lite (#823): `LiteUnsupportedFeature`, emitted ONLY by `scripts/lite/validate.ps1` when a plan uses a feature Lite does not implement (waves, overwatch, tiering, …). The harness never emits it. Never allocate it for anything else | ``

c. **The next-free prose.** The SSOT carries a long paragraph that ends "…so an unrelated new code should
   take **`GR2090`** — …". Find it with
   `Select-String -Path docs/plans/02-schemas-and-contracts.md -Pattern 'should take \*\*`GR2090`\*\*'`
   (expect exactly one hit). Change it so it says a new code should take **`GR2091`**, and add
   `GR2090` to the list of RESERVED BY NAME codes it names (reserved for Guardrails Lite's
   `LiteUnsupportedFeature`, #823). After your edit the phrase `should take **`GR2090`**` must appear
   nowhere, and `should take **`GR2091`**` exactly once.

Do not put any of these inside an HTML comment (`<!-- -->`): the guardrail strips comments before
matching, because a commented-out row renders as nothing.

### 2. `src/Guardrails.Core/Loading/DiagnosticCodes.cs`

Mirror how GR2083 is reserved (grep `GR2083 is RESERVED BY NAME` — a plain `//` comment block, NOT a
constant):

- Directly after the `StallTimeoutInGuardrailOverrides` constant (the `GR2089` declaration), add a `//`
  comment block reserving the code, e.g.
  `// GR2090 is RESERVED BY NAME for Guardrails Lite (#823): LiteUnsupportedFeature, emitted only by`
  `// scripts/lite/validate.ps1 for a plan feature Lite does not implement. The harness never emits it,`
  `// so it is deliberately NOT a constant. Do not allocate GR2090 for anything else.`
  The first line of the block must contain `GR2090`, `RESERVED BY NAME` and `LiteUnsupportedFeature`
  on the SAME line.
- **Do NOT declare a constant with the value `"GR2090"`.** A constant would claim the harness emits it.
- Change the single live marker line `// CURRENT next-free code: GR2090.` to `GR2091`, and in the sentence
  that follows it (which already says GR2083 and GR2077 are RESERVED BY NAME) add that GR2090 is RESERVED
  BY NAME for Guardrails Lite (#823). There must remain **exactly one** line that STARTS with
  `// CURRENT next-free code:` — `DiagnosticCatalogueTests.TheNextFreeMarkerNamesACodeThatIsActuallyFree`
  enforces that and must stay green.

### 3. `tests/Guardrails.Core.Tests/Loading/LiteProfileContractTests.cs` — the pinning test

Namespace `Guardrails.Core.Tests.Loading`, class **`LiteProfileContractTests`**, carrying
`[Trait("Category", "Lite")]` at class level. Build the plans in a temp directory the way
`tests/Guardrails.Core.Tests/ActionModelOverrideTests.cs` does (read its `PlanWith` helper and mirror it;
clean up the temp dir). A minimal valid flat plan is: `guardrails.json`, one task folder
`tasks/01-only/` holding a `task.json` with `"description"`, `"dependsOn": []` and `"writeScope": []`,
an `action.ps1`, and one `guardrails/01-ok.ps1` whose first line is `# catches: nothing`. Load with
`new PlanLoader().Load(dir)` and validate with `new PlanValidator(FakeExecutableProbe.All).Validate(plan)`.
Write exactly these three `[Fact]` methods — the names are pinned, the guardrail looks for them by name
in the test results:

- **`ProfileLite_LoadsWithNoErrorDiagnostics`** — `guardrails.json` =
  `{ "version": 1, "profile": "lite" }`; assert the load has no errors and the validator reports no
  diagnostic whose severity is `Error`, and that no diagnostic message mentions `profile`.
- **`ProfileLite_ValidatesToTheSameDiagnosticCodesAsTheUnmarkedPlan`** — build two otherwise
  byte-identical plans, one with `"profile": "lite"` and one without; assert the ordered set of
  diagnostic CODES (loader diagnostics + validator diagnostics) is identical. (This is what catches a
  future loader that starts rejecting or warning on the key.)
- **`ContrastCase_MalformedGuardrailsJson_IsReportedAsGR1002`** — the same load path over a
  `guardrails.json` that is truncated JSON (`{ "version": 1, "profile": "lite",`); assert the load result
  carries a `GR1002` diagnostic. This proves the two tests above exercise a load path that DOES surface
  config problems — without it, a test that silently loaded the wrong folder would pass.

**Named exception (GR2075) — say it in your summary.** This task authors a test and is graded by it, with
no TDD-red half: the behaviour it pins ALREADY exists, so the test is green on arrival by design (it is a
regression guard, not new behaviour). The compensating controls are (1) the contrast case above, which
proves the load path can fail, and (2) the guardrail's per-test census, which requires all three pinned
methods to be observed **Passed** in the test results — a skipped or renamed test fails it.

### Completion criteria (these match the guardrails)
1. The SSOT carries the `"profile":` line inside §2's block, the `GR2090` reserved row naming
   `LiteUnsupportedFeature`, and the next-free prose now naming `GR2091` (none of it inside `<!-- -->`).
2. `DiagnosticCodes.cs` carries the `GR2090 … RESERVED BY NAME … LiteUnsupportedFeature` comment, NO
   `"GR2090"` constant, and exactly one live marker reading `// CURRENT next-free code: GR2091`.
3. `dotnet build tests/Guardrails.Core.Tests` succeeds.
4. The three pinned tests AND the existing `DiagnosticCatalogueTests` all pass.
