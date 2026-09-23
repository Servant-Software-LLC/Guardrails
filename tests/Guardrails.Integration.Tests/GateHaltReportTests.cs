using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #762: the console block for a plan-gate halt. Before it, a failed plan preflight printed one generic sentence and
/// a pointer to <c>run.json</c>, dropping the check's name, its reason, the captured-output directory and the tail
/// of its own headline, all of which it had just journaled. These pin the rendered text exactly (#754: operator-
/// visible wording is a contract), and the real-run tests in <see cref="PreflightHaltConsoleTests"/> prove the
/// composition root prints it.
/// </summary>
public sealed class GateHaltReportTests
{
    private const string JournalPath = "/plans/demo/state/run.json";

    private static RunHalt Halt(string? logDir, params (string Name, string Reason)[] checks) => new()
    {
        Kind = RunHaltKind.PlanPreflightFailed,
        HaltedAt = DateTimeOffset.UnixEpoch,
        Headline = "Plan preflight FAILED — halting before scheduling any task: "
                   + string.Join(", ", checks.Select(c => c.Name)),
        FailedChecks = checks.Select(c => new FailedGuardrail { Name = c.Name, Reason = c.Reason }).ToList(),
        LogDir = logDir
    };

    private static string[] Render(RunHalt halt)
    {
        var output = new StringWriter();
        GateHaltReport.Write(halt, JournalPath, output);
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
                "  Logs:  logs/2026-09-23T11-40-03Z-d61c/preflights",
                "  State: /plans/demo/state/run.json (\"planPreflights\")",
                ""
            ],
            lines);
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
                "  Logs:  logs/run-1/preflights",
                "  State: /plans/demo/state/run.json (\"planPreflights\")",
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
        Assert.Equal("  State: /plans/demo/state/run.json (\"planPreflights\")", lines[^2]);
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
        // The sample-pair and openai-compat preflights halt before any capture directory exists.
        string[] lines = Render(Halt(null, ("sample pair 'x'", "ValidRejected: the .valid half failed")));

        Assert.DoesNotContain(lines, l => l.StartsWith("  Logs:", StringComparison.Ordinal));
        Assert.Equal("  State: /plans/demo/state/run.json (\"planPreflights\")", lines[^2]);
    }

    [Fact]
    public void TheTerminalGateNamesItsOwnSection()
    {
        RunHalt halt = Halt("logs/run-1/guardrails", ("01-suite", "3 tests failed")) with
        {
            Kind = RunHaltKind.PlanGuardrailFailed,
            Headline = "Terminal gate FAILED on the merged HEAD: 01-suite"
        };

        Assert.Equal("  State: /plans/demo/state/run.json (\"planGuardrails\")", Render(halt)[^2]);
    }

    [Fact]
    public void NoReadableHalt_WritesNothing_SoTheCallerCanFallBack()
    {
        var output = new StringWriter();

        bool wrote = GateHaltReport.TryWriteFromJournal(
            Path.Combine(Path.GetTempPath(), "gr-no-such-" + Guid.NewGuid().ToString("N"), "run.json"), output);

        Assert.False(wrote);
        Assert.Equal(string.Empty, output.ToString());
    }
}
