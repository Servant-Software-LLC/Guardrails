using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using Guardrails.Core.Telemetry;

namespace Guardrails.Integration.Tests.ClaudeGateway;

/// <summary>
/// #782 §4, the maintainer's <c>gateway-cost</c> answer on the operator's surfaces: a gateway dispatch has no honest
/// dollar figure, so its TOKEN usage stands in for cost — never <c>$0.00</c>, never a blank — and a MIXED run keeps
/// the two units apart (<c>$X + Nk tok (gateway)</c>). Asserted on the rendered strings of the real commands:
/// <c>guardrails status</c>, the <c>guardrails run</c> summary, and the <c>guardrails telemetry report</c> COST column.
/// </summary>
public sealed class ClaudeGatewaySpendRenderingTests : IDisposable
{
    private const string Gateway = "http://127.0.0.1:4000";
    private const string Backend = "http://127.0.0.1:8080 Qwen3.6-35B-A3B";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr782-spend-" + Guid.NewGuid().ToString("N"));

    private static readonly bool Windows = OperatingSystem.IsWindows();

    private string PlanDir => Path.Combine(_root, "plan");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ───────────────────────────── status ─────────────────────────────

    [Fact]
    public async Task Status_OnAMixedRun_KeepsDollarsAndGatewayTokensApart()
    {
        await RunWithStubsAsync(("01-paid", "claude"), ("02-gw", "qwen"));

        string total = TotalLine(await InvokeAsync("status", PlanDir));

        Assert.Equal("Total prompt cost: $1.8400 + 310.5k tok (gateway)", total);
    }

    [Fact]
    public async Task Status_OnAGatewayOnlyRun_ShowsTokens_NeverDollarsOrABlank()
    {
        await RunWithStubsAsync(("02-gw", "qwen"));

        string total = TotalLine(await InvokeAsync("status", PlanDir));

        Assert.Equal("Total prompt cost: 310.5k tok (gateway)", total);
        Assert.DoesNotContain("$", total, StringComparison.Ordinal);
    }

    // ───────────────────────────── the run summary, end to end ─────────────────────────────

