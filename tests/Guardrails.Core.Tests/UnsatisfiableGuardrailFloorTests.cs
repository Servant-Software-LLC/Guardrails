using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2055 (issue #484) — a guardrail that CANNOT PASS for any input: its zero-match floor demands more
/// executed tests than the literal name list its own <c>--filter</c> is built from can ever select.
///
/// <para>The motivating case shipped into a live run and dead-ended a task whose implementation was
/// complete and verified. A filter naming SIX clauses was guarded by <c>if ($ran -lt 14)</c>; the floor
/// had been correct for an EARLIER whole-class filter and was left behind when a later scoping fix
/// narrowed the filter. Each edit was individually sound, the two numbers sat ~30 lines apart, and two
/// human review passes missed it.</para>
///
/// <para>It is invisible to the #479 execution probes: such a guardrail is red before the task runs,
/// which is correct, and red forever, which is not — and a baseline probe cannot distinguish those.
/// That is precisely why it must be caught statically.</para>
///
/// <para>Half these tests are FALSE-POSITIVE guards. A validator that cries wolf gets ignored, and then
/// its true positives are lost with it.</para>
/// </summary>
public sealed class UnsatisfiableGuardrailFloorTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("gr2055-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    /// <summary>The measured instance, reduced: six names in the filter, a floor of fourteen.</summary>
    [Fact]
    public void FloorExceedingFilterCardinality_IsUnsatisfiable_AndFires()
    {
        GuardrailDefinition guardrail = WriteScript("02-conformance",
            "$tests = @(\n" +
            "    'Judge_ResolvesThroughSameResolver_AtActorsRung',\n" +
            "    'Judge_WeakActor_StrengthBump_NotTierBump',\n" +
            "    'Judge_OnlyStrongerBlockIsCostly_DegradesAndProceeds',\n" +
            "    'Judge_PinnedCostlyActor_MayBumpIntoCostly_D29',\n" +
            "    'Judge_VerifierMinTier_RaisesNeverLowers',\n" +
            "    'Judge_ProvenanceReachesRunJson_BothPaths'\n" +
            ")\n" +
            "$filter = ($tests | ForEach-Object { \"FullyQualifiedName~Stage2ConformanceTests.$_\" }) -join '|'\n" +
            "$out = dotnet test tests/Some.Tests --filter $filter --nologo\n" +
            "if ($ran -lt 14) { exit 1 }\n" +
            "exit 0\n");

        Diagnostic d = Assert.Single(Validate(guardrail), x => x.Code == DiagnosticCodes.UnsatisfiableGuardrailFloor);

        // The message must carry BOTH numbers — the whole point is that a reader sees the contradiction.
        Assert.Contains("6 name(s)", d.Message, StringComparison.Ordinal);
        Assert.Contains("14", d.Message, StringComparison.Ordinal);
    }

    /// <summary>A floor the filter can actually reach is correct and must stay silent.</summary>
    [Fact]
    public void FloorEqualToFilterCardinality_IsSatisfiable_AndStaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("02-ok",
            "$tests = @( 'A_One', 'B_Two', 'C_Three' )\n" +
            "$filter = ($tests | ForEach-Object { \"FullyQualifiedName~$_\" }) -join '|'\n" +
            "if ($ran -lt 3) { exit 1 }\n" +
            "exit 0\n");

        AssertSilent(guardrail);
    }

    /// <summary>
    /// The shipped FIX for the measured case: the filter is the whole class MINUS the clauses later
    /// tasks own, so there is no literal name list feeding it and the floor is legitimately higher than
    /// the exclusion list's length. This must not fire — it is the correct form.
    /// </summary>
    [Fact]
    public void WholeClassMinusExclusions_WithHigherFloor_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("02-fixed",
            "$notYet = @( 'Judge_WeakVerifier_AdvisoryRecorded', 'Attempt_UsageTokensReachRunJson' )\n" +
            "$expected = 15\n" +
            "$filter = 'FullyQualifiedName~Stage2ConformanceTests' +\n" +
            "          (($notYet | ForEach-Object { \"&FullyQualifiedName!~$_\" }) -join '')\n" +
            "if ($ran -lt $expected) { exit 1 }\n" +
            "exit 0\n");

        AssertSilent(guardrail);
    }

    /// <summary>
    /// FALSE-POSITIVE GUARD, and the load-bearing one: an array and a floor that have nothing to do with
    /// each other. Without the filter-linkage requirement this fires on ordinary scripts.
    /// </summary>
    [Fact]
    public void ArrayNotFeedingAFilter_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("02-unrelated",
            "$requiredFiles = @( 'a.cs', 'b.cs' )\n" +
            "foreach ($f in $requiredFiles) { if (-not (Test-Path $f)) { exit 1 } }\n" +
            "$lines = (Get-Content report.txt | Measure-Object -Line).Lines\n" +
            "if ($lines -lt 40) { exit 1 }\n" +
            "exit 0\n");

        AssertSilent(guardrail);
    }

    /// <summary>
    /// FALSE-POSITIVE GUARD (#97): a header comment EXPLAINING a floor must never be what trips the
    /// check. Comment lines are stripped before the scan, as GR2037 does.
    /// </summary>
    [Fact]
    public void FloorDescribedOnlyInAComment_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("02-comment",
            "# An earlier revision named $tests = @( 'x', 'y' ) and guarded with -lt 14, which could\n" +
            "# never pass. Recorded here so the next author does not reintroduce it.\n" +
            "$filter = 'FullyQualifiedName~SomeClass'\n" +
            "if ($ran -lt 15) { exit 1 }\n" +
            "exit 0\n");

        AssertSilent(guardrail);
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    private IReadOnlyList<Diagnostic> Validate(GuardrailDefinition guardrail) =>
        new PlanValidator(FakeExecutableProbe.All, new BannedPatternRegistry([]))
            .Validate(PlanWithTaskGuardrail(guardrail));

    private void AssertSilent(GuardrailDefinition guardrail) =>
        Assert.DoesNotContain(Validate(guardrail), d => d.Code == DiagnosticCodes.UnsatisfiableGuardrailFloor);

    private GuardrailDefinition WriteScript(string name, string body)
    {
        string dir = Path.Combine(_tempRoot, "tasks", "01-a", "guardrails");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".ps1");
        File.WriteAllText(path, body);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    private PlanDefinition PlanWithTaskGuardrail(GuardrailDefinition guardrail)
    {
        TaskNode task = new()
        {
            Id = "01-a",
            Directory = Path.Combine(_tempRoot, "tasks", "01-a"),
            Description = "task 01-a",
            Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", "01-a", "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [guardrail],
            Preflights = [],
        };

        return new PlanDefinition
        {
            PlanDirectory = _tempRoot,
            Workspace = _tempRoot,
            // Serial so the worktree-mode git-root/terminal-gate checks stay silent; GR2055 is the rule under test.
            Config = new RunConfig { Version = 1, MaxParallelism = 1 },
            Tasks = [task],
            PlanPreflights = [],
            PlanGuardrails = [],
        };
    }
}
