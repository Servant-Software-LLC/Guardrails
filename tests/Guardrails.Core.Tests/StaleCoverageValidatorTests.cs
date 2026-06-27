using Guardrails.Core.Io;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2026 (issue #157 §1): <see cref="PlanValidator"/> emits a WARNING when a task's
/// <c>covers-key-behaviors</c>-style guardrail requires a coverage token that the task's action
/// prompt never mentions — the prompt was edited (a scenario removed) without updating the guardrail,
/// so a correct implementation following the prompt can never satisfy the guardrail and the task
/// dead-ends at needs-human. The check is a HEURISTIC: it fires only when the archetype and a clear
/// literal token are both confidently identified, so these tests also pin the silent cases (all tokens
/// present; not the archetype). Plans are built on disk because the check reads the action and
/// guardrail bodies.
/// </summary>
public sealed class StaleCoverageValidatorTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "gr-gr2026-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => SafeDelete.DeleteDirectory(_tempRoot);

    private const string Gr2026 = DiagnosticCodes.StaleCoverageToken;

    [Fact]
    public void StaleToken_AbsentFromActionPrompt_WarnsGr2026()
    {
        // The prompt enumerates 2 scenarios; the covers-key-behaviors guardrail still requires 3.
        // CommanderRest was dropped from the prompt but is still required → stale.
        string actionPrompt =
            "Write tests for two scenarios: XtcFileOnly and TcApiLocal.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "$hits = 0\n" +
            "if ($content -match 'XtcFileOnly') { $hits++ }\n" +
            "if ($content -match 'TcApiLocal') { $hits++ }\n" +
            "if ($content -match 'CommanderRest') { $hits++ }\n" +
            "if ($hits -lt 3) { Write-Output 'missing a scenario'; exit 1 }\n" +
            "exit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Diagnostic diagnostic = Assert.Single(Validate(dir), d => d.Code == Gr2026);
        Assert.Contains("CommanderRest", diagnostic.Message);
        Assert.Contains("01-author-tests", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void AllTokensPresentInActionPrompt_NoWarning()
    {
        string actionPrompt =
            "Write tests for: XtcFileOnly, TcApiLocal, and CommanderRest.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "$hits = 0\n" +
            "if ($content -match 'XtcFileOnly') { $hits++ }\n" +
            "if ($content -match 'TcApiLocal') { $hits++ }\n" +
            "if ($content -match 'CommanderRest') { $hits++ }\n" +
            "if ($hits -lt 3) { exit 1 }\n" +
            "exit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2026);
    }

    [Fact]
    public void PerTermCanonicalForm_StaleToken_WarnsGr2026()
    {
        // The catalogue/dotnet realization: `-notmatch ... exit 1` per term, no $hits — recognised
        // via the canonical covers-key-behaviors guardrail name.
        string actionPrompt = "Encode a test asserting ProcessID keying.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'ProcessId') { Write-Output 'no ProcessId'; exit 1 }\n" +
            "if ($content -notmatch 'RollupCount') { Write-Output 'no RollupCount'; exit 1 }\n" +
            "exit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        // RollupCount is never mentioned in the prompt → stale; ProcessId IS mentioned (case-insensitive).
        Diagnostic diagnostic = Assert.Single(Validate(dir), d => d.Code == Gr2026);
        Assert.Contains("RollupCount", diagnostic.Message);
        Assert.DoesNotContain("ProcessId'", diagnostic.Message); // ProcessId is present, not flagged
    }

    [Fact]
    public void NoCoverageGuardrail_NoWarning()
    {
        // A task whose only guardrail is a plain build check is not the archetype → never warns.
        string actionPrompt = "Write some tests for ScenarioOne.\n";
        string buildCheck = "dotnet build\nif ($LASTEXITCODE -ne 0) { exit 1 }\nexit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, buildCheck, guardrailName: "01-build-passes.ps1");

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2026);
    }

    [Fact]
    public void RegexMetacharToken_IsNotFlagged()
    {
        // A `-match` literal carrying regex syntax is not a clear keyword — it is skipped, so even
        // though the prompt never contains that regex it must NOT warn (conservatism).
        string actionPrompt = "Write tests for CommanderRest only.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "$hits = 0\n" +
            "if ($content -match '^public\\s+class\\s+\\w+Tests') { $hits++ }\n" + // metachars → skipped
            "if ($content -match 'CommanderRest') { $hits++ }\n" +                  // present in prompt
            "if ($hits -lt 2) { exit 1 }\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2026);
    }

    [Fact]
    public void CaseInsensitiveKeywordPresence_IsHonoured()
    {
        // The prompt mentions the token in a different case; presence is case-insensitive → no warning.
        string actionPrompt = "Cover the commanderrest scenario.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "$hits = 0\n" +
            "if ($content -match 'CommanderRest') { $hits++ }\n" +
            "if ($hits -lt 1) { exit 1 }\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2026);
    }

    [Fact]
    public void NegativeAssertion_TokenAbsentFromPrompt_NoWarning()
    {
        // Issue #177: a NEGATIVE assertion (fail when CommanderRest is PRESENT) intentionally checks the
        // token is ABSENT from the authored file — so its absence from the prompt is EXPECTED, not stale.
        // GR2026 must NOT fire (the false positive #177 reports).
        string actionPrompt = "Write dispatch tests. Mode C is wizard-blocked, so do not exercise it.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -match 'CommanderRest') {\n" +
            "    Write-Output 'MigrateDispatchTests.cs contains a CommanderRest reference'\n" +
            "    exit 1\n" +
            "}\n" +
            "exit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Assert.DoesNotContain(Validate(dir), d => d.Code == Gr2026);
    }

    [Fact]
    public void MixedPolarity_WarnsOnlyOnMissingRequirePresentToken_NeverOnNegativeAssertion()
    {
        // The prompt mentions XtcFileOnly (require-present, satisfied) but NOT TcApiLocal (require-present,
        // stale) and NOT CommanderRest (negative assertion, expected absent). GR2026 fires on TcApiLocal
        // ONLY — never on the negative assertion.
        string actionPrompt = "Write tests for the XtcFileOnly scenario.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'XtcFileOnly') { exit 1 }\n" +
            "if ($content -notmatch 'TcApiLocal') { exit 1 }\n" +
            "if ($content -match 'CommanderRest') { Write-Output 'forbidden'; exit 1 }\n" +
            "exit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Diagnostic diagnostic = Assert.Single(Validate(dir), d => d.Code == Gr2026);
        Assert.Contains("TcApiLocal", diagnostic.Message);
        Assert.DoesNotContain("CommanderRest", diagnostic.Message);
    }

    [Fact]
    public void MultiLineNotMatchForm_StaleToken_StillWarnsGr2026()
    {
        // Preserve #157 for the catalogue's REAL multi-line `-notmatch … exit 1` shape (literal and
        // exit on different lines): a require-present token missing from the prompt is still stale.
        string actionPrompt = "Encode a test asserting ProcessID keying.\n";
        string coverage =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'ProcessId') {\n" +
            "    Write-Output 'does not test ProcessID keying'\n" +
            "    exit 1\n" +
            "}\n" +
            "if ($content -notmatch 'RollupCount') {\n" +
            "    Write-Output 'does not test rollup counts'\n" +
            "    exit 1\n" +
            "}\n" +
            "exit 0\n";

        string dir = PlanWithPromptTask(actionPrompt, coverage);

        Diagnostic diagnostic = Assert.Single(Validate(dir), d => d.Code == Gr2026);
        Assert.Contains("RollupCount", diagnostic.Message);
    }

    // --- on-disk plan builder ---------------------------------------------------------------

    private IReadOnlyList<Diagnostic> Validate(string dir)
    {
        PlanLoadResult result = new PlanLoader().Load(dir);
        Assert.NotNull(result.Plan);
        return new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);
    }

    /// <summary>
    /// Build a one-task plan: a prompt action (action.prompt.md) carrying <paramref name="actionPrompt"/>
    /// and a single script guardrail carrying <paramref name="guardrailBody"/>. The guardrail file name
    /// defaults to the canonical <c>03-covers-key-behaviors.ps1</c>.
    /// </summary>
    private string PlanWithPromptTask(
        string actionPrompt, string guardrailBody, string guardrailName = "03-covers-key-behaviors.ps1")
    {
        string dir = Path.Combine(_tempRoot, "plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // A serial-friendly config keeps the parallel gates (GR2015/2017/2018) quiet; declare a prompt
        // runner so the prompt action does not trip GR2008.
        File.WriteAllText(Path.Combine(dir, "guardrails.json"), """
            {
              "version": 1,
              "maxParallelism": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """);

        string taskDir = Path.Combine(dir, "tasks", "01-author-tests");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "author the tests", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), actionPrompt);
        File.WriteAllText(Path.Combine(taskDir, "guardrails", guardrailName), guardrailBody);

        return dir;
    }
}
