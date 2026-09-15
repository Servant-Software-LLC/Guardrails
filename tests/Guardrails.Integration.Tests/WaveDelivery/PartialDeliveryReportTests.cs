using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Red-bar tests for design 39 §4 — a PARTIALLY-DELIVERED run (one or more waves already landed on the
/// user's branch at their own barrier while the run itself did not finish wholly delivered) is a NEW run
/// outcome, distinct from both a fully-delivered run and a wholly-undelivered one, and must render as
/// neither in the printed report or in <c>run.json</c>'s delivery record.
/// <para>
/// Five requirements, each earned by a defect already shipped here: the report prints BEFORE the verdict
/// (the #340 <c>mergeOnSuccess</c> banner's own defect); <c>git branch --no-merged</c> is the confirmation
/// and the report says so; the exit code never changes because of delivery state; <c>run.json</c>'s delivery
/// record tells the truth about a partially-delivered run (round 4, <c>d39-partial-delivery-record</c>); and
/// nothing that already landed is ever described as undelivered (review 2026-09-13, round 5
/// <c>d39-barrier-terminal-gate</c> / <c>d39-hooks-untracked-tooling</c>).
/// </para>
/// <para>
/// Does NOT implement the report, the delivery record, the banner or the label — task 19's job. This file
/// only pins the contract. The four DescribeDelivery/exit-code/no-op rows already hold on today's code
/// (declared exempt from the red census) and are written to assert the guarantee, not to fail.
/// </para>
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class PartialDeliveryReportTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private const string PlanDir = "/repo/docs/plans/39-incremental-delivery";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Pure fixtures — no run, no git (WaveDeliveredRecord / TaskResult builders).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static TaskResult Green(string id) => new() { TaskId = id, Outcome = TaskOutcome.Succeeded, Summary = "ok" };

    private static WaveDeliveredRecord DeliveredRecord(params string[] covers) => new()
    {
        Status = WaveDeliveryStatus.Delivered,
        StartedAt = Now,
        At = Now,
        Commit = "deadbeef",
        Covers = covers,
    };

    private static WaveDeliveredRecord RefusedRecord(DeliveryOutcome outcome, string detail, params string[] covers) => new()
    {
        Status = WaveDeliveryStatus.Refused,
        StartedAt = Now,
        At = Now,
        Outcome = outcome,
        Detail = detail,
        Covers = covers,
    };

    private static WaveDeliveredRecord SuppressedRecord(string detail, params string[] covers) => new()
    {
        Status = WaveDeliveryStatus.Suppressed,
        StartedAt = Now,
        At = Now,
        Detail = detail,
        Covers = covers,
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // DescribeDelivery — pure calls, no run and no git.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DescribeDelivery_APartialDelivery_IsPartiallyDelivered()
    {
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            WaveHalt = new WaveHalt
            {
                WaveDir = "wave-03-build",
                Kind = WaveHaltKind.DeliveryRefused,
                Headline = "wave-03-build: delivery refused",
            },
            DeliveredToBranch = "master",
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-02-build"] = DeliveredRecord("wave-02-build"),
                ["wave-03-build"] = RefusedRecord(DeliveryOutcome.TrialGateFailed, "trial-tree exit gate failed", "wave-03-build"),
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.Equal(DeliveryOutcome.PartiallyDelivered, d.Outcome);
        Assert.False(d.Delivered);
        Assert.NotNull(d.DeliveredToBranch);
        Assert.NotNull(d.PlanBranch);
        Assert.NotNull(d.Reason);
        Assert.Contains("wave-02-build", d.Reason!, StringComparison.Ordinal);
        Assert.Contains("wave-03-build", d.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeDelivery_AGreenRunWhoseRunEndDeliveryWasHeld_IsPartiallyDelivered()
    {
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            WhollyGreenButUndelivered = true,
            DeliverySuppressingDecision = new DecisionEntry
            {
                Boundary = "wave",
                Policy = "auto",
                Decision = DecisionTokens.ProceededBestGuess,
                Subject = "wave-03-build",
                Headline = "best-guessed at wave-03-build",
            },
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-02-build"] = DeliveredRecord("wave-02-build"),
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.Equal(DeliveryOutcome.PartiallyDelivered, d.Outcome);
        Assert.False(d.Delivered);
        Assert.NotNull(d.PlanBranch);
        Assert.NotNull(d.Reason);
        Assert.Contains("wave-02-build", d.Reason!, StringComparison.Ordinal);
        Assert.Contains(DecisionTokens.ProceededBestGuess, d.Reason!, StringComparison.Ordinal);
        Assert.Contains("wave-03-build", d.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeDelivery_ARefusedRunEndMergeAfterAWaveDelivered_IsPartiallyDelivered()
    {
        const string detail = "refused: local changes would be overwritten";
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            MergeOnSuccessOutcome = MergeOnSuccessResult.Conflict,
            MergeOnSuccessDetail = detail,
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-02-build"] = DeliveredRecord("wave-02-build"),
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.Equal(DeliveryOutcome.PartiallyDelivered, d.Outcome);
        Assert.False(d.Delivered);
        Assert.Equal(detail, d.Detail);
        Assert.NotNull(d.Reason);
        Assert.Contains("wave-02-build", d.Reason!, StringComparison.Ordinal);
        Assert.Contains("conflict", d.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// EXEMPT — green on arrival. <see cref="RunCommand.DescribeDelivery"/> returns a landed run-end merge
    /// as delivered without reading <see cref="RunReport.WaveDeliveries"/>, whatever the barrier records say.
    /// </summary>
    [Fact]
    public void DescribeDelivery_ARunEndDeliveryAfterABarrierDelivery_IsDelivered()
    {
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            MergeOnSuccessOutcome = MergeOnSuccessResult.FastForwarded,
            DeliveredToBranch = "master",
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-01-deliver"] = DeliveredRecord("wave-01-deliver"),
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.True(d.Delivered);
        Assert.Equal(DeliveryOutcome.FastForwarded, d.Outcome);
        Assert.Null(d.Reason);
        Assert.Null(d.PlanBranch);
    }

    /// <summary>
    /// EXEMPT — green on arrival, for the same reason as the row above: a rejecting hook holds deliveries to
    /// run end rather than halting, and a run-end merge that landed carried every held wave.
    /// </summary>
    [Fact]
    public void DescribeDelivery_AHookRejectionHeldDeliveriesThenTheRunEndMergeLanded_IsDelivered()
    {
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            MergeOnSuccessOutcome = MergeOnSuccessResult.Merged,
            DeliveredToBranch = "master",
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-01-deliver"] = DeliveredRecord("wave-01-deliver"),
                ["wave-02-hookheld"] = RefusedRecord(DeliveryOutcome.HookRejected, "pre-commit hook exited 1", "wave-02-hookheld"),
                ["wave-03-ride"] = SuppressedRecord("held by wave-02-hookheld: hook-rejected", "wave-03-ride"),
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.True(d.Delivered);
        Assert.Equal(DeliveryOutcome.Merged, d.Outcome);
        Assert.Null(d.Reason);
        Assert.Null(d.PlanBranch);
    }

    [Fact]
    public void ATerminalGateFailureAfterAWaveDelivered_StillRecordsPartiallyDelivered()
    {
        var report = new RunReport
        {
            Tasks = [Green("01-a"), Green("wave-02-final/01-write")],
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-01-deliver"] = DeliveredRecord("wave-01-deliver"),
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: false, PlanDir);

        Assert.Equal(DeliveryOutcome.PartiallyDelivered, d.Outcome);
        Assert.False(d.Delivered);
        Assert.NotNull(d.Reason);
        Assert.Contains("wave-01-deliver", d.Reason!, StringComparison.Ordinal);
        Assert.Contains("terminal gate", d.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // RenderUndeliveredWorkWarning / RenderWaveDeliveryReport — pure calls against a StringWriter.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheUndeliveredWorkBanner_NamesTheWavesThatAlreadyDelivered()
    {
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            WhollyGreenButUndelivered = true,
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-02-build"] = DeliveredRecord("wave-02-build"),
            },
        };

        var sw = new StringWriter();
        RunCommand.RenderUndeliveredWorkWarning(report, terminalGatePassed: true, PlanDir, sw);

        Assert.Contains("wave-02-build", sw.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Final adversarial pass: a rejecting hook holds every incremental delivery, but the run-end merge (in
    /// the user's own checkout, where the rejecting hook's tooling is present) lands them all anyway. The
    /// hold must still show up in the REPORT — the operator's evidence that something was held mid-run — even
    /// though the delivery RECORD correctly reads delivered/Merged, because every held wave did arrive by
    /// run end.
    /// </summary>
    [Fact]
    public void TheReportNamesAHookHold_EvenWhenTheRunEndMergeLanded()
    {
        const string hookDetail = "pre-commit hook exited 1: refusing commit";
        var report = new RunReport
        {
            Tasks = [Green("01-a")],
            MergeOnSuccessOutcome = MergeOnSuccessResult.Merged,
            DeliveredToBranch = "master",
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-01-deliver"] = DeliveredRecord("wave-01-deliver"),
                ["wave-02-hookheld"] = RefusedRecord(DeliveryOutcome.HookRejected, hookDetail, "wave-02-hookheld"),
                ["wave-03-ride"] = SuppressedRecord("held by wave-02-hookheld: hook-rejected", "wave-03-ride"),
            },
        };

        var sw = new StringWriter();
        RunCommand.RenderWaveDeliveryReport(report, sw);
        string output = sw.ToString();

        Assert.Contains("wave-02-hookheld", output, StringComparison.Ordinal);
        Assert.Contains(hookDetail, output, StringComparison.Ordinal);
        Assert.Contains("wave-03-ride", output, StringComparison.Ordinal);

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);
        Assert.True(d.Delivered);
        Assert.Equal(DeliveryOutcome.Merged, d.Outcome);
        Assert.Null(d.Reason);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // PrintWaveHalt — the DeliveryRefused halt's own label, over a StringConsoleIo.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeliveryRefusedHalt_PrintsItsOwnLabel_NotTheGenericWaveHalt()
    {
        var io = new StringConsoleIo();
        var halt = new WaveHalt
        {
            WaveDir = "wave-02-switch",
            Kind = WaveHaltKind.DeliveryRefused,
            Headline = "run started on 'master'; HEAD is now 'elsewhere' -- delivery refused",
        };

        RunCommand.PrintWaveHalt(halt, io);

        string output = io.OutText;
        string? labelLine = output
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .FirstOrDefault(l => l.Length > 0);

        Assert.NotNull(labelLine);
        Assert.StartsWith("WAVE DELIVERY REFUSED:", labelLine, StringComparison.Ordinal);
        Assert.DoesNotContain("WAVE HALT:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GATE FAILED", output, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The journal round trip — PartiallyDelivered through RunJournal.RecordDelivery / LoadOrCreate.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PartiallyDelivered_RoundTripsThroughTheJournal()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "gr-pdr-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            PlanDefinition plan = BuildJournalPlan(tempDir);
            RunJournal journal = RunJournal.LoadOrCreate(plan);

            journal.RecordDelivery(new DeliverySection
            {
                Delivered = false,
                Outcome = DeliveryOutcome.PartiallyDelivered,
                Reason = "wave-01-deliver delivered; wave-02-halt held",
                PlanBranch = "guardrails/plan",
            });

            DeliverySection? reloaded = RunJournal.LoadOrCreate(plan).Document.Delivery;

            Assert.NotNull(reloaded);
            Assert.Equal(DeliveryOutcome.PartiallyDelivered, reloaded!.Outcome);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private static PlanDefinition BuildJournalPlan(string tempDir)
    {
        string planDir = Path.Combine(tempDir, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        var task = new TaskNode
        {
            Id = "01-task",
            Directory = taskDir,
            Description = "t",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "x", Kind = ActionKind.Script }]
        };

        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task],
            Workspace = planDir
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Real, git-driven runs through CommandFactory.BuildRootCommand(io) — the ORDER/exit-code/journal
    // rows need the actual CLI flow, never a hand-assembled RunReport.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "gr-pdr-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            WorktreeRoot = Path.Combine(Root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# partial-delivery-report test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best-effort teardown
            }
        }
    }

    private static async Task<(int Exit, string Output)> RunCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static JournalDocument JournalOf(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir));

    private static string StandardGuardrailsJson() =>
        """
        {
          "version": 1,
          "guardrailMode": "failFast",
          "workspace": "..",
          "defaultRetries": 0,
          "maxParallelism": 2
        }
        """;

    private static void WriteDeliversBrief(string waveDir)
    {
        Directory.CreateDirectory(waveDir);
        File.WriteAllText(Path.Combine(waveDir, "brief.md"),
            "---\ndelivers: true\n---\n# wave brief\n\nDelivers at its own barrier.\n");
    }

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void WriteFileExistsGate(string path, string file)
    {
        string body = Ps
            ? $"# catches: {file} not present\n" +
              $"if (-not (Test-Path \"$env:GUARDRAILS_WORKSPACE/{file}\")) {{ exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present\n" +
              $"[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || exit 1\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteAlwaysFailGate(string path, string reason = "deliberately fails")
    {
        string body = Ps
            ? $"# catches: {reason}\nexit 1\n"
            : $"#!/usr/bin/env bash\n# catches: {reason}\nexit 1\n";
        WriteExecutable(path, body);
    }

    private static void WriteFileWritingTask(string taskDir, string file)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}}", "writeScope": ["{{file}}"] }""");

        string body = Ps
            ? $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/{file}\" -Value 'x'\nexit 0\n"
            : $"#!/usr/bin/env bash\nprintf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteFileExistsGate(Path.Combine(taskDir, "guardrails", Script("01-check")), file);
    }

    /// <summary>
    /// Two waves: wave-01 delivers cleanly at its own barrier (the already-shipped task 08/29 mechanic);
    /// wave-02 is an ORDINARY (non-delivering) wave whose own EXIT gate always fails — a pre-design-39
    /// mechanic, so the halt itself is not in question, only what the delivery report says about it.
    /// </summary>
    private sealed record TwoWaveScenario(string PlanDir, string DeliveredWaveDir, string HaltedWaveDir);

    private static TwoWaveScenario CreatePartialDeliveryScenarioPlan(TempGitRepo repo)
    {
        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        const string w1 = "wave-01-deliver";
        const string w2 = "wave-02-halt";

        string wd1 = Path.Combine(planDir, w1);
        WriteDeliversBrief(wd1);
        WriteFileWritingTask(Path.Combine(wd1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(wd1, "guardrails", Script("01-check")), "wave1.txt");

        string wd2 = Path.Combine(planDir, w2);
        WriteFileWritingTask(Path.Combine(wd2, "tasks", "01-write"), "wave2.txt");
        WriteAlwaysFailGate(Path.Combine(wd2, "guardrails", Script("01-always-fail")));

        return new TwoWaveScenario(planDir, w1, w2);
    }

    [Fact]
    public async Task TheReportPointsAtGitBranchNoMerged()
    {
        using var repo = new TempGitRepo();
        TwoWaveScenario s = CreatePartialDeliveryScenarioPlan(repo);

        (int exit, string output) = await RunCliAsync("run", s.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);
        Assert.Contains("git branch --no-merged", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReportNamesDeliveredAndHeldWavesSeparately()
    {
        using var repo = new TempGitRepo();
        TwoWaveScenario s = CreatePartialDeliveryScenarioPlan(repo);

        (int exit, string output) = await RunCliAsync("run", s.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);
        int markerIndex = output.IndexOf("git branch --no-merged", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "the delivery report was not printed:\n" + output);
        Assert.Contains(s.DeliveredWaveDir, output, StringComparison.Ordinal);
        Assert.Contains(s.HaltedWaveDir, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReportIsPrintedBeforeTheVerdict()
    {
        using var repo = new TempGitRepo();
        TwoWaveScenario s = CreatePartialDeliveryScenarioPlan(repo);

        (int exit, string output) = await RunCliAsync("run", s.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);
        int reportIndex = output.IndexOf("git branch --no-merged", StringComparison.Ordinal);
        int verdictIndex = output.IndexOf("WAVE EXIT GATE FAILED:", StringComparison.Ordinal);
        Assert.True(reportIndex >= 0, "the delivery report was not printed:\n" + output);
        Assert.True(verdictIndex >= 0, "the wave-halt verdict was not printed:\n" + output);
        Assert.True(reportIndex < verdictIndex, "the delivery report must print before the verdict:\n" + output);
    }

    /// <summary>EXEMPT — a run with a failed wave already exits 2; nothing about this design changes that.</summary>
    [Fact]
    public async Task AFailedWaveDoesNotChangeTheExitCode()
    {
        using var repo = new TempGitRepo();
        TwoWaveScenario s = CreatePartialDeliveryScenarioPlan(repo);

        (int exit, _) = await RunCliAsync("run", s.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);
    }

    [Fact]
    public async Task ATerminalGateFailureAfterAWaveDelivered_WritesTheDeliveryRecordBeforeReturning()
    {
        using var repo = new TempGitRepo();
        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string w1 = Path.Combine(planDir, "wave-01-deliver");
        WriteDeliversBrief(w1);
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        WriteAlwaysFailGate(
            Path.Combine(planDir, "guardrails", Script("01-terminal")), "plan-level terminal gate always fails");

        (int exit, _) = await RunCliAsync("run", planDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = JournalOf(planDir);
        Assert.NotNull(doc.Delivery);
        Assert.Equal(DeliveryOutcome.PartiallyDelivered, doc.Delivery!.Outcome);
    }

    /// <summary>EXEMPT — the never-weaker requirement: a plan marking no wave prints what it prints today.</summary>
    [Fact]
    public async Task AFullyDeliveredRunReadsAsTodayDoes()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-a");

        (int exit, string output) = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("git branch --no-merged", output, StringComparison.Ordinal);
    }
}
