using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #827 through the real executor and scheduler: an untracked nested git repo with NO commit is left out of
/// harness staging, and that is SAID (attempt summary, attempt log, integration-staging.log), never silent; one that
/// appears during an attempt outside the task's writeScope is an offense, not a bypass.
/// </summary>
public sealed partial class WriteScopeRunTests
{
    private static void FreshNestedRepo(string root, string relative)
    {
        string dir = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        TempGitRepo.Git(dir, "init");
        File.WriteAllText(Path.Combine(dir, "inner.txt"), "inner");
    }

    [Fact]
    public async Task Serial_APreExistingFreshNestedRepo_DoesNotStopTheCheck_AndIsNamedInTheSummaryAndLog_Issue827()
    {
        using var repo = new TempGitRepo();
        FreshNestedRepo(repo.RepoPath, "vendor/new");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, _, invocation) => WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done"));

        TaskResult task = Assert.Single((await RunSerialAsync(planDir, agent)).Tasks);

        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.DoesNotContain("NOT checked", task.Summary);
        Assert.Contains("left out of write-scope staging", task.Summary);
        Assert.Contains("vendor/new", task.Summary);
        string log = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-impl", 1), "write-scope-check.log"));
        Assert.Contains("vendor/new", log);
        Assert.DoesNotContain("NOT checked", log);
    }

    [Fact]
    public async Task Serial_AFreshNestedRepoCreatedOutsideTheScope_IsAnOffense_AndLeftOnDisk_Issue827()
    {
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done");
            FreshNestedRepo(invocation.WorkingDirectory, "vendor/new");
        });

        TaskResult task = Assert.Single((await RunSerialAsync(planDir, agent)).Tasks);

        Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);
        Assert.Contains("vendor/new", task.Summary);
        Assert.True(File.Exists(Path.Combine(repo.RepoPath, "vendor", "new", "inner.txt")));
    }

    [Fact]
    public async Task Serial_AFreshNestedRepoCreatedInsideTheScope_Succeeds_AndIsStillNamed_Issue827()
    {
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs", "vendor/**"]));
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done");
            FreshNestedRepo(invocation.WorkingDirectory, "vendor/new");
        });

        TaskResult task = Assert.Single((await RunSerialAsync(planDir, agent)).Tasks);

        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.Contains("vendor/new", task.Summary);
    }

    [Fact]
    public async Task Worktree_AFreshNestedRepoInScope_GoesGreen_ButTheLostWorkIsNamedLoudly_Issue827()
    {
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs", "vendor/**"]));
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done");
            FreshNestedRepo(invocation.WorkingDirectory, "vendor/new");
        });

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.Contains("WARNING", task.Summary);
        Assert.Contains("will NOT be committed or delivered", task.Summary);
        Assert.Contains("vendor/new", task.Summary);

        // The Scheduler's segment-commit report: the task's integration-staging.log names the repo.
        string taskLogDir = Path.GetDirectoryName(AttemptDir(planDir, "01-impl", 1))!;
        string stagingPath = Path.Combine(taskLogDir, "integration-staging.log");
        Assert.True(File.Exists(stagingPath), "found: " + string.Join(" | ",
            Directory.EnumerateFiles(planDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(planDir, f)).Where(f => !f.Contains("attempt-"))));
        string staging = File.ReadAllText(stagingPath);
        Assert.Contains("vendor/new", staging);
        Assert.Contains("#827", staging);
    }

    [Fact]
    public async Task Worktree_AFreshNestedRepoOutsideTheScope_IsAnOffense_Issue827()
    {
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done");
            FreshNestedRepo(invocation.WorkingDirectory, "vendor/new");
        });

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);
        Assert.Contains("vendor/new", task.Summary);
    }
}
