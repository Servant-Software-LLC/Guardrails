using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// The terminal gate is visible while it runs (issue #625).
///
/// <para><b>The defect was a page that looked exactly like a finished run.</b> The log site regenerates on
/// observer events and the terminal gate is not a task, so it raised none: the last write landed as the
/// final task went green and nothing scheduled another. Measured on run <c>2026-09-05T07-47-36Z-2ada</c>,
/// live — task 04 succeeded at <c>08:50:12Z</c>, <c>index.html</c> was last written at <c>08:50:12Z</c>, and
/// twelve minutes later a whole-solution <c>dotnet test</c> was still running as
/// <c>02-all-tests-pass</c>. The generated page contained <b>zero</b> occurrences of "Terminal Gate", "Full
/// Flight", or any check name — four green tasks and nothing else.</para>
///
/// <para>An operator reading that page concludes the run is done. The gate that can still fail it is
/// mid-flight.</para>
/// </summary>
public sealed class TerminalGateVisibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"gr625-{Guid.NewGuid():N}");

    private const string RunId = "2026-09-05T07-47-36Z-2ada";

    private string LogsRoot => Path.Combine(_root, "logs", RunId);

    private OnTheFlyLogSiteObserver NewObserver()
    {
        Directory.CreateDirectory(LogsRoot);
        return new OnTheFlyLogSiteObserver(
            IRunObserver.Null, LogsRoot, RunId, [Task("01-alpha"), Task("02-beta")], liveUrlForTask: null);
    }

    private static TaskNode Task(string id) => new()
    {
        Id = id,
        Directory = id,
        Description = id,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
    };

    private string ReadIndex() => File.ReadAllText(Path.Combine(LogsRoot, "index.html"));

    [Fact]
    public void BeforeTheGateStarts_TheIndexCarriesNoGateBand()
    {
        // The load-bearing negative. A band that always rendered would have to say something about a phase
        // that has not begun — and a byte-for-byte golden pins this page's no-halt output, so an
        // unconditional band would also rewrite every existing run's index for an event that never happened.
        OnTheFlyLogSiteObserver observer = NewObserver();
        observer.TaskStarting(Task("01-alpha"));

        Assert.DoesNotContain("Terminal gate", ReadIndex(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenTheGateSTARTS_TheIndexSaysSo_AndSaysTheRunIsNotFinished()
    {
        // The whole point. "All tasks green" and "all tasks green, gate still running" must not render
        // identically, because they are the two states an operator most needs to tell apart at that moment.
        OnTheFlyLogSiteObserver observer = NewObserver();
        observer.TaskStarting(Task("01-alpha"));

        observer.TerminalGateStarting(
            ["01-solution-builds", "02-all-tests-pass", "03-sdk-union-verified"],
            DateTimeOffset.Parse("2026-09-05T08:50:15Z", System.Globalization.CultureInfo.InvariantCulture));

        string index = ReadIndex();

        Assert.Contains("Terminal gate RUNNING", index, StringComparison.Ordinal);
        Assert.Contains("NOT finished", index, StringComparison.Ordinal);

        // Every check named. The measured page contained none of these strings at all, which is why an
        // operator could not tell WHAT was running, let alone that anything was.
        Assert.Contains("01-solution-builds", index, StringComparison.Ordinal);
        Assert.Contains("02-all-tests-pass", index, StringComparison.Ordinal);
        Assert.Contains("03-sdk-union-verified", index, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileRunning_NoCheckIsReportedAsPassed()
    {
        // Nothing distinguishes a check that has finished from one that has not while the gate is in
        // flight, and inventing that distinction would be the same defect wearing the opposite polarity —
        // a page asserting a verdict that has not happened.
        OnTheFlyLogSiteObserver observer = NewObserver();
        observer.TaskStarting(Task("01-alpha"));

        observer.TerminalGateStarting(["01-solution-builds"], DateTimeOffset.UtcNow);

        string index = ReadIndex();
        Assert.Contains("01-solution-builds — pending", index, StringComparison.Ordinal);
        Assert.DoesNotContain("01-solution-builds — passed", index, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheGatePASSES_TheBandSettles_AndTheRunReadsFinished()
    {
        OnTheFlyLogSiteObserver observer = NewObserver();
        observer.TaskStarting(Task("01-alpha"));

        observer.TerminalGateStarting(["01-solution-builds", "02-all-tests-pass"], DateTimeOffset.UtcNow);
        observer.TerminalGateFinished(passed: true, failedNames: []);

        string index = ReadIndex();
        Assert.Contains("Terminal gate PASSED", index, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT finished", index, StringComparison.Ordinal);
        Assert.Contains("01-solution-builds — passed", index, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheGateFAILS_TheBandNamesWHICHCheckFailed()
    {
        // At this boundary there is no retry, no attempt directory and no feedback file, so "which one
        // failed" is the entire post-mortem — and a band that only said "failed" would send the operator
        // back to the logs to work out something the harness already knew.
        OnTheFlyLogSiteObserver observer = NewObserver();
        observer.TaskStarting(Task("01-alpha"));

        observer.TerminalGateStarting(["01-solution-builds", "02-all-tests-pass"], DateTimeOffset.UtcNow);
        observer.TerminalGateFinished(passed: false, failedNames: ["02-all-tests-pass"]);

        string index = ReadIndex();
        Assert.Contains("Terminal gate FAILED", index, StringComparison.Ordinal);
        Assert.Contains("02-all-tests-pass — FAILED", index, StringComparison.Ordinal);

        // The passing sibling is still reported as passing: "1 of 2 failed" is a different fact from
        // "the gate failed", and collapsing them loses which half is sound.
        Assert.Contains("01-solution-builds — passed", index, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunningStatusHasItsOwnJournalToken_soPostMortemToolingCanReadIt()
    {
        // The state half of #625: PlanPhaseStatus.Running is persisted, so `run.json` answers "is the gate
        // running?" while it matters — not only "did it pass?" once it is over. Appended to the enum, never
        // inserted, because the enum is persisted.
        Assert.Equal("running", JournalJson.PlanPhaseToken(PlanPhaseStatus.Running));
        Assert.Equal("passed", JournalJson.PlanPhaseToken(PlanPhaseStatus.Passed));
        Assert.Equal("plan-guardrail-failed", JournalJson.PlanPhaseToken(PlanPhaseStatus.PlanGuardrailFailed));
    }

    [Theory]
    [InlineData(PlanPhaseStatus.Running)]
    [InlineData(PlanPhaseStatus.Passed)]
    [InlineData(PlanPhaseStatus.PlanPreflightFailed)]
    [InlineData(PlanPhaseStatus.PlanGuardrailFailed)]
    public void EveryPlanPhaseStatusROUNDTRIPS_becauseAWriteOnlyTokenBreaksTheNextRead(PlanPhaseStatus status)
    {
        // The defect this exists to stop, found while building #625 and fixed before it shipped: the token
        // was added to the WRITE side only. Every plan-phase journal write is a read-modify-write, so the
        // very next one threw JsonException("Unknown plan phase status 'running'") — the run died at the
        // terminal gate, after paying for the entire DAG to get there.
        //
        // A one-directional converter is not a half-working feature; it is a run-killer that only fires
        // once the value has been written. Parameterised over the whole enum so a future member cannot be
        // added write-only either.
        var section = new PlanGuardrailsSection { Status = status, PlanHash = "sha256:abc" };
        var document = new JournalDocument
        {
            RunId = "2026-09-05T07-47-36Z-2ada",
            PlanHash = "sha256:abc",
            PlanGuardrails = section
        };

        string json = System.Text.Json.JsonSerializer.Serialize(document, JournalJson.Options);
        JournalDocument back = System.Text.Json.JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;

        Assert.Equal(status, back.PlanGuardrails!.Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
