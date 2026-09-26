using System.Diagnostics;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 §4, closed END TO END: no fake <see cref="IPromptRunner"/> anywhere. The plan's gateway block is built by
/// the production <see cref="PromptRunnerRegistry.FromConfig"/> into a real gateway <see cref="ClaudePromptRunner"/>
/// instance, which launches <see cref="GatewayFakeClaude"/> through the real <see cref="ProcessRunner"/>. The fake's
/// stream REPORTS a cost ($1.23) and tokens (1000 in / 200 out), so every assertion below that the cost is absent
/// and the tokens are present is a claim about the real parse → null-at-the-source → journal path, read from the
/// BYTES each surface wrote:
/// <list type="bullet">
/// <item><c>run.json</c>: the ACTION attempt's and the JUDGE's <c>gateway</c> / <c>backendModel</c>, neither with a cost;</item>
/// <item><c>events.jsonl</c> (<see cref="RunEventStream"/>): the attempt's <c>tokens</c> and <c>gateway</c>, no <c>costUsd</c>;</item>
/// <item><c>observer.jsonl</c> (<see cref="ObserverProjection"/>): <c>inputTokens</c>/<c>outputTokens</c>/<c>gateway</c>/<c>backendModel</c>;</item>
/// <item>an <c>ai-merge</c> dispatch driven through <see cref="AiMergeResolver"/> itself: its overhead record, and a
/// run total that renders tokens, never dollars.</item>
/// </list>
/// </summary>
[Collection(GitEnvironmentCollection.Name)]
public sealed class ClaudeGatewayEndToEndProvenanceTests : IDisposable
{
    private const string TaskId = "01-task";
    private const string Model = "Qwen";
    private const string Backend = "http://127.0.0.1:8080 Qwen3.6-35B-A3B";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr782-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayFakeClaude _fake;
    private readonly string _gateway = "http://127.0.0.1:4000";

    public ClaudeGatewayEndToEndProvenanceTests()
    {
        Directory.CreateDirectory(_root);
        _fake = new GatewayFakeClaude(Path.Combine(_root, "fake"));
    }

    private string PlanDir => Path.Combine(_root, "plan");

    private string EventsDir => Path.Combine(_root, "events");

    public void Dispose()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ───────────────────────────── attempt + judge through a real run ─────────────────────────────

    [Fact]
    public async Task ARealGatewayRun_RecordsGatewayAndBackend_OnTheAttemptAndTheJudge_WithNoCost_InRunJsonBytes()
    {
        (RunReport report, PlanDefinition plan) = await RunGatewayPlanAsync();

        Assert.True(report.AllSucceeded, Assert.Single(report.Tasks).Summary);
        // Two dispatches through the gateway instance — the action and the judge — each a gateway launch.
        IReadOnlyList<FakeClaudeCall> calls = _fake.Calls();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(_gateway, c.Read(ClaudeGatewayEnvironment.BaseUrl)));
        Assert.All(calls, c => Assert.Single(c.Args, a => a == "--settings"));

        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        JsonElement attempt = run.RootElement.GetProperty("tasks").GetProperty(TaskId).GetProperty("attempts")[0];
        JsonElement provenance = attempt.GetProperty("provenance");

        Assert.Equal(_gateway, provenance.GetProperty("gateway").GetString());
        Assert.Equal(Backend, provenance.GetProperty("backendModel").GetString());
        AssertNoCost(attempt, "the action attempt");
        Assert.Equal(GatewayFakeClaude.InputTokens, attempt.GetProperty("usage").GetProperty("inputTokens").GetInt32());
        Assert.Equal(GatewayFakeClaude.OutputTokens, attempt.GetProperty("usage").GetProperty("outputTokens").GetInt32());

        // The JUDGE rode the same gateway instance — its own provenance says so, its reported $1.23 is gone, its
        // tokens are kept.
        JsonElement judge = provenance.GetProperty("judge");
        Assert.Equal(_gateway, judge.GetProperty("gateway").GetString());
        Assert.Equal(Backend, judge.GetProperty("backendModel").GetString());
        AssertNoCost(judge, "the judge");
        Assert.Equal(GatewayFakeClaude.InputTokens, judge.GetProperty("usage").GetProperty("inputTokens").GetInt32());

        // Nothing anywhere in run.json is the fiction the CLI reported.
        Assert.DoesNotContain("1.23", run.RootElement.GetRawText(), StringComparison.Ordinal);

