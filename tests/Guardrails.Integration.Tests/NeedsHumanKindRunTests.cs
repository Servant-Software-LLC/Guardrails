using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #485 END-TO-END through the real composition root, with REAL scripts (OS-picked <c>.ps1</c> /
/// <c>.sh</c>): an action emits <c>{"needsHuman": {"question": …, "kind": …}}</c>, and the claim must
/// survive every hop the unit tests fake — fragment parse → <c>ActionRun</c> → <c>TaskResult</c> →
/// <c>run.json</c> → the console, the run summary, <c>guardrails status</c>, and the static log site.
///
/// <para>The journal hop is the one that cannot be faked away: <c>guardrails status</c> and the static
/// export read ONLY <c>run.json</c>, so a claim that never lands there is a claim that does not survive
/// the run.</para>
/// </summary>
public sealed class NeedsHumanKindRunTests
{
    private const string Question = "01-check claims WriteScope is absent; it is at WriteScope.cs:14";

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static string RunId(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    /// <summary>
    /// Overwrite a task's action with one that writes a <c>needsHuman</c> fragment and exits clean.
    /// <paramref name="kindJson"/> is the raw <c>, "kind": "…"</c> tail, or empty for the plain
    /// question-only structured form (the UNCLASSIFIED control).
    /// </summary>
    private static void MakeEscalatingAction(ScriptPlanBuilder plan, string taskId, string kindJson)
    {
        string fragment = "{\"needsHuman\": {\"question\": \"" + Question + "\"" + kindJson + "}}";
        string body = OperatingSystem.IsWindows()
            ? $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragment}'\r\nexit 0\r\n"
            : $"#!/usr/bin/env bash\nprintf '%s' '{fragment}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n";

        File.WriteAllText(plan.ActionPath(taskId), body);
    }

    [Fact]
    public async Task DefectiveGuardrailClaim_SurvivesTheWholeRun_ToEverySurface()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        MakeEscalatingAction(plan, "01-escalates", ", \"kind\": \"defective-guardrail\"");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        // (1) --no-ui: the grep-anchored leading line is untouched, and the claim rides one line below it.
        Assert.Contains($"[NEEDS HUMAN] 01-escalates — needs human: {Question}", output, StringComparison.Ordinal);
        Assert.Contains(
            "  [claim] defective-guardrail — the agent disputes the check, not the work (unverified)",
            output, StringComparison.Ordinal);

        // (2) The run summary points at the CHECK and drops the misdirecting "fix the action or guardrails".
        Assert.Contains("Agent's claim [defective-guardrail] — look at the CHECK, not the task.", output, StringComparison.Ordinal);
        Assert.Contains("and if the claim holds fix the guardrail (/guardrails-review)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fix the action or guardrails", output, StringComparison.Ordinal);

        // (3) The JOURNAL carries it — the hop `status` and the static export depend on.
        JsonNode journal = JsonNode.Parse(await File.ReadAllTextAsync(
            RunJournal.PathFor(plan.PlanDir), TestContext.Current.CancellationToken))!;
        JsonNode attempt = journal["tasks"]!["01-escalates"]!["attempts"]!.AsArray()[^1]!;
        Assert.Equal("needs-human", (string?)attempt["outcome"]);
        Assert.Equal("defective-guardrail", (string?)attempt["needsHumanKind"]);

        // (4) `guardrails status` names the kind instead of leaking the raw enum.
        (int statusExit, string statusOut) = await InvokeAsync("status", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, statusExit);
        Assert.Contains("agent escalated [defective-guardrail]", statusOut, StringComparison.Ordinal);
        Assert.DoesNotContain("NeedsHuman", statusOut, StringComparison.Ordinal);

