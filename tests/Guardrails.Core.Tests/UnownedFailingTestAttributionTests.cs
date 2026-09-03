using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #587 check B — the FAILURE-TIME ownership note (<see cref="UnownedFailingTestAttribution"/>).
///
/// <para>The positive and negative controls are both drawn from the SAME measured run: plan 33's twelve-task
/// breakdown, whose task 06 changed the tripwire
/// <c>tests/Guardrails.Core.Tests/BreakdownSalvageAllowListTests.cs</c> while declaring only
/// <c>src/Guardrails.Core/Execution/Scheduler.cs</c> in its <c>writeScope</c>. The plan-level baseline
/// preflight went red, the run halted, and <c>guardrails reset</c> cascaded to six tasks. The negative
/// control is a frame from the same run whose file IS owned (task 05 declared it) — proving the check is
/// keyed on OWNERSHIP and not merely on "a test failed".</para>
///
/// <para>Frame bytes are copied from the run logs verbatim, including the foreign attempt-worktree prefix
/// (<c>…\gr-wt\&lt;run&gt;\&lt;plan&gt;\&lt;task&gt;\attempt-1\…</c>) that makes containment fail and forces the
/// suffix arm — the shape a reader built only for "the path is under the root" silently misses.</para>
/// </summary>
public sealed class UnownedFailingTestAttributionTests : IDisposable
{
    /// <summary>The gate's worktree root: a real directory, because the suffix arm confirms itself against the tree.</summary>
    private readonly string _root;

    public UnownedFailingTestAttributionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-own-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // =========================================================================
    // Fixture: plan 33's real write scopes and real stack-trace bytes
    // =========================================================================

    /// <summary>
    /// Plan 33's writeScope union AS BROKEN (commit c04c3d1). Task 06 declares only Scheduler.cs; nothing
    /// anywhere in the twelve tasks declares BreakdownSalvageAllowListTests.cs.
    /// </summary>
    private static readonly string[] PlanThirtyThreeDefectScope =
    [
        "src/Guardrails.Core/Loading/GuardrailClauseText.cs",       // 01
        "src/Guardrails.Core/Loading/PlanValidator.cs",             // 01, 02, 04
        "src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs",      // 02
        "src/Guardrails.Core/Loading/GitLsFilesProbe.cs",           // 02
        "tests/Guardrails.Core.Tests/ProducerCoverageTests.cs",     // 03
        "src/Guardrails.Core/Loading/ProducerCoverage.cs",          // 04
        "src/Guardrails.Core/Loading/DiagnosticCodes.cs",           // 04, 08
        "tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs",        // 05  <- the negative control's owner
        "src/Guardrails.Core/Execution/Scheduler.cs",               // 06  <- and NOTHING else
        "docs/plans/02-schemas-and-contracts.md",                   // 07, 08
        "tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs", // 09
        ".claude/skills/guardrails-review/SKILL.md",                // 10
        ".claude/skills/plan-breakdown/SKILL.md",                   // 10
        ".claude/skills/guardrails-domain-knowledge/SKILL.md",      // 11
        "docs/plans/19-producer-coverage.md",                       // 12
    ];

    private const string TripwireFile = "tests/Guardrails.Core.Tests/BreakdownSalvageAllowListTests.cs";
    private const string OwnedTestFile = "tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs";

    /// <summary>The `dotnet test` failure block for plan 33's own defect, verbatim in shape.</summary>
    private const string TripwireFailureOutput = """
          Determining projects to restore...
          All projects are up-to-date for restore.
          Guardrails.Core -> C:\...\src\Guardrails.Core\bin\Debug\net10.0\Guardrails.Core.dll
        [xUnit.net 00:00:03.41]     Guardrails.Core.Tests.BreakdownSalvageAllowListTests.TheAllowListIsExactlyOneCode_SoWideningItIsADeliberateActWithAFailingTest [FAIL]
          Failed Guardrails.Core.Tests.BreakdownSalvageAllowListTests.TheAllowListIsExactlyOneCode_SoWideningItIsADeliberateActWithAFailingTest [7 ms]
          Error Message:
           Assert.Single() Failure: The collection contained 2 items
          Stack Trace:
             at Guardrails.Core.Tests.BreakdownSalvageAllowListTests.TheAllowListIsExactlyOneCode_SoWideningItIsADeliberateActWithAFailingTest() in C:\Users\David\AppData\Local\Temp\gr-wt\f5ca558e\86106163\06-excuse-gr2060-at-jit-gate\attempt-1\tests\Guardrails.Core.Tests\BreakdownSalvageAllowListTests.cs:line 61
        """;