        // And the rendered spend is tokens (actor spend only — the judge stays out of the total, as for dollars).
        Assert.Equal("1.2k tok (gateway)", JournalCost.Render(JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory))));
        Assert.Contains("; 1.2k tok (gateway)", Assert.Single(report.Tasks).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealGatewayRun_EventsAndObserverStreams_CarryTokensAndGateway_NeverACost()
    {
        await RunGatewayPlanAsync();

        string eventLine = Assert.Single(
            File.ReadAllLines(Path.Combine(EventsDir, "events.jsonl")),
            l => l.Contains("\"attempt-finished\"", StringComparison.Ordinal));
        using (JsonDocument ev = JsonDocument.Parse(eventLine))
        {
            JsonElement root = ev.RootElement;
            Assert.Equal(GatewayFakeClaude.InputTokens + GatewayFakeClaude.OutputTokens, root.GetProperty("tokens").GetInt64());
            Assert.Equal(_gateway, root.GetProperty("gateway").GetString());
            Assert.False(root.TryGetProperty("costUsd", out _), $"an events row must not carry a gateway cost: {eventLine}");
        }

        string observerLine = Assert.Single(
            File.ReadAllLines(Path.Combine(EventsDir, "observer.jsonl")),
            l => l.Contains("\"AttemptFinished\"", StringComparison.Ordinal));
        using JsonDocument obs = JsonDocument.Parse(observerLine);
        JsonElement o = obs.RootElement;
        Assert.Equal(GatewayFakeClaude.InputTokens, o.GetProperty("inputTokens").GetInt32());
        Assert.Equal(GatewayFakeClaude.OutputTokens, o.GetProperty("outputTokens").GetInt32());
        Assert.Equal(_gateway, o.GetProperty("gateway").GetString());
        Assert.Equal(Backend, o.GetProperty("backendModel").GetString());
        Assert.True(
            !o.TryGetProperty("costUsd", out JsonElement cost) || cost.ValueKind == JsonValueKind.Null,
            $"the observer row must carry no gateway cost: {observerLine}");
    }

    [Fact]
    public void ANonGatewayAttempt_EventsAndObserverRows_CarryNoGatewayField()
    {
        Directory.CreateDirectory(EventsDir);
        var observer = new RunEventStream(new ObserverProjection(IRunObserver.Null, EventsDir), EventsDir, "run-paid");
        var task = new TaskNode
        {
            Id = TaskId,
            Directory = PlanDir,
            Description = "paid",
            Action = new ActionDefinition { Path = "action.prompt.md", Kind = ActionKind.Prompt },
            Guardrails = []
        };

        ((IRunObserver)observer).AttemptFinished(task, new AttemptRecord
        {
            Attempt = 1,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Succeeded,
            LogDir = "logs/x",
            CostUsd = 0.25m,
            Usage = new AttemptUsage { InputTokens = 10, OutputTokens = 5 },
            Provenance = new AttemptProvenance { Model = "claude-sonnet-4-5" }
        });

        using JsonDocument ev = JsonDocument.Parse(File.ReadAllLines(Path.Combine(EventsDir, "events.jsonl")).Single());
        Assert.Equal(0.25m, ev.RootElement.GetProperty("costUsd").GetDecimal());
        Assert.Equal(15, ev.RootElement.GetProperty("tokens").GetInt64());
        Assert.False(ev.RootElement.TryGetProperty("gateway", out _));

        using JsonDocument obs = JsonDocument.Parse(File.ReadAllLines(Path.Combine(EventsDir, "observer.jsonl")).Single());
        Assert.Equal(0.25m, obs.RootElement.GetProperty("costUsd").GetDecimal());
        Assert.True(!obs.RootElement.TryGetProperty("gateway", out JsonElement g) || g.ValueKind == JsonValueKind.Null);
    }

    // ───────────────────────────── ai-merge through AiMergeResolver itself ─────────────────────────────

    [Fact]
    public async Task AnAiMergeDispatchedThroughTheResolver_ToAGatewayInstance_IsRecorded_WithNoCost()
    {
        WritePlan();
        PlanDefinition plan = LoadPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(WithGatewayRun(plan.Config), new ProcessRunner());
        string repo = ConflictRepo();

        var resolver = new AiMergeResolver(registry.Resolve("qwen"));
        bool resolved = await resolver.TryResolveAsync(
            repo, "conflict-branch", planDirectory: plan.PlanDirectory, journal, TestContext.Current.CancellationToken);

        // The fake writes no MERGE_OUT, so the gates fail every attempt of the resolver's budget — and each attempt's
        // spend is recorded regardless (#314), one overhead record per dispatch the fake actually received.
        Assert.False(resolved);
        IReadOnlyList<FakeClaudeCall> calls = _fake.Calls();
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Equal(_gateway, c.Read(ClaudeGatewayEnvironment.BaseUrl)));

        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        Assert.False(run.RootElement.TryGetProperty("overheadCostUsd", out _), "a gateway merge must not charge the fake's $1.23");
        JsonElement[] dispatches = [.. run.RootElement.GetProperty("overheadGatewayDispatches").EnumerateArray()];
        Assert.Equal(calls.Count, dispatches.Length);
        Assert.All(dispatches, dispatch =>
        {
            Assert.Equal("ai-merge", dispatch.GetProperty("source").GetString());
            Assert.Equal(_gateway, dispatch.GetProperty("gateway").GetString());
            Assert.Equal(Backend, dispatch.GetProperty("backendModel").GetString());
            Assert.Equal(GatewayFakeClaude.InputTokens, dispatch.GetProperty("usage").GetProperty("inputTokens").GetInt32());
            Assert.Equal(GatewayFakeClaude.OutputTokens, dispatch.GetProperty("usage").GetProperty("outputTokens").GetInt32());
            Assert.False(dispatch.TryGetProperty("costUsd", out _));
        });

        Assert.Equal(
            SpendFormat.Tokens(calls.Count * (long)(GatewayFakeClaude.InputTokens + GatewayFakeClaude.OutputTokens)) + " (gateway)",
            JournalCost.Render(journal.Document));
        Assert.Equal(0m, journal.CurrentCostUsd());
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static void AssertNoCost(JsonElement element, string what) =>
        Assert.True(
            !element.TryGetProperty("costUsd", out JsonElement cost) || cost.ValueKind == JsonValueKind.Null,
            $"{what} of a gateway dispatch must record no cost, found {element.GetRawText()}");

    private RunConfig WithGatewayRun(RunConfig config) => config with
    {
        GatewayRun = new ClaudeGatewayRunContext
        {
            ConfigDirectory = Path.Combine(_root, "claude-config"),
            BackendIdentities = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaudeGatewayRunContext.IdentityKey(_gateway, Model)] = Backend
            }
        }
    };

    private async Task<(RunReport Report, PlanDefinition Plan)> RunGatewayPlanAsync()
    {
        WritePlan();
        PlanDefinition plan = LoadPlan();
        plan = plan with { Config = WithGatewayRun(plan.Config) };

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        Directory.CreateDirectory(EventsDir);
        IRunObserver observer = new RunEventStream(new ObserverProjection(IRunObserver.Null, EventsDir), EventsDir, "run-e2e");

        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(plan.Config, new ProcessRunner());
        Assert.NotNull(Assert.IsType<ClaudePromptRunner>(registry.Resolve("qwen")).Gateway);

        var executor = new TaskExecutor(
            plan, new ProcessRunner(), new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters),
            stateManager, journal, observer, registry);
        var scheduler = new Scheduler(plan, executor, journal, observer: observer, maxParallelism: 1);
        RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);
        return (report, plan);
    }

    private PlanDefinition LoadPlan()
    {
        PlanLoadResult load = new PlanLoader().Load(PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    private void WritePlan()
    {
        Write(Path.Combine(PlanDir, "guardrails.json"), $$"""
            {
              "version": 1, "workspace": ".", "maxParallelism": 1, "defaultRetries": 0, "defaultTimeoutSeconds": 120,
              "promptRunners": {
                "default": "qwen",
                "qwen": { "command": "{{_fake.Command.Replace("\\", "\\\\")}}", "baseUrl": "{{_gateway}}", "model": "{{Model}}" }
              }
            }
            """);

        string taskDir = Path.Combine(PlanDir, "tasks", TaskId);
        Write(Path.Combine(taskDir, "task.json"),
            """{ "description": "gateway end-to-end fixture", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        Write(Path.Combine(taskDir, "guardrails", "01-verdict.prompt.md"), "Judge the thing and write a verdict.\n");
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A throwaway repo left mid-merge with one genuine content conflict (<c>UU conflict.txt</c>).</summary>
    private string ConflictRepo()
    {
        string repo = Path.Combine(_root, "merge-repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init");
        Git(repo, "config", "user.email", "test@guardrails.local");
        Git(repo, "config", "user.name", "Guardrails Test");
        Git(repo, "config", "commit.gpgsign", "false");
        Git(repo, "config", "core.hooksPath", Directory.CreateDirectory(Path.Combine(_root, "no-hooks")).FullName);
        File.WriteAllText(Path.Combine(repo, "conflict.txt"), "base\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "base");
        string main = Git(repo, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        Git(repo, "checkout", "-b", "conflict-branch");
        File.WriteAllText(Path.Combine(repo, "conflict.txt"), "theirs\n");
        Git(repo, "commit", "-am", "theirs");
        Git(repo, "checkout", main);
        File.WriteAllText(Path.Combine(repo, "conflict.txt"), "ours\n");
        Git(repo, "commit", "-am", "ours");
        Git(repo, tolerateFailure: true, "merge", "--no-commit", "--no-ff", "conflict-branch");
        return repo;
    }

    private static string Git(string repo, params string[] args) => Git(repo, tolerateFailure: false, args);

    private static string Git(string repo, bool tolerateFailure, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !tolerateFailure)
        {
            throw new InvalidOperationException($"git {string.Join(" ", args)} exited {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
