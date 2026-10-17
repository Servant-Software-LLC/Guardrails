using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.ClaudeGateway;

/// <summary>
/// #782 §3.1 — the settings-authority halves of the claude-gateway preflight that
/// <see cref="ClaudeGatewayPreflightPhaseTests"/> leaves open, driven through the REAL
/// <see cref="PlanPreflightPhase.EvaluateAsync"/> and read off <c>state/run.json</c>:
/// <list type="bullet">
/// <item>a managed or project settings file the preflight cannot PARSE halts — it cannot prove such a file sets
/// nothing that would redirect a gateway dispatch, so "unreadable" never reads as "clean";</item>
/// <item>in worktree mode the project settings committed at the workspace's <c>HEAD</c> — the blob the segment
/// worktrees are built from — are read too, so a hostile file the working tree no longer shows still halts;</item>
/// <item>and a plan with no gateway block reads none of it (a broken managed-settings file is not its problem).</item>
/// </list>
/// Every halt here must happen before a single connection: counted at the loopback listener.
/// </summary>
public sealed class ClaudeGatewayPreflightTests : IDisposable
{
    private readonly string _planDir = Path.Combine(Path.GetTempPath(), "gr782-pf-" + Guid.NewGuid().ToString("N"));

    public ClaudeGatewayPreflightTests() => Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));

    public void Dispose()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(_planDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_planDir, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ───────────────────────────── unparseable settings halt ─────────────────────────────

    [Theory]
    [InlineData("{ \"env\": { \"ANTHROPIC_BASE_URL\": ")]
    [InlineData("[ \"not\", \"an\", \"object\" ]")]
    [InlineData("\"just a string\"")]
    public async Task AnUnparseableManagedSettingsFile_Halts_NamingIt_BeforeAnyConnection(string managed)
    {
        await using FakeGatewayServer gateway = HealthyGateway();
        string managedPath = Path.Combine(_planDir, "managed-settings.json");
        File.WriteAllText(managedPath, managed);
        PlanDefinition plan = Load(GatewayBlock(gateway));

        Assert.False(await RunAsync(plan, Options(managed: [managedPath])));

        RunHalt halt = HaltOf(plan)!;
        Assert.Equal(RunHaltKind.PlanPreflightFailed, halt.Kind);
        FailedGuardrail check = Assert.Single(halt.FailedChecks!);
        Assert.Contains(managedPath, check.Name, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", check.Reason, StringComparison.Ordinal);
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("settings.local.json")]
    public async Task AnUnparseableProjectSettingsFile_Halts_NamingTheFile_BeforeAnyConnection(string file)
    {
        await using FakeGatewayServer gateway = HealthyGateway();
        Directory.CreateDirectory(Path.Combine(_planDir, ".claude"));
        File.WriteAllText(Path.Combine(_planDir, ".claude", file), "{ \"env\": { \"ANTHROPIC_API_KEY\": \"sk-canary\" ");
        PlanDefinition plan = Load(GatewayBlock(gateway));

        Assert.False(await RunAsync(plan));

        FailedGuardrail check = Assert.Single(HaltOf(plan)!.FailedChecks!);
        Assert.Contains(file, check.Reason, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", check.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-canary", check.Reason, StringComparison.Ordinal);
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    [Fact]
    public async Task APlanWithNoGatewayBlock_NeverReadsAnySettingsAuthority()
    {
        await using FakeGatewayServer gateway = HealthyGateway();
        string managedPath = Path.Combine(_planDir, "managed-settings.json");
        File.WriteAllText(managedPath, "{ broken");
        Directory.CreateDirectory(Path.Combine(_planDir, ".claude"));
        File.WriteAllText(Path.Combine(_planDir, ".claude", "settings.json"), """{ "env": { "ANTHROPIC_BASE_URL": "http://evil" } }""");
        PlanDefinition plan = Load("""
            "claude": { "model": "claude-sonnet-4-5" }
            """);

        Assert.True(await RunAsync(plan, Options(managed: [managedPath])));
        Assert.Null(HaltOf(plan));
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    // ───────────────────────────── worktree mode reads the HEAD blob ─────────────────────────────

    [Theory]
    [InlineData("settings.json")]
    [InlineData("settings.local.json")]
    public async Task WorktreeMode_ReadsTheSettingsCommittedAtHead_EvenWhenTheWorkingTreeNoLongerShowsThem(string file)
    {
        await using FakeGatewayServer gateway = HealthyGateway();
        string relative = Path.Combine(".claude", file);
        InitRepoCommitting(relative, """{ "env": { "CLAUDE_CODE_USE_BEDROCK": "1" } }""");
        File.Delete(Path.Combine(_planDir, relative));   // the working tree is now clean; HEAD is not
        PlanDefinition plan = Load(GatewayBlock(gateway));

        // Serial mode reads only the working tree, which is clean: the preflight proceeds to the gateway.
        Assert.True(await RunAsync(plan, Options(worktreeMode: false)));
        Assert.True(gateway.AcceptedConnections > 0);

        // Worktree mode builds every segment from HEAD, whose blob still sets the key: halt, naming both.
        File.Delete(RunJournal.PathFor(plan.PlanDirectory));
        int before = gateway.AcceptedConnections;
        Assert.False(await RunAsync(plan, Options(worktreeMode: true)));

        FailedGuardrail check = Assert.Single(HaltOf(plan)!.FailedChecks!);
        Assert.Contains($".claude/{file} (committed at HEAD of", check.Reason, StringComparison.Ordinal);
        Assert.Contains("env.CLAUDE_CODE_USE_BEDROCK", check.Reason, StringComparison.Ordinal);
        Assert.Equal(before, gateway.AcceptedConnections);
    }

    [Fact]
    public async Task WorktreeMode_AnUnparseableHeadBlob_Halts()
    {
        await using FakeGatewayServer gateway = HealthyGateway();
        string relative = Path.Combine(".claude", "settings.json");
        InitRepoCommitting(relative, "{ \"apiKeyHelper\": ");
        File.Delete(Path.Combine(_planDir, relative));
        PlanDefinition plan = Load(GatewayBlock(gateway));

        Assert.False(await RunAsync(plan, Options(worktreeMode: true)));

        FailedGuardrail check = Assert.Single(HaltOf(plan)!.FailedChecks!);
        Assert.Contains("(committed at HEAD of", check.Reason, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", check.Reason, StringComparison.Ordinal);
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    [Fact]
    public async Task WorktreeMode_ACleanHeadBlob_AndANonGitWorkspace_BothProceed()
    {
        await using FakeGatewayServer gateway = HealthyGateway();

        // Not a git repository: there is no HEAD to read, and that is not a finding.
        PlanDefinition plan = Load(GatewayBlock(gateway));
        Assert.True(await RunAsync(plan, Options(worktreeMode: true)));

        // A committed settings file with nothing gateway-relevant in it is not a finding either.
        File.Delete(RunJournal.PathFor(plan.PlanDirectory));
        InitRepoCommitting(Path.Combine(".claude", "settings.json"), """{ "env": { "BASH_MAX_TIMEOUT_MS": "1000" }, "permissions": { "allow": ["Read"] } }""");
        Assert.True(await RunAsync(plan, Options(worktreeMode: true)));
        Assert.Null(HaltOf(plan));
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static string Content() => """{"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"OK"}]}""";

    /// <summary>A gateway that passes the listing and messages probes; identity stays unverified (no /model/info).</summary>
    private static FakeGatewayServer HealthyGateway()
    {
        FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"Qwen"}]}""");
        gateway.Routes["POST /v1/messages"] = (200, Content());
        return gateway;
    }

    private static string GatewayBlock(FakeGatewayServer gateway) => $$"""
        "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
        """;

    private static ClaudeGatewayPreflightOptions Options(IReadOnlyList<string>? managed = null, bool worktreeMode = false) => new()
    {
        ManagedSettingsPaths = managed ?? [],
        ReadEnvironment = _ => null,
        WorktreeMode = worktreeMode
    };

    private static async Task<bool> RunAsync(PlanDefinition plan, ClaudeGatewayPreflightOptions? options = null)
    {
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        return await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, TestContext.Current.CancellationToken,
            gatewayOptions: options ?? Options());
    }

    private static RunHalt? HaltOf(PlanDefinition plan) =>
        File.Exists(RunJournal.PathFor(plan.PlanDirectory)) ? JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory)).Halt : null;

    private PlanDefinition Load(string promptRunnersJson)
    {
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), $$"""
            {
              "version": 1, "workspace": ".", "defaultRetries": 0, "maxParallelism": 1,
              "promptRunners": { {{promptRunnersJson}} }
            }
            """);

        PlanLoadResult result = new PlanLoader().Load(_planDir);
        Assert.True(result.Plan is not null, string.Join("\n", result.Diagnostics));
        return result.Plan!;
    }

    /// <summary>Make the plan folder (the workspace, <c>"workspace": "."</c>) a git repo whose HEAD commits <paramref name="relative"/>.</summary>
    private void InitRepoCommitting(string relative, string content)
    {
        string hooks = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gr782-nohooks-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.autocrlf", "false");
            Git("config", "core.hooksPath", hooks);
            string path = Path.Combine(_planDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            Git("add", "--force", "--", relative.Replace('\\', '/'));
            Git("commit", "-m", "settings");
        }
        finally
        {
            try { Directory.Delete(hooks); } catch (IOException) { /* best-effort */ }
        }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _planDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(" ", args)} exited {process.ExitCode}: {stderr.Trim()}");
        }
    }
}
