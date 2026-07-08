using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="DefinitionDriftReporter"/> (SSOT §7.2, issue #274 Part A): the Tier-2
/// per-file breakdown (added / removed / modified + an approximate ± line count) against a scripted
/// old-bytes provider, and the Tier-1 degradation (note, empty breakdown) when the old bytes are not
/// recoverable — with the aggregate old→new hash and the transitive-descendant set always present.
/// </summary>
public sealed class DefinitionDriftReporterTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _taskDir;

    public DefinitionDriftReporterTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "gr-ddr-" + Guid.NewGuid().ToString("N"));
        _taskDir = Path.Combine(_repoRoot, "tasks", "04-codegen");
        Directory.CreateDirectory(Path.Combine(_taskDir, "guardrails"));
    }

    /// <summary>A no-git provider that scripts old bytes + old file listing; git ops throw (never called).</summary>
    private sealed class ScriptedProvider(string repoRoot, Func<string, string?> oldFor, IReadOnlyList<string> oldFiles)
        : IWorktreeProvider
    {
        public string? ReadFileAtCommit(string commitSha, string absolutePath) => oldFor(absolutePath);

        public string? RepoRelativePath(string absolutePath) =>
            Path.GetRelativePath(repoRoot, absolutePath).Replace('\\', '/');

        public IReadOnlyList<string> ListFilesAtCommit(string commitSha, string absoluteDir) => oldFiles;

        public IntegrationHandle CreateIntegration(string p, string r, CancellationToken c) => throw new NotSupportedException();
        public WorktreeHandle CreateSegment(string t, int a, IntegrationHandle i, CancellationToken c) => throw new NotSupportedException();
        public WorktreeHandle ReuseSegment(WorktreeHandle u, string t, int a) => throw new NotSupportedException();
        public WorktreeHandle ForkFromTip(string s, string t, int a) => throw new NotSupportedException();
        public IntegrationResult Integrate(WorktreeHandle s, IntegrationHandle i, CancellationToken c) => throw new NotSupportedException();
        public void Discard(WorktreeHandle h) => throw new NotSupportedException();
        public void PruneOrphans(IReadOnlyCollection<string> l, IntegrationHandle i) => throw new NotSupportedException();
        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle i, CancellationToken c) => throw new NotSupportedException();
    }

    private TaskNode BuildTask()
    {
        File.WriteAllText(Path.Combine(_taskDir, "task.json"), "{ \"description\": \"t\", \"dependsOn\": [] }\n");
        File.WriteAllText(Path.Combine(_taskDir, "action.sh"), "line1\nCHANGED\nline3\n");     // modified vs old
        File.WriteAllText(Path.Combine(_taskDir, "guardrails", "01-check.sh"), "check\n");      // unchanged
        // guardrails/02-extra.sh is REMOVED (present at old commit, absent now).

        return new TaskNode
        {
            Id = "04-codegen",
            Directory = _taskDir,
            Description = "t",
            DependsOn = [],
            Action = new ActionDefinition { Path = Path.Combine(_taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(_taskDir, "guardrails", "01-check.sh"),
                    Kind = ActionKind.Script
                }
            ]
        };
    }

    [Fact]
    public void Tier2_ClassifiesModifiedAndRemoved_WithLineDelta()
    {
        TaskNode task = BuildTask();
        var plan = new PlanDefinition
        {
            PlanDirectory = _repoRoot,
            Workspace = _repoRoot,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        string actionAbs = Path.Combine(_taskDir, "action.sh");
        string taskJsonAbs = Path.Combine(_taskDir, "task.json");
        string checkAbs = Path.Combine(_taskDir, "guardrails", "01-check.sh");
        string extraAbs = Path.Combine(_taskDir, "guardrails", "02-extra.sh");

        var oldBytes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [taskJsonAbs] = "{ \"description\": \"t\", \"dependsOn\": [] }\n", // unchanged
            [actionAbs] = "line1\nline2\n",                                    // modified: line2 → CHANGED,line3
            [checkAbs] = "check\n",                                            // unchanged
            [extraAbs] = "old-extra\nsecond\n"                                 // removed
        };
        var provider = new ScriptedProvider(_repoRoot, abs => oldBytes.GetValueOrDefault(abs), oldBytes.Keys.ToList());

        DefinitionDriftReport report = DefinitionDriftReporter.Build(
            plan, new DependencyGraph(plan.Tasks),
            [new DefinitionDriftReporter.DriftInput("04-codegen", "sha256:old", "sha256:new", "abc1234def")],
            provider);

        DriftedTask drifted = Assert.Single(report.Tasks);
        Assert.Null(drifted.Note); // full breakdown available.

        ChangedDefinitionFile modified = Assert.Single(drifted.ChangedFiles, f => f.Path == "action.sh");
        Assert.Equal("modified", modified.Change);
        Assert.True(modified.Added > 0, "a modified file should report added lines");
        Assert.True(modified.Removed > 0, "a modified file should report removed lines");

        ChangedDefinitionFile removed = Assert.Single(drifted.ChangedFiles, f => f.Path == "guardrails/02-extra.sh");
        Assert.Equal("removed", removed.Change);

        // Unchanged files are NOT part of the breakdown.
        Assert.DoesNotContain(drifted.ChangedFiles, f => f.Path == "task.json");
        Assert.DoesNotContain(drifted.ChangedFiles, f => f.Path == "guardrails/01-check.sh");

        // Tier 1 + the reference command are always present.
        Assert.Equal("sha256:old", drifted.OldHash);
        Assert.Equal("sha256:new", drifted.NewHash);
        Assert.Contains("git diff abc1234..HEAD", drifted.DiffCommand);
    }

    [Fact]
    public void Tier2_AddedFile_Classified_WhenRecoveryWorksForOthers()
    {
        TaskNode task = BuildTask();
        // Add a brand-new guardrail on disk that did NOT exist at the old commit.
        File.WriteAllText(Path.Combine(_taskDir, "guardrails", "03-new.sh"), "new guardrail\n");
        var plan = new PlanDefinition
        {
            PlanDirectory = _repoRoot, Workspace = _repoRoot,
            Config = new RunConfig { Version = 1 }, Tasks = [task]
        };

        string actionAbs = Path.Combine(_taskDir, "action.sh");
        string taskJsonAbs = Path.Combine(_taskDir, "task.json");
        string checkAbs = Path.Combine(_taskDir, "guardrails", "01-check.sh");
        // Old bytes recover for the pre-existing files; the new guardrail returns null (absent then).
        var oldBytes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [taskJsonAbs] = "{ \"description\": \"t\", \"dependsOn\": [] }\n",
            [actionAbs] = "line1\nCHANGED\nline3\n", // unchanged this time (isolate the "added" case)
            [checkAbs] = "check\n"
        };
        var provider = new ScriptedProvider(_repoRoot, abs => oldBytes.GetValueOrDefault(abs), oldBytes.Keys.ToList());

        DefinitionDriftReport report = DefinitionDriftReporter.Build(
            plan, new DependencyGraph(plan.Tasks),
            [new DefinitionDriftReporter.DriftInput("04-codegen", "sha256:old", "sha256:new", "abc1234")],
            provider);

        DriftedTask drifted = Assert.Single(report.Tasks);
        Assert.Null(drifted.Note);
        ChangedDefinitionFile added = Assert.Single(drifted.ChangedFiles, f => f.Path == "guardrails/03-new.sh");
        Assert.Equal("added", added.Change);
    }

    [Fact]
    public void NullProvider_DegradesToTier1WithNote_KeepsHashesAndDependents()
    {
        // Diamond: 01 → {02,03} → 04. Drift on the root; no provider → Tier-2 degrades, Tier 1 stands.
        PlanDefinition plan = Plan(
            Task("01-root"), Task("02-left", "01-root"), Task("03-right", "01-root"),
            Task("04-sink", "02-left", "03-right"));

        DefinitionDriftReport report = DefinitionDriftReporter.Build(
            plan, new DependencyGraph(plan.Tasks),
            [new DefinitionDriftReporter.DriftInput("01-root", "sha256:old", "sha256:new", OldCommit: null)],
            provider: null);

        DriftedTask drifted = Assert.Single(report.Tasks);
        Assert.Empty(drifted.ChangedFiles);
        Assert.NotNull(drifted.Note);
        Assert.Equal("sha256:old", drifted.OldHash);
        Assert.Equal("sha256:new", drifted.NewHash);
        Assert.Equal(new[] { "02-left", "03-right", "04-sink" }, drifted.Dependents.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void AllOldBytesUnrecoverable_DegradesWithNote_NoCrash()
    {
        TaskNode task = BuildTask();
        var plan = new PlanDefinition
        {
            PlanDirectory = _repoRoot, Workspace = _repoRoot,
            Config = new RunConfig { Version = 1 }, Tasks = [task]
        };

        // Provider present but recovers nothing (the plan folder was untracked at the old commit).
        var provider = new ScriptedProvider(_repoRoot, _ => null, oldFiles: []);

        DefinitionDriftReport report = DefinitionDriftReporter.Build(
            plan, new DependencyGraph(plan.Tasks),
            [new DefinitionDriftReporter.DriftInput("04-codegen", "sha256:old", "sha256:new", "abc1234")],
            provider);

        DriftedTask drifted = Assert.Single(report.Tasks);
        Assert.Empty(drifted.ChangedFiles);         // no false "added" entries.
        Assert.NotNull(drifted.Note);               // degradation note.
        Assert.Equal("sha256:old", drifted.OldHash); // Tier 1 always present.
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }
}
