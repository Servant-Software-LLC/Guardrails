using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #762: the console block for a plan-gate halt. Before it, a failed plan preflight printed one generic sentence and
/// a pointer to <c>run.json</c>, dropping the check's name, its reason, the captured-output directory and the tail
/// of its own headline, all of which it had just journaled. These pin the rendered text exactly (#754: operator-
/// visible wording is a contract), and the real-run tests in <see cref="PreflightHaltConsoleTests"/> and
/// <c>SampleVerifierWiringTests</c> prove the composition root prints it.
/// </summary>
public sealed class GateHaltReportTests
{
    private static readonly string PlanDir = Path.Combine(Path.GetTempPath(), "gr-halt-report-demo");

    private static string StateLine(string section = "planPreflights") =>
        $"  State: {RunJournal.PathFor(PlanDir)} (\"{section}\")";

    private static string LogsLine(string logDir) => $"  Logs:  {Path.GetFullPath(Path.Combine(PlanDir, logDir))}";

    private static RunHalt Halt(string? logDir, params (string Name, string Reason)[] checks) => new()
    {
        Kind = RunHaltKind.PlanPreflightFailed,
        HaltedAt = DateTimeOffset.UnixEpoch,
        Headline = "Plan preflight FAILED — halting before scheduling any task: "
                   + string.Join(", ", checks.Select(c => c.Name)),
        FailedChecks = checks.Select(c => new FailedGuardrail { Name = c.Name, Reason = c.Reason }).ToList(),
        LogDir = logDir
    };

    private static string[] Render(RunHalt halt, string? leadLine = null, bool pointersOnly = false)
    {
        var output = new StringWriter();
        Assert.True(GateHaltReport.Write(halt, PlanDir, output, leadLine, pointersOnly));
        return output.ToString().Replace("\r\n", "\n").Split('\n');
    }

    [Fact]
    public void OneCheck_RendersTheFullHeadline_NameReasonLogs_AndTheStatePointerLast()
    {
        // The issue's reported case, reason verbatim: a check-authored diagnosis AND remedy that used to reach
        // only run.json.
        const string reason =
            "No '[CI] Bifrost' (bifrost-ci.yml) run found for main@36b912a7 - the commit this plan branched from. "
            + "CI may not have run yet for that commit, or the search window (last 30 main runs) doesn't reach back far enough.";

        string[] lines = Render(Halt("logs/2026-09-23T11-40-03Z-d61c/preflights", ("01-baseline-main-ci-green", reason)));

        Assert.Equal(
            [
                "",
                "Plan preflight FAILED — halting before scheduling any task: 01-baseline-main-ci-green",
                "",
                "  FAILED: 01-baseline-main-ci-green",
                "    " + reason,
                "",
                LogsLine("logs/2026-09-23T11-40-03Z-d61c/preflights"),
                StateLine(),
                ""
            ],
            lines);
    }

    [Fact]
    public void TheLogsLine_IsAbsolute_LikeTheStateLine()
    {
        string logs = Render(Halt("logs/run-1/preflights", ("01-a", "r"))).Single(l => l.StartsWith("  Logs:", StringComparison.Ordinal));

        string path = logs["  Logs:  ".Length..];
        Assert.True(Path.IsPathFullyQualified(path), path);
        Assert.Equal(Path.GetFullPath(Path.Combine(PlanDir, "logs", "run-1", "preflights")), path);
    }

    [Fact]
    public void SeveralChecks_EachGetTheirOwnBlock_InRecordOrder_WithMultiLineReasonsIndented()
    {
        string[] lines = Render(Halt(
            "logs/run-1/preflights",
            ("01-alpha", "alpha diagnosis\nalpha remedy"),
            ("02-beta", "beta diagnosis")));

        Assert.Equal(
            [
                "",
                "Plan preflight FAILED — halting before scheduling any task: 01-alpha, 02-beta",
                "",
                "  FAILED: 01-alpha",
                "    alpha diagnosis",
                "    alpha remedy",
                "  FAILED: 02-beta",
                "    beta diagnosis",
                "",
                LogsLine("logs/run-1/preflights"),
                StateLine(),
                ""
            ],
            lines);
    }

    [Fact]
    public void AnOverlongReason_IsCutToTheSharedOutputTail_WithAMarkerBeforeIt()
    {
        // 70 lines: the first 10 must go, the last 60 must stay, and the marker must say the START was dropped.
        string reason = string.Join("\n", Enumerable.Range(1, 70).Select(i => $"reason line {i:D2}"));

        string[] lines = Render(Halt("logs/run-1/preflights", ("01-verbose", reason)));

        int name = Array.IndexOf(lines, "  FAILED: 01-verbose");
        Assert.Equal(
            $"    [earlier output omitted: showing the last {OutputTail.MaxLines} lines / {OutputTail.MaxChars} characters; "
            + "the full reason is in run.json]",
            lines[name + 1]);
        Assert.Equal("    reason line 11", lines[name + 2]);
        Assert.Equal("    reason line 70", lines[name + 1 + OutputTail.MaxLines]);
        Assert.DoesNotContain("    reason line 10", lines);
        Assert.Equal(StateLine(), lines[^2]);
    }

