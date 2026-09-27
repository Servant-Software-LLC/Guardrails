using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Bundle;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #799, handoff row 5: <c>guardrails bundle</c> through the real CLI (SSOT §17).
/// <list type="number">
/// <item><b>Live bundle from a second process</b>: a real worktree-mode <c>guardrails run</c> is held in flight by a
/// gated task while the shipped CLI bundles it, then bundled in a loop through the run's commit and merge phases. The
/// run must settle green with no <c>AtomicFile</c> exhaustion (#727) and no <c>index.lock</c> left, the in-flight bundle
/// must name the attempt and its #798 phase, and that bundle must change no byte of the repository, the plan folder or
/// the worktrees.</item>
/// <item><b>Windows sharing</b>: bundles read while <c>AtomicFile</c> rewrites every target the bundle reads; no write
/// exhausts its retries. A file held with <c>FileShare.None</c> is excluded and named (Windows only).</item>
/// <item><b>Path refusal</b>: a destination inside a git working tree is refused before anything is read.</item>
/// <item><b>D1 end to end</b>: an unset judge-only <c>authTokenEnv</c> refuses; <c>--without-agent-text</c> ships
/// with the free-text canary gone.</item>
/// <item><b>Content default vs <c>--lean</c></b>, and <b>zip hygiene</b> (raw bytes, determinism).</item>
/// </list>
/// Everything machine-dependent the in-process tests touch is decided by the <see cref="BundleCommandHost"/> seam;
/// every assertion is about a decision or a byte, never a duration.
/// </summary>
public sealed class BundleCliTests
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    // =====================================================================================================
    // 1. Live bundle from a second process
    // =====================================================================================================

    /// <summary>
    /// A real <c>guardrails run</c> (worktree mode: a git workspace and <c>maxParallelism: 2</c>) of a four-task chain:
    /// <c>01-held</c> blocks in its action until the test releases it, <c>02-prompt</c> is a prompt task on a fake
    /// Claude CLI (so the run has transcripts and a stream log), and <c>03-third</c>/<c>04-fourth</c> add more
    /// commit-and-merge settles. The shipped CLI bundles it once while <c>01-held</c> is gated, then in a loop from the
    /// release until the run exits, then once more after it ended.
    /// </summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task ALiveRunBundledFromASecondProcess_SettlesGreen_AndTheBundleChangesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var live = new LiveRunFixture();

        using Process run = live.StartRun();
        Task<string> runOut = run.StandardOutput.ReadToEndAsync(ct);
        Task<string> runErr = run.StandardError.ReadToEndAsync(ct);
        try
        {
            // Gate: 01-held's action has started (its in-flight marker was persisted before it launched).
            await WaitUntilAsync(() => File.Exists(live.StartedSignal), run, async () => await runOut + await runErr, "01-held's action to start", ct);

            // --- the in-flight bundle, from a separate process, bracketed by a byte-level snapshot -------------
            Dictionary<string, string> before = Snapshot(live.RepoPath, live.WorktreeRoot);
            string inFlightZip = Path.Combine(live.OutDir, "in-flight.zip");
            CliResult inFlight = await RunCliAsync(["bundle", live.PlanDir, "--out", inFlightZip, "--force-path"], live.ChildEnvironment, ct);
            Dictionary<string, string> after = Snapshot(live.RepoPath, live.WorktreeRoot);

            Assert.True(inFlight.ExitCode == 0, $"the in-flight bundle exited {inFlight.ExitCode}:\n{inFlight.Error}");
            AssertSnapshotsEqual(before, after);

            string summary = EntryText(inFlightZip, "SUMMARY.md");
            Assert.Contains("- In flight: attempt-1 (phase action,", summary, StringComparison.Ordinal);
            Assert.Contains("from run.json inFlightAttempt", summary, StringComparison.Ordinal);
            Assert.Contains("- Liveness: Running (owner process", summary, StringComparison.Ordinal);
            Assert.Contains($"{inFlightZip} (", inFlight.Output, StringComparison.Ordinal);

            // --- release, and bundle in loops through every remaining commit/merge settle --------------------
            // Three concurrent loops, each started BEFORE the release, so bundle reads and git calls are in flight
            // across all four settles (each a segment commit plus a merge into the plan branch) and the delivery
            // merge. A single bundle takes longer than one settle, so one loop would leave most settles unwatched.
            var loopResults = new ConcurrentQueue<CliResult>();
            Task[] loops =
            [
                .. Enumerable.Range(0, 3).Select(worker => Task.Run(async () =>
                {
                    for (int i = 0; !run.HasExited; i++)
                    {
                        string zip = Path.Combine(live.OutDir, $"loop-{worker}-{i}.zip");
                        loopResults.Enqueue(await RunCliAsync(
                            ["bundle", live.PlanDir, "--out", zip, "--force-path"], live.ChildEnvironment, ct));
                    }
                }, ct)),
            ];

            File.WriteAllText(live.ReleaseSignal, "go");
            await run.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(5), ct);
            await Task.WhenAll(loops).WaitAsync(TimeSpan.FromMinutes(5), ct);

            string runOutput = await runOut + await runErr;
            Assert.True(run.ExitCode == 0, $"the bundled run exited {run.ExitCode}:\n{runOutput}");
            Assert.DoesNotContain("Could not replace", runOutput, StringComparison.Ordinal);

            Assert.NotEmpty(loopResults);
            Assert.All(loopResults, r => Assert.True(r.ExitCode == 0, $"a loop bundle exited {r.ExitCode}:\n{r.Error}"));

            // --- the run settled correctly --------------------------------------------------------------------
            JournalDocument journal = JsonSerializer.Deserialize<JournalDocument>(
                File.ReadAllText(Path.Combine(live.PlanDir, "state", "run.json")), JournalJson.Options)!;
            foreach (string task in LiveRunFixture.TaskIds)
            {
                TaskJournalEntry entry = journal.Tasks[task];
                Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);
                Assert.Null(entry.InFlightAttempt);
                Assert.Equal(1, Assert.Single(entry.Attempts).Attempt);
            }

            string[] locks = [.. Directory.EnumerateFiles(Path.Combine(live.RepoPath, ".git"), "*.lock", SearchOption.AllDirectories)];
            Assert.True(locks.Length == 0, "git lock files left behind: " + string.Join(", ", locks));
            Assert.Contains("Guardrails-Task: 04-fourth", TempRepo.Git(live.RepoPath, "log", "--all", "--format=%B"), StringComparison.Ordinal);

            // --- and a bundle of the ended run says it ended, with nothing in flight --------------------------
            string endedZip = Path.Combine(live.OutDir, "ended.zip");
            CliResult ended = await RunCliAsync(["bundle", live.PlanDir, "--out", endedZip, "--force-path"], live.ChildEnvironment, ct);
            Assert.True(ended.ExitCode == 0, ended.Error);
            string endedSummary = EntryText(endedZip, "SUMMARY.md");
            Assert.Contains("- Liveness: Ended (", endedSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("- In flight:", endedSummary, StringComparison.Ordinal);
            Assert.Contains("tasks/02-prompt/attempt-1/transcript.md", EntryNames(endedZip));
        }
        finally
        {
            File.WriteAllText(live.ReleaseSignal, "go");
            if (!run.HasExited)
            {
                try { run.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            }
        }
    }

    /// <summary>
    /// #805 W7: a prompt attempt caught mid-stream. The fake Claude emits <see cref="LiveRunFixture.HeldPromptLines"/>
    /// stream lines and then blocks; the bundle taken while it is blocked must carry the stream's CURRENT tail cut on a
    /// newline, and SUMMARY must name the in-flight prompt attempt, its stream (as evidence), and the Live files block.
    /// </summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task APromptHeldMidStream_BundlesTheCurrentStreamTail_AndSummaryNamesTheLiveAttempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var live = new LiveRunFixture(holdPrompt: true);

        using Process run = live.StartRun();
        Task<string> runOut = run.StandardOutput.ReadToEndAsync(ct);
        Task<string> runErr = run.StandardError.ReadToEndAsync(ct);
        try
        {
            // Wait until the harness has teed every emitted line into the prompt attempt's stream log.
            await WaitUntilAsync(() => live.PromptStreamLines() >= LiveRunFixture.HeldPromptLines, run,
                async () => await runOut + await runErr, "the held prompt's stream lines to reach claude-stream.jsonl", ct);

            string zip = Path.Combine(live.OutDir, "held-prompt.zip");
            CliResult bundle = await RunCliAsync(["bundle", live.PlanDir, "--out", zip, "--force-path"], live.ChildEnvironment, ct);
            Assert.True(bundle.ExitCode == 0, $"the held-prompt bundle exited {bundle.ExitCode}:\n{bundle.Error}");

            const string stream = "tasks/02-prompt/attempt-1/claude-stream.jsonl";
            string tail = EntryText(zip, stream);
            Assert.Contains($"held line {LiveRunFixture.HeldPromptLines}", tail, StringComparison.Ordinal);
            Assert.EndsWith("\n", tail, StringComparison.Ordinal);
            Assert.All(tail.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => JsonDocument.Parse(line).Dispose());

            string summary = EntryText(zip, "SUMMARY.md");
            Assert.Contains("### 02-prompt", summary, StringComparison.Ordinal);
            Assert.Contains("- In flight: attempt-1 (phase ", summary, StringComparison.Ordinal);
            Assert.Contains("- In flight for ", summary, StringComparison.Ordinal);
            Assert.Contains("### Live files", summary, StringComparison.Ordinal);
            Assert.Contains($"- live: {stream} — ", summary, StringComparison.Ordinal);
            Assert.Contains($"- {stream}\n", summary.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("### Process tree", summary, StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(live.ReleaseSignal, "go");
            await run.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(3), ct).ContinueWith(_ => { }, TaskScheduler.Default);
            if (!run.HasExited)
            {
                try { run.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            }
        }
    }

    // =====================================================================================================
    // 2. Windows sharing (#727)
    // =====================================================================================================

    /// <summary>
    /// A writer loop rewrites <c>run.json</c>, an attempt's <c>feedback.md</c> and <c>attempt-provenance.json</c>, and a
    /// gate <c>result.json</c> through <see cref="AtomicFile"/> (its <c>onRetry</c> seam counting retries) while the
    /// CLI bundles the plan repeatedly. No write may exhaust its retries. On Windows this is the #727 hazard itself; on
    /// POSIX a rename never collides with a reader, so the same loop proves the bundle still parses a journal that is
    /// being replaced under it.
    /// <para>
    /// <b>This is a SMOKE test, not a proof of contention (#805 W8).</b> Whether a bundle's read handle is open at the
    /// instant a replace runs is a scheduling accident: nothing here can force the overlap, so the test cannot assert
    /// that a retry happened (asserting it would be flaky, and asserting it on POSIX would be false). What it does
    /// assert is that writes and bundles really ran concurrently (<c>writesDuring &gt; 0</c>) and that no write ever
    /// exhausted its retries. The retry count is reported as a diagnostic so a run that did collide is visible. The
    /// deterministic proof of the read discipline is in Core: <c>BundleFileReaderTests</c> drives the sharing-error
    /// retry and backoff through an injected opener.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task BundlesReadingWhileAtomicFileRewritesEveryTarget_NeverExhaustTheReplaceRetries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);

        string[] targets =
        [
            Path.Combine(run.PlanDir, "state", "run.json"),
            run.LogPath("01-work/attempt-1/feedback.md"),
            run.LogPath("01-work/attempt-1/attempt-provenance.json"),
            run.LogPath("guardrails/99-terminal/result.json"),
        ];

        var failures = new ConcurrentQueue<Exception>();
        int writes = 0;
        int retries = 0;
        using var stop = new CancellationTokenSource();
        var firstRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(() =>
        {
            for (int round = 0; !stop.IsCancellationRequested; round++)
            {
                foreach (string target in targets)
                {
                    try
                    {
                        AtomicFile.WriteAllText(target, run.ContentFor(target, round), _ => Interlocked.Increment(ref retries));
                        Interlocked.Increment(ref writes);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Enqueue(ex);
                    }
                }

                firstRound.TrySetResult();
            }
        }, CancellationToken.None);

        await firstRound.Task.WaitAsync(ct);
        int writesAtStart = Volatile.Read(ref writes);
        var exits = new List<(int Exit, string Error)>();
        for (int i = 0; i < 4; i++)
        {
            string zip = Path.Combine(run.OutDir, $"shared-{i}.zip");
            (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip);
            exits.Add((exit, io.ErrorText));
            Assert.True(File.Exists(zip) || exit != 0, $"bundle {i} reported success but wrote no zip");
        }

        int writesDuring = Volatile.Read(ref writes) - writesAtStart;
        await stop.CancelAsync();
        await writer.WaitAsync(ct);

        Assert.All(exits, e => Assert.True(e.Exit == 0, $"a bundle under concurrent writes exited {e.Exit}:\n{e.Error}"));
        Assert.True(failures.IsEmpty, "AtomicFile exhausted its retries while a bundle read: " + string.Join("\n", failures.Select(f => f.Message)));
        Assert.True(writesDuring > 0, "the writer loop never overlapped the bundles, so nothing was proved");
        TestContext.Current.SendDiagnosticMessage($"#799 sharing loop: {writesDuring} atomic writes during 4 bundles, {Volatile.Read(ref retries)} retried replaces");
    }

    /// <summary>A file some other process holds with <c>FileShare.None</c> is excluded and named in MANIFEST.md; the bundle still succeeds.</summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task AFileHeldWithFileShareNone_IsExcludedAndNamed_NotACrash()
    {
        Assert.SkipUnless(Windows,
            "FileShare.None is a mandatory lock only on Windows; on POSIX .NET takes an advisory lock the bundle's shared read does not honor, so there is no sharing violation to exclude. The concurrency loop above still runs on every OS.");

        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string held = run.LogPath("01-work/attempt-1/action-result.json");
        string zip = Path.Combine(run.OutDir, "held.zip");

        int exit;
        StringConsoleIo io;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (exit, io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip);
        }

        Assert.True(exit == 0, io.ErrorText);
        Assert.Equal(("excluded", "sharing-violation"), ManifestRow(EntryText(zip, "MANIFEST.md"), "tasks/01-work/attempt-1/action-result.json"));
        Assert.DoesNotContain("tasks/01-work/attempt-1/action-result.json", EntryNames(zip));
    }

    // =====================================================================================================
    // 3. Path refusal (§17.8)
    // =====================================================================================================

    [Theory]
    [Trait("Category", "Bundle")]
    [InlineData("--out")]
    [InlineData("--dir")]
    public async Task ADestinationInsideAGitWorkingTree_IsRefusedBeforeAnythingIsRead(string flag)
    {
        using var run = new SyntheticRun();
        using var repo = new TempRepo();
        var host = new RecordingHost(run.Home, run.Environment);
        string destination = flag == "--out"
            ? Path.Combine(repo.Path, "evidence", "b.zip")
            : Path.Combine(repo.Path, "evidence-tree");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, flag, destination);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("inside a git working tree", io.ErrorText, StringComparison.Ordinal);
        Assert.Contains("--force-path", io.ErrorText, StringComparison.Ordinal);
        host.AssertNothingWasRead();
        Assert.Equal(new[] { repo.Path }, host.WorkTreeChecks.Select(Path.TrimEndingDirectorySeparator));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "evidence")));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "evidence-tree")));
        Assert.Empty(io.OutText);
    }

    [Theory]
    [Trait("Category", "Bundle")]
    [InlineData("--out")]
    [InlineData("--dir")]
    public async Task ForcePath_AllowsADestinationInsideAGitWorkingTree(string flag)
    {
        using var run = new SyntheticRun();
        using var repo = new TempRepo();
        var host = new RecordingHost(run.Home, run.Environment);
        string destination = flag == "--out"
            ? Path.Combine(repo.Path, "evidence", "b.zip")
            : Path.Combine(repo.Path, "evidence-tree");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, flag, destination, "--force-path");

        Assert.True(exit == 0, io.ErrorText);
        Assert.Empty(host.WorkTreeChecks);
        if (flag == "--out")
        {
            Assert.True(File.Exists(destination));
            Assert.Contains("SUMMARY.md", EntryNames(destination));
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(destination, "SUMMARY.md")));
            Assert.True(File.Exists(Path.Combine(destination, "MANIFEST.md")));
        }

        Assert.StartsWith($"{Path.GetFullPath(destination)} (", io.OutText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task ANonEmptyDir_IsRefusedBeforeAnythingIsRead_AndLeftAlone()
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string tree = Path.Combine(run.OutDir, "occupied");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "keep.txt"), "mine");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--dir", tree);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("is not empty", io.ErrorText, StringComparison.Ordinal);
        host.AssertNothingWasRead();
        Assert.Equal(new[] { "keep.txt" }, Directory.EnumerateFileSystemEntries(tree).Select(Path.GetFileName));
    }

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task TheDefaultDestination_IsGuardrailsBundlesUnderTheInjectedHome()
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir);

        Assert.True(exit == 0, io.ErrorText);
        string expected = Path.Combine(run.Home, BundleCommand.DefaultDirectoryName, $"{SyntheticRun.PlanName}-{SyntheticRun.RunId}.zip");
        Assert.True(File.Exists(expected), $"no bundle at {expected}; stdout: {io.OutText}");
        Assert.StartsWith($"{expected} (", io.OutText, StringComparison.Ordinal);
        Assert.Contains(BundleCommand.UploadHint.Split('\n')[0], io.OutText, StringComparison.Ordinal);
    }

    /// <summary>A dotfiles repository at <c>~</c> makes even the default destination a git working tree: refused.</summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task TheDefaultDestination_UnderAHomeThatIsAGitRepository_IsRefused()
    {
        using var run = new SyntheticRun();
        using var dotfiles = new TempRepo();
        var host = new RecordingHost(dotfiles.Path, run.Environment);

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("inside a git working tree", io.ErrorText, StringComparison.Ordinal);
        host.AssertNothingWasRead();
        Assert.False(Directory.Exists(Path.Combine(dotfiles.Path, BundleCommand.DefaultDirectoryName)));
    }

    // =====================================================================================================
    // 4. D1 end to end (§17.6.5)
    // =====================================================================================================

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task AJudgeOnlyAuthTokenEnvUnsetInTheBundlingShell_RefusesBeforeWriting_NamingTheVariable()
    {
        using var run = new SyntheticRun(judgeBlock: true);
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "d1", "b.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains($"  export {SyntheticRun.JudgeTokenVar} in this shell and re-run `guardrails bundle`", io.ErrorText, StringComparison.Ordinal);
        Assert.Contains("--without-agent-text", io.ErrorText, StringComparison.Ordinal);
        Assert.False(File.Exists(zip));
        Assert.False(Directory.Exists(Path.GetDirectoryName(zip)));
        Assert.Equal(0, host.ToolVersionReads);
        Assert.Equal(0, host.GitCalls);
        Assert.Empty(io.OutText);

        // The same shell WITH the variable is not refused: the refusal is about this variable, not the block.
        var withToken = new Dictionary<string, string>(run.Environment) { [SyntheticRun.JudgeTokenVar] = "judge-token-value-000111" };
        (int exitWith, StringConsoleIo ioWith) = await InvokeBundleAsync(
            new RecordingHost(run.Home, withToken).Host, "bundle", run.PlanDir, "--out", zip);
        Assert.True(exitWith == 0, ioWith.ErrorText);
    }

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task WithoutAgentText_ShipsPastD1_WithTheFreeTextCanaryAbsentEverywhere()
    {
        using var run = new SyntheticRun(judgeBlock: true);
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "wat.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip, "--without-agent-text");

        Assert.True(exit == 0, io.ErrorText);
        byte[] raw = File.ReadAllBytes(zip);
        Assert.False(ContainsUtf8(raw, SyntheticRun.FreeTextCanary), "the free-text canary is in the raw zip bytes");
        Dictionary<string, byte[]> entries = Entries(zip);
        Assert.All(entries, e => Assert.False(
            Encoding.UTF8.GetString(e.Value).Contains(SyntheticRun.FreeTextCanary, StringComparison.Ordinal),
            $"{e.Key} still carries agent text"));

        // The canary WAS planted where the design says it would otherwise ship: this is what the flag removed.
        (int fullExit, StringConsoleIo fullIo) = await InvokeBundleAsync(
            new RecordingHost(run.Home, new Dictionary<string, string>(run.Environment) { [SyntheticRun.JudgeTokenVar] = "judge-token-value-000111" }).Host,
            "bundle", run.PlanDir, "--out", Path.Combine(run.OutDir, "control.zip"));
        Assert.True(fullExit == 0, fullIo.ErrorText);
        Dictionary<string, byte[]> control = Entries(Path.Combine(run.OutDir, "control.zip"));
        foreach (string path in SyntheticRun.AgentTextFiles.Append("state/run.json"))
        {
            Assert.True(control.TryGetValue(path, out byte[]? bytes), $"control bundle lacks {path}");
            Assert.Contains(SyntheticRun.FreeTextCanary, Encoding.UTF8.GetString(bytes!), StringComparison.Ordinal);
        }

        // Structured facts remain; reason fields are withheld, not dropped; the forcing variable is named.
        string journal = Encoding.UTF8.GetString(entries["state/run.json"]);
        Assert.Contains(AgentTextProjection.Withheld, journal, StringComparison.Ordinal);
        Assert.Contains("\"01-work\"", journal, StringComparison.Ordinal);
        Assert.True(entries.ContainsKey("tasks/01-work/attempt-1/attempt-route.log"));
        Assert.Contains(AgentTextProjection.Withheld, Encoding.UTF8.GetString(entries["gates/guardrails/99-terminal/result.json"]), StringComparison.Ordinal);
        string manifest = Encoding.UTF8.GetString(entries["MANIFEST.md"]);
        foreach (string path in SyntheticRun.AgentTextFiles)
        {
            Assert.Equal(("withheld", "agent-text"), ManifestRow(manifest, path));
        }

        Assert.Contains(SyntheticRun.JudgeTokenVar, Encoding.UTF8.GetString(entries["SUMMARY.md"]), StringComparison.Ordinal);
    }

    // =====================================================================================================
    // 5. Content default vs --lean (§17.3, §17.9)
    // =====================================================================================================

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task ADefaultBundle_IncludesTheFullClassRedacted_AndWarnsOnStderrAndFirstInSummary()
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "full.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip);

        Assert.True(exit == 0, io.ErrorText);
        Assert.Contains(BundleBuilder.FullBundleWarning, io.ErrorText.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Dictionary<string, byte[]> entries = Entries(zip);
        Assert.StartsWith(BundleBuilder.FullBundleWarning, Encoding.UTF8.GetString(entries["SUMMARY.md"]), StringComparison.Ordinal);
        foreach (string path in SyntheticRun.FullClassFiles)
        {
            Assert.True(entries.TryGetValue(path, out byte[]? bytes), $"{path} missing from a default (full) bundle");
            string text = Encoding.UTF8.GetString(bytes!);
            Assert.Contains($"[REDACTED:{SyntheticRun.SecretVar}#1]", text, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticRun.Secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task Lean_WithholdsEveryFullClassFile_NamingEachInTheManifest()
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "lean.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip, "--lean");

        Assert.True(exit == 0, io.ErrorText);
        Dictionary<string, byte[]> entries = Entries(zip);
        string manifest = Encoding.UTF8.GetString(entries["MANIFEST.md"]);
        foreach (string path in SyntheticRun.FullClassFiles)
        {
            Assert.False(entries.ContainsKey(path), $"{path} shipped in a --lean bundle");
            Assert.Equal(("withheld", "lean"), ManifestRow(manifest, path));
        }

        Assert.True(entries.ContainsKey("tasks/01-work/attempt-1/feedback.md"));
        string note = BundleBuilder.LeanNote(SyntheticRun.FullClassFiles.Length);
        Assert.Contains(note, io.ErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING: this bundle includes", io.ErrorText, StringComparison.Ordinal);
        Assert.StartsWith(note, Encoding.UTF8.GetString(entries["SUMMARY.md"]), StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Bundle")]
    [InlineData("../elsewhere")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task APathLikeRunId_IsRefusedBeforeAnythingIsRead(string runId)
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "refused", "b.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip, "--run", runId);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("refused: --run must be a single run id", io.ErrorText, StringComparison.Ordinal);
        host.AssertNothingWasRead();
        Assert.False(Directory.Exists(Path.GetDirectoryName(zip)));
    }

    [Theory]
    [Trait("Category", "Bundle")]
    [InlineData("--out")]
    [InlineData("--dir")]
    public async Task ADestinationUnderThePlanDirectory_IsRefusedEvenWithForcePath(string flag)
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string destination = flag == "--out"
            ? Path.Combine(run.PlanDir, "evidence", "b.zip")
            : Path.Combine(run.PlanDir, "evidence-tree");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, flag, destination, "--force-path");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("is under the plan directory", io.ErrorText, StringComparison.Ordinal);
        host.AssertNothingWasRead();
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(Path.Combine(run.PlanDir, flag == "--out" ? "evidence" : "evidence-tree")));
    }

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task LeanWithIncludeWorktreeDiff_IsRefusedBeforeAnythingIsRead()
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "refused", "b.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(
            host.Host, "bundle", run.PlanDir, "--out", zip, "--lean", "--include-worktree-diff");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("refused: --include-worktree-diff", io.ErrorText, StringComparison.Ordinal);
        host.AssertNothingWasRead();
        Assert.Empty(host.WorkTreeChecks);
        Assert.False(Directory.Exists(Path.GetDirectoryName(zip)));
    }

    // =====================================================================================================
    // 6. Zip hygiene
    // =====================================================================================================

    /// <summary>
    /// Every planted credential is absent from the RAW zip bytes (entry names and the central directory are stored
    /// uncompressed, so a secret in a file NAME would show here even though every entry body is deflated) and from
    /// every inflated entry, in each spelling the fixture wrote it: raw, System.Text.Json-escaped, and URL-encoded.
    /// </summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task EveryPlantedCredential_IsAbsentFromTheRawZipBytesAndEveryEntry()
    {
        using var run = new SyntheticRun();
        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "hygiene.zip");

        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip);

        Assert.True(exit == 0, io.ErrorText);
        byte[] raw = File.ReadAllBytes(zip);
        Dictionary<string, byte[]> entries = Entries(zip);
        foreach (string spelling in SyntheticRun.CredentialSpellings)
        {
            Assert.False(ContainsUtf8(raw, spelling), $"'{spelling}' is in the raw zip bytes");
            foreach ((string path, byte[] bytes) in entries)
            {
                Assert.False(ContainsUtf8(bytes, spelling), $"'{spelling}' survived in {path}");
            }
        }

        // The credentials were really planted in what shipped: each labelled where it was.
        string transcript = Encoding.UTF8.GetString(entries["tasks/01-work/attempt-1/transcript.md"]);
        Assert.Contains($"[REDACTED:{SyntheticRun.SecretVar}#1]", transcript, StringComparison.Ordinal);
        Assert.Contains($"[REDACTED:{SyntheticRun.PlanLiteralVar}#", transcript, StringComparison.Ordinal);
        Assert.Contains($"[REDACTED:{SyntheticRun.SecretVar}#1]", Encoding.UTF8.GetString(entries["state/run.json"]), StringComparison.Ordinal);
    }

    /// <summary>
    /// Entry names are canaries too (#799 review): Claude Code names a session directory after the cwd with every
    /// separator turned into <c>-</c>, so the directory name alone carries the home path and the user name — and a
    /// known secret, if one is in the cwd. None of it may reach an entry name (stored uncompressed, so visible in the
    /// raw bytes) or the MANIFEST.
    /// </summary>
    [Fact]
    [Trait("Category", "Bundle")]
    public async Task SessionDirectoryNames_NeverReachAnEntryNameOrTheManifest()
    {
        using var run = new SyntheticRun();
        string encodedHome = new([.. run.Home.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        string withHome = encodedHome + "-src-app";
        string withSecret = "-work-" + SyntheticRun.Secret + "-repo";
        foreach (string dir in new[] { withHome, withSecret })
        {
            string file = run.LogPath($"claude-config/projects/{dir}/entry-name-canary.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{\"text\":\"session\"}\n");
        }

        var host = new RecordingHost(run.Home, run.Environment);
        string zip = Path.Combine(run.OutDir, "entry-names.zip");
        (int exit, StringConsoleIo io) = await InvokeBundleAsync(host.Host, "bundle", run.PlanDir, "--out", zip);

        Assert.True(exit == 0, io.ErrorText);
        byte[] raw = File.ReadAllBytes(zip);
        Dictionary<string, byte[]> entries = Entries(zip);
        string manifest = EntryText(zip, "MANIFEST.md");
        List<string> leaks = [withHome, withSecret, encodedHome, SyntheticRun.Secret];
        if (System.Environment.UserName.Length >= 4)
        {
            leaks.Add(System.Environment.UserName);
        }

        foreach (string leak in leaks)
        {
            Assert.False(ContainsUtf8(raw, leak), $"'{leak}' is in the raw zip bytes (an entry name?)");
            Assert.DoesNotContain(entries.Keys, name => name.Contains(leak, StringComparison.Ordinal));
            Assert.DoesNotContain(leak, manifest, StringComparison.Ordinal);
        }

        Assert.Equal(2, entries.Keys.Count(name => name.EndsWith("/entry-name-canary.jsonl", StringComparison.Ordinal)));
        Assert.All(entries.Keys.Where(name => name.EndsWith("/entry-name-canary.jsonl", StringComparison.Ordinal)),
            name => Assert.Matches("^gateway/sessions/project-[0-9]+/entry-name-canary\\.jsonl$", name));
    }

    [Fact]
    [Trait("Category", "Bundle")]
    public async Task IdenticalStateAndInjectedProbes_GiveByteIdenticalZips()
    {
        using var run = new SyntheticRun();
        string first = Path.Combine(run.OutDir, "first.zip");
        string second = Path.Combine(run.OutDir, "second.zip");

        (int exit1, StringConsoleIo io1) = await InvokeBundleAsync(new RecordingHost(run.Home, run.Environment).Host, "bundle", run.PlanDir, "--out", first);
        (int exit2, StringConsoleIo io2) = await InvokeBundleAsync(new RecordingHost(run.Home, run.Environment).Host, "bundle", run.PlanDir, "--out", second);

        Assert.True(exit1 == 0, io1.ErrorText);
        Assert.True(exit2 == 0, io2.ErrorText);
        byte[] a = File.ReadAllBytes(first);
        byte[] b = File.ReadAllBytes(second);
        if (!a.AsSpan().SequenceEqual(b))
        {
            Dictionary<string, byte[]> ea = Entries(first);
            Dictionary<string, byte[]> eb = Entries(second);
            string[] differing = [.. ea.Keys.Union(eb.Keys).Where(k => !ea.TryGetValue(k, out byte[]? x) || !eb.TryGetValue(k, out byte[]? y) || !x.AsSpan().SequenceEqual(y))];
            Assert.Fail("two bundles of identical state differ in: " + string.Join(", ", differing));
        }

        Assert.Contains("- Liveness: Running (owner process 4242 is alive)", EntryText(first, "SUMMARY.md"), StringComparison.Ordinal);
    }

    // =====================================================================================================
    // Fixtures and helpers
    // =====================================================================================================

    private sealed record CliResult(int ExitCode, string Output, string Error);

    /// <summary>The shipped CLI as a separate OS process (the idiom <c>CrossProcessLivenessTests</c> uses).</summary>
    private static ProcessStartInfo CliStartInfo(IEnumerable<string> args, IReadOnlyDictionary<string, string> environment)
    {
        string appHost = Path.Combine(AppContext.BaseDirectory, Windows ? "Guardrails.Cli.exe" : "Guardrails.Cli");
        ProcessStartInfo psi = File.Exists(appHost) ? new ProcessStartInfo(appHost) : new ProcessStartInfo("dotnet");
        if (!File.Exists(appHost))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Guardrails.Cli.dll"));
        }

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;

        // An enclosing `guardrails run` (a plan preflight running this suite) must not leak its GUARDRAILS_* in.
        foreach (string key in psi.Environment.Keys.Where(k => k.StartsWith("GUARDRAILS_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            psi.Environment.Remove(key);
        }

        foreach ((string key, string value) in environment)
        {
            psi.Environment[key] = value;
        }

        return psi;
    }

    private static async Task<CliResult> RunCliAsync(IEnumerable<string> args, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        using Process process = Process.Start(CliStartInfo(args, environment))
            ?? throw new InvalidOperationException("the guardrails CLI did not start");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<(int ExitCode, StringConsoleIo Io)> InvokeBundleAsync(BundleCommandHost host, params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(BundleCommand.Create(io, host));
        int exit = await root.Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, io);
    }

    /// <summary>
    /// A cross-process gate: poll a condition another process makes true. The deadline is a hang guard only; nothing
    /// asserts how long the wait took.
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition, Process mustStayAlive, Func<Task<string>> output, string what, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        while (!condition())
        {
            if (mustStayAlive.HasExited)
            {
                throw new InvalidOperationException(
                    $"the run exited ({mustStayAlive.ExitCode}) before {what}:\n{await mustStayAlive.StandardOutput.ReadToEndAsync(ct)}\n{await mustStayAlive.StandardError.ReadToEndAsync(ct)}");
            }

            await Task.Delay(50, deadline.Token);
        }
    }

    /// <summary>Every file under the roots: path → length, last-write ticks and SHA-256.</summary>
    private static Dictionary<string, string> Snapshot(params string[] roots)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                string hash;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }

                files[file] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{hash}";
            }

            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                files[dir + Path.DirectorySeparatorChar] = "dir";
            }
        }

        return files;
    }

    private static void AssertSnapshotsEqual(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        string[] added = [.. after.Keys.Except(before.Keys).Order(StringComparer.Ordinal)];
        string[] removed = [.. before.Keys.Except(after.Keys).Order(StringComparer.Ordinal)];
        string[] changed = [.. before.Keys.Intersect(after.Keys).Where(k => before[k] != after[k]).Order(StringComparer.Ordinal)];
        Assert.True(added.Length + removed.Length + changed.Length == 0,
            "the bundle changed the run's files:\n  added: " + string.Join(", ", added)
            + "\n  removed: " + string.Join(", ", removed) + "\n  changed: " + string.Join(", ", changed));
    }

    private static Dictionary<string, byte[]> Entries(string zipPath)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            entries[entry.FullName] = buffer.ToArray();
        }

        return entries;
    }

    private static IReadOnlyCollection<string> EntryNames(string zipPath) => Entries(zipPath).Keys;

    private static string EntryText(string zipPath, string entry) =>
        Entries(zipPath).TryGetValue(entry, out byte[]? bytes)
            ? Encoding.UTF8.GetString(bytes)
            : throw new InvalidOperationException($"{zipPath} has no {entry}");

    private static bool ContainsUtf8(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    /// <summary>(status, reason) of the MANIFEST.md row whose bundle path is <paramref name="bundlePath"/>.</summary>
    private static (string Status, string Reason) ManifestRow(string manifest, string bundlePath)
    {
        string? line = manifest.Split('\n').FirstOrDefault(l => l.StartsWith($"| {bundlePath} |", StringComparison.Ordinal));
        Assert.True(line is not null, $"MANIFEST.md has no row for {bundlePath}:\n{manifest}");
        string[] cells = line!.Trim().Trim('|').Split(" | ");
        return (cells[^2].Trim(), cells[^1].Trim());
    }

    /// <summary>A <see cref="BundleCommandHost"/> whose every machine question is answered here, and counted.</summary>
    private sealed class RecordingHost
    {
        private int _environmentReads;
        private int _clockReads;
        private int _toolVersionReads;
        private readonly CountingGit _git = new();
        private readonly FixedProbe _probe = new();

        public RecordingHost(string home, IReadOnlyDictionary<string, string> environment)
        {
            Host = new BundleCommandHost
            {
                HomeDirectory = () => home,
                Environment = () =>
                {
                    Interlocked.Increment(ref _environmentReads);
                    return environment;
                },
                Now = () =>
                {
                    Interlocked.Increment(ref _clockReads);
                    return SyntheticRun.Clock;
                },
                ToolVersions = () =>
                {
                    Interlocked.Increment(ref _toolVersionReads);
                    return [new("claude", "2.0.0 (Claude Code)"), new("agent", "not on PATH"), new("dotnet", "10.0.100"), new("git", "git version 2.50.0")];
                },
                ProcessProbe = _probe,
                Git = _git,
                // The REAL `git rev-parse --is-inside-work-tree`, recorded: the refusal is proved against real git.
                IsInsideGitWorkTree = directory =>
                {
                    lock (WorkTreeChecks)
                    {
                        WorkTreeChecks.Add(directory);
                    }

                    return BundleCommandHost.Real.IsInsideGitWorkTree(directory);
                },
                // Never mutate the test process's environment: compute the plan, apply nothing.
                ApplyGitEnvironment = () => BundleGitEnvironment.Compute(_ => null),

                // The fixed probe says the owner (4242) is Running, so SUMMARY asks for its process tree. The real table
                // would differ bundle to bundle; a fixed answer keeps two bundles of one state byte-identical.
                ProcessTree = pid => new BundleProcessTree([new BundleProcessRow(pid, 1, "S", "00:05:00", "0.0", "guardrails run plan-b")], null),
            };
        }

        public BundleCommandHost Host { get; }

        public List<string> WorkTreeChecks { get; } = [];

        public int ToolVersionReads => Volatile.Read(ref _toolVersionReads);

        public int GitCalls => _git.Calls;

        /// <summary>Nothing the bundle reads about the run or the machine was asked for.</summary>
        public void AssertNothingWasRead()
        {
            Assert.Equal(0, Volatile.Read(ref _environmentReads));
            Assert.Equal(0, Volatile.Read(ref _clockReads));
            Assert.Equal(0, Volatile.Read(ref _toolVersionReads));
            Assert.Equal(0, _git.Calls);
            Assert.Equal(0, _probe.Calls);
        }

        private sealed class CountingGit : IBundleGit
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public BundleProcessResult Run(string workingDirectory, IReadOnlyList<string> arguments)
            {
                Interlocked.Increment(ref _calls);
                string output = arguments[0] switch
                {
                    "status" => "## guardrails/plan-b\n M src/app.cs\n",
                    "log" => "abc1234 Sat Sep 27 10:00:00 2026 fix the thing\n",
                    "diff" => " src/app.cs | 2 +-\n 1 file changed\n",
                    _ => string.Empty,
                };
                return new BundleProcessResult(0, output, string.Empty, TimedOut: false, NotFound: false);
            }
        }

        private sealed class FixedProbe : IProcessProbe
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public ProcessCheck Check(RunOwner owner)
            {
                Interlocked.Increment(ref _calls);
                return ProcessCheck.Running;
            }
        }
    }

    /// <summary>A throwaway <c>git init</c>-ed directory with one commit.</summary>
    private sealed class TempRepo : IDisposable
    {
        public TempRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gr-bundle-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Git(Path, "init");
            Git(Path, "config", "user.email", "test@guardrails.local");
            Git(Path, "config", "user.name", "Guardrails Test");
            File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "# bundle path refusal\n");
            Git(Path, "add", ".");
            Git(Path, "commit", "-m", "Initial commit");
        }

        public string Path { get; }

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)!;
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} (in {workingDir}) exited {process.ExitCode}: {stderr.Result.Trim()}");
            }

            return stdout;
        }

        public void Dispose() => DeleteTree(Path);
    }

    private static void DeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            // .git/objects files are read-only on Windows.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort teardown of a temp directory.
        }
    }

    private static void WriteScript(string path, string content)
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

    /// <summary>
    /// A loadable plan (<c>plan-b</c>) with a hand-written journal and a run's logs, every artifact kind carrying the
    /// planted canaries: a known-value credential from the bundling shell (<see cref="Secret"/>, which contains
    /// <c>+</c> so System.Text.Json escapes it), a plan-literal credential from <c>task.json</c>'s <c>env</c>
    /// (<see cref="PlanLiteral"/>), a GitHub-token-shaped string (<see cref="PatternSecret"/>), and a lower-case
    /// free-text marker (<see cref="FreeTextCanary"/>) that no redaction pass touches, for <c>--without-agent-text</c>.
    /// </summary>
    private sealed class SyntheticRun : IDisposable
    {
        public const string PlanName = "plan-b";
        public const string RunId = "2026-09-27T10-00-00Z-cd34";
        public const string SecretVar = "GR_IT_SERVICE_TOKEN";
        public const string Secret = "Kq7v+R2mXz9LpW4tN8bYc31";
        public const string PlanLiteralVar = "GR_IT_PLAN_TOKEN";
        public const string PlanLiteral = "Vb3nQ8rTz1YwK6pHs2LmD7";
        public const string PatternSecret = "ghp_R7tYu2Wq9ZxC4vBn6Mk1Lp8Hj3Gf5Ds0AaEe";
        public const string FreeTextCanary = "zebra-quartz-canary-7731";
        public const string JudgeTokenVar = "GR_IT_JUDGE_KEY";
        public static readonly DateTimeOffset Clock = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        public static readonly string[] FullClassFiles =
        [
            "tasks/01-work/attempt-1/transcript.md",
            "tasks/01-work/attempt-1/claude-stream.jsonl",
            "tasks/01-work/attempt-1/composed-prompt.md",
            "tasks/01-work/attempt-1/prior-attempt.patch",
            "gateway/sessions/project-1/session-1.jsonl", // the only project dir, renumbered (its name encodes a path)
            "gateway/sessions/01-work/attempt-1/project-1/session-2.jsonl",
        ];

        /// <summary>Agent-text files that ship in a default bundle and are withheld under <c>--without-agent-text</c>.</summary>
        public static readonly string[] AgentTextFiles =
        [
            .. FullClassFiles,
            "tasks/01-work/attempt-1/feedback.md",
            "tasks/01-work/attempt-1/action-result.json",
            "tasks/01-work/attempt-1/guardrail-01-ok.stdout.log",
            "tasks/01-work/attempt-1/guardrail-01-ok.verdict.json",
            "tasks/01-work/feedback.md",
            "tasks/01-work/triage.json",
            "tasks/01-work/overwatch.jsonl",
            "run/events.jsonl",
            "run/observer.jsonl",
            "run/autonomy.jsonl",
            "run/escalations/0001-needs-human.json",
            "gates/guardrails/99-terminal/stdout.log",
            "gates/guardrails/99-terminal/stderr.log",
            "plan/tasks/01-work/task.json",
        ];

        /// <summary>Every spelling of every credential the fixture wrote.</summary>
        public static readonly string[] CredentialSpellings =
        [
            Secret, Secret.Replace("+", "\\u002B", StringComparison.Ordinal), Uri.EscapeDataString(Secret),
            PlanLiteral, PatternSecret,
        ];

        private readonly string _root;

        public SyntheticRun(bool judgeBlock = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-bundle-cli-" + Guid.NewGuid().ToString("N"));
            PlanDir = Path.Combine(_root, "ws", PlanName);
            Home = Path.Combine(_root, "home");
            OutDir = Path.Combine(_root, "out");
            Directory.CreateDirectory(PlanDir);
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(OutDir);

            string judge = judgeBlock
                ? ",\n    \"judge\": { \"baseUrl\": \"http://127.0.0.1:9\", \"model\": \"Qwen\", \"authTokenEnv\": \"" + JudgeTokenVar + "\" }"
                : string.Empty;
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                $$"""
                {
                  "version": 1,
                  "workspace": "..",
                  "defaultRetries": 0,
                  "maxParallelism": 1,
                  "promptRunners": {
                    "default": "claude",
                    "claude": { "command": "claude" }{{judge}}
                  }
                }
                """);

            string taskDir = Path.Combine(PlanDir, "tasks", "01-work");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""
                {
                  "description": "work task; {{FreeTextCanary}}",
                  "writeScope": ["src/**"],
                  "action": { "path": "action.prompt.md", "env": { "{{PlanLiteralVar}}": "{{PlanLiteral}}" } }
                }
                """);
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the work.\n");
            WriteScript(Path.Combine(taskDir, "guardrails", Windows ? "01-ok.ps1" : "01-ok.sh"),
                Windows ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            if (judgeBlock)
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "02-judge.prompt.md"), "---\nrunner: judge\n---\nJudge the work.\n");
            }

            WriteJournal(0);
            WriteLogs();
        }

        public string PlanDir { get; }

        public string Home { get; }

        public string OutDir { get; }

        /// <summary>The bundling shell: the service token is set (the known-value pass's source).</summary>
        public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal) { [SecretVar] = Secret };

        public string LogPath(string relative) =>
            Path.Combine(PlanDir, "logs", RunId, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>The content a writer loop puts at <paramref name="target"/> on round <paramref name="round"/>.</summary>
        public string ContentFor(string target, int round) => Path.GetFileName(target) switch
        {
            "run.json" => BuildJournal(round),
            "feedback.md" => $"Guardrail 01-ok failed (round {round}): {Planted}\n",
            "attempt-provenance.json" => $"{{\"model\":\"fixture-model\",\"runner\":\"claude\",\"summary\":\"round {round}\"}}\n",
            _ => $"{{\"name\":\"99-terminal\",\"passed\":false,\"exitCode\":1,\"reason\":\"round {round}\"}}\n",
        };

        private static string Planted => $"{FreeTextCanary} saw {Secret} and {PlanLiteral} and {PatternSecret}";

        private void WriteJournal(int round) =>
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(PlanDir, "state")).FullName, "run.json"), BuildJournal(round));

        private static string BuildJournal(int round)
        {
            var document = new JournalDocument
            {
                RunId = RunId,
                PlanHash = $"sha256:plan-{round}",
                Tasks = new Dictionary<string, TaskJournalEntry>
                {
                    ["01-work"] = new()
                    {
                        Status = JournalTaskStatus.NeedsHuman,
                        Attempts =
                        [
                            new AttemptRecord
                            {
                                Attempt = 1,
                                StartedAt = Clock.AddMinutes(-30),
                                EndedAt = Clock.AddMinutes(-27),
                                ActionExitCode = 0,
                                Outcome = AttemptOutcome.GuardrailFailed,
                                FailedGuardrails = [new FailedGuardrail { Name = "01-ok", Reason = Planted }],
                                LogDir = $"logs/{RunId}/01-work/attempt-1",
                                Provenance = new AttemptProvenance { Model = "fixture-model", Runner = "claude" },
                            },
                        ],
                    },
                },
                Halt = new RunHalt
                {
                    Kind = RunHaltKind.PlanGuardrailFailed,
                    HaltedAt = Clock.AddMinutes(-26),
                    Headline = Planted,
                    FailedChecks = [new FailedGuardrail { Name = "99-terminal", Reason = Planted }],
                },
                Owner = new RunOwner { Pid = 4242, ProcessStartedAt = Clock.AddHours(-1), Host = RunLiveness.ThisHost() },
                Environment = new RunEnvironment { HarnessVersion = "1.26.0", Os = "FixtureOS 1", Host = RunLiveness.ThisHost() },
            };
            return JsonSerializer.Serialize(document, JournalJson.Options);
        }

        private void Log(string relative, string content)
        {
            string path = LogPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private void WriteLogs()
        {
            string planted = Planted;
            string json = JsonSerializer.Serialize(planted); // System.Text.Json escaping: '+' becomes +
            const string attempt = "01-work/attempt-1/";
            Log(attempt + "transcript.md", $"I ran the tool and saw {planted}.\n");
            Log(attempt + "claude-stream.jsonl",
                $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"I ran the tool and saw {planted}.\"}}]}}}}\n");
            Log(attempt + "composed-prompt.md", $"Do the work. Context: {planted}\n");
            Log(attempt + "prior-attempt.patch", $"--- a/src/app.cs\n+++ b/src/app.cs\n@@ -1 +1 @@\n-old\n+{planted}\n");
            Log(attempt + "feedback.md", $"Guardrail 01-ok failed: {planted}\n");
            Log(attempt + "attempt-provenance.json", $"{{\"model\":\"fixture-model\",\"runner\":\"claude\",\"summary\":{json}}}\n");
            Log(attempt + "attempt-route.log", "runner: claude\nmodel: fixture-model\n");
            Log(attempt + "action-result.json", $"{{\"kind\":\"prompt\",\"exitCode\":0,\"summary\":{json}}}\n");
            Log(attempt + "guardrail-01-ok.stdout.log", $"check output {planted}\n");
            Log(attempt + "guardrail-01-ok.verdict.json", $"{{\"pass\":false,\"reason\":{json}}}\n");
            Log(attempt + "claude-config/projects/proj-b/session-2.jsonl", $"{{\"text\":{json}}}\n");
            Log("01-work/feedback.md", $"Latest failure: {planted}\n");
            Log("01-work/triage.json", $"{{\"summary\":{json}}}\n");
            Log("01-work/overwatch.jsonl", $"{{\"note\":{json}}}\n");
            Log("events.jsonl", $"{{\"kind\":\"task-finished\",\"detail\":{json}}}\n");
            Log("observer.jsonl", $"{{\"member\":\"TaskFinished\",\"detail\":{json}}}\n");
            Log("autonomy.jsonl", $"{{\"decision\":\"halt\",\"detail\":{json}}}\n");
            Log("escalations/0001-needs-human.json", $"{{\"reason\":{json}}}\n");
            Log("guardrails/99-terminal/stdout.log", $"terminal gate says {planted}\n");
            Log("guardrails/99-terminal/stderr.log", $"terminal gate error {planted}\n");
            Log("guardrails/99-terminal/result.json", $"{{\"name\":\"99-terminal\",\"passed\":false,\"exitCode\":1,\"reason\":{json}}}\n");
            Log("claude-config/projects/proj-a/session-1.jsonl", $"{{\"text\":\"{planted}\"}}\n");
            Log("claude-config/.claude.json", "{\"userID\":\"private-config\"}\n");
        }

        public void Dispose() => DeleteTree(_root);
    }

    /// <summary>
    /// A real worktree-mode plan in a real git repository for the live test: <c>01-held</c> (script, blocks on a
    /// release file) → <c>02-prompt</c> (prompt, fake Claude CLI) → <c>03-third</c> → <c>04-fourth</c> (scripts). Each
    /// writes one file under <c>src/</c> so every settle commits and merges.
    /// </summary>
    private sealed class LiveRunFixture : IDisposable
    {
        public static readonly string[] TaskIds = ["01-held", "02-prompt", "03-third", "04-fourth"];

        private readonly string _root;

        /// <summary>How many stream lines the held fake Claude emits before it blocks (#805 W7).</summary>
        public const int HeldPromptLines = 5;

        public LiveRunFixture(bool holdPrompt = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-bundle-live-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "wt");
            OutDir = Path.Combine(_root, "out");
            string gate = Path.Combine(_root, "gate");
            string fake = Path.Combine(_root, "fake");
            foreach (string dir in new[] { RepoPath, WorktreeRoot, OutDir, gate, fake })
            {
                Directory.CreateDirectory(dir);
            }

            StartedSignal = Path.Combine(gate, "started");
            ReleaseSignal = Path.Combine(gate, "release");
            PlanDir = Path.Combine(RepoPath, "plan");

            TempRepo.Git(RepoPath, "init");
            TempRepo.Git(RepoPath, "config", "user.email", "test@guardrails.local");
            TempRepo.Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# bundle live test\n");

            string fakeClaude = holdPrompt ? WriteHeldFakeClaude(fake, ReleaseSignal) : WriteFakeClaude(fake);
            Directory.CreateDirectory(PlanDir);
            File.WriteAllText(Path.Combine(PlanDir, PlanGitignore.FileName), PlanGitignore.Content);
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                $$"""
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": "..",
                  "defaultRetries": 0,
                  "maxParallelism": 2,
                  "defaultTimeoutSeconds": 300,
                  "promptRunners": {
                    "default": "claude",
                    "claude": {
                      "command": "{{fakeClaude.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                      "permissionMode": "acceptEdits",
                      "allowedTools": ["Read", "Write"],
                      "maxTurns": 5
                    }
                  }
                }
                """);

            WriteScriptTask("01-held", [], holdPrompt ? WriteFileBody("01-held") : HeldActionBody(StartedSignal, ReleaseSignal));
            WritePromptTask("02-prompt", ["01-held"]);
            WriteScriptTask("03-third", ["02-prompt"], WriteFileBody("03-third"));
            WriteScriptTask("04-fourth", ["03-third"], WriteFileBody("04-fourth"));

            TempRepo.Git(RepoPath, "add", ".");
            TempRepo.Git(RepoPath, "commit", "-m", "Add the bundle live-test plan");
        }

        public string RepoPath { get; }

        public string PlanDir { get; }

        public string WorktreeRoot { get; }

        public string OutDir { get; }

        public string StartedSignal { get; }

        public string ReleaseSignal { get; }

        /// <summary>The run and every bundle share one worktree root, so the bundle anonymizes and reads the run's own.</summary>
        public IReadOnlyDictionary<string, string> ChildEnvironment =>
            new Dictionary<string, string>(StringComparer.Ordinal) { [SchedulerFactory.WorktreeRootEnvVar] = WorktreeRoot };

        /// <summary>The lines the harness has written so far to 02-prompt's first attempt's stream log (any run id).</summary>
        public int PromptStreamLines()
        {
            string logs = Path.Combine(PlanDir, "logs");
            if (!Directory.Exists(logs))
            {
                return 0;
            }

            foreach (string runDir in Directory.EnumerateDirectories(logs))
            {
                string stream = Path.Combine(runDir, "02-prompt", "attempt-1", "claude-stream.jsonl");
                if (File.Exists(stream))
                {
                    using var reader = new StreamReader(new FileStream(stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    return reader.ReadToEnd().Split('\n').Count(l => l.Contains("held line", StringComparison.Ordinal));
                }
            }

            return 0;
        }

        public Process StartRun()
        {
            Process process = Process.Start(CliStartInfo(["run", PlanDir, "--no-ui"], ChildEnvironment))
                ?? throw new InvalidOperationException("guardrails run did not start");
            process.StandardInput.Close();
            return process;
        }

        private void WriteScriptTask(string id, string[] dependsOn, string actionBody)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            WriteTaskJson(taskDir, id, dependsOn, action: null);
            WriteScript(Path.Combine(taskDir, Windows ? "action.ps1" : "action.sh"), actionBody);
            WriteGuardrail(taskDir);
        }

        private void WritePromptTask(string id, string[] dependsOn)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            WriteTaskJson(taskDir, id, dependsOn, action: "action.prompt.md");
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Write src/02-prompt.txt.\n");
            WriteGuardrail(taskDir);
        }

        private static void WriteTaskJson(string taskDir, string id, string[] dependsOn, string? action)
        {
            Directory.CreateDirectory(taskDir);
            string depends = "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
            string actionJson = action is null ? string.Empty : $",\n  \"action\": {{ \"path\": \"{action}\" }}";
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{\n  \"description\": \"bundle live test {id}\",\n  \"writeScope\": [\"src/**\"],\n  \"dependsOn\": {depends}{actionJson}\n}}\n");
        }

        private static void WriteGuardrail(string taskDir) =>
            WriteScript(Path.Combine(taskDir, "guardrails", Windows ? "01-check.ps1" : "01-check.sh"),
                Windows ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");

        private static string WriteFileBody(string id) => Windows
            ? $"New-Item -ItemType Directory -Force -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'src') | Out-Null\n"
              + $"Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'src/{id}.txt') -Value '{id}'\nexit 0\n"
            : $"#!/usr/bin/env bash\nmkdir -p \"$GUARDRAILS_WORKSPACE/src\"\nprintf '{id}' > \"$GUARDRAILS_WORKSPACE/src/{id}.txt\"\nexit 0\n";

        /// <summary>Write its file, signal <c>started</c>, then hold until <c>release</c> exists (3-minute hang guard).</summary>
        private static string HeldActionBody(string started, string release) => Windows
            ? WriteFileBody("01-held").Replace("exit 0\n", string.Empty, StringComparison.Ordinal)
              + $"Set-Content -NoNewline -Path '{started}' -Value 'x'\n"
              + "$deadline = (Get-Date).AddMinutes(3)\n"
              + $"while (-not (Test-Path '{release}')) {{ if ((Get-Date) -gt $deadline) {{ exit 3 }}; Start-Sleep -Milliseconds 50 }}\n"
              + "exit 0\n"
            : WriteFileBody("01-held").Replace("exit 0\n", string.Empty, StringComparison.Ordinal)
              + $"printf 'x' > '{started}'\n"
              + $"for i in $(seq 1 3600); do [ -e '{release}' ] && exit 0; sleep 0.05; done\n"
              + "exit 3\n";

        /// <summary>
        /// A fake Claude CLI that emits <see cref="HeldPromptLines"/> assistant lines, flushing each, then blocks until
        /// <paramref name="release"/> exists (3-minute hang guard), then writes its file and a result line.
        /// </summary>
        private static string WriteHeldFakeClaude(string dir, string release)
        {
            if (Windows)
            {
                string ps1 = Path.Combine(dir, "held-claude.ps1");
                File.WriteAllText(ps1,
                    $$$"""
                    $null = [Console]::In.ReadToEnd()
                    for ($i = 1; $i -le {{{HeldPromptLines}}}; $i++) {
                      [Console]::Out.WriteLine('{"type":"assistant","message":{"content":[{"type":"text","text":"held line ' + $i + '"}]}}')
                      [Console]::Out.Flush()
                    }
                    $deadline = (Get-Date).AddMinutes(3)
                    while (-not (Test-Path '{{{release}}}')) { if ((Get-Date) -gt $deadline) { exit 3 }; Start-Sleep -Milliseconds 50 }
                    $ws = $env:GUARDRAILS_WORKSPACE; if (-not $ws) { $ws = (Get-Location).Path }
                    New-Item -ItemType Directory -Force -Path (Join-Path $ws 'src') | Out-Null
                    Set-Content -NoNewline -Path (Join-Path $ws 'src/02-prompt.txt') -Value 'prompt'
                    [Console]::Out.WriteLine('{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":1}')
                    [Console]::Out.Flush()
                    """);
                string cmd = Path.Combine(dir, "held-claude.cmd");
                File.WriteAllText(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"\r\n");
                return cmd;
            }

            string sh = Path.Combine(dir, "held-claude.sh");
            WriteScript(sh,
                $$$"""
                #!/usr/bin/env bash
                cat > /dev/null
                for i in $(seq 1 {{{HeldPromptLines}}}); do
                  printf '{"type":"assistant","message":{"content":[{"type":"text","text":"held line %s"}]}}\n' "$i"
                done
                for i in $(seq 1 3600); do [ -e '{{{release}}}' ] && break; sleep 0.05; done
                ws="${GUARDRAILS_WORKSPACE:-$PWD}"
                mkdir -p "$ws/src"
                printf 'prompt' > "$ws/src/02-prompt.txt"
                printf '{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":1}\n'
                """);
            return sh;
        }

        /// <summary>A fake Claude CLI: drain stdin, write its file, emit an assistant line and a result line.</summary>
        private static string WriteFakeClaude(string dir)
        {
            if (Windows)
            {
                string ps1 = Path.Combine(dir, "fake-claude.ps1");
                File.WriteAllText(ps1,
                    """
                    $null = [Console]::In.ReadToEnd()
                    $ws = $env:GUARDRAILS_WORKSPACE; if (-not $ws) { $ws = (Get-Location).Path }
                    New-Item -ItemType Directory -Force -Path (Join-Path $ws 'src') | Out-Null
                    Set-Content -NoNewline -Path (Join-Path $ws 'src/02-prompt.txt') -Value 'prompt'
                    Write-Output '{"type":"assistant","message":{"content":[{"type":"text","text":"I wrote src/02-prompt.txt"}]}}'
                    Write-Output '{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":1}'
                    """);
                string cmd = Path.Combine(dir, "fake-claude.cmd");
                File.WriteAllText(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"\r\n");
                return cmd;
            }

            string sh = Path.Combine(dir, "fake-claude.sh");
            WriteScript(sh,
                """
                #!/usr/bin/env bash
                cat > /dev/null
                ws="${GUARDRAILS_WORKSPACE:-$PWD}"
                mkdir -p "$ws/src"
                printf 'prompt' > "$ws/src/02-prompt.txt"
                printf '{"type":"assistant","message":{"content":[{"type":"text","text":"I wrote src/02-prompt.txt"}]}}\n'
                printf '{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":1}\n'
                """);
            return sh;
        }

        public void Dispose() => DeleteTree(_root);
    }
}
