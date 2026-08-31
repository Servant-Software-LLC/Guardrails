using System.Net;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// The pre-DAG ENDPOINT PREFLIGHT tests (plan 28 §6.6/§7, issue #223) — the run-time reachability and
/// tool-capability probe that closes the false GREEN §6.6 describes: a server that accepts a
/// <c>tools</c> array, calls nothing, and returns an immaculate <c>{"pass": true}</c> from a verifier
/// that read no evidence. Reachability lives HERE, in the pre-DAG phase, and never in
/// <c>guardrails validate</c> — validate stays static and offline (plan §7); this is the one place a
/// dead endpoint or a non-tool-calling model is caught BEFORE a token is spent.
///
/// <para><b>Driven against the REAL <see cref="PlanPreflightPhase.EvaluateAsync"/> and a REAL
/// <see cref="FakeOpenAiServer"/> over a loopback socket</b> — the same seam-fidelity rule §8 states for
/// the runner itself: a check certified only against a fake of the HTTP wire is a green light over a
/// broken one. <see cref="PlanPreflightPhase"/> already hosts two other pre-DAG checks (committed
/// sample pairs, plan-level Full Flight Checks — see <c>SampleVerifierWiringTests</c> for the identical
/// direct-call idiom this file copies); this is the third, added by the task that implements against
/// these tests.</para>
///
/// <para><b>Do NOT implement it here.</b> Every test below is RED against today's
/// <c>EvaluateAsync</c> because it never looks at <c>plan.Config.PromptRunners</c> at all — a plan
/// declaring an <c>openai-compat</c> block returns <c>true</c> unconditionally today, regardless of
/// whether the endpoint exists. The one deliberate exception is
/// <see cref="NoOpenAiCompatBlock_OpensZeroConnections"/>: it legitimately passes today AND after
/// implementation, because a plan with nothing to probe must never open a connection either way — the
/// same "declared exception" shape <c>SampleVerifierWiringTests</c> uses for its own sound-pair case.</para>
///
/// <para><b>"The seam was called" is never the assertion.</b> Each halt is read off
/// <c>state/run.json</c> — <see cref="RunHalt.Kind"/> plus (where the plan requires the failure to NAME
/// something) a raw substring match on the declared model — and each proceed is corroborated against
/// what the fake server actually SAW on the wire (<see cref="FakeOpenAiServer.Requests"/> /
/// <see cref="FakeOpenAiServer.ChatRequests"/>), never merely the returned boolean. The dedup tests go
/// further still: they prove "once per (endpoint, model)" with the server's own accepted-connection-
/// derived request list, never a counter the preflight itself would increment — plan §7's explicit
/// instruction, because a self-reported counter would only prove the preflight agrees with itself.</para>
/// </summary>
public sealed class OpenAiCompatPreflightTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Reachability + model presence (plan §7, first three bullets).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reachable_ModelListed_AndToolCapable_Proceeds()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")))
            .Listing(ScriptedModels.List("test-model"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.True(
            proceed,
            "a reachable endpoint that lists the declared model, whose model answers the tool-capability " +
            "probe by calling the tool, must let the DAG proceed");

        Assert.True(
            server.Requests.Any(r => r.IsModelListing),
            "the preflight must actually GET /models — proceeding without asking is indistinguishable " +
            "from never checking at all");
        Assert.NotEmpty(server.ChatRequests);
        Assert.True(
            server.ChatRequests[0].HasTools,
            "the tool-capability probe (plan 28 §6.6/§7) must offer at least one tool on the wire — an " +
            "ordinary completion request proves nothing about tool-calling capability");

        Assert.Null(HaltOf(plan));
    }

    [Fact]
    public async Task Unreachable_ConnectionRefused_Halts()
    {
        // Grab a loopback port and free it immediately: nothing is listening there, so a client that
        // connects gets an immediate refusal — no fake server, no timeout to wait out.
        string deadEndpoint = $"http://127.0.0.1:{FreeLoopbackPort()}/v1";

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", deadEndpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.False(
            proceed,
            $"nothing is listening at {deadEndpoint} — the run must halt before the DAG rather than let a " +
            "task spend a token against a dead endpoint");
        Assert.Equal(RunHaltKind.PlanPreflightFailed, HaltOf(plan)?.Kind);
    }

    [Fact]
    public async Task Unreachable_ServerErrorFromModelsEndpoint_Halts()
    {
        // A 500 is "the server is broken", the opposite of the 404/405 "the server does not offer this"
        // case below — plan §7 is explicit that only 404/405 downgrade; every other status stays a halt.
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.Status(500, FakeOpenAiServer.ErrorBody("boom", "server_error", "internal_error")));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.False(proceed, "a 500 from /models means the server is broken, not merely that it omits the " +
            "listing endpoint — this must stay a halt, never a warning");
        Assert.Equal(RunHaltKind.PlanPreflightFailed, HaltOf(plan)?.Kind);
    }

    [Fact]
    public async Task DeclaredModelNotInListing_Halts_NamingTheModel()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.List("some-other-model"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "missing-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.False(
            proceed,
            "'missing-model' never appears in the server's /models listing, so the run must halt before " +
            "the DAG rather than let a task spend a token against a model that was never pulled");
        Assert.Equal(RunHaltKind.PlanPreflightFailed, HaltOf(plan)?.Kind);
        Assert.Contains("missing-model", ReadJournalText(plan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsEndpoint404_DowngradesToWarning_AndStillProbesToolCapability()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")))
            .Listing(ScriptedModels.NotFound());
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.True(
            proceed,
            "GET /models 404ing means the server does not OFFER a listing, not that there is no server — " +
            "an engine that serves chat perfectly while omitting the listing endpoint must not be locked " +
            "out by a check that exists to help");
        Assert.NotEmpty(
            server.ChatRequests); // only the model-PRESENCE assertion is skipped, not the tool-capability probe
        Assert.Null(HaltOf(plan));
    }

    [Fact]
    public async Task ModelsEndpoint405_DowngradesToWarning_AndStillProbesToolCapability()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")))
            .Listing(ScriptedModels.MethodNotAllowed());
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.True(proceed, "405 is the other shape of 'the server answered but does not offer this' — a warning, not a halt");
        Assert.NotEmpty(server.ChatRequests);
        Assert.Null(HaltOf(plan));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The tool-capability probe (plan §6.6/§7): three outcomes, three tests. The "capable" outcome is
    // covered by Reachable_ModelListed_AndToolCapable_Proceeds above — it IS the capable case, and
    // splitting it into a redundant duplicate would test nothing new.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCapabilityProbe_ServerRejectsTools_Halts_NamingTheModel()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(ScriptedResponse.ToolsRejected())
            .Listing(ScriptedModels.List("test-model"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.False(
            proceed,
            "the server rejected the tools array with a 400 — a server with no tool support cannot host a " +
            "verifier, so the run must halt before the DAG rather than every judge on it failing one at a time");
        Assert.Equal(RunHaltKind.PlanPreflightFailed, HaltOf(plan)?.Kind);
        Assert.Contains("test-model", ReadJournalText(plan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCapabilityProbe_RespondsWithNoToolCalls_Halts_TheSilentCase()
    {
        // §6.6's entire reason for existing: a 200 with NO tool_calls is, on the wire, indistinguishable
        // from "I considered the tools and needed none". Trusting it is the false GREEN the whole runner
        // exists to close — this is the single most important test in this file.
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(ScriptedResponse.Completion("I don't need any tools — the answer is 42."))
            .Listing(ScriptedModels.List("test-model"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            SingleOpenAiCompatBlock("judge", server.Endpoint, "test-model"));

        bool proceed = await RunPreflightAsync(plan);

        Assert.False(
            proceed,
            "the probe got a well-formed 200 that never called the trivial tool — this is the SILENT " +
            "false-green case §6.6 exists to close, and trusting it would let every judge on this endpoint " +
            "certify work it never read");
        Assert.Equal(RunHaltKind.PlanPreflightFailed, HaltOf(plan)?.Kind);
        Assert.Contains("test-model", ReadJournalText(plan), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // "Once per (endpoint, model)" (plan §7) — proved with the fake server's own accepted-connection-
    // derived request list, never a counter the preflight increments.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCapabilityProbe_RunsOnce_WhenMultipleBlocksShareTheSameEndpointAndModel()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")))
            .Listing(ScriptedModels.List("shared-model"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        string blocks =
            SingleOpenAiCompatBlock("judge-a", server.Endpoint, "shared-model") + ",\n" +
            SingleOpenAiCompatBlock("judge-b", server.Endpoint, "shared-model");
        PlanDefinition plan = fixture.LoadWithPromptRunners(blocks);

        bool proceed = await RunPreflightAsync(plan);

        Assert.True(proceed);
        Assert.Single(server.Requests, r => r.IsModelListing);
        Assert.Single(server.ChatRequests); // two blocks name the SAME (endpoint, model) — one probe, not two
    }

    [Fact]
    public async Task ToolCapabilityProbe_RunsSeparately_ForEachDistinctModelOnTheSameEndpoint()
    {
        // Model-level matters (plan §7): one server can host a model whose template emits tool calls and
        // one whose template does not, so the endpoint-level GET /models is shared but each model gets
        // its own tool-capability probe.
        FakeOpenAiScript script = FakeOpenAiScript
            .Of(
                ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")),
                ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")))
            .Listing(ScriptedModels.List("m1", "m2"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new PreflightPlanFixture();
        string blocks =
            SingleOpenAiCompatBlock("judge-small", server.Endpoint, "m1") + ",\n" +
            SingleOpenAiCompatBlock("judge-large", server.Endpoint, "m2");
        PlanDefinition plan = fixture.LoadWithPromptRunners(blocks);

        bool proceed = await RunPreflightAsync(plan);

        Assert.True(proceed);
        Assert.Single(server.Requests, r => r.IsModelListing); // one endpoint, one listing
        Assert.Equal(2, server.ChatRequests.Count); // two distinct models, two probes
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The zero-cost condition (plan §7, borrowed from plan 26 §7): a plan with nothing to probe must
    // open ZERO connections. Proved the rung-1 way — a listener that fails the test on ANY accepted
    // connection — never a counter the preflight itself increments, which would only measure our own
    // bookkeeping. This is the one test that legitimately passes today AND after implementation.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoOpenAiCompatBlock_OpensZeroConnections()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(); // never referenced anywhere below

        using var fixture = new PreflightPlanFixture();
        PlanDefinition plan = fixture.LoadWithPromptRunners(
            """
            "claude": { "command": "claude", "permissionMode": "acceptEdits", "maxTurns": 50 }
            """);

        bool proceed = await RunPreflightAsync(plan);

        Assert.True(proceed);
        Assert.Equal(
            0,
            server.AcceptedConnections);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Harness — mirrors SampleVerifierWiringTests' fixture idiom for driving
    // PlanPreflightPhase.EvaluateAsync directly (Guardrails.Cli, so the wiring test lives here rather
    // than in Guardrails.Core.Tests, which does not reference that assembly).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> RunPreflightAsync(PlanDefinition plan)
    {
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        return await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);
    }

    private static RunHalt? HaltOf(PlanDefinition plan) =>
        JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory)).Halt;

    private static string ReadJournalText(PlanDefinition plan) =>
        File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory));

    /// <summary>One <c>openai-compat</c> promptRunners entry, as a raw JSON object body (no trailing comma).</summary>
    private static string SingleOpenAiCompatBlock(string name, string endpoint, string model, int contextTokens = 8192) =>
        $$"""
        "{{name}}": {
          "kind": "openai-compat",
          "endpoint": "{{endpoint}}",
          "model": "{{model}}",
          "contextTokens": {{contextTokens}}
        }
        """;

    /// <summary>Grab a loopback ephemeral port and free it immediately — nothing answers there afterward.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A plan folder in a temp directory, minimal enough to load a <see cref="PlanDefinition"/> and drive
    /// <see cref="PlanPreflightPhase.EvaluateAsync"/> directly — no tasks, since the endpoint preflight is
    /// a registry scan over <c>promptRunners</c>, never over task usage (plan §7: "discovery is a
    /// registry scan"). <c>maxParallelism: 1</c> pins serial mode, matching the sample-pair fixture this
    /// copies, so the phase never reaches for a git worktree.
    /// </summary>
    private sealed class PreflightPlanFixture : IDisposable
    {
        private readonly string _planDir;

        public PreflightPlanFixture()
        {
            _planDir = Path.Combine(Path.GetTempPath(), "gr-openai-preflight-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));
        }

        /// <summary>Write <c>guardrails.json</c> with the given raw <c>promptRunners</c> entries and load it through the real <see cref="PlanLoader"/>.</summary>
        public PlanDefinition LoadWithPromptRunners(string promptRunnersJson)
        {
            File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), $$"""
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": 1,
                  "promptRunners": {
                    {{promptRunnersJson}}
                  }
                }
                """);

            PlanLoadResult result = new PlanLoader().Load(_planDir);
            Assert.True(result.Plan is not null, $"plan failed to load:\n{string.Join("\n", result.Diagnostics)}");
            return result.Plan!;
        }

        public void Dispose()
        {
            try { Directory.Delete(_planDir, recursive: true); }
            catch (IOException) { /* best-effort teardown */ }
        }
    }
}
