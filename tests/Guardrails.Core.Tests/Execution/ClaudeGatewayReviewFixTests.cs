using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 review fixes that live in Core: a JIT wave breakdown through a gateway leaves an overhead record (corr B1), a
/// pre-launch refusal is a KNOWN zero rather than "usage not reported" (N14), a mixed tier rung labels its gateway
/// tokens (N2), and THE reach set carries task pins (sec B2).
/// </summary>
public sealed class ClaudeGatewayReviewFixTests : IDisposable
{
    private const string Gateway = "http://127.0.0.1:4000";
    private readonly string _root = Directory.CreateTempSubdirectory("gr782-review-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task AJitWaveBreakdownThroughAGateway_IsRecordedAsAnOverheadDispatch()
    {
        PlanDefinition plan = LoadPlan("""{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var result = new PromptResult
        {
            Completed = true,
            IsError = false,
            Summary = "claude completed (via gateway)",
            Gateway = Gateway,
            BackendModel = "unverified",
            Usage = new PromptUsage { InputTokens = 12_000, OutputTokens = 400 }
        };

        var prepared = new BreakdownInvocationPlan
        {
            Prompt = "Break down this wave.",
            ComposedPromptPath = Path.Combine(_root, "wave", "composed-prompt.md"),
            ComposedPromptBytes = 0,
            StreamLogPath = Path.Combine(_root, "wave", "claude-stream.jsonl"),
            TranscriptLogPath = Path.Combine(_root, "wave", "transcript.md"),
            MaxTurns = 10
        };

        // `wave` is read only to PREPARE the invocation; a prepared one is passed, so none is needed.
        await new WaveBreakdownInvoker(new StubRunner(result)).InvokeAsync(
            wave: null!, plan, _root, Path.Combine(_root, "wave"), journal, TestContext.Current.CancellationToken,
            prepared: prepared);

        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        JsonElement dispatch = Assert.Single(run.RootElement.GetProperty("overheadGatewayDispatches").EnumerateArray());
        Assert.Equal("breakdown", dispatch.GetProperty("source").GetString());
        Assert.Equal(Gateway, dispatch.GetProperty("gateway").GetString());
        Assert.Equal("12.4k tok (gateway)", JournalCost.Render(journal.Document));
    }

    [Fact]
    public async Task APreLaunchGatewayRefusal_IsAKnownZero_NotAnUnmeasuredDispatch()
    {
        var runner = new ClaudePromptRunner(
            "qwen", "claude-never-launched", new ProcessRunner(),
            new ClaudeGatewayConfig { BlockName = "qwen", BaseUrl = Gateway, Model = null });

        PromptResult refused = await runner.RunAsync(new PromptInvocation
        {
            ComposedPrompt = "x",
            Role = PromptRole.Action,
            WorkingDirectory = _root,
            PlanDirectory = _root,
            Environment = new Dictionary<string, string>(),
            Settings = new PromptRunnerSettings(),
            Timeout = TimeSpan.FromSeconds(5),
            StreamLogPath = string.Empty
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PromptFailureKind.RunnerConfiguration, refused.FailureKind);
        Assert.Equal(0, refused.Usage!.InputTokens + refused.Usage.OutputTokens);

        JournalDocument document = Document(Attempt(tier: null, gateway: Gateway, usage: new AttemptUsage()));
        Assert.Equal(0, JournalCost.GatewayDispatchesWithoutUsage(document));
    }

    [Fact]
    public void AMixedTierRung_LabelsItsGatewayTokens_ApartFromTheTokensAndDollars()
    {
        JournalDocument document = Document(
            Attempt("hard", gateway: null, usage: new AttemptUsage { InputTokens = 40_000, OutputTokens = 2_000 }, cost: 1.2m),
            Attempt("hard", gateway: Gateway, usage: new AttemptUsage { InputTokens = 12_000, OutputTokens = 0 }),
            Attempt("easy", gateway: Gateway, usage: new AttemptUsage { InputTokens = 5_000, OutputTokens = 0 }));

        string rendered = JournalTierSpend.Render(document)!;

        Assert.Contains("easy: 5k tok (gateway)", rendered, StringComparison.Ordinal);
        Assert.Contains("hard: 42k tok / $1.2000 + 12k tok (gateway)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ARungWithNoGateway_RendersExactlyAsBefore()
    {
        JournalDocument document = Document(
            Attempt("hard", gateway: null, usage: new AttemptUsage { InputTokens = 40_000, OutputTokens = 2_000 }, cost: 1.2m));

        Assert.Equal("hard: 42k tok / $1.2000", JournalTierSpend.Render(document));
    }

    [Fact]
    public void TheReachSet_CarriesBlockModels_OverrideModels_TaskPins_AndExtraArgsFlags()
    {
        PlanDefinition plan = LoadPlan(
            """{ "description": "t", "writeScope": [], "dependsOn": [], "action": { "model": "qwen3.8" } }""",
            """
            "default": "q",
            "q": { "baseUrl": "http://127.0.0.1:4000", "model": "qwen3.6", "extraArgs": ["--fallback-model", "x"],
                   "guardrailOverrides": { "model": "judge-model" } },
            "claude": { }
            """);

        ClaudeGatewayModelReach[] reach = [.. ClaudeGatewayReach.Of(plan)];

        Assert.Equal(
            ["qwen3.6", "judge-model", "x", "qwen3.8"],
            reach.Select(r => r.Model));
        Assert.Equal(
            [ClaudeGatewayModelSource.BlockModel, ClaudeGatewayModelSource.OverrideModel,
             ClaudeGatewayModelSource.ExtraArgs, ClaudeGatewayModelSource.ActionPin],
            reach.Select(r => r.Source));
        Assert.All(reach, r => Assert.Equal("q", r.Block.Name));
    }

    // ───────────────────────────── harness ─────────────────────────────

    private sealed class StubRunner(PromptResult result) : IPromptRunner
    {
        public string Name => "qwen";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static AttemptRecord Attempt(string? tier, string? gateway, AttemptUsage? usage, decimal? cost = null) => new()
    {
        Attempt = 1,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = AttemptOutcome.Succeeded,
        LogDir = "logs/x",
        CostUsd = cost,
        Usage = usage,
        Provenance = new AttemptProvenance { Tier = tier, Gateway = gateway, BackendModel = gateway is null ? null : "unverified" }
    };

    private static JournalDocument Document(params AttemptRecord[] attempts) => new()
    {
        RunId = "r",
        PlanHash = "h",
        Tasks = attempts
            .Select((a, i) => (Id: $"t{i}", Entry: new TaskJournalEntry { Status = JournalTaskStatus.Succeeded, Attempts = [a] }))
            .ToDictionary(p => p.Id, p => p.Entry, StringComparer.Ordinal)
    };

    private PlanDefinition LoadPlan(string taskJson, string? promptRunners = null)
    {
        string plan = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N")[..8]);
        string taskDir = Path.Combine(plan, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(plan, "guardrails.json"), $$"""
            { "version": 1, "workspace": ".", "maxParallelism": 1,
              "promptRunners": { {{promptRunners ?? "\"default\": \"q\", \"q\": { \"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\" }"}} } }
            """);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        PlanLoadResult result = new PlanLoader().Load(plan);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }
}
