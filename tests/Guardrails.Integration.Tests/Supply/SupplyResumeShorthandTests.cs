using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Authored-red tests for <c>guardrails supply --resume &lt;plan&gt; &lt;path&gt;</c> (design 40 §3,
/// the review's DECIDED opt-in shorthand).
/// <para>
/// <b>The decision, stated so these tests encode it and not the rejected alternative.</b> The three
/// existing verbs — <c>supply</c>, <c>reset</c>, <c>run</c> — stay the DEFAULT, and the needs-human
/// halt keeps printing that explicit form, because <c>reset</c> chooses WHICH descendants to re-arm
/// and folding that choice into <c>supply</c> would hide a real decision. <c>--resume</c> is an
/// opt-in shorthand for the operator who already knows what it does — it does not replace the
/// three-command path, it collapses it into one invocation for the operator who wants that.
/// </para>
/// <para>
/// <b>The requirement that falls out, and the one most likely to be missed:</b> both paths must
/// produce the IDENTICAL journal and provenance record. A shorthand that took a different code path
/// (e.g. its own bespoke drain, or a supplied-record with a different <c>by</c>) would be a second
/// mechanism for one decision — precisely the defect §4 exists to prevent.
/// </para>
/// <para>
/// <b>There is no stub for this task.</b> <c>SupplyCommand</c> declares no <c>--resume</c> option at
/// all, so every invocation below that passes it fails to PARSE — "Unrecognized command or argument
/// '--resume'", exit 1 — before any behaviour-specific logic could ever run (the same shape
/// <c>ReviewMarkerCliTests</c> documents for an option that exists on one verb but not another). The
/// three pinned-red tests below assert what the shorthand must eventually DO; today they fail because
/// nothing happens at all, never because of a hollow or trivially-true body.
/// </para>
/// <para>
/// <b><see cref="WithoutResume_TheThreeCommandPathIsUnchanged"/> is NOT part of the pinned red
/// census</b> (<c>guardrails/02-tests-fail-on-current-code.ps1</c> pins only the other three method
/// names) — it asserts only that the three-command path's own outcome (the halted task settles
/// <c>Succeeded</c>) is unaffected by whatever <c>--resume</c> turns out to need, which is already
/// true today.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SupplyResumeShorthandTests
{
    private const string HaltedTaskId = "02-second";
    private const string MarkerRelPath = "resume-marker.txt";
    private const string MarkerContent = "drained-for-resume-shorthand";

    // ── the pinned-red behaviours (design 40 §3) ────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_StagesResetsAndResumesInOneInvocation()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateHaltThenResumePlan(repo.RepoPath);

        (int haltExit, _) = await RunViaCliAsync(
            "run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.TaskFailed, haltExit); // sanity: the run really did halt needs-human

        WriteMarkerSource(repo.RepoPath);

        // The DECIDED shorthand: ONE invocation stages the file, resets the halted task, and resumes
        // — never the three separate commands `WithoutResume_TheThreeCommandPathIsUnchanged` exercises.
        (int resumeExit, _) = await RunViaCliAsync("supply", "--resume", planDir, MarkerRelPath);

        Assert.Equal(ExitCodes.Success, resumeExit);

        JournalDocument journal = ReadJournal(planDir);
        Assert.Equal(JournalTaskStatus.Succeeded, journal.Tasks[HaltedTaskId].Status);
    }

    [Fact]
    public async Task Resume_ProducesTheIdenticalJournalAsTheThreeCommandPath()
    {
        // Run both, compare — on their OWN, otherwise-identical fixtures, so neither path can taint
        // the other's git repo or plan folder.
        JournalDocument viaThreeCommands = await RunThreeCommandPathAsync();
        JournalDocument viaResumeShorthand = await RunResumeShorthandPathAsync();

        Assert.Equal(NormalizeTasks(viaThreeCommands), NormalizeTasks(viaResumeShorthand));
    }

    [Fact]
    public async Task Resume_ProducesTheIdenticalProvenanceRecordAsTheThreeCommandPath()
    {
        JournalDocument viaThreeCommands = await RunThreeCommandPathAsync();
        JournalDocument viaResumeShorthand = await RunResumeShorthandPathAsync();

        NormalizedSuppliedRecord? expected = NormalizeSupplied(viaThreeCommands);

        // Sanity on the REFERENCE path itself: it must have actually recorded provenance, or the
        // Assert.Equal below could pass by both sides being null — which would prove nothing.
        Assert.NotNull(expected);

        // Compared structurally (paths/bytes/by), never on `at`/`commit` — those are legitimately
        // different across two independent runs on two independent repos.
        Assert.Equal(expected, NormalizeSupplied(viaResumeShorthand));
    }

    // ── the regression floor: the pre-existing DEFAULT path is untouched by adding the shorthand ────
    // Not part of this task's pinned red census (guardrails/02-tests-fail-on-current-code.ps1 pins
    // only the three behaviours above) — it exists to pin that the three-command path's own outcome
    // is unaffected by whatever `--resume` turns out to need, never to assert anything about the
    // shorthand itself.

    [Fact]
    public async Task WithoutResume_TheThreeCommandPathIsUnchanged()
    {
        JournalDocument journal = await RunThreeCommandPathAsync();

        Assert.Equal(JournalTaskStatus.Succeeded, journal.Tasks[HaltedTaskId].Status);
    }

    // ── shared plumbing: the two paths, each on its OWN repo/plan ───────────────────────────────────

    /// <summary>
    /// The design's own three-command recipe (§3(b)): halt, supply (no <c>--resume</c>), reset the
    /// halted task, resume via a plain <c>run</c>. Returns the resulting journal.
    /// </summary>
    private static async Task<JournalDocument> RunThreeCommandPathAsync()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateHaltThenResumePlan(repo.RepoPath);

        (int haltExit, _) = await RunViaCliAsync(
            "run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.TaskFailed, haltExit);

        WriteMarkerSource(repo.RepoPath);

        (int supplyExit, _) = await RunViaCliAsync("supply", planDir, MarkerRelPath);
        Assert.Equal(ExitCodes.Success, supplyExit);

        (int resetExit, _) = await RunViaCliAsync("reset", planDir, HaltedTaskId);
        Assert.Equal(ExitCodes.Success, resetExit);

        (int resumeExit, _) = await RunViaCliAsync(
            "run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.Success, resumeExit);

        return ReadJournal(planDir);
    }

    /// <summary>
    /// The same halt, resolved with the single <c>supply --resume</c> shorthand instead. Returns the
    /// resulting journal — today, since <c>--resume</c> does not exist, this is unchanged from the
    /// halted state read straight after the first <c>run</c>.
    /// </summary>
    private static async Task<JournalDocument> RunResumeShorthandPathAsync()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateHaltThenResumePlan(repo.RepoPath);

        (int haltExit, _) = await RunViaCliAsync(
            "run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.TaskFailed, haltExit);

        WriteMarkerSource(repo.RepoPath);

        await RunViaCliAsync("supply", "--resume", planDir, MarkerRelPath);

        return ReadJournal(planDir);
    }

    // ── normalization: compare STRUCTURE, never run-specific identifiers (runId/timestamps/shas) ────

    private sealed record NormalizedTaskState(string TaskId, JournalTaskStatus Status, int AttemptCount);

    private static List<NormalizedTaskState> NormalizeTasks(JournalDocument document) =>
        document.Tasks
            .Select(kv => new NormalizedTaskState(kv.Key, kv.Value.Status, kv.Value.Attempts.Count))
            .OrderBy(t => t.TaskId, StringComparer.Ordinal)
            .ToList();

    private sealed record NormalizedSuppliedRecord(string PathsJoined, long Bytes, string By);

    /// <summary>Null when nothing was supplied — the shorthand's own not-yet-implemented starting point.</summary>
    private static NormalizedSuppliedRecord? NormalizeSupplied(JournalDocument document) =>
        document.Supplied is { Count: > 0 } supplied
            ? new NormalizedSuppliedRecord(
                string.Join("|", supplied[0].Paths), supplied[0].Bytes, supplied[0].By)
            : null;

    // ── driving the real composition root, as an OPERATOR ───────────────────────────────────────────

    /// <summary>
    /// The task-action env var namespace <c>TaskExecutor.BuildEnvironment</c> actually sets (SSOT
    /// §5.1, mirrors <c>SupplyCommandTests</c>' own copy) — cleared around every CLI call below so this
    /// suite's OWN ambient environment (it typically runs AS a guardrails task's guardrail action, and
    /// so genuinely carries <c>GUARDRAILS_TASK_ID</c> et al.) can never make <c>supply</c>'s
    /// caller-scope check misclassify these operator-shaped invocations as a task invocation — which,
    /// unhandled, refuses every supply below with an empty inherited writeScope.
    /// </summary>
    private static readonly string[] TaskScopedEnvironmentKeys =
    [
        "GUARDRAILS_PLAN_DIR", "GUARDRAILS_TASK_ID", "GUARDRAILS_TASK_DIR", "GUARDRAILS_ATTEMPT",
        "GUARDRAILS_STATE_IN", "GUARDRAILS_STATE_OUT", "GUARDRAILS_LOG_DIR", "GUARDRAILS_WORKSPACE",
        "GUARDRAILS_STAGING_DIR", "GUARDRAILS_FEEDBACK"
    ];

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        Dictionary<string, string?> previous = TaskScopedEnvironmentKeys.ToDictionary(
            k => k, Environment.GetEnvironmentVariable);
        foreach (string key in TaskScopedEnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        try
        {
            var io = new StringConsoleIo();
            var root = CommandFactory.BuildRootCommand(io);
            int exit = await root.Parse(args).InvokeAsync();
            return (exit, io.OutText);
        }
        finally
        {
            foreach ((string key, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    /// <summary>
    /// Write the file the operator "has in hand" directly into the workspace (never through git) —
    /// exactly what <c>guardrails supply</c> reads its source from (design 40 §1: the second argument
    /// names where the file must land IN THE WORKSPACE, taken from an operator-supplied source that
    /// already sits there).
    /// </summary>
    private static void WriteMarkerSource(string workspaceRoot) =>
        File.WriteAllText(Path.Combine(workspaceRoot, MarkerRelPath), MarkerContent);

    // ── plan fixture: a green task, then one gated on the marker landing on the base ────────────────

    private static readonly bool Windows = OperatingSystem.IsWindows();

    private static void WriteGuardrailsJson(string planDir)
    {
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);
    }

    private static void WriteTaskJson(string taskDir, string[] dependsOn, string description)
    {
        string dependsJson = dependsOn.Length == 0
            ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{description}}", "writeScope": [], "dependsOn": {{dependsJson}} }""");
    }

    private static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>An ordinary green no-op task (action and guardrail both exit 0).</summary>
    private static void WriteTrivialTask(string taskDir, string[] dependsOn, string description)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        WriteTaskJson(taskDir, dependsOn, description);

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>A task whose GUARDRAIL halts needs-human until <see cref="MarkerRelPath"/> (with
    /// <see cref="MarkerContent"/>) is present on the base — design 40's measured case.</summary>
    private static void WriteMarkerGateTask(string taskDir, string[] dependsOn)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        WriteTaskJson(taskDir, dependsOn, $"halts needs-human until {MarkerRelPath} lands on the base");

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                $"$target = Join-Path $env:GUARDRAILS_WORKSPACE \"{MarkerRelPath}\"\r\n" +
                $"if ((Test-Path $target) -and ((Get-Content -Raw -Path $target) -eq \"{MarkerContent}\")) {{ exit 0 }} else {{ exit 1 }}\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"),
                "#!/usr/bin/env bash\n" +
                $"target=\"$GUARDRAILS_WORKSPACE/{MarkerRelPath}\"\n" +
                $"if [ -f \"$target\" ] && [ \"$(cat \"$target\")\" = \"{MarkerContent}\" ]; then exit 0; else exit 1; fi\n");
        }
    }

    /// <summary>01-first is a trivial green task; <see cref="HaltedTaskId"/> (depending on it) halts
    /// needs-human until <see cref="MarkerRelPath"/> lands on the base — the measured incident's
    /// shape (mirrors <c>SuppliedBoundaryWiringTests.CreateHaltThenResumePlan</c>).</summary>
    private static string CreateHaltThenResumePlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        WriteGuardrailsJson(planDir);
        WriteTrivialTask(
            Path.Combine(planDir, "tasks", "01-first"), dependsOn: [],
            "a green task that settles before the halt");
        WriteMarkerGateTask(Path.Combine(planDir, "tasks", HaltedTaskId), dependsOn: ["01-first"]);
        return planDir;
    }

    // ── a real git repository, not a fake of one ───────────────────────────────────────────────────

    /// <summary>
    /// A throwaway single-use git repository in a temp directory (mirroring the shared idiom already
    /// duplicated across this project — there is no shared fixture project it references). Hooks are
    /// pointed at an empty directory inside <c>.git</c>, <c>autocrlf</c> is forced off, and
    /// <c>commit.gpgsign</c> is forced off, so a machine-global git config can never affect content
    /// bytes or block a commit in this throwaway repo. The drain (design 40 §2) is a real git commit,
    /// so a REAL repo is required here — a fake one would prove nothing about either path.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _rootDir;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "gr-supply-resume-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_rootDir, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# supply-resume-shorthand fixture");
            Git("add", "README.md");
            Git("commit", "-m", "Initial commit");
        }

        private string Git(params string[] arguments)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} (in {RepoPath}) exited {process.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose() => SafeDelete.DeleteDirectory(_rootDir);
    }
}
