using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2076 (issue #601 candidate 1) — one task REQUIRES a literal in a file that ANOTHER task's guardrail
/// FORBIDS in the same file.
///
/// <para><b>GR2057 already proves this within one guardrail body. The cross-task case had no reader at
/// all</b> — not <c>validate</c>, not <c>graph --check</c>, not a full <c>/guardrails-review</c>. Every one
/// of them has a scope narrower than the collision: Probe C reconciles clauses inside a guardrail file,
/// #474 traces a datum to its carrier, and the write-scope check asks whether a scope covers a path —
/// which, on the measured plan, it did.</para>
///
/// <para>Measured on plan 35 task 13: task 12's test required the terminal row to survive listener teardown
/// while task 13's prompt forbade the only change that allows it. <b>Three attempts and an overwatch
/// intervention</b> before an agent proved it and halted. The plan was unsatisfiable the moment it was
/// authored, and every gate it passed on the way said so about a different scope.</para>
/// </summary>
public sealed class CrossTaskClauseCollisionTests
{
    [Fact]
    public void ARequiredLiteralOneTaskOwesAndAnotherBans_Warns()
    {
        string planDir = BuildPlan([
            ("01-add-marker", @"if ($c -notmatch 'TerminalRescue') { exit 1 }"),
            ("02-remove-it", @"if ($c -match 'TerminalRescue') { exit 1 }")
        ]);
        try
        {
            Diagnostic warning = Assert.Single(
                Validate(planDir), d => d.Code == DiagnosticCodes.CrossTaskClauseCollision);

            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);

            // Both task ids and both guardrail names, because the remedy is a re-author and the author has
            // to know which two things to reconcile. A message naming one side sends them to read the file
            // that is correct.
            Assert.Contains("01-add-marker", warning.Message, StringComparison.Ordinal);
            Assert.Contains("02-remove-it", warning.Message, StringComparison.Ordinal);
            Assert.Contains("TerminalRescue", warning.Message, StringComparison.Ordinal);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void TheSameCollisionInsideONETask_IsLeftToGR2057()
    {
        // Both clauses in one task: GR2057's case, proved from a single body. Reporting it here too would
        // double every finding that check already makes, and an operator resolving one code would be left
        // with a second saying the same thing about the same lines.
        string planDir = BuildPlan([
            ("01-both", @"if ($c -notmatch 'TerminalRescue') { exit 1 }" + "\n"
                        + @"if ($c -match 'TerminalRescue') { exit 1 }")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.CrossTaskClauseCollision);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void TwoTasksOverDIFFERENTFiles_AreSilent()
    {
        // The load-bearing negative. Requiring a token in one file and banning it in another is ordinary
        // and correct — a lint that ignored the path would fire on most plans of any size.
        string planDir = BuildPlan([
            ("01-add-marker", @"if ($c -notmatch 'TerminalRescue') { exit 1 }", "src/A.cs"),
            ("02-remove-it", @"if ($c -match 'TerminalRescue') { exit 1 }", "src/B.cs")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.CrossTaskClauseCollision);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void TwoTasksOverONEFileWithNoOverlap_AreSilent()
    {
        // Same file, unrelated tokens. This is the normal shape of a plan where two tasks touch one file,
        // and it must stay quiet or the code becomes noise on every such plan.
        string planDir = BuildPlan([
            ("01-add-marker", @"if ($c -notmatch 'TerminalRescue') { exit 1 }"),
            ("02-ban-other", @"if ($c -match 'ObsoleteHelper') { exit 1 }")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.CrossTaskClauseCollision);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void AGuardrailReadingTWOFilesIsSilent_becauseTheSubjectIsAmbiguous()
    {
        // With two paths in one body there is no way to say WHICH file a clause is about. Guessing is how a
        // cross-task lint reports a collision that is not there, and its remedy costs a re-author — so the
        // ambiguous case is silence, deliberately, even though a real collision may be hiding in it.
        string planDir = BuildPlan([
            ("01-add-marker",
             "$a = Get-Content 'src/A.cs' -Raw\n$b = Get-Content 'src/Other.cs' -Raw\n"
             + @"if ($a -notmatch 'TerminalRescue') { exit 1 }", null),
            ("02-remove-it", @"if ($c -match 'TerminalRescue') { exit 1 }", "src/A.cs")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.CrossTaskClauseCollision);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void ATooShortWitnessIsSilent_becauseAThreeLetterTokenProvesNothing()
    {
        // A short literal matches half the file by accident. GR2057 applies the same floor for the same
        // reason, and it matters more here: this finding accuses a SECOND task of a defect.
        string planDir = BuildPlan([
            ("01-add-marker", @"if ($c -notmatch 'ab') { exit 1 }"),
            ("02-remove-it", @"if ($c -match 'ab') { exit 1 }")
        ]);
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.CrossTaskClauseCollision);
        }
        finally { Cleanup(planDir); }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static IReadOnlyList<Diagnostic> Validate(string planDir) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(new PlanLoader().Load(planDir).Plan!);

    private static string BuildPlan(IReadOnlyList<(string Id, string Clause)> tasks) =>
        BuildPlan([.. tasks.Select(t => (t.Id, t.Clause, (string?)"src/A.cs"))]);

    /// <param name="tasks">(id, clause, subject path — null means the body supplies its own paths).</param>
    private static string BuildPlan(IReadOnlyList<(string Id, string Clause, string? Subject)> tasks)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr2076-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(planDir).FullName, "guardrails.json"),
            "{\n  \"version\": 1\n}\n");

        foreach ((string id, string clause, string? subject) in tasks)
        {
            string taskDir = Path.Combine(planDir, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{\n  \"description\": \"{id}\",\n  \"dependsOn\": [],\n  \"writeScope\": [\"src/A.cs\"]\n}}\n");
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho ran\nexit 0\n");

            string read = subject is null ? "" : $"$c = Get-Content '{subject}' -Raw\n";
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-shape.ps1"),
                "# catches: the marker is missing or present when it should not be\n" + read + clause + "\nexit 0\n");
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