    [Fact]
    public void AnOverlongSingleLine_IsCutByTheCharacterCap()
    {
        string reason = new string('a', 100) + new string('z', OutputTail.MaxChars);

        string[] lines = Render(Halt(null, ("01-wide", reason)));

        int name = Array.IndexOf(lines, "  FAILED: 01-wide");
        Assert.StartsWith("    [earlier output omitted", lines[name + 1], StringComparison.Ordinal);
        Assert.Equal("    " + new string('z', OutputTail.MaxChars), lines[name + 2]);
    }

    [Fact]
    public void WithNoCapturedLogDir_TheLogsLineIsOmitted_AndTheStatePointerIsStillLast()
    {
        string[] lines = Render(Halt(null, ("01-a", "the reason")));

        Assert.DoesNotContain(lines, l => l.StartsWith("  Logs:", StringComparison.Ordinal));
        Assert.Equal(StateLine(), lines[^2]);
    }

    [Fact]
    public void PointersOnly_PrintsNoSecondHeadlineAndNoFindings_JustLogsAndState()
    {
        // The sample-pair and endpoint sources printed every finding themselves; repeating them is noise.
        string[] lines = Render(Halt("logs/run-1/preflights", ("sample pair 'x'", "ValidRejected: the .valid half failed")),
            pointersOnly: true);

        Assert.Equal(["", LogsLine("logs/run-1/preflights"), StateLine(), ""], lines);
    }

    [Fact]
    public void ALeadLine_ReplacesTheRecordedHeadline()
    {
        string[] lines = Render(Halt(null, ("01-a", "still broken")), leadLine: "Plan preflight still failing:");

        Assert.Equal("Plan preflight still failing:", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("halting before scheduling", StringComparison.Ordinal));
        Assert.Contains("  FAILED: 01-a", lines);
    }

    [Fact]
    public void TheTerminalGateNamesItsOwnSection()
    {
        RunHalt halt = Halt("logs/run-1/guardrails", ("01-suite", "3 tests failed")) with
        {
            Kind = RunHaltKind.PlanGuardrailFailed,
            Headline = "Terminal gate FAILED on the merged HEAD: 01-suite"
        };

        Assert.Equal(StateLine("planGuardrails"), Render(halt)[^2]);
    }

    [Fact]
    public void NoReadableHalt_WritesNothing_SoTheCallerCanFallBack()
    {
        var output = new StringWriter();

        bool wrote = GateHaltReport.TryWriteFromJournal(
            Path.Combine(Path.GetTempPath(), "gr-no-such-" + Guid.NewGuid().ToString("N")), output);

        Assert.False(wrote);
        Assert.Equal(string.Empty, output.ToString());
    }

    // ── A hand-edited run.json (#762 review): nulls where the model promises none must not crash the CLI. ──

    [Theory]
    [InlineData("null", false, "")]                                                 // no list at all → fallback
    [InlineData("[null]", false, "")]                                               // nothing usable → fallback
    [InlineData("[null, {\"name\": \"01-kept\", \"reason\": \"r\"}]", true, "  FAILED: 01-kept")]  // null skipped
    [InlineData("[{\"name\": null, \"reason\": \"r\"}]", false, "")]                // nameless → fallback
    [InlineData("[{\"name\": \"01-noreason\", \"reason\": null}]", true, "    (no reason recorded)")]
    public void AHandEditedHalt_WithNullChecks_NeverThrows(string failedChecksJson, bool expectWritten, string expectedLine)
    {
        string planDir = Directory.CreateTempSubdirectory("gr-halt-nulls-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(planDir, "state"));
            File.WriteAllText(RunJournal.PathFor(planDir),
                $$"""
                {
                  "planHash": "x",
                  "runId": "r",
                  "tasks": {},
                  "halt": {
                    "kind": "plan-preflight-failed",
                    "haltedAt": "2026-09-23T11:40:03Z",
                    "headline": "Plan preflight FAILED — halting before scheduling any task: 01-kept",
                    "failedChecks": {{failedChecksJson}}
                  }
                }
                """);
            Assert.NotNull(JournalReader.Read(RunJournal.PathFor(planDir)).Halt);   // the fixture parses

            var output = new StringWriter();
            bool wrote = GateHaltReport.TryWriteFromJournal(planDir, output);

            Assert.Equal(expectWritten, wrote);
            if (expectWritten)
            {
                Assert.Contains(expectedLine, output.ToString().Replace("\r\n", "\n").Split('\n'));
            }
            else
            {
                Assert.Equal(string.Empty, output.ToString());
            }
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch (IOException) { }
        }
    }
}
