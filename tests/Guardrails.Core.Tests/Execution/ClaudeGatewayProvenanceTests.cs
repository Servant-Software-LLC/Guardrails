using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 stage 5 — the gateway facts reach <c>run.json</c> and a telemetry row, read from their BYTES: an attempt's
/// provenance carries <c>gateway</c> and <c>backendModel</c> with no <c>costUsd</c> (tokens kept), an ai-merge
/// overhead dispatch leaves its own record, and every rendered spend shows tokens — never <c>$0.00</c> or a blank.
/// Driven through a REAL serial run; the only fake is the <see cref="IPromptRunner"/>.
/// </summary>
public sealed class ClaudeGatewayProvenanceTests : IDisposable
{
    private const string TaskId = "01-task";
    private const string Gateway = "http://127.0.0.1:4000";
    private const string Backend = "http://127.0.0.1:8080 Qwen3.6-35B-A3B";

    private static readonly bool Ps = OperatingSystem.IsWindows();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr782-prov-" + Guid.NewGuid().ToString("N"));

    private string PlanDir => Path.Combine(_root, "plan");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task AGatewayAttempt_RecordsGatewayAndBackend_NoCost_TokensKept_InTheBytesOfRunJson()
    {
        (RunReport report, PlanDefinition plan) = await RunSerialAsync(GatewayResult());

        Assert.True(report.AllSucceeded);
        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        JsonElement attempt = run.RootElement.GetProperty("tasks").GetProperty(TaskId).GetProperty("attempts")[0];
        JsonElement provenance = attempt.GetProperty("provenance");

        Assert.Equal(Gateway, provenance.GetProperty("gateway").GetString());
        Assert.Equal(Backend, provenance.GetProperty("backendModel").GetString());
        Assert.True(
            !attempt.TryGetProperty("costUsd", out JsonElement cost) || cost.ValueKind == JsonValueKind.Null,
            "a gateway attempt must record costUsd: null, never a number");
        Assert.Equal(48_000, attempt.GetProperty("usage").GetProperty("inputTokens").GetInt32());
        Assert.Equal(200, attempt.GetProperty("usage").GetProperty("outputTokens").GetInt32());
        Assert.False(run.RootElement.TryGetProperty("overheadCostUsd", out _));
    }

