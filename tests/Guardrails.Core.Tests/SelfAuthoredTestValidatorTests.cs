using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2075 (issue #521 gap 2) — a task that AUTHORS ITS OWN TEST and then grades itself with it.
///
/// <para><b>The probe that should have caught this is keyed on something that does not exist.</b> The "tests
/// gameable" rule reads: <i>the implementation task's <c>writeScope</c> must EXCLUDE the test files its
/// UPSTREAM TEST-AUTHOR TASK owns</i>. A task with no upstream test-author has nothing to exclude, so the
/// rule is <b>vacuously satisfied</b> — and #155's red-then-green, the strongest anti-tautology control in
/// the system, is not weakened here but <b>absent entirely</b>.</para>
///
/// <para><b>Why it is a WARNING.</b> Authoring the test alongside the wiring is legitimate — a
/// composition-root test often cannot be written before the thing it wires exists. The requirement is that
/// the case be NAMED and carry a compensating control, not that it be banned.</para>
///
/// <para><b>Why it exists at all is the compounding with GR2074.</b> Neither gap alone reaches blocker: with
/// a real TDD-red half a hollow test is caught by the census whatever the grep accepts, and with a
/// call-anchored grep the missing red half is covered by the structural check. Together, the only control
/// over the test's honesty is a grep and the grep accepts a mention — so the task goes green with nothing
/// wired.</para>
/// </summary>
public sealed class SelfAuthoredTestValidatorTests
{
    [Fact]
    public void ATaskThatWritesItsOwnTestWithNoUpstreamAuthor_Warns()
    {
        // The measured shape: one task, its own test file in its own writeScope, its own check over it.
        string planDir = BuildPlan([
            ("01-wire-it", "src/App.cs|tests/App.Tests/WiringTests.cs", "")
        ]);
        try
        {
            Diagnostic warning = Assert.Single(
                Validate(planDir), d => d.Code == DiagnosticCodes.TaskGradesItsOwnAuthoredTest);

            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains("WiringTests.cs", warning.Message, StringComparison.Ordinal);

            // The message must offer the split AND the named-exception route, because the shape is
            // legitimate — a warning that only says "don't" gets ignored by the author who had a reason.
            Assert.Contains("SPLIT", warning.Message, StringComparison.Ordinal);
            Assert.Contains("name the exception", warning.Message, StringComparison.Ordinal);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void TheNormalTDDPAIR_IsSilent()
    {
        // The polarity control that matters most. In the ordinary shape an upstream author-tests task owns
        // the test file and the implementation depends on it — that is the pattern the whole system is
        // built around, and flagging it would fire on nearly every well-formed plan.
        string planDir = BuildPlan([
            ("01-author-tests", "tests/App.Tests/WiringTests.cs", ""),
            ("02-implement", "src/App.cs|tests/App.Tests/WiringTests.cs", "01-author-tests")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.TaskGradesItsOwnAuthoredTest);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void AnAncestorTwoHopsUp_StillCounts()
    {
        // Ancestry is transitive: the author-tests task need not be the direct parent. Reading only
        // dependsOn[0] would re-open the gap for any plan with an intermediate task.
        string planDir = BuildPlan([
            ("01-author-tests", "tests/App.Tests/WiringTests.cs", ""),
            ("02-scaffold", "src/Scaffold.cs", "01-author-tests"),
            ("03-implement", "src/App.cs|tests/App.Tests/WiringTests.cs", "02-scaffold")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.TaskGradesItsOwnAuthoredTest);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void AnAuthorTestsTaskIsSILENT_becauseItsCheckAssertsTheTestsFAIL()
    {
        // The case that broke the first version of this lint, and the one that decides whether it is
        // shippable at all. An author-tests task ALSO writes its own test files and ALSO carries guardrails
        // over them — and it is the CORRECT shape, the upstream half of the pair this rule exists to
        // restore. Its census asserts the tests FAIL against stubs, which nothing hollow can satisfy.
        //
        // Keyed on "writes a test and has a guardrail", this fired on the author-tests task of every
        // well-formed TDD plan — a lint that flags the pattern it is trying to encourage. The discriminator
        // is that the task asserts its tests PASS.
        string planDir = BuildPlan([("01-author-tests-wiring", "tests/App.Tests/WiringTests.cs", "")]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.TaskGradesItsOwnAuthoredTest);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void ATaskWritingNoTestFile_IsSilent()
    {
        // Most tasks. A predicate careless about what counts as a test path would fire on all of them.
        string planDir = BuildPlan([("01-wire-it", "src/App.cs|docs/notes.md", "")]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.TaskGradesItsOwnAuthoredTest);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void APathMerelyLIVINGUnderTestsIsNotATestFile()
    {
        // `tests/App.Tests/Fixtures/sample.json` sits under a test tree and is not a test. The lint keys on
        // the FILE, not the directory, because a task that writes a fixture has not authored its own grader.
        string planDir = BuildPlan([("01-wire-it", "src/App.cs|tests/App.Tests/Fixtures/sample.json", "")]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.TaskGradesItsOwnAuthoredTest);
        }
        finally { Cleanup(planDir); }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static IReadOnlyList<Diagnostic> Validate(string planDir) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(new PlanLoader().Load(planDir).Plan!);

    /// <param name="tasks">(id, pipe-separated writeScope, comma-separated dependsOn).</param>
    /// <remarks>
    /// The guardrail NAME is load-bearing, so the fixture assigns it the way a real plan does: an
    /// <c>author-tests</c> task carries the TDD-red census (<c>tests-fail-on-stubs</c>), everything else
    /// asserts its tests pass. Giving every task a <c>tests-pass</c> guardrail — as the first version of
    /// this fixture did — makes the author-tests half look like the defect and the lint look broken.
    /// </remarks>
    private static string BuildPlan(IReadOnlyList<(string Id, string Scope, string DependsOn)> tasks)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr2075-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(planDir).FullName, "guardrails.json"),
            "{\n  \"version\": 1\n}\n");

        foreach ((string id, string scope, string dependsOn) in tasks)
        {
            string taskDir = Path.Combine(planDir, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string scopeJson = string.Join(", ", scope.Split('|').Select(e => $"\"{e}\""));
            string depsJson = string.IsNullOrEmpty(dependsOn)
                ? ""
                : string.Join(", ", dependsOn.Split(',').Select(d => $"\"{d}\""));

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{\n  \"description\": \"{id}\",\n  \"dependsOn\": [{depsJson}],\n"
                + $"  \"writeScope\": [{scopeJson}]\n}}\n");
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho ran\nexit 0\n");
            string guardrailName = id.Contains("author-tests", StringComparison.Ordinal)
                ? "01-tests-fail-on-stubs"
                : "01-tests-pass";

            File.WriteAllText(Path.Combine(taskDir, "guardrails", guardrailName + ".sh"),
                "# catches: the action produced no evidence it ran\nexit 0\n");
        }

        return planDir;
    }

    private static void Cleanup(string planDir)
    {
        try { Directory.Delete(planDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
