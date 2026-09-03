using System.Diagnostics;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// The round-trip proof for <c>observer.jsonl</c>'s <c>AttemptFinished</c> line (plan 35 task 06) - the
/// single most important fixture in this plan, because the defect it guards against is COMPLETELY
/// SILENT.
///
/// <para><b>The defect.</b> <see cref="Journal.AttemptRecord"/> has five <c>required</c> members:
/// <see cref="Journal.AttemptRecord.Attempt"/>, <see cref="Journal.AttemptRecord.StartedAt"/>,
/// <see cref="Journal.AttemptRecord.EndedAt"/>, <see cref="Journal.AttemptRecord.Outcome"/>,
/// <see cref="Journal.AttemptRecord.LogDir"/>. If <see cref="ObserverProjection"/>'s flattened
/// <c>AttemptFinished</c> line omits any one of them, a replay that tries to rebuild the record throws
/// <c>FormatException</c> - and <c>AttachCommand</c> catches that and SKIPS the line, by design, so it
/// stays forward-compatible with members it does not recognise. The result: <c>guardrails attach</c>
/// replays a run in which NO ATTEMPT EVER FINISHED - no exception, no log line, no failing test, exit
/// code 0. Every other assertion in this plan still passes in that state.</para>
///
/// <para><b>Today's actual gap</b> (verified by reading the source, not assumed): <see cref="ObserverProjection.AttemptFinished"/>
/// currently flattens only <c>taskId</c>/<c>attempt</c>/<c>outcome</c> onto the line - <c>startedAt</c>,
/// <c>endedAt</c>, <c>logDir</c>, and every optional (<c>costUsd</c>, <c>turns</c>, the provenance
/// fields) never reach the wire at all. <c>AttachCommand.Dispatch</c>'s <c>AttemptFinished</c> case
/// mirrors that gap today: it reads only <c>attempt</c>/<c>outcome</c> off the wire and fills the rest
/// with sentinels the renderer never reads, rather than requiring them. Closing BOTH gaps together -
/// the producer writing every member, the consumer requiring every member - is task 07's job. This file
/// authors the tests task 07 must turn green; it changes neither <see cref="ObserverProjection"/> nor
/// <c>AttachCommand</c> itself.</para>
///
/// <para><b>Why the assertions below are shaped the way they are.</b> <c>AttachCommand</c> has no public
/// replay method - its rendered <c>stdout</c> (via the real <see cref="Guardrails.Cli.Ui.LiveRunObserver"/>
/// it drives) is the only observable surface, and that surface renders exactly two facts off an
/// <c>AttemptFinished</c> event: the attempt number and the outcome. Tests 2 and 3 below therefore prove
/// "replayed, not silently skipped" the only way that is actually observable - by pairing a normal
/// event with a HAND-BUILT negative control that is missing one required member. Today, since
/// <c>AttachCommand</c> does not yet require that member off the wire, the missing-member line
/// (wrongly) still renders - the negative-control assertion is exactly what makes each test fail RIGHT
/// NOW, and exactly what task 07's fix must make pass.</para>
/// </summary>
public sealed class ObserverRecordRoundTripTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    /// <summary>A throwaway directory tree for driving <see cref="ObserverProjection"/> directly, with no real run behind it.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-record-roundtrip-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public string Dir(params string[] parts)
        {
            string path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// A HAND-BUILT <c>AttemptFinished</c> line shaped like the wire format task 07 must ship - every
    /// required <see cref="Journal.AttemptRecord"/> member present - with exactly
    /// <paramref name="omittedField"/> missing. Deliberately hand-written, unlike the fixtures driven
    /// through the real <see cref="ObserverProjection"/> below: its whole purpose is to simulate "one
    /// required member never reached the wire", which today's real producer cannot vary parametrically
    /// (it omits the same three members on every call). This is the ONLY way to prove attach's replay
    /// can tell a complete event apart from an incomplete one.
    /// </summary>
    private static string AttemptFinishedLineMissing(string taskId, int attempt, string omittedField)
    {
        var line = new JsonObject
        {
            ["member"] = "AttemptFinished",
            ["taskId"] = taskId,
            ["attempt"] = attempt,
            ["outcome"] = "Succeeded",
            ["startedAt"] = JsonValue.Create(DateTimeOffset.UtcNow),
            ["endedAt"] = JsonValue.Create(DateTimeOffset.UtcNow),
            ["logDir"] = $"logs/fixture/{taskId}/attempt-{attempt}"
        };
        line.Remove(omittedField);
        return line.ToJsonString();
    }

    private static void AssertRequiredMemberPresent(JsonNode line, string field) =>
        Assert.True(
            line[field] is not null,
            $"AttemptFinished's observer.jsonl line is missing the required AttemptRecord member '{field}' - "
            + "an attach replay rebuilding this record would have to invent a value for it, or throw and skip "
            + "the whole line.");

    private static void AssertOptionalMemberSurvived(JsonNode line, string field) =>
        Assert.True(
            line[field] is not null,
            $"AttemptFinished's observer.jsonl line dropped the optional AttemptRecord member '{field}' the "
            + "record actually held.");

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CLI plumbing — the SAME in-process pattern LogsCliTests / AttachReplayTests use for "run".
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Run a real (trivial) plan to completion and return its <c>logs/&lt;runId&gt;/</c> directory. The
    /// production observer chain already writes its OWN <c>observer.jsonl</c> into this directory for the
    /// real script attempt the run executed (task 08/15) - every test below deletes it before writing its
    /// own controlled fixture, so it starts from a clean, known file.
    /// </summary>
    private static async Task<string> RunToCompletionAsync(ScriptPlanBuilder plan)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(["run", plan.PlanDir, "--no-ui", "--no-log-server"])
            .InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument document = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        string logsDir = Path.Combine(plan.PlanDir, "logs", document.RunId);
        Directory.CreateDirectory(logsDir);
        return logsDir;
    }

    /// <summary>
    /// <c>guardrails attach</c> as a genuinely separate OS process (the same idiom
    /// <c>AttachReplayTests.InvokeAttachOutOfProcessAsync</c> uses), with stdout/stderr redirected and
    /// captured. Out-of-process is not incidental here: <c>attach</c>'s replayed output is rendered by a
    /// real <see cref="Guardrails.Cli.Ui.LiveRunObserver"/> through Spectre's process-wide
    /// <c>AnsiConsole</c>, which writes to this PROCESS's own console - a genuinely separate process is
    /// the only way to capture that text reliably without fighting Spectre's own static state.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> InvokeAttachOutOfProcessAsync(string planDir)
    {
        string appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Guardrails.Cli.exe" : "Guardrails.Cli");
        ProcessStartInfo psi = File.Exists(appHost)
            ? new ProcessStartInfo(appHost)
            : new ProcessStartInfo("dotnet");
        if (!File.Exists(appHost))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Guardrails.Cli.dll"));
        }

        psi.ArgumentList.Add("attach");
        psi.ArgumentList.Add(planDir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. The wire-format proof — drive the REAL producer, read the line back, name every gap.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void ObserverLine_CarriesEveryRequiredAttemptRecordMember()
    {
        using var tree = new TempTree();
        string logsDir = tree.Dir("logs", "fixture-run");
        TaskNode task = FlatTask("01-first");

        var startedAt = new DateTimeOffset(2026, 3, 4, 10, 15, 0, TimeSpan.Zero);
        var endedAt = new DateTimeOffset(2026, 3, 4, 10, 22, 30, TimeSpan.Zero);
        var record = new AttemptRecord
        {
            Attempt = 4,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Outcome = AttemptOutcome.Succeeded,
            LogDir = "logs/fixture-run/01-first/attempt-4",
            CostUsd = 1.23m,
            Turns = 7,
            Provenance = new AttemptProvenance
            {
                Model = "claude-sonnet-5",
                Runner = "primary",
                Kind = "claude",
                Tier = "medium",
                TierSource = TierSource.Task
            }
        };

        var projection = new ObserverProjection(IRunObserver.Null, logsDir);
        projection.AttemptFinished(task, record);

        string[] lines = File.ReadAllLines(Path.Combine(logsDir, "observer.jsonl"));
        Assert.Single(lines);
        JsonNode line = JsonNode.Parse(lines[0])
            ?? throw new InvalidOperationException("observer.jsonl's AttemptFinished line did not parse as JSON.");
        Assert.Equal("AttemptFinished", line["member"]?.GetValue<string>());

        // The five REQUIRED members — one assertion each, by name, so a failure says exactly which one
        // the wire is missing.
        AssertRequiredMemberPresent(line, "attempt");
        Assert.Equal(4, line["attempt"]!.GetValue<int>());

        AssertRequiredMemberPresent(line, "startedAt");
        Assert.Equal(startedAt, line["startedAt"]!.GetValue<DateTimeOffset>());

        AssertRequiredMemberPresent(line, "endedAt");
        Assert.Equal(endedAt, line["endedAt"]!.GetValue<DateTimeOffset>());

        AssertRequiredMemberPresent(line, "outcome");
        Assert.Equal("Succeeded", line["outcome"]!.GetValue<string>());

        AssertRequiredMemberPresent(line, "logDir");
        Assert.Equal("logs/fixture-run/01-first/attempt-4", line["logDir"]!.GetValue<string>());

        // The optionals the record actually held.
        AssertOptionalMemberSurvived(line, "costUsd");
        Assert.Equal(1.23m, line["costUsd"]!.GetValue<decimal>());

        AssertOptionalMemberSurvived(line, "turns");
        Assert.Equal(7, line["turns"]!.GetValue<int>());

        AssertOptionalMemberSurvived(line, "model");
        Assert.Equal("claude-sonnet-5", line["model"]!.GetValue<string>());

        AssertOptionalMemberSurvived(line, "runner");
        Assert.Equal("primary", line["runner"]!.GetValue<string>());

        AssertOptionalMemberSurvived(line, "tier");
        Assert.Equal("medium", line["tier"]!.GetValue<string>());

        AssertOptionalMemberSurvived(line, "tierSource");
        Assert.Equal("Task", line["tierSource"]!.GetValue<string>());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. The replay-vs-skip proof — a real producer's line must show; a line missing one required
    //    member must not.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task AttachReplaysTheAttempt_RatherThanSilentlySkippingIt()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string logsDir = await RunToCompletionAsync(plan);
        string observerJsonlPath = Path.Combine(logsDir, "observer.jsonl");

        // Start from a clean, known file — the real run above already wrote its own AttemptFinished
        // line for its own script attempt (task 08/15's production wiring); this fixture wants full
        // control over exactly what observer.jsonl holds.
        File.Delete(observerJsonlPath);

        TaskNode task = FlatTask("01-first");

        // Produced by the REAL ObserverProjection — not a hand-written fixture. A hand-written line
        // could not detect that the producer itself omits a field; this one can, because it IS the
        // producer's own output.
        var projection = new ObserverProjection(IRunObserver.Null, logsDir);
        projection.AttemptFinished(task, new AttemptRecord
        {
            Attempt = 7,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Succeeded,
            LogDir = "logs/fixture/01-first/attempt-7"
        });

        // The negative control: a line otherwise shaped like the wire format task 07 must ship, missing
        // exactly one required member (logDir). Without this half the test cannot tell "replayed" apart
        // from "skipped" — a renderer that always prints SOMETHING for every line would pass the
        // assertion above no matter what attach actually did with a malformed event.
        File.AppendAllText(
            observerJsonlPath, AttemptFinishedLineMissing(task.Id, attempt: 13, omittedField: "logDir") + "\n");

        (int exitCode, string output, string error) = await InvokeAttachOutOfProcessAsync(plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exitCode);
        string combined = output + error;

        Assert.Contains("attempt 7:", combined, StringComparison.Ordinal);

        // A required member never reached the wire: attach must SKIP the whole event rather than render
        // it with a guessed/sentinel value standing in for the one field it could not read. Today it
        // does not — AttachCommand.Dispatch fills a sentinel LogDir instead of requiring one off the
        // wire — so attempt 13 (wrongly) still shows, and this assertion is what makes the test fail.
        Assert.DoesNotContain("attempt 13:", combined, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. The value-fidelity proof — the replayed values must be the values that went in, and a
    //    reconstruction attach cannot actually complete must not render at all.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task AttachReplay_ReconstructsTheRecordFields()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string logsDir = await RunToCompletionAsync(plan);
        string observerJsonlPath = Path.Combine(logsDir, "observer.jsonl");
        File.Delete(observerJsonlPath);

        TaskNode task = FlatTask("01-first");

        var projection = new ObserverProjection(IRunObserver.Null, logsDir);
        projection.AttemptFinished(task, new AttemptRecord
        {
            Attempt = 9,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.GuardrailFailed,
            LogDir = "logs/fixture/01-first/attempt-9"
        });

        // A second, independent negative control (endedAt this time, not logDir) — proving the gap is
        // not specific to one field.
        File.AppendAllText(
            observerJsonlPath, AttemptFinishedLineMissing(task.Id, attempt: 21, omittedField: "endedAt") + "\n");

        (int exitCode, string output, string error) = await InvokeAttachOutOfProcessAsync(plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exitCode);
        string combined = output + error;

        // The values that came back are the values that went in — at minimum the attempt number and
        // the outcome, read off attach's rendered output (its only observable surface).
        Assert.Contains("attempt 9:", combined, StringComparison.Ordinal);
        Assert.Contains("GuardrailFailed", combined, StringComparison.Ordinal);

        Assert.DoesNotContain("attempt 21:", combined, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. RunFinished — the projection's OWN documented contract ("record every observed call").
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunFinishedIsRecordedOnTheObserverStream()
    {
        using var tree = new TempTree();
        string logsDir = tree.Dir("logs", "run-finished-fixture");
        var projection = new ObserverProjection(IRunObserver.Null, logsDir);

        projection.RunFinished(0, null);

        string observerJsonlPath = Path.Combine(logsDir, "observer.jsonl");
        Assert.True(
            File.Exists(observerJsonlPath),
            "ObserverProjection.RunFinished appended no line to observer.jsonl at all — the projection's "
            + "own documented contract (\"record every observed call, in order\") is false for this member.");

        string[] lines = File.ReadAllLines(observerJsonlPath);
        Assert.Single(lines);
        JsonNode line = JsonNode.Parse(lines[0])
            ?? throw new InvalidOperationException("observer.jsonl's RunFinished line did not parse as JSON.");
        Assert.Equal("RunFinished", line["member"]?.GetValue<string>());
    }
}