    [Fact]
    public async Task Run_AMixedPlanThroughAFakeGateway_PrintsTheMixedTotal_AndStatusAgrees()
    {
        // `guardrails run` reads the host's REAL managed-settings sources (there is no CLI override). A machine that
        // has one is not a machine this test can speak for — say so, rather than asserting on its contents.
        string? managed = ClaudeGatewayPreflight.DefaultManagedSettingsPaths().FirstOrDefault(File.Exists);
        Assert.SkipWhen(managed is not null, $"this host has a Claude Code managed-settings file ({managed}); the gateway preflight would read it");

        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"Qwen"}]}""");
        gateway.Routes["POST /v1/messages"] = (200, """{"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"OK"}]}""");

        using var plan = new FakeClaudePlanBuilder()
            .AddPromptTask("01-paid", cost: "1.84")
            .AddPromptTask("02-gw", cost: "5.00", env: new Dictionary<string, string>
            {
                ["FAKE_INPUT_TOKENS"] = "300000",
                ["FAKE_OUTPUT_TOKENS"] = "10500"
            });
        AddGatewayBlock(plan, gateway.BaseUrl);
        PinRunner(plan, "02-gw", "qwen");

        string output = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Contains($"Gateway: block 'qwen' → {gateway.BaseUrl}, model 'Qwen': backend identity unverified.", output, StringComparison.Ordinal);
        Assert.Equal("Total prompt cost: $1.8400 + 310.5k tok (gateway)", TotalLine(output));
        Assert.Contains("310.5k tok (gateway)", SummaryLineFor(output, "02-gw"), StringComparison.Ordinal);
        Assert.DoesNotContain("$5", output, StringComparison.Ordinal);

        // run.json holds the RAW facts — a null cost and the counts — never the abbreviation.
        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(plan.RunJsonPath));
        JsonElement gw = run.RootElement.GetProperty("tasks").GetProperty("02-gw").GetProperty("attempts")[0];
        Assert.False(gw.TryGetProperty("costUsd", out JsonElement c) && c.ValueKind != JsonValueKind.Null);
        Assert.Equal(300_000, gw.GetProperty("usage").GetProperty("inputTokens").GetInt32());
        Assert.Equal(gateway.BaseUrl, gw.GetProperty("provenance").GetProperty("gateway").GetString());
        Assert.Equal(ClaudeGatewayConfig.UnverifiedBackend, gw.GetProperty("provenance").GetProperty("backendModel").GetString());
        Assert.DoesNotContain("310.5k", run.RootElement.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(TotalLine(output), TotalLine(await InvokeAsync("status", plan.PlanDir)));
    }

    // ───────────────────────────── telemetry COST column ─────────────────────────────

    [Fact]
    public async Task TelemetryReport_AGatewayStratum_RendersTokensInTheCostColumn_APaidOneDollars()
    {
        string corpus = Path.Combine(_root, "corpus");
        var store = new TelemetryCorpusStore(corpus);
        for (int i = 0; i < TelemetryReport.DefaultMinimumSampleSize; i++)
        {
            AppendSample(store, $"run-gw-{i}", "gr782-gateway-model", gateway: Gateway, cost: null, input: 10_000, output: 2_000);
            AppendSample(store, $"run-paid-{i}", "gr782-paid-model", gateway: null, cost: 0.25m, input: 10_000, output: 2_000);
        }

        string report = await InvokeAsync("telemetry", "report", "--corpus-root", corpus);

        string gatewayRow = LineContaining(report, "gr782-gateway-model");
        Assert.EndsWith("60.0k tok (gateway)", gatewayRow.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("$", gatewayRow, StringComparison.Ordinal);
        Assert.DoesNotContain("(not reported)", gatewayRow, StringComparison.Ordinal);

        string paidRow = LineContaining(report, "gr782-paid-model");
        Assert.EndsWith("$1.25", paidRow.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("tok", paidRow, StringComparison.Ordinal);
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static void AppendSample(
        TelemetryCorpusStore store, string runId, string model, string? gateway, decimal? cost, int input, int output)
    {
        DateTimeOffset at = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        store.Append(new TelemetryRow
        {
            SchemaVersion = TelemetryRow.CurrentSchemaVersion, RunId = runId, TaskId = "01-task", Attempt = 0,
            StartedAt = at, EndedAt = at, Outcome = "succeeded", Repo = "gr782-repo"
        });
        store.Append(new TelemetryRow
        {
            SchemaVersion = TelemetryRow.CurrentSchemaVersion, RunId = runId, TaskId = "01-task", Attempt = 1,
            StartedAt = at, EndedAt = at.AddMinutes(1), Outcome = "succeeded", Repo = "gr782-repo",
            Model = model, Runner = "r", Kind = "claude", CostUsd = cost, InputTokens = input, OutputTokens = output,
            Gateway = gateway, BackendModel = gateway is null ? null : Backend
        });
    }

    private static async Task<string> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        await CommandFactory.BuildRootCommand(io).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return io.OutText;
    }

    private static string TotalLine(string output) =>
        Assert.Single(output.Split('\n'), l => l.StartsWith("Total prompt cost:", StringComparison.Ordinal)).TrimEnd('\r');

    private static string LineContaining(string output, string needle) =>
        Assert.Single(output.Split('\n'), l => l.Contains(needle, StringComparison.Ordinal)).TrimEnd('\r');

    /// <summary>The `--no-ui` settle line for <paramref name="taskId"/> — the one that carries its summary.</summary>
    private static string SummaryLineFor(string output, string taskId) =>
        output.Split('\n').Last(l => l.Contains(taskId, StringComparison.Ordinal) && l.Contains("(gateway)", StringComparison.Ordinal));

    private static void AddGatewayBlock(FakeClaudePlanBuilder plan, string baseUrl)
    {
        string path = Path.Combine(plan.PlanDir, "guardrails.json");
        string json = File.ReadAllText(path);
        string block = $$"""
            "default": "claude",
                "qwen": { "command": "{{plan.FakeCliPath.Replace("\\", "\\\\")}}", "baseUrl": "{{baseUrl}}", "model": "Qwen" },
            """;
        Assert.Contains("\"default\": \"claude\",", json, StringComparison.Ordinal);
        File.WriteAllText(path, json.Replace("\"default\": \"claude\",", block, StringComparison.Ordinal));
    }

    private static void PinRunner(FakeClaudePlanBuilder plan, string taskId, string runner)
    {
        string path = Path.Combine(plan.PlanDir, "tasks", taskId, "task.json");
        string json = File.ReadAllText(path);
        Assert.Contains("\"path\": \"action.prompt.md\",", json, StringComparison.Ordinal);
        File.WriteAllText(path, json.Replace(
            "\"path\": \"action.prompt.md\",", $"\"path\": \"action.prompt.md\", \"runner\": \"{runner}\",", StringComparison.Ordinal));
    }

    /// <summary>
    /// A real serial run of a plan with a plain claude block and a gateway block, each task pinned to one; the only
    /// fake is the <see cref="IPromptRunner"/>, which returns what each kind of runner reports — the paid one a
    /// cost, the gateway one tokens and a null cost.
    /// </summary>
    private async Task RunWithStubsAsync(params (string Id, string Runner)[] tasks)
    {
        Write(Path.Combine(PlanDir, "guardrails.json"), $$"""
            {
              "version": 1, "workspace": ".", "maxParallelism": 1, "defaultRetries": 0, "defaultTimeoutSeconds": 60,
              "promptRunners": {
                "default": "claude",
                "claude": { "model": "claude-sonnet-4-5" },
                "qwen": { "baseUrl": "{{Gateway}}", "model": "Qwen" }
              }
            }
            """);

        foreach ((string id, string runner) in tasks)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            Write(Path.Combine(taskDir, "task.json"),
                $$"""{ "description": "spend fixture", "writeScope": [], "dependsOn": [], "action": { "path": "action.prompt.md", "runner": "{{runner}}" } }""");
            Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
            string guardrail = Path.Combine(taskDir, "guardrails", Windows ? "01-ok.cmd" : "01-ok.sh");
            Write(guardrail, Windows ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(guardrail,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        PlanLoadResult load = new PlanLoader().Load(PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        PlanDefinition plan = load.Plan!;

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var registry = PromptRunnerRegistry.Build(plan.Config, block => new StubRunner(block.Name, block.IsClaudeGateway
            ? new PromptResult
            {
                Completed = true, IsError = false, ResultText = "done", CostUsd = null,
                Usage = new PromptUsage { InputTokens = 300_000, OutputTokens = 10_500 },
                Gateway = Gateway, BackendModel = Backend, Summary = $"claude completed (via gateway {Gateway})"
            }
            : new PromptResult
            {
                Completed = true, IsError = false, ResultText = "done", CostUsd = 1.84m,
                Usage = new PromptUsage { InputTokens = 1_000, OutputTokens = 100 }, Summary = "claude completed"
            }));

        var executor = new TaskExecutor(
            plan, new ProcessRunner(), new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters),
            stateManager, journal, IRunObserver.Null, registry);
        RunReport report = await new Scheduler(plan, executor, journal, maxParallelism: 1)
            .RunAsync(plan, TestContext.Current.CancellationToken);
        Assert.True(report.AllSucceeded, string.Join("\n", report.Tasks.Select(t => t.Summary)));
    }

    private sealed class StubRunner(string name, PromptResult result) : IPromptRunner
    {
        public string Name => name;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