    [Fact]
    public async Task AGatewayAttempt_SummaryShowsTokens_NeverDollars()
    {
        (RunReport report, _) = await RunSerialAsync(GatewayResult());

        string summary = Assert.Single(report.Tasks).Summary;
        Assert.Contains("48.2k tok (gateway)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("$", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("cost not reported", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGatewayAttempt_RouteLog_LabelsTheModelAsTheCliEcho_AndNamesTheBackend()
    {
        (_, PlanDefinition plan) = await RunSerialAsync(GatewayResult());

        string routeLog = Directory.EnumerateFiles(Path.Combine(plan.PlanDirectory, "logs"), "attempt-route.log", SearchOption.AllDirectories).Single();
        string text = File.ReadAllText(routeLog);
        Assert.Contains($"gateway: {Gateway}", text, StringComparison.Ordinal);
        Assert.Contains($"backend model: {Backend}", text, StringComparison.Ordinal);
        Assert.Contains("CLI's echo", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGatewayAttempt_TelemetryRow_CarriesGatewayBackendAndTokens_WithNullCost()
    {
        (_, PlanDefinition plan) = await RunSerialAsync(GatewayResult());

        string corpus = Path.Combine(_root, "corpus");
        TelemetryIngest.Ingest(JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory)), new TelemetryCorpusStore(corpus), "repo");

        string line = File.ReadLines(Directory.EnumerateFiles(corpus, "*.jsonl").Single())
            .Single(l => l.Contains("\"attempt\":1", StringComparison.Ordinal));
        using JsonDocument row = JsonDocument.Parse(line);
        Assert.Equal(Gateway, row.RootElement.GetProperty("gateway").GetString());
        Assert.Equal(Backend, row.RootElement.GetProperty("backendModel").GetString());
        Assert.True(!row.RootElement.TryGetProperty("costUsd", out JsonElement cost) || cost.ValueKind == JsonValueKind.Null);
        Assert.Equal(48_000, row.RootElement.GetProperty("inputTokens").GetInt64());
    }

    [Fact]
    public async Task APlainClaudeAttempt_RecordsNoGatewayFields_AndKeepsItsCost()
    {
        PromptResult paid = GatewayResult() with { Gateway = null, BackendModel = null, CostUsd = 0.25m, Summary = "claude completed" };
        (RunReport report, PlanDefinition plan) = await RunSerialAsync(paid);

        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        JsonElement attempt = run.RootElement.GetProperty("tasks").GetProperty(TaskId).GetProperty("attempts")[0];
        Assert.False(attempt.GetProperty("provenance").TryGetProperty("gateway", out _));
        Assert.Equal(0.25m, attempt.GetProperty("costUsd").GetDecimal());
        Assert.Contains("; cost $0.2500", Assert.Single(report.Tasks).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAiMergeGatewayDispatch_IsRecordedInRunJson_WithNoCost()
    {
        WriteConfig();
        WriteTask();
        PlanDefinition plan = new PlanLoader().Load(PlanDir).Plan!;
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.AddOverheadDispatch("ai-merge", GatewayResult() with { Usage = new PromptUsage { InputTokens = 300_000, OutputTokens = 10_500 } });

        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        Assert.False(run.RootElement.TryGetProperty("overheadCostUsd", out _));
        JsonElement dispatch = Assert.Single(run.RootElement.GetProperty("overheadGatewayDispatches").EnumerateArray());
        Assert.Equal("ai-merge", dispatch.GetProperty("source").GetString());
        Assert.Equal(Gateway, dispatch.GetProperty("gateway").GetString());
        Assert.Equal(Backend, dispatch.GetProperty("backendModel").GetString());
        Assert.False(dispatch.TryGetProperty("costUsd", out _));

        Assert.Equal("310.5k tok (gateway)", JournalCost.Render(journal.Document));
    }

    [Fact]
    public void ANonGatewayOverheadDispatch_OnlyChargesItsCost()
    {
        WriteConfig();
        WriteTask();
        PlanDefinition plan = new PlanLoader().Load(PlanDir).Plan!;
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.AddOverheadDispatch("ai-merge", GatewayResult() with { Gateway = null, BackendModel = null, CostUsd = 0.5m });

        Assert.Equal(0.5m, journal.Document.OverheadCostUsd);
        Assert.Null(journal.Document.OverheadGatewayDispatches);
    }

    [Theory]
    [InlineData(0, "0 tok")]
    [InlineData(640, "640 tok")]
    [InlineData(1_000, "1.0k tok")]
    [InlineData(48_200, "48.2k tok")]
    [InlineData(310_549, "310.5k tok")]
    [InlineData(999_949, "999.9k tok")]
    [InlineData(999_950, "1.0M tok")]
    [InlineData(1_300_000, "1.3M tok")]
    public void Tokens_AbbreviatesInThousandsThenMillions(long tokens, string expected) =>
        Assert.Equal(expected, SpendFormat.Tokens(tokens));

    [Fact]
    public void Total_KeepsTheTwoUnitsApart_AndNeverInventsAZero()
    {
        Assert.Equal("$1.8400 + 310.5k tok (gateway)", SpendFormat.Total(1.84m, 310_500));
        Assert.Equal("$1.8400", SpendFormat.Total(1.84m, null));
        Assert.Equal("310.5k tok (gateway)", SpendFormat.Total(null, 310_500));
        Assert.Null(SpendFormat.Total(null, null));
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static PromptResult GatewayResult() => new()
    {
        Completed = true,
        IsError = false,
        ResultText = "done",
        CostUsd = null,
        Usage = new PromptUsage { InputTokens = 48_000, OutputTokens = 200 },
        ObservedModel = "Qwen",
        Gateway = Gateway,
        BackendModel = Backend,
        Summary = $"claude completed, 2 turn(s) (via gateway {Gateway})"
    };

    private sealed class StubPromptRunner(PromptResult result) : IPromptRunner
    {
        public string Name => "qwen";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private async Task<(RunReport Report, PlanDefinition Plan)> RunSerialAsync(PromptResult stubResult)
    {
        WriteConfig();
        WriteTask();

        PlanLoadResult load = new PlanLoader().Load(PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        PlanDefinition plan = load.Plan!;

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => new StubPromptRunner(stubResult));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);
        return (report, plan);
    }

    private void WriteConfig() =>
        Write(Path.Combine(PlanDir, "guardrails.json"), """
            {
              "version": 1, "workspace": ".", "maxParallelism": 1, "defaultRetries": 0, "defaultTimeoutSeconds": 60,
              "promptRunners": { "default": "qwen", "qwen": { "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" } }
            }
            """);

    private void WriteTask()
    {
        string taskDir = Path.Combine(PlanDir, "tasks", TaskId);
        Write(Path.Combine(taskDir, "task.json"),
            """{ "description": "gateway provenance fixture", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        string guardrail = Path.Combine(taskDir, "guardrails", Ps ? "01-ok.cmd" : "01-ok.sh");
        Write(guardrail, Ps ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(guardrail,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
