using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #535 (<c>model-evidence-and-graduation</c>) task 12/13: a completed <c>guardrails run</c> must
/// record its own attempts into the local telemetry corpus WITHOUT anyone typing <c>guardrails telemetry
/// ingest</c>. Tasks 09-11 built and wired the manual verb (<see cref="Commands.TelemetryCommandTests"/>,
/// <see cref="Commands.TelemetryCommandWiringTests"/>); this pair does for run-end ingest what those two
/// did for the verb itself — proves the harness calls the real ETL from its OWN completion path, not
/// merely that the ETL exists and works when invoked by hand.
///
/// <para><b>TDD red (task 12).</b> <c>RunCommand.Finish</c> does not call <see cref="TelemetryIngest"/>
/// at all yet — wiring it in is task 13's entire deliverable — so every test below drives a REAL
/// <c>guardrails run</c> through <see cref="CommandFactory.BuildRootCommand"/> (the actual composition
/// root <c>Program.cs</c> builds — the <see cref="WorktreeContainmentHookWiringTests"/> idiom, applied to
/// the CLI layer the way <see cref="NeedsHumanKindRunTests"/> already does) and inspects the corpus ON
/// DISK afterwards. This never asserts against a hand-built <c>RunReport</c>: the claim under test is
/// that the PRODUCTION run path ingests, and only the real path can prove that.</para>
///
/// <para><b>The last two tests assert a CONTRAST, not a single fact.</b> On the unwired tree nothing
/// ingests, so "the exit code is unchanged" and "nothing was written" are both ALREADY true today — a
/// test asserting only those would pass against the stub and never bind anything. Each of
/// <see cref="Run_TelemetryWriteFailure_IsReported_AndExitCodeUnchanged"/> and
/// <see cref="Run_CollectionDisabled_SuppressesIngestThatOtherwiseHappens"/> therefore also asserts
/// something that is FALSE until task 13 lands (respectively: a console line naming the failure; that an
/// enabled run actually produced rows at all).</para>
///
/// <para><b>The corpus-root contract this suite pins for task 13.</b> <c>guardrails run</c> takes no
/// <c>--corpus-root</c> flag — that belongs to the <c>telemetry</c> verb (task 10), and adding CLI
/// surface to <c>run</c> is not this pair's job — and the real default (<c>~/.guardrails/telemetry/</c>)
/// is off-limits to every test here (per this task's own instructions). So this suite defines
/// <see cref="CorpusRootEnvVar"/> — <c>GUARDRAILS_TELEMETRY_CORPUS_ROOT</c> — as the escape hatch
/// <c>RunCommand.Finish</c> must read and pass verbatim as <c>TelemetryCommand.ResolveCorpusRoot</c>'s
/// override, mirroring the EXACT idiom <c>SchedulerFactory.WorktreeRootFor</c> already uses for
/// <c>GUARDRAILS_WORKTREE_ROOT</c> (env override wins when non-blank; unset/blank falls through to the
/// real default). No other file in this task's writeScope can declare that constant, so it is spelled
/// out here, verbatim, for task 13 to find — reading this test file first is literally what task 13's own
/// action.prompt.md instructs it to do.</para>
///
/// <para>Every test sets <see cref="CorpusRootEnvVar"/> (and, where relevant,
/// <see cref="TelemetryCorpusStore.OptOutEnvVar"/>) around ONE invocation and restores both in a
/// <c>finally</c> — the same env-var-mutate-then-restore idiom
/// <c>TelemetryCommandTests.Ingest_WhenOptedOut_WritesNothing</c> already uses — and every corpus lives
/// under a fresh temp directory, deleted afterwards. None ever points at the real
/// <c>~/.guardrails/telemetry/</c>.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
[Collection(TelemetryEnvironmentCollection.Name)]
public sealed class RunEndTelemetryIngestTests
{
    /// <summary>
    /// The run-end corpus-root override this suite pins as part of task 13's contract (see the class
    /// doc): when set to a non-blank value, <c>RunCommand.Finish</c> must pass it verbatim as
    /// <c>TelemetryCommand.ResolveCorpusRoot</c>'s override instead of resolving the real
    /// <c>~/.guardrails/telemetry/</c>. Named alongside <see cref="TelemetryCorpusStore.OptOutEnvVar"/> —
    /// same <c>GUARDRAILS_TELEMETRY</c> family, a different concern (where vs. whether).
    /// </summary>
    private const string CorpusRootEnvVar = "GUARDRAILS_TELEMETRY_CORPUS_ROOT";

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// Runs <paramref name="args"/> with <see cref="CorpusRootEnvVar"/> pointed at
    /// <paramref name="corpusRoot"/> and <see cref="TelemetryCorpusStore.OptOutEnvVar"/> set to
    /// <paramref name="optOut"/> (null leaves it unset — collection ON), restoring both afterwards so one
    /// test's env mutation can never leak into another's.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAgainstCorpusAsync(
        string corpusRoot, string? optOut, params string[] args)
    {
        string? previousRoot = Environment.GetEnvironmentVariable(CorpusRootEnvVar);
        string? previousOptOut = Environment.GetEnvironmentVariable(TelemetryCorpusStore.OptOutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CorpusRootEnvVar, corpusRoot);
            Environment.SetEnvironmentVariable(TelemetryCorpusStore.OptOutEnvVar, optOut);
            return await InvokeAsync(args);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CorpusRootEnvVar, previousRoot);
            Environment.SetEnvironmentVariable(TelemetryCorpusStore.OptOutEnvVar, previousOptOut);
        }
    }

    private static string RunId(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    /// <summary>
    /// Every row on disk under <paramref name="corpusRoot"/>, deserialized via the store's own wire
    /// options (internal, but visible here — <c>Guardrails.Core</c> grants <c>InternalsVisibleTo</c> to
    /// this assembly) — the same round-trip idiom <c>TelemetryCommandTests.ReadRows</c> uses.
    /// </summary>
    private static List<TelemetryRow> ReadRows(string corpusRoot)
    {
        var rows = new List<TelemetryRow>();
        if (!Directory.Exists(corpusRoot))
        {
            return rows;
        }

        foreach (string file in Directory.GetFiles(corpusRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<TelemetryRow>(line, TelemetryCorpusStore.JsonOptions) is { } row)
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Overwrites a script task's action to emit a <c>needsHuman</c> fragment and exit clean — the same
    /// fragment shape <c>NeedsHumanKindRunTests.MakeEscalatingAction</c> uses, inlined here since that
    /// helper is private to its own class.
    /// </summary>
    private static void MakeEscalatingAction(ScriptPlanBuilder plan, string taskId, string question)
    {
        string body = OperatingSystem.IsWindows()
            ? $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{{\"needsHuman\": \"{question}\"}}'\r\nexit 0\r\n"
            : $"#!/usr/bin/env bash\nprintf '%s' '{{\"needsHuman\": \"{question}\"}}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n";
        File.WriteAllText(plan.ActionPath(taskId), body);
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Run_IngestsItsOwnJournal_WithoutAManualVerb()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-a");
        using var corpus = new TempDir();

        (int exit, _) = await RunAgainstCorpusAsync(corpus.Path, null, "run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);

        // Nothing above ever calls `guardrails telemetry ingest` — the corpus must have filled itself.
        // Both grains (task 05/06): the task row on the reserved Attempt == 0 sentinel, the real
        // attempt on Attempt == 1.
        string runId = RunId(plan.PlanDir);
        List<TelemetryRow> rows = ReadRows(corpus.Path);
        Assert.Contains(rows, r => r.RunId == runId && r.TaskId == "01-a" && r.Attempt == 0);
        Assert.Contains(rows, r => r.RunId == runId && r.TaskId == "01-a" && r.Attempt == 1);
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Run_ThatEndedNeedsHuman_StillIngestsItsAttempts()
    {
        const string question = "run-end-ingest fixture: needs a human";
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        using var corpus = new TempDir();
        MakeEscalatingAction(plan, "01-escalates", question);

        (int exit, _) = await RunAgainstCorpusAsync(corpus.Path, null, "run", plan.PlanDir, "--no-ui", "--no-log-server");

        // The run genuinely ends unresolved — this is not a green run wearing a different label. A model
        // that fails is exactly the evidence a model comparison is made of, so this attempt must land in
        // the corpus as surely as a succeeded one does.
        Assert.Equal(ExitCodes.TaskFailed, exit);

        string runId = RunId(plan.PlanDir);
        List<TelemetryRow> rows = ReadRows(corpus.Path);
        Assert.Contains(rows, r => r.RunId == runId && r.TaskId == "01-escalates" && r.Attempt == 1);
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Run_TelemetryWriteFailure_IsReported_AndExitCodeUnchanged()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-a");
        using var corpusParent = new TempDir();
        Directory.CreateDirectory(corpusParent.Path);

        // Portable across the repo's three OSes: a plain FILE occupying the exact path the store would
        // need to Directory.CreateDirectory into throws IOException there on Windows, Linux and macOS
        // alike — no platform-specific permission bits required (verified locally: attempting
        // Directory.CreateDirectory on a path already occupied by a file throws
        // System.IO.IOException "Cannot create '...' because a file or directory with the same name
        // already exists.").
        string blockedCorpusRoot = Path.Combine(corpusParent.Path, "occupied-by-a-file");
        File.WriteAllText(blockedCorpusRoot, "not a directory");

        (int exit, string output) =
            await RunAgainstCorpusAsync(blockedCorpusRoot, null, "run", plan.PlanDir, "--no-ui", "--no-log-server");

        // (1) Already true of unwired code today — the baseline, not the point of this test.
        Assert.Equal(ExitCodes.Success, exit);

        // (2) Also already true of unwired code today — the summary is unrelated machinery, and a
        // telemetry hiccup must never suppress it (mirrors WriteDurableFinalSite's own "a render hiccup
        // must never change the run's exit code" promise, one seam over).
        Assert.Contains("Summary", output, StringComparison.Ordinal);
        Assert.Contains("task(s) green", output, StringComparison.Ordinal);

        // (3) FALSE until task 13 lands: today nothing attempts telemetry at all, so nothing can report
        // failing at it. This is the assertion that makes the test red for the right reason — a silent
        // failure here would be indistinguishable from a run that was never opted into telemetry at all,
        // which is exactly the defect this whole plan exists to avoid.
        Assert.Contains("Telemetry ingest failed", output, StringComparison.OrdinalIgnoreCase);

        // The blocked path itself was never disturbed — it was the store's own directory-creation
        // attempt that failed, nothing upstream of it.
        Assert.True(File.Exists(blockedCorpusRoot));
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Run_CollectionDisabled_SuppressesIngestThatOtherwiseHappens()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-a");
        using var corpus = new TempDir();

        // Pass 1 — collection ON: the run that PROVES ingest happens at all (fails today; this is the
        // half an opted-out run alone could never demonstrate, since an unwired harness also writes
        // nothing).
        (int firstExit, _) = await RunAgainstCorpusAsync(corpus.Path, null, "run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, firstExit);

        string firstRunId = RunId(plan.PlanDir);
        List<TelemetryRow> rowsAfterFirst = ReadRows(corpus.Path);
        Assert.Contains(rowsAfterFirst, r => r.RunId == firstRunId && r.TaskId == "01-a" && r.Attempt == 1);

        // Pass 2 — the SAME plan, the SAME corpus root, `--fresh` for a genuinely NEW run (and thus a new
        // runId the store's own (runId, taskId, attempt) idempotency key cannot short-circuit on its
        // own), collection OFF. Without --fresh a second `run` would resume the SAME runId and the
        // idempotency check alone would explain zero new rows regardless of the opt-out — --fresh is what
        // makes this a real test of the opt-out rather than of dedup.
        (int secondExit, _) = await RunAgainstCorpusAsync(
            corpus.Path, "off", "run", plan.PlanDir, "--fresh", "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, secondExit);

        string secondRunId = RunId(plan.PlanDir);
        Assert.NotEqual(firstRunId, secondRunId);

        List<TelemetryRow> rowsAfterSecond = ReadRows(corpus.Path);

        // Count only OUR OWN runs' rows. A bare total-count equality assumes nothing else writes to this
        // corpus — but the root is a PROCESS-WIDE env var, so while RunAgainstCorpusAsync holds it pointed
        // at this TempDir, any test class running in PARALLEL that spawns a real `guardrails run` inherits
        // the pointer and writes its rows in here. The try/finally restores the variable but cannot close
        // that window: the shared state is the process's, not the lock's — the same reasoning
        // GitEnvironmentCollection records for GIT_DIR, and the same reasoning TelemetryCorpusIsolation
        // gives for making its scratch root per-PROCESS ("parallel suite runs … cannot make each other
        // flaky by counting each other's rows"); that initializer just cannot help INSIDE one process.
        // Measured: plan 34 added AttachReplayTests, whose 7 real runs put 2 extra rows here and turned
        // this into `Expected: 8, Actual: 10` in the full suite — while passing 3/3 in isolation.
        // Scoping to our own runIds is immune to that AND a sharper claim than a global total.
        int ownRowsAfterFirst = rowsAfterFirst.Count(r => r.RunId == firstRunId);
        int ownRowsAfterSecond = rowsAfterSecond.Count(r => r.RunId == firstRunId || r.RunId == secondRunId);
        Assert.Equal(ownRowsAfterFirst, ownRowsAfterSecond);
        Assert.DoesNotContain(rowsAfterSecond, r => r.RunId == secondRunId);
    }

    /// <summary>A fresh temp directory, deleted on <see cref="Dispose"/>. Never
    /// <c>~/.guardrails/telemetry/</c>.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gr-runendingest-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try { Directory.Delete(Path, recursive: true); }
                catch (IOException) { }
            }
        }
    }
}
