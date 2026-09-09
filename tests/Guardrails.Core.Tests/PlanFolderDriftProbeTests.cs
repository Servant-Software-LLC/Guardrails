using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// <see cref="GitRevListDriftProbe"/> against a REAL git repository (issue #576).
///
/// <para>Driven through real git rather than a fake, because the whole value of this probe is that it
/// asks git a reachability question — "are there plan-folder commits on my branch that merging the plan
/// branch alone would leave behind" — and a fake that returned numbers would be pinning this test's
/// idea of <c>rev-list</c>, not git's.</para>
///
/// <para>In <see cref="GitEnvironmentCollection"/> per that collection's standing instruction ("if you
/// add a test class that shells out to git, add it here"). Note this class needs no
/// <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> mutation of its own: the probe takes an explicit working
/// directory, which is exactly the constructor parameter #593 added so a test could point at its own
/// repository without touching the process.</para>
/// </summary>
[Collection(GitEnvironmentCollection.Name)]
public sealed class PlanFolderDriftProbeTests : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), $"gr576-{Guid.NewGuid():N}");

    private const string PlanFolder = "docs/plans/32-executed-definition-hash";
    private const string PlanBranch = "guardrails/32-executed-definition-hash";

    public PlanFolderDriftProbeTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-b", "master");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");

        Write($"{PlanFolder}/guardrails.json", "{ \"version\": 1 }");
        Write($"{PlanFolder}/tasks/01-a/task.json", "{ \"description\": \"a\" }");
        Write("src/Thing.cs", "// v1");
        Git("add", "-A");
        Git("commit", "-m", "the breakdown");

        // The plan branch is cut ONCE here, exactly as the harness cuts it at the first run's start.
        Git("branch", PlanBranch);
    }

    [Fact]
    public void APlanFolderFixMadeAfterTheBranchWasCut_IsCounted()
    {
        // The measured shape: an operator fixes the plan folder between resumes. The run uses the fix
        // immediately (the harness reads the folder from the main checkout) and the plan branch never
        // sees it.
        Write($"{PlanFolder}/tasks/01-a/guardrails/02-tests-fail-on-stubs.ps1", "# fixed filter");
        Git("add", "-A");
        Git("commit", "-m", "fix task 01's filter");

        Assert.Equal(1, Probe().CommitsNotOnPlanBranch(PlanBranch, PlanFolder));
    }

    [Fact]
    public void EachResumesFixIsCountedSeparately()
    {
        // Plan 32 had THREE, and the count is what tells the operator how much is about to be left behind.
        for (int i = 1; i <= 3; i++)
        {
            Write($"{PlanFolder}/tasks/01-a/fix-{i}.txt", $"resume {i}");
            Git("add", "-A");
            Git("commit", "-m", $"resume {i} fix");
        }

        Assert.Equal(3, Probe().CommitsNotOnPlanBranch(PlanBranch, PlanFolder));
    }

    [Fact]
    public void CommitsOutsideThePlanFolder_AreNotCounted()
    {
        // The load-bearing negative. A branch that simply moved on — the ordinary case — must not be
        // reported as plan-folder drift, or the note fires on every run and stops being read.
        Write("src/Thing.cs", "// v2, unrelated work");
        Git("add", "-A");
        Git("commit", "-m", "unrelated");

        Assert.Equal(0, Probe().CommitsNotOnPlanBranch(PlanBranch, PlanFolder));
    }

    [Fact]
    public void NoDrift_IsZero_NotNull()
    {
        // Zero and not-known are different answers and the banner renders them differently, so the probe
        // must distinguish them: a clean branch is a KNOWN zero.
        Assert.Equal(0, Probe().CommitsNotOnPlanBranch(PlanBranch, PlanFolder));
    }

    [Fact]
    public void AMissingPlanBranch_IsNOTKNOWN_NeverZero()
    {
        // A run that never used worktree mode has no plan branch at all. git exits non-zero on the
        // unknown revision, and reporting that as 0 would tell an operator their folder is in sync on
        // the strength of a question git refused to answer.
        Assert.Null(Probe().CommitsNotOnPlanBranch("guardrails/never-existed", PlanFolder));
    }

    [Fact]
    public void NoGitOnPath_IsNOTKNOWN()
    {
        var absent = new StubExecutableProbe(exists: false);
        Assert.Null(new GitRevListDriftProbe(absent, _repo).CommitsNotOnPlanBranch(PlanBranch, PlanFolder));
    }

    [Fact]
    public void NotARepository_IsNOTKNOWN()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), $"gr576-norepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        try
        {
            Assert.Null(new GitRevListDriftProbe(elsewhere).CommitsNotOnPlanBranch(PlanBranch, PlanFolder));
        }
        finally
        {
            try { Directory.Delete(elsewhere, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The shape PRODUCTION actually passes: an ABSOLUTE plan directory with native separators.
    ///
    /// <para>Every other test here passes a repo-relative path, which is convenient and is not what
    /// <c>RunCommand</c> does — it hands over <c>PlanDefinition.PlanDirectory</c>, which is absolute.
    /// A suite that only ever exercised the relative form would be green while the one call site in the
    /// product used a shape no test had tried, which is the #382 fake-masked failure in miniature.</para>
    /// </summary>
    [Fact]
    public void AnABSOLUTEPlanDirectory_IsTheShapeProductionPasses_AndCountsTheSame()
    {
        Write($"{PlanFolder}/tasks/01-a/guardrails/02-tests-fail-on-stubs.ps1", "# fixed filter");
        Git("add", "-A");
        Git("commit", "-m", "fix task 01's filter");

        string absolute = Path.Combine(_repo, PlanFolder.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(1, Probe().CommitsNotOnPlanBranch(PlanBranch, absolute));
        // And it agrees with the relative form, so the two spellings cannot silently diverge.
        Assert.Equal(
            Probe().CommitsNotOnPlanBranch(PlanBranch, PlanFolder),
            Probe().CommitsNotOnPlanBranch(PlanBranch, absolute));
    }

    private GitRevListDriftProbe Probe() => new(_repo);

    private void Write(string relativePath, string content)
    {
        string full = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} exited {process.ExitCode}: {stderr}");
    }

    public void Dispose()
    {
        try { DeleteTree(_repo); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // git marks objects read-only; a plain recursive delete fails on Windows.
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { }
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class StubExecutableProbe(bool exists) : IExecutableProbe
    {
        public bool Exists(string command) => exists;
    }
}
