using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The WEAK-finding guard (issue #274 Part A): the resume drift pre-pass recomputes each already-succeeded
/// task's <c>TaskDefinitionHash</c> by reading its definition files from disk, and that read runs BEFORE
/// the worker loop's #150 fault capture. A transient share-lock on Windows (an editor / antivirus / indexer
/// holding a guardrail or task.json) must therefore NOT crash a healthy resume with a raw stack trace — it
/// must degrade to an HONEST ABORTED report (and must NOT emit a false drift verdict by silently skipping
/// the check). Reproduced cross-platform: a <c>FileShare.None</c> lock on Windows, <c>chmod 000</c> on Unix
/// (skipped when the environment can still read the file — e.g. running as root).
/// </summary>
public sealed class DefinitionDriftReadFailureTests
{
    private static async Task<(RunReport Report, RunJournal Journal)> RunSerialAsync(
        PlanDefinition plan, RunJournal journal, StateManager stateManager, CancellationToken ct)
    {
        var registry = PromptRunnerRegistry.Build(plan.Config,
            _ => throw new InvalidOperationException("no prompt runners in this test"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1); // no provider → serial
        RunReport report = await scheduler.RunAsync(plan, ct);
        return (report, journal);
    }

    /// <summary>Make <paramref name="path"/> unreadable; returns the restore action + whether a read now fails.</summary>
    private static (IDisposable Restore, bool Reproduced) MakeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return (handle, ReadFails(path));
        }

        UnixFileMode original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return (new RestoreMode(path, original), ReadFails(path));
    }

    private static bool ReadFails(string path)
    {
        try { File.ReadAllText(path); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed class RestoreMode(string path, UnixFileMode mode) : IDisposable
    {
        public void Dispose()
        {
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, mode); } catch (IOException) { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task ResumePrePass_DefinitionFileReadFails_HonestAbort_NotUnhandled_NoFalseDrift()
    {
        using var plan = new StatePlanBuilder().AddTask("01-only");

        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Phase 1: run to green so 01-only is journaled succeeded WITH a definition hash.
        (RunReport run1, _) = await RunSerialAsync(load.Plan!, journal, stateManager, ct);
        Assert.True(run1.AllSucceeded);
        Assert.NotNull(journal.RecordedDefinitionHash("01-only"));

        // Lock a guardrail file (read by the pre-pass hash recompute, NOT by PlanHash/PlanLoader — which
        // are not re-invoked here since the plan object is reused). This isolates the pre-pass IO guard.
        string guardrailPath = Path.Combine(
            plan.PlanDir, "tasks", "01-only", "guardrails", StatePlanBuilder.GuardrailFileName);
        (IDisposable restore, bool reproduced) = MakeUnreadable(guardrailPath);
        try
        {
            Assert.SkipUnless(reproduced,
                "environment can still read the locked/chmod-000 definition file (root or advisory " +
                "locking) — cannot reproduce a definition-file read failure here.");

            // Phase 2: resume. The pre-pass recompute hits the unreadable file. It must degrade to an
            // HONEST ABORTED report — never an unhandled exception — and must NOT emit a false drift.
            RunReport run2 = await new Scheduler(
                    load.Plan!,
                    new TaskExecutor(load.Plan!, new ProcessRunner(),
                        new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters),
                        stateManager, journal, IRunObserver.Null,
                        PromptRunnerRegistry.Build(load.Plan!.Config, _ => throw new InvalidOperationException("no runners"))),
                    journal, maxParallelism: 1)
                .RunAsync(load.Plan!, ct);

            Assert.True(run2.Aborted, "a definition-file read failure in the pre-pass must abort honestly, not crash");
            Assert.Null(run2.DefinitionDrift); // MUST NOT emit a false drift verdict.
            Assert.Contains("definition file", run2.Abort!.Headline, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            restore.Dispose();
        }
    }
}
