using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Design 41 §2.3 (issue #712) — the overwatcher's missing-resource brief,
/// <see cref="Overwatch.ProposeResourceSupplyAsync"/>, plus the #382 drive-the-real-seam proof task 09 is
/// gated on.
///
/// <para>
/// TDD red: <c>ProposeResourceSupplyAsync</c> currently throws <see cref="NotImplementedException"/>
/// unconditionally (task 08's stub) and <see cref="OverwatchTriggers.Token"/> has no case for
/// <see cref="OverwatchTrigger.MissingResource"/> (its <c>_ =&gt;</c> default throws), so every test below
/// that drives either is expected to FAIL against the stub tree. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red. The one exception is
/// <see cref="TheGenericDiagnoseBriefIsUnchanged"/>, which drives the SHIPPED
/// <see cref="Overwatch.EvaluateAsync"/> and is declared green-on-arrival — see its own doc comment.
/// </para>
///
/// <para>
/// The real-seam row exists because every text-only row here proves the brief's CONTENT against a fake
/// <see cref="IPromptRunner"/> — exactly the substitution that let the measured <c>CriticalityJudge</c>
/// empty-<c>StreamLogPath</c> bug ship green: a blanket <c>catch</c> turned a crash through the REAL
/// <see cref="ClaudePromptRunner"/> into a safe default, and the judge escalated 100% of the time with
/// nothing saying so. A component whose only tests inject a fake runner can be broken through the real one
/// and every test still passes (#382). So one row here constructs the real <see cref="ClaudePromptRunner"/>
/// over a fake CLI PROCESS instead.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchResourceSupplyBriefTests : IDisposable
{
    private const string ResourcePath = "vendor/resource.js";

    private const string Question =
        "Cannot embed the runtime: vendor/resource.js is missing from this worktree; I will not stub or fetch it.";

    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly TempGitRepo _checkout = new();
    private readonly string _scratchDir =
        Path.Combine(Path.GetTempPath(), "gr-orsbt-scratch-" + Guid.NewGuid().ToString("N"));

    private readonly PlanDefinition _plan;
    private readonly RunJournal _journal;
    private readonly TaskNode _task;
    private readonly string _taskLogDir;

    public OverwatchResourceSupplyBriefTests()
    {
        Directory.CreateDirectory(_scratchDir);
        File.WriteAllText(Path.Combine(_checkout.RepoPath, "guardrails.json"), """{ "version": 1 }""");

        _task = new TaskNode
        {
            Id = "02-needs-resource",
            Directory = _checkout.RepoPath,
            Description = "embed the vendored runtime bundle",
            Action = new ActionDefinition
            {
                Path = Path.Combine(_checkout.RepoPath, "action.prompt.md"),
                Kind = ActionKind.Prompt
            },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(_checkout.RepoPath, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ],
            WriteScope = ["02-done.txt"]
        };

        _plan = new PlanDefinition
        {
            PlanDirectory = _checkout.RepoPath,
            Workspace = _checkout.RepoPath,
            Config = new RunConfig { Version = 1 },
            Tasks = [_task]
        };

        _journal = RunJournal.LoadOrCreate(_plan);
        _taskLogDir = Path.Combine(_checkout.RepoPath, "logs", _journal.Document.RunId, _task.Id);
    }

    public void Dispose()
    {
        _checkout.Dispose();
        SafeDelete.DeleteDirectory(_scratchDir);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fake <see cref="IPromptRunner"/> that CAPTURES the invocation so the composed brief can be
    /// asserted at the source. Never used by the real-seam row — see that test's own doc comment.</summary>
    private sealed class CapturingRunner(bool completed, bool isError, string? resultText, decimal? costUsd = null)
        : IPromptRunner
    {
        public PromptInvocation? Seen { get; private set; }

        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return Task.FromResult(new PromptResult
            {
                Completed = completed,
                IsError = isError,
                ResultText = resultText,
                CostUsd = costUsd ?? 0.05m,
                Summary = completed ? "ok" : "failed"
            });
        }
    }

    /// <summary>Records the operator-visible surfaces (mirrors <c>OverwatchNoVerdictTests</c>'s fake).</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string TaskId, string Reason)> NoVerdicts { get; } = [];

        public List<DecisionEntry> Decisions { get; } = [];

        public void OverwatchNoVerdict(string taskId, string reason) => NoVerdicts.Add((taskId, reason));

        public void DecisionRecorded(DecisionEntry entry) => Decisions.Add(entry);

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<MissingResourceCandidate> OneCandidate() =>
        [new MissingResourceCandidate { Path = ResourcePath, SourceCommit = _checkout.HeadSha() }];

    private static string ValidVerdictJson(string path) =>
        $$"""{"classification":"retryable","diagnosis":"the checkout's committed copy resolves the missing file","fixes":[{"kind":"resource-supply","path":"{{path}}"}]}""";

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // ── the pinned heading ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheBriefsFirstLineIsThePinnedResourceSupplyHeading()
    {
        var runner = new CapturingRunner(completed: true, isError: false, ValidVerdictJson(ResourcePath));
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);

        await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 3, Question, OneCandidate(), _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        string firstLine = runner.Seen.ComposedPrompt.Split('\n')[0];

        // The whole line, not a Contains on a fragment: the real-seam proof's fake CLI (and, in
        // production, the §7 wiring proof's fake CLI) matches a PREFIX, so a heading that is merely
        // similar would fail there and nowhere else.
        Assert.Equal($"# Overwatch resource supply: task '{_task.Id}' (attempt 3, trigger: missing-resource)", firstLine);
    }

    // ── harness facts first; the agent's question is delimited and marked untrusted ────────────────────

    [Fact]
    public async Task TheBriefStatesHarnessFactsFirst_AndDelimitsTheAgentsQuestionAsUntrusted()
    {
        var runner = new CapturingRunner(completed: true, isError: false, ValidVerdictJson(ResourcePath));
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);

        await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 2, Question, OneCandidate(), _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        string prompt = runner.Seen.ComposedPrompt;

        int untrustedOpen = prompt.IndexOf("UNTRUSTED", StringComparison.Ordinal);
        Assert.True(untrustedOpen >= 0, "expected an UNTRUSTED delimiter marking the agent's question");

        int questionIndex = prompt.IndexOf(Question, StringComparison.Ordinal);
        Assert.True(questionIndex > untrustedOpen, "the agent's question must sit inside the untrusted block");

        // Harness facts (here, the task's own description) come BEFORE the untrusted block — #709's rule.
        int descriptionIndex = prompt.IndexOf(_task.Description, StringComparison.Ordinal);
        Assert.True(
            descriptionIndex >= 0 && descriptionIndex < untrustedOpen,
            "a harness fact (the task description) must precede the untrusted question block");

        // The question text appears EXACTLY once: never repeated outside the delimited block, where it
        // would read as a verified harness fact rather than the agent's own unverified claim (#709).
        Assert.Equal(1, CountOccurrences(prompt, Question));

        Assert.Contains(
            "Do not assert anything about files, tests, other tasks or plan-level gates beyond them",
            prompt);
    }

    // ── every candidate is tabled, each with its own source sha and branch ─────────────────────────────

    [Fact]
    public async Task TheBriefTablesEveryCandidateWithItsSourceShaAndBranch()
    {
        string sha1 = _checkout.HeadSha();
        _checkout.WriteFile("vendor/second.js", "export const two = true;");
        _checkout.Commit("add a second resource");
        string sha2 = _checkout.HeadSha();
        string branch = _checkout.CurrentBranch();

        MissingResourceCandidate[] candidates =
        [
            new MissingResourceCandidate { Path = ResourcePath, SourceCommit = sha1 },
            new MissingResourceCandidate { Path = "vendor/second.js", SourceCommit = sha2 }
        ];

        var runner = new CapturingRunner(completed: true, isError: false, ValidVerdictJson(ResourcePath));
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);

        await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 2, Question, candidates, _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        string prompt = runner.Seen.ComposedPrompt;

        // Two, not one: a builder that renders only candidates[0] must fail this.
        Assert.Contains(ResourcePath, prompt);
        Assert.Contains("vendor/second.js", prompt);

        // Each candidate's OWN checkout sha (distinct commits, so a merged/first-only render is caught).
        Assert.Contains(sha1[..10], prompt);
        Assert.Contains(sha2[..10], prompt);

        // The branch the bytes would be read from.
        Assert.Contains(branch, prompt);

        // The "absent from the run's base" fact design 41 §2.3 pins for every row.
        Assert.Contains("absent at run base", prompt);
    }

    // ── only the resource-supply fix vocabulary is offered ─────────────────────────────────────────────

    [Fact]
    public async Task TheBriefOffersOnlyTheResourceSupplyFixVocabulary()
    {
        var runner = new CapturingRunner(completed: true, isError: false, ValidVerdictJson(ResourcePath));
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);

        await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 2, Question, OneCandidate(), _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        string prompt = runner.Seen.ComposedPrompt;

        Assert.Contains("\"kind\":\"resource-supply\"", prompt);
        Assert.Contains("\"path\":", prompt);

        // NONE of the four shipped op shapes: a brief that offers a budget bump invites a fix this gate
        // (OverwatchSupplyAutoResolve.Certify) can never certify.
        Assert.DoesNotContain("\"kind\":\"guidance\"", prompt);
        Assert.DoesNotContain("\"kind\":\"budget\"", prompt);
        Assert.DoesNotContain("\"kind\":\"file-edit\"", prompt);
        Assert.DoesNotContain("\"kind\":\"task-field\"", prompt);
    }

    // ── the trigger token ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMissingResourceTriggerTokenIsMissingResource() =>
        Assert.Equal("missing-resource", OverwatchTriggers.Token(OverwatchTrigger.MissingResource));

    // ── the fix-kind token (a DIFFERENT member from the trigger token above) ───────────────────────────

    [Fact]
    public async Task TheResourceSupplyFixKindTokenIsResourceSupply()
    {
        // Overwatch.FixKindToken is private static, so this cannot call it directly. It is proven through
        // the OBSERVABLE it builds: the overwatch.jsonl detail record's fixes[].kind, which today falls to
        // the `_ => "unknown"` arm for OverwatchFixKind.ResourceSupply.
        var runner = new CapturingRunner(completed: true, isError: false, ValidVerdictJson(ResourcePath));
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);

        await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 2, Question, OneCandidate(), _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        string jsonl = await File.ReadAllTextAsync(
            Path.Combine(_taskLogDir, "overwatch.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("\"kind\":\"resource-supply\"", jsonl);
    }

    // ── an unparseable verdict is a REPORTED no-verdict, not a second silent capability (#712) ─────────

    [Fact]
    public async Task AnUnparseableVerdict_IsRecordedAsNoVerdict_NotSilence()
    {
        var runner = new CapturingRunner(completed: true, isError: false, "I think you should try again.");
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);
        var observer = new RecordingObserver();

        OverwatchProposal? result = await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 2, Question, OneCandidate(), _taskLogDir, _journal,
            observer, TestContext.Current.CancellationToken);

        // Advisory-never-gates: no parseable verdict = no proposal.
        Assert.Null(result);

        // (1) The operator SEES it, exactly as the §9.2 diagnose already reports (#452).
        Assert.Single(observer.NoVerdicts);

        // (2) It is DURABLE.
        IReadOnlyList<DecisionEntry> decisions = RunJournal.LoadOrCreate(_plan).Document.Decisions ?? [];
        DecisionEntry entry = Assert.Single(decisions);
        Assert.Equal(DecisionTokens.NoVerdict, entry.Decision);
        Assert.Equal(_task.Id, entry.Subject);

        // (3) And the per-fire detail stream carries it too — the diagnose spend was charged BEFORE the
        // parse, so a billed supervisor failure is never silent.
        string jsonl = await File.ReadAllTextAsync(
            Path.Combine(_taskLogDir, "overwatch.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("\"decision\":\"no-verdict\"", jsonl);
    }

    // ── declared exemption: the generic diagnose brief is shipped code this task does not touch ────────

    /// <summary>
    /// Green on arrival, deliberately. The generic <c># Overwatch diagnose:</c> brief is shipped code this
    /// task does not touch, so a CORRECT test here is green against BOTH the stub tree and the implemented
    /// tree — demanding red would demand a correct implementation fail. It must still exist and pass: the
    /// red census asserts that, and task 09's forward census requires it observed <c>Passed</c>.
    /// </summary>
    [Fact]
    public async Task TheGenericDiagnoseBriefIsUnchanged()
    {
        var runner = new CapturingRunner(
            completed: true, isError: false, """{"classification":"retryable","diagnosis":"d","fixes":[]}""");
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt);

        await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, _task, _plan, attempt: 2, _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        string prompt = runner.Seen.ComposedPrompt;

        Assert.StartsWith($"# Overwatch diagnose: task '{_task.Id}'", prompt);
        Assert.Contains("\"kind\":\"guidance\"", prompt);
        Assert.Contains("\"kind\":\"budget\"", prompt);
        Assert.Contains("\"kind\":\"file-edit\"", prompt);
        Assert.Contains("\"kind\":\"task-field\"", prompt);
        Assert.DoesNotContain("resource-supply", prompt);
    }

    // ── the drive-the-real-seam proof (#382, bucket E, seam Overwatch -> IPromptRunner) ────────────────

    /// <summary>
    /// Every row above proves the brief's TEXT against a fake <see cref="IPromptRunner"/>. That is exactly
    /// the substitution that blinds the guardrail (the measured <c>CriticalityJudge</c> empty-
    /// <c>StreamLogPath</c> bug — green against a fake runner, throwing through the real
    /// <see cref="ClaudePromptRunner"/>, with a blanket <c>catch</c> turning the crash into a safe default
    /// so the judge escalated 100% of the time with nothing saying so). This row constructs the REAL
    /// <see cref="ClaudePromptRunner"/> over a fake CLI PROCESS instead — never a fake
    /// <see cref="IPromptRunner"/> — and asserts effects only the real runner can produce.
    /// </summary>
    [Fact]
    public async Task ProposeResourceSupply_DrivesTheRealClaudePromptRunner_WritingItsStreamLogAndReturningARealVerdict()
    {
        string stdinLogPath = Path.Combine(_scratchDir, "stdin.log");
        string fakeCli = WriteFakeCli(stdinLogPath, ValidVerdictJson(ResourcePath), costUsd: 0.09m);

        IPromptRunner realRunner = new ClaudePromptRunner("overwatch", fakeCli, new ProcessRunner());
        var overwatch = new Overwatch(realRunner, terminalTriage: null, AutonomyPolicy.Auto, autonomyBlockPresent: true);

        decimal costBefore = _journal.CurrentCostUsd();

        OverwatchProposal? proposal = await overwatch.ProposeResourceSupplyAsync(
            _task, _plan, attempt: 4, Question, OneCandidate(), _taskLogDir, _journal,
            new RecordingObserver(), TestContext.Current.CancellationToken);

        // (1) the per-attempt stream log EXISTS and carries the fake CLI's own line — a file no fake
        // IPromptRunner would ever write.
        string streamLogPath = Path.Combine(_taskLogDir, "overwatch-stream-attempt-4.jsonl");
        Assert.True(File.Exists(streamLogPath), $"expected a real stream log at {streamLogPath}");
        string streamLog = await File.ReadAllTextAsync(streamLogPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"type\":\"result\"", streamLog);

        // (2) a REAL parsed verdict — not the catch-and-safe-default no-verdict the blanket catch produces.
        Assert.NotNull(proposal);
        Assert.Equal(OverwatchClassification.Retryable, proposal.Classification);
        OverwatchFixOp fix = Assert.Single(proposal.Fixes);
        Assert.Equal(OverwatchFixKind.ResourceSupply, fix.Kind);
        Assert.Equal(ResourcePath, fix.TargetPath);

        // (3) the composed brief provably crossed the process boundary: the fake CLI's recorded stdin
        // begins with the pinned heading.
        Assert.True(File.Exists(stdinLogPath), $"expected the fake CLI to have drained stdin to {stdinLogPath}");
        string stdin = await File.ReadAllTextAsync(stdinLogPath, TestContext.Current.CancellationToken);
        Assert.StartsWith("# Overwatch resource supply:", stdin);

        // (4) the diagnose spend reached the journal's cumulative cost — the real runner parses
        // total_cost_usd, and the charge happens BEFORE the parse (#452).
        Assert.True(
            _journal.CurrentCostUsd() >= costBefore + 0.09m,
            $"expected the journal's cumulative cost to include the diagnose spend (before={costBefore}, after={_journal.CurrentCostUsd()})");
    }

    /// <summary>
    /// A minimal fake CLI (the proven <c>ClaudePromptRunnerStreamLogTests.WriteFakeCli</c> pattern: a
    /// <c>.cmd</c> shim invoking a <c>.ps1</c> on Windows, a <c>.sh</c> with the executable bit set
    /// elsewhere) that (a) drains stdin to <paramref name="stdinLogPath"/> and (b) emits ONE stream-json
    /// terminal line whose <c>result</c> is <paramref name="resultJson"/>. The response line is written to
    /// its own file and the script merely echoes it, rather than embedding JSON-in-JSON text inside the
    /// script source, which would need two layers of escaping (shell-string, then JSON-string).
    /// </summary>
    private string WriteFakeCli(string stdinLogPath, string resultJson, decimal costUsd)
    {
        string responsePath = Path.Combine(_scratchDir, "response.jsonl");
        string streamLine =
            $$"""{"type":"result","is_error":false,"result":{{JsonSerializer.Serialize(resultJson)}},"total_cost_usd":{{costUsd.ToString(CultureInfo.InvariantCulture)}},"num_turns":1}""";
        File.WriteAllText(responsePath, streamLine);

        if (Windows)
        {
            string ps1Path = Path.Combine(_scratchDir, "fake.ps1");
            string cmdPath = Path.Combine(_scratchDir, "fake.cmd");
            File.WriteAllText(
                ps1Path,
                "$stdin = [Console]::In.ReadToEnd()\r\n" +
                $"[System.IO.File]::WriteAllText('{stdinLogPath}', $stdin)\r\n" +
                $"Get-Content -Raw '{responsePath}'\r\n");
            File.WriteAllText(
                cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(_scratchDir, "fake.sh");
        File.WriteAllText(
            shPath,
            "#!/usr/bin/env bash\n" +
            $"cat > \"{stdinLogPath}\"\n" +
            $"cat \"{responsePath}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return shPath;
    }

    // ── a real git repository, not a fake of one (mirrors MissingResourceFactsTests' TempGitRepo) ──────

    /// <summary>
    /// A throwaway single-use git repository standing in for the operator's checkout. Seeds
    /// <c>vendor/resource.js</c> at construction, since every test in this file needs at least one
    /// candidate whose bytes are readable at a real checkout <c>HEAD</c> sha and branch.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        internal string RepoPath { get; } =
            Path.Combine(Path.GetTempPath(), "gr-orsbt-repo-" + Guid.NewGuid().ToString("N"));

        internal TempGitRepo()
        {
            Directory.CreateDirectory(RepoPath);

            Git("init");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            WriteFile("README.md", "# fixture repo");
            Commit("Initial commit");

            WriteFile(ResourcePath, "export const ok = true;");
            Commit("Commit the vendored resource");
        }

        internal string HeadSha() => Git("rev-parse", "HEAD").Trim();

        internal string CurrentBranch() => Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        internal void WriteFile(string relativePath, string content)
        {
            string full = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        internal void Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-m", message);
        }

        internal string Git(params string[] arguments)
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

        public void Dispose() => SafeDelete.DeleteDirectory(RepoPath);
    }
}
