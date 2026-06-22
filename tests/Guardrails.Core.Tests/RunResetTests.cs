using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Reset semantics (SSOT §6.1). Focus here is the issue #51 baseline store: <c>--fresh</c> MUST wipe
/// <c>state/captured/</c> (a stale baseline surviving would revert a legitimately re-authored file on
/// the next run), and a single-task reset clears that task's captured subdir.
/// </summary>
public sealed class RunResetTests : IDisposable
{
    private readonly string _planDir;

    public RunResetTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    [Fact]
    public void Fresh_WipesCapturedBaselineStore()
    {
        // Seed a baseline under state/captured/ as a prior run would have.
        string baseline = Path.Combine(_planDir, "state", "captured", "01-author", "tests", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "ORIGINAL");

        RunReset.Fresh(_planDir);

        Assert.False(Directory.Exists(Path.Combine(_planDir, "state", "captured")),
            "--fresh must delete state/captured/ so a stale baseline cannot revert a re-authored file");
        // Sanity: --fresh re-seeded state.json.
        Assert.True(File.Exists(Path.Combine(_planDir, "state", "state.json")));
    }

    [Fact]
    public void Fresh_WithNoCapturedStore_DoesNotThrow()
    {
        // The store is absent on a first run; --fresh must be a no-op for it, not a failure.
        RunReset.Fresh(_planDir);
        Assert.False(Directory.Exists(Path.Combine(_planDir, "state", "captured")));
    }

    [Fact]
    public void Fresh_DeletesPlanRootLogsTree_NotJustStateLogs()
    {
        // SSOT §8 / plan-08: per-attempt artifacts (and any exported static log site) live under the
        // PLAN-ROOT logs/<runId>/ tree — a sibling of state/, NOT state/logs/. --fresh must delete
        // that tree, or it grows unbounded across runs (the path the writer never cleaned).
        string attemptFile = Path.Combine(_planDir, "logs", "run-1", "01-task", "attempt-1", "action-stdout.log");
        Directory.CreateDirectory(Path.GetDirectoryName(attemptFile)!);
        File.WriteAllText(attemptFile, "prior run output");

        RunReset.Fresh(_planDir);

        Assert.False(Directory.Exists(Path.Combine(_planDir, "logs")),
            "--fresh must delete the plan-root logs/ tree (where attempt artifacts actually land, SSOT §8)");
    }

    [Fact]
    public void Fresh_DeletesLocalReviewMarker()
    {
        // SSOT §13: the local review marker is gitignored runtime under state/; a fresh slate honestly
        // discards the prior local review act.
        string marker = Path.Combine(_planDir, "state", "guardrails-review.json");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "{}");

        RunReset.Fresh(_planDir);

        Assert.False(File.Exists(marker), "--fresh must delete state/guardrails-review.json (SSOT §13)");
    }

    [Fact]
    public void ResetTask_ClearsThatTasksCapturedSubdir_OnlyItsOwn()
    {
        // A journal must exist with the task for RunReset.Task to act.
        PlanDefinition plan = Plan(Task("01-author"), Task("02-other")) with { PlanDirectory = _planDir };
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.MarkRunning("01-author");
        journal.MarkRunning("02-other");

        string ownBaseline = Path.Combine(_planDir, "state", "captured", "01-author", "Tests.cs");
        string otherBaseline = Path.Combine(_planDir, "state", "captured", "02-other", "Tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(ownBaseline)!);
        Directory.CreateDirectory(Path.GetDirectoryName(otherBaseline)!);
        File.WriteAllText(ownBaseline, "A");
        File.WriteAllText(otherBaseline, "B");

        Assert.True(RunReset.Task(plan, "01-author"));

        Assert.False(Directory.Exists(Path.Combine(_planDir, "state", "captured", "01-author")),
            "reset <task> must clear that task's captured subdir");
        Assert.True(File.Exists(otherBaseline), "another task's baseline must be untouched");
    }
}
