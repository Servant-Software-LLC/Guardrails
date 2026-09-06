using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2073 (issue #540) — a task whose <c>writeScope</c> mixes a <c>.claude/</c> deliverable with a normal
/// one is a TWO-MECHANISM task, and one atomic attempt has to deliver through both.
///
/// <para>
/// <b>Measured.</b> <c>27-operator-visibility</c> task <c>08-record-visibility-surfaces-in-ssot</c>:
/// <b>12 attempts, ~$4.66, never green</b>, on the last task of an otherwise-green 8-task plan whose
/// other seven passed in 8 attempts total (7 of them first-pass). Two paths, <c>dependsOn</c> fan-in of
/// 1, no <c>maxTurns</c> bump — GR2042 is silent and correctly so. The task was not too big by any
/// measure the tooling reads. It was two mechanisms: the SSOT by direct <c>Edit</c>, the skill by
/// <c>needsHarnessWrite</c>, because the tool-permission layer refuses a direct write under
/// <c>.claude/</c>.
/// </para>
///
/// <para>
/// <b>Why it costs the whole task.</b> An attempt is atomic and a failed attempt is rolled back — correct
/// design, not the defect. The consequence is that partial progress is impossible: attempt 7 wrote the
/// SSOT correctly (+35 lines, verified in its salvage ref) and never wrote the skill, so the whole
/// attempt rolled back including the good half; by attempt 10 the SSOT guardrail — the half already
/// solved — was failing again. Getting 90% right scores zero.
/// </para>
/// </summary>
public sealed class MixedWriteMechanismValidatorTests
{
    /// <summary>
    /// The headline, and the exact shape that cost 12 attempts: one <c>.claude/</c> path, one normal one.
    /// </summary>
    [Fact]
    public void AScopeMixingClaudeWithANormalPath_Warns()
    {
        string planDir = BuildPlan([
            "docs/plans/02-schemas-and-contracts.md",
            ".claude/skills/guardrails-domain-knowledge/SKILL.md"
        ]);
        try
        {
            Diagnostic warning = Assert.Single(
                Validate(planDir), d => d.Code == DiagnosticCodes.MixedWriteMechanisms);

            // The message must name BOTH halves and which mechanism each needs — a warning that says
            // "this task is mixed" without saying what to split is a warning nobody can act on.
            Assert.Contains("needsHarnessWrite", warning.Message, StringComparison.Ordinal);
            Assert.Contains(".claude/skills/guardrails-domain-knowledge/SKILL.md", warning.Message,
                StringComparison.Ordinal);
            Assert.Contains("docs/plans/02-schemas-and-contracts.md", warning.Message,
                StringComparison.Ordinal);
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        }
        finally { Cleanup(planDir); }
    }

    /// <summary>
    /// The polarity controls, and they matter more than the positive one: this check fires on a
    /// two-path task, which is SMALL by every other measure, so a false positive would demand a split
    /// that buys nothing and would train an author to ignore the code. Neither single-mechanism shape
    /// says anything.
    /// </summary>
    [Theory]
    [InlineData("docs/a.md", "docs/b.md")]                                  // both direct
    [InlineData(".claude/skills/x/SKILL.md", ".claude/agents/y.md")]        // both harness-written
    public void ASingleMechanismScope_IsSilent(string first, string second)
    {
        string planDir = BuildPlan([first, second]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.MixedWriteMechanisms);
        }
        finally { Cleanup(planDir); }
    }

    /// <summary>
    /// A one-path task cannot mix anything, whichever mechanism it uses. Stated as a test because the
    /// check reads <c>writeScope</c> and a careless predicate over an empty or single-entry list is how
    /// a lint starts firing on every task in a plan.
    /// </summary>
    [Theory]
    [InlineData(".claude/skills/x/SKILL.md")]
    [InlineData("docs/a.md")]
    public void ASinglePathScope_IsSilent(string only)
    {
        string planDir = BuildPlan([only]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.MixedWriteMechanisms);
        }
        finally { Cleanup(planDir); }
    }

    /// <summary>
    /// A nested <c>.claude/</c> is the same mechanism — the tool-permission layer keys on the segment,
    /// not on the path starting there. Without this, a plan folder that lives under a subdirectory
    /// escapes the check for a reason that has nothing to do with how the file gets written.
    /// </summary>
    [Fact]
    public void ANestedClaudeSegment_CountsAsHarnessWritten()
    {
        string planDir = BuildPlan(["src/App.cs", "tools/repo/.claude/skills/x/SKILL.md"]);
        try
        {
            Assert.Single(Validate(planDir), d => d.Code == DiagnosticCodes.MixedWriteMechanisms);
        }
        finally { Cleanup(planDir); }
    }

    /// <summary>
    /// GR2073 and GR2042 stay separate, and this pins the boundary the #378 work drew. A two-path task
    /// is silent for #378 (too small) and loud for #540 (mixed) — sharing a code would let an operator
    /// resolving one silence the other, and their remedies differ: #378's is a split by collaborator,
    /// this one's is a split by mechanism.
    /// </summary>
    [Fact]
    public void ItIsIndependentOfTheOverScopeLint()
    {
        string planDir = BuildPlan(["src/App.cs", ".claude/skills/x/SKILL.md"]);
        try
        {
            IReadOnlyList<Diagnostic> diagnostics = Validate(planDir);

            Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.MixedWriteMechanisms);
            Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.StructuralOverScope);
        }
        finally { Cleanup(planDir); }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static IReadOnlyList<Diagnostic> Validate(string planDir) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(new PlanLoader().Load(planDir).Plan!);

    private static string BuildPlan(IReadOnlyList<string> scopeEntries)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr540-" + Guid.NewGuid().ToString("N"));
        string taskDir = Path.Combine(planDir, "tasks", "01-do-thing");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{\n  \"version\": 1\n}\n");

        string scope = string.Join(", ", scopeEntries.Select(e => $"\"{e}\""));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $"{{\n  \"description\": \"Do the one thing\",\n  \"dependsOn\": [],\n  \"writeScope\": [{scope}]\n}}\n");

        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho ran\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"),
            "# catches: the action produced no evidence it ran\nexit 0\n");

        return planDir;
    }

    private static void Cleanup(string planDir)
    {
        try { Directory.Delete(planDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