    /// <summary>The negative control from the same run — a failing test whose file task 05 DOES own.</summary>
    private const string OwnedFailureOutput = """
          Failed Guardrails.Core.Tests.JitPrefixVetoTests.PartialPrefix_TrippingGr2060_IsNotReverted [12 ms]
          Error Message:
           Assert.True() Failure
          Stack Trace:
             at Guardrails.Core.Tests.JitPrefixVetoTests.PartialPrefix_TrippingGr2060_IsNotReverted() in C:\Users\David\AppData\Local\Temp\gr-wt\f5ca558e\86106163\05-author-tests-jit-prefix-veto\attempt-1\tests\Guardrails.Core.Tests\JitPrefixVetoTests.cs:line 172
        """;

    /// <summary>Materialize a workspace-relative file under the gate's worktree root.</summary>
    private void Materialize(params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// fixture\n");
        }
    }

    /// <summary>One stack frame under the gate's OWN worktree root (the containment arm — no file needed).</summary>
    private string LocalFrame(string typeAndMethod, string relativePath, int line) =>
        $"   at {typeAndMethod}() in {Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar))}:line {line}";

    // =========================================================================
    // The measured defect: positive control, then its negative twin
    // =========================================================================

    [Fact]
    public void PlanThirtyThreeDefectState_NamesTheTripwireTestNoTaskOwned()
    {
        Materialize(TripwireFile);

        string? note = UnownedFailingTestAttribution.Note(
            TripwireFailureOutput, PlanThirtyThreeDefectScope, _root);

        // The wording is the contract, so it is pinned VERBATIM rather than sampled: it names the file,
        // states the ownership fact, keeps the causal half CONDITIONAL (a pre-existing red is equally
        // possible — #181, "never build on red"), and offers the two real remedies in the order that keeps
        // the deliverable. It must never suggest deleting or weakening the assertion, which is the only
        // thing here still defending the invariant.
        Assert.Equal(
            "OWNERSHIP: the failing test file 'tests/Guardrails.Core.Tests/BreakdownSalvageAllowListTests.cs' "
            + "is in NO task's writeScope. If this plan's change is what turned it red, no task can fix it - "
            + "the run will spend its DAG and halt here. Give some task that file AND the work of updating "
            + "it, or the change does not belong in this plan.",
            note);
        Assert.DoesNotContain("delete", note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanThirtyThreeFixedState_IsSilent_BecauseTaskSixNowOwnsTheFile()
    {
        // The shipped fix (5b7fe9f) added the tripwire to task 06's writeScope. Same bytes, same run,
        // one entry different — and the note must vanish. This is the pair that proves the check is keyed
        // on OWNERSHIP and not on "a plan-level gate failed".
        Materialize(TripwireFile);
        string[] fixedScope = [.. PlanThirtyThreeDefectScope, TripwireFile];

        Assert.Null(UnownedFailingTestAttribution.Note(TripwireFailureOutput, fixedScope, _root));
    }

    [Fact]
    public void OwnedFailingTest_FromTheSameRun_IsSilent()
    {
        // Task 05 declared tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs. A red there is somebody's
        // job, so there is nothing for this note to say.
        Materialize(OwnedTestFile);

        Assert.Null(UnownedFailingTestAttribution.Note(OwnedFailureOutput, PlanThirtyThreeDefectScope, _root));
    }

    // =========================================================================
    // The silence conditions
    // =========================================================================

    [Fact]
    public void NullScope_IsSilent_BecauseAnIncompleteUnionCannotProveNoTaskOwnsIt()
    {
        // GR2060's condition 9 in a different dress: some task declares no writeScope, so it may write
        // anywhere, and "NO task owns this file" is not provable.
        Materialize(TripwireFile);

        Assert.Null(UnownedFailingTestAttribution.Note(TripwireFailureOutput, producibleScope: null, _root));
    }

    [Fact]
    public void EmptyScope_IsSilent()
    {
        // A plan authorized to write nothing makes the claim true of every file in the tree and useful
        // about none of them.
        Materialize(TripwireFile);

        Assert.Null(UnownedFailingTestAttribution.Note(TripwireFailureOutput, [], _root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("error CS0111: Type 'Launcher' already defines a member called 'Run'\nBuild FAILED.")]
    [InlineData("npm ERR! code ELIFECYCLE\nnpm ERR! Test failed.  See above for more details.")]
    public void NoStackFrame_IsSilent(string? output)
    {
        // The overwhelmingly common failing gate is a build error or a non-.NET tool. No frame, no note.
        Assert.Null(UnownedFailingTestAttribution.Note(output, PlanThirtyThreeDefectScope, _root));
    }

    [Fact]
    public void UnrelativizablePath_IsDropped_NotGuessedAt()
    {
        // A frame from another machine entirely: neither under this worktree root, nor resolvable to any
        // suffix that names a real file under it. Silence beats naming a file the operator cannot find.
        const string foreign =
            "Stack Trace:\n   at Acme.Widgets.Tests.WidgetTests.Explodes() in "
            + "/home/ci/build/9182/tests/Acme.Widgets.Tests/WidgetTests.cs:line 44";

        Assert.Null(UnownedFailingTestAttribution.Note(foreign, PlanThirtyThreeDefectScope, _root));
    }

    [Fact]
    public void UnrelativizablePath_IsDroppedIndividually_WithoutSilencingTheRest()
    {
        // The drop is per-path, not per-output: one foreign frame must not suppress the finding the other
        // frame supports.
        Materialize(TripwireFile);
        string output = string.Join('\n',
            "Stack Trace:",
            "   at Acme.Widgets.Tests.WidgetTests.Explodes() in /home/ci/build/9182/tests/Acme/WidgetTests.cs:line 44",
            "Error Message:",
            "   Assert.Single() Failure",
            "Stack Trace:",
            "   at Guardrails.Core.Tests.BreakdownSalvageAllowListTests.TheAllowListIsExactlyOneCode() in "
            + @"C:\Users\David\AppData\Local\Temp\gr-wt\f5ca558e\86106163\06-excuse-gr2060-at-jit-gate\attempt-1\tests\Guardrails.Core.Tests\BreakdownSalvageAllowListTests.cs:line 61");

        string? note = UnownedFailingTestAttribution.Note(output, PlanThirtyThreeDefectScope, _root);

        Assert.NotNull(note);
        Assert.Contains(TripwireFile, note!, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetTests.cs", note!, StringComparison.Ordinal);
        // ONE file named ⇒ the singular wording.
        Assert.StartsWith("OWNERSHIP: the failing test file", note!, StringComparison.Ordinal);
    }

    // =========================================================================
    // Which frame is "the failing test file"
    // =========================================================================

    [Fact]
    public void NamesTheOutermostFrameOfAStack_NotTheProductionFramesBeneathIt()
    {
        // A .NET stack lists frames innermost-first, so the LAST source-carrying frame is the method the
        // test framework invoked — the file that DECLARES the failing test. The production files the
        // exception passed through are not "the failing test file" and no plan is obliged to own them.
        Materialize("src/Guardrails.Core/Loading/PlanValidator.cs", "tests/Guardrails.Core.Tests/OrphanTests.cs");
        string output = string.Join('\n',
            "  Error Message:",
            "   System.InvalidOperationException : boom",
            "  Stack Trace:",
            LocalFrame("Guardrails.Core.Loading.PlanValidator.Validate", "src/Guardrails.Core/Loading/PlanValidator.cs", 88),
            LocalFrame("Guardrails.Core.Tests.OrphanTests.Boom", "tests/Guardrails.Core.Tests/OrphanTests.cs", 12));

        // A scope that owns NEITHER file, so only the outermost-frame rule can explain the result.
        string? note = UnownedFailingTestAttribution.Note(
            output, ["src/Guardrails.Core/Execution/Scheduler.cs"], _root);

        Assert.NotNull(note);
        Assert.Contains("tests/Guardrails.Core.Tests/OrphanTests.cs", note!, StringComparison.Ordinal);
        Assert.DoesNotContain("PlanValidator.cs", note!, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncContinuationSeparator_DoesNotSplitOneStack()
    {
        // '--- End of stack trace from previous location ---' sits INSIDE one logical stack. Splitting on
        // it would promote the inner (production) half to an outermost frame of its own.
        Materialize("src/Guardrails.Core/Execution/TaskExecutor.cs", "tests/Guardrails.Core.Tests/AsyncTests.cs");
        string output = string.Join('\n',
            "  Stack Trace:",
            LocalFrame("Guardrails.Core.Execution.TaskExecutor.RunAsync", "src/Guardrails.Core/Execution/TaskExecutor.cs", 41),
            "--- End of stack trace from previous location ---",
            LocalFrame("Guardrails.Core.Tests.AsyncTests.Waits", "tests/Guardrails.Core.Tests/AsyncTests.cs", 9));

        string? note = UnownedFailingTestAttribution.Note(output, ["docs/plans/19-producer-coverage.md"], _root);

        Assert.NotNull(note);
        Assert.Contains("tests/Guardrails.Core.Tests/AsyncTests.cs", note!, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskExecutor.cs", note!, StringComparison.Ordinal);
    }

    // =========================================================================
    // Bounds: de-duplication and the cap
    // =========================================================================

    [Fact]
    public void RepeatedFile_IsNamedOnce()
    {
        // Three failing tests in ONE unowned file is still one ownership problem.
        string output = string.Join('\n',
            "  Stack Trace:",
            LocalFrame("T.Tests.OrphanTests.A", "tests/T.Tests/OrphanTests.cs", 10),
            "  Stack Trace:",
            LocalFrame("T.Tests.OrphanTests.B", "tests/T.Tests/OrphanTests.cs", 20),
            "  Stack Trace:",
            LocalFrame("T.Tests.OrphanTests.C", "tests/T.Tests/OrphanTests.cs", 30));

        string? note = UnownedFailingTestAttribution.Note(output, ["src/T/Thing.cs"], _root);

        Assert.NotNull(note);
        Assert.StartsWith("OWNERSHIP: the failing test file 'tests/T.Tests/OrphanTests.cs'", note!, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(note!, "OrphanTests.cs"));
    }

    [Fact]
    public void MoreThanFiveUnownedFiles_NamesFiveAndCountsTheRest()
    {
        // A suite-wide red must not turn an operator-facing reason into a wall of text.
        var lines = new List<string>();
        for (int i = 1; i <= 8; i++)
        {
            lines.Add("  Stack Trace:");
            lines.Add(LocalFrame($"T.Tests.Orphan{i:00}Tests.A", $"tests/T.Tests/Orphan{i:00}Tests.cs", i));
        }

        string? note = UnownedFailingTestAttribution.Note(
            string.Join('\n', lines), ["src/T/Thing.cs"], _root);

        // The plural wording, pinned verbatim alongside the cap and the honest "+N more" count.
        Assert.Equal(
            "OWNERSHIP: these failing test files are in NO task's writeScope: "
            + "'tests/T.Tests/Orphan01Tests.cs', 'tests/T.Tests/Orphan02Tests.cs', "
            + "'tests/T.Tests/Orphan03Tests.cs', 'tests/T.Tests/Orphan04Tests.cs', "
            + "'tests/T.Tests/Orphan05Tests.cs' (+3 more). If this plan's change is what turned them red, "
            + "no task can fix them - the run will spend its DAG and halt here. Give some task those files "
            + "AND the work of updating them, or the change does not belong in this plan.",
            note);
        Assert.DoesNotContain("Orphan06Tests.cs", note!, StringComparison.Ordinal);
    }

    // =========================================================================
    // Coverage is decided by the SAME matcher the harness enforces at write time
    // =========================================================================

    [Theory]
    [InlineData("tests/OrphanSuite/OrphanTests.cs")] // the literal
    [InlineData("tests/OrphanSuite/**")]             // a directory glob
    [InlineData("tests/OrphanSuite/")]               // an explicit directory marker (#136)
    [InlineData("tests/OrphanSuite")]                // a bare directory
    [InlineData("tests/**/OrphanTests.cs")]          // a ** glob
    public void CoverageUsesWriteScopeIsInScope_SoAGlobEntryCounts(string scopeEntry)
    {
        // The note must not disagree with the runtime write-scope check about what a task may write.
        string output = "  Stack Trace:\n" + LocalFrame("T.Tests.OrphanTests.A", "tests/OrphanSuite/OrphanTests.cs", 10);

        Assert.Null(UnownedFailingTestAttribution.Note(output, [scopeEntry], _root));
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