        // (5) The static log site, written during the run: the index cell carries both the machine-readable
        //     kind and the terse chip, and data-status is UNCHANGED so the existing red rule still applies.
        string runId = RunId(plan.PlanDir);
        string index = await File.ReadAllTextAsync(
            Path.Combine(plan.PlanDir, "logs", runId, "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("data-status=\"needs-human\" data-claim=\"defective-guardrail\"", index, StringComparison.Ordinal);
        Assert.Contains("<span class=\"claim\">guardrail</span>", index, StringComparison.Ordinal);

        // (6) The TASK page — the page the live table's `logs` link points at — finally says the task halted.
        string taskPage = await File.ReadAllTextAsync(
            Path.Combine(plan.PlanDir, "logs", runId, "01-escalates", "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("<span class=\"status\" data-status=\"needs-human\" data-claim=\"defective-guardrail\">needs-human</span>",
            taskPage, StringComparison.Ordinal);
        Assert.Contains("<span class=\"claim\">guardrail</span>", taskPage, StringComparison.Ordinal);

        // (7) A post-hoc `logs --export` reproduces it from the JOURNAL ALONE (no in-memory run state).
        (int exportExit, _) = await InvokeAsync("logs", plan.PlanDir, "--export");
        Assert.Equal(ExitCodes.Success, exportExit);
        string exported = await File.ReadAllTextAsync(
            Path.Combine(plan.PlanDir, "logs", runId, "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("data-claim=\"defective-guardrail\"", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedWorkClaim_SurvivesTheWholeRun_AndPointsAtTheTask()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        MakeEscalatingAction(plan, "01-escalates", ", \"kind\": \"blocked-work\"");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        Assert.Contains("  [claim] blocked-work — the agent could not complete the work (unverified)", output, StringComparison.Ordinal);
        Assert.Contains("Agent's claim [blocked-work] — look at the TASK.", output, StringComparison.Ordinal);
        Assert.Contains("answer the question or re-scope the task (action, writeScope, dependencies)", output, StringComparison.Ordinal);

        (_, string statusOut) = await InvokeAsync("status", plan.PlanDir);
        Assert.Contains("agent escalated [blocked-work]", statusOut, StringComparison.Ordinal);

        string index = await File.ReadAllTextAsync(
            Path.Combine(plan.PlanDir, "logs", RunId(plan.PlanDir), "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("data-claim=\"blocked-work\"", index, StringComparison.Ordinal);
        Assert.Contains("<span class=\"claim\">work</span>", index, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and the case that will dominate in the field: an escalation with NO <c>kind</c> must
    /// add nothing anywhere — no claim line, no chip, no attribute, no journal field.
    /// </summary>
    [Fact]
    public async Task UnclassifiedEscalation_AddsNothingOnAnySurface()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        MakeEscalatingAction(plan, "01-escalates", kindJson: "");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        Assert.Contains($"[NEEDS HUMAN] 01-escalates — needs human: {Question}", output, StringComparison.Ordinal);
        Assert.Contains("fix the action or guardrails, then re-run to resume.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[claim]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent's claim", output, StringComparison.Ordinal);

        string journal = await File.ReadAllTextAsync(RunJournal.PathFor(plan.PlanDir), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("needsHumanKind", journal, StringComparison.Ordinal);

        string index = await File.ReadAllTextAsync(
            Path.Combine(plan.PlanDir, "logs", RunId(plan.PlanDir), "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("data-status=\"needs-human\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("data-claim", index, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"claim\"", index, StringComparison.Ordinal);

        (_, string statusOut) = await InvokeAsync("status", plan.PlanDir);
        Assert.Contains("agent escalated", statusOut, StringComparison.Ordinal);
        Assert.DoesNotContain("agent escalated [", statusOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrecognised <c>kind</c> degrades to UNCLASSIFIED — not an error, not a warning, not a log line.
    /// A plan authored against a future harness must still run here rather than halting on a token.
    /// </summary>
    [Fact]
    public async Task UnrecognisedKind_DegradesToUnclassified_WithoutComplaint()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        MakeEscalatingAction(plan, "01-escalates", ", \"kind\": \"some-future-kind\"");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        Assert.Contains($"[NEEDS HUMAN] 01-escalates — needs human: {Question}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[claim]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("some-future-kind", output, StringComparison.Ordinal);

        string journal = await File.ReadAllTextAsync(RunJournal.PathFor(plan.PlanDir), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("needsHumanKind", journal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The free-text form <c>{"needsHuman": "…"}</c> is UNCHANGED (back-compat): it carries no kind, and a
    /// <c>kind</c> is read from the OBJECT form only.
    /// </summary>
    [Fact]
    public async Task FreeTextForm_StillShortCircuits_AndCarriesNoKind()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        string body = OperatingSystem.IsWindows()
            ? "Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{\"needsHuman\": \"plain question\"}'\r\nexit 0\r\n"
            : "#!/usr/bin/env bash\nprintf '%s' '{\"needsHuman\": \"plain question\"}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n";
        File.WriteAllText(plan.ActionPath("01-escalates"), body);

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);
        Assert.Contains("[NEEDS HUMAN] 01-escalates — needs human: plain question", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[claim]", output, StringComparison.Ordinal);
    }
}
