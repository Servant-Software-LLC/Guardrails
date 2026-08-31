using System.CommandLine;
using System.Text.RegularExpressions;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// <c>guardrails providers check &lt;block-name&gt;</c> (plan <c>28-local-inference-runner.md</c> §8,
/// issue #223) — the MANUAL, OPT-IN, non-CI verb that retires <b>dialect risk</b>: the gap the
/// <see cref="FakeOpenAiServer"/> can never close, precisely because it is a fake WE wrote and can
/// therefore only ever agree with our own assumptions. It is not in CI, not in <c>run</c>, not in
/// <c>validate</c> — the same posture as the existing opt-in real-Claude smoke (§8, last section).
///
/// <para><b>The seven dialect assumptions, each reported met / unmet / unknown (plan §8, last
/// paragraph):</b> <c>stream_options.include_usage</c> honoured; <c>tools</c> accepted AND actually
/// called; <c>num_ctx</c> honoured; the model-not-found body shape; SSE framing; <c>reasoning_effort</c>
/// tolerance; whether <c>GET /models</c> exists at all. <b>The three-way outcome is the point</b> — an
/// "unknown" the implementation collapses into "unmet" makes the report LIE about what it could not
/// determine, which is worse than not checking at all.</para>
///
/// <para><b>It is a REPORT, never a GATE.</b> The task is explicit: exit non-zero ONLY for a genuine
/// failure to REACH the endpoint (refused, DNS, timeout — the connection-level failure), never merely
/// because an assumption came back <c>unmet</c> or <c>unknown</c>. <see
/// cref="Check_EveryAssumptionUnmet_StillExitsZero_NeverGatingOnDialectResults"/> is the single most
/// important test in this file for exactly that reason.</para>
///
/// <para><b>Driven through the REAL production CLI dispatch</b>
/// (<see cref="CommandFactory.BuildRootCommand"/>), never a hand-assembled command — the issue #120
/// convention every other <c>*CliTests</c> file in this project follows, and the ONLY way a green test
/// here proves the verb is actually wired in. Against a REAL <see cref="FakeOpenAiServer"/> over a
/// loopback socket, for the same #382 reason <c>OpenAiCompatPreflightTests</c> gives: a check certified
/// only against a fake of the HTTP wire is a green light over a broken one.</para>
///
/// <para><b>Do NOT implement it here.</b> Every test below is RED against today's code because
/// <c>ProvidersCommand</c> declares only the <c>init</c> leaf (<c>ProvidersCommand.cs:54</c>) — "providers
/// check" is an unrecognized subcommand and every invocation below fails to parse. The implementing task
/// (25) adds the <c>check</c> leaf, reusing task 23's <c>PlanPreflightPhase</c> reachability /
/// tool-capability probe logic rather than duplicating it (extract-never-copy, plan §3.7).</para>
///
/// <para><b>The CLI shape this file PINS: <c>guardrails providers check [folder] &lt;block-name&gt;</c></b>
/// — an optional leading <c>folder</c> positional (defaulting to the current directory, exactly
/// <c>init</c>'s and <c>reset</c>'s own convention: <c>FolderArgument.Create()</c> declared first, a
/// second positional after it — see <c>ResetCommand.cs:21-27</c>) followed by the REQUIRED block name.
/// The plan's own usage line abbreviates to <c>&lt;block-name&gt;</c> alone (§8), but an explicit folder
/// argument is not merely convenient here — it is the only way this verb CAN be tested in this suite at
/// all: <c>ScriptPlanBuilder</c>'s own doc comment already rules out mutating the process working
/// directory, because other tests running in parallel assert on
/// <see cref="Directory.GetCurrentDirectory"/>. Every test below therefore passes the folder explicitly,
/// so the block-name/folder ORDERING is pinned but the single-token "block name alone, folder defaults to
/// cwd" case the plan's usage line shows is never exercised here (it needs no fixture and carries no risk
/// this suite must retire).</para>
///
/// <para><b>Verdict vocabulary is pinned as literal, case-insensitive whole words.</b> A report line
/// naming an assumption (using the plan's own §8 phrasing, reproduced in the <c>Assumption*</c> constants
/// below) must also carry exactly one of <c>met</c> / <c>unmet</c> / <c>unknown</c> as a whole word — see
/// <see cref="AssertVerdict"/>, which matches on a WORD BOUNDARY so a line saying "unmet" is never
/// mistaken for one saying "met".</para>
/// </summary>
public sealed class ProvidersCheckTests
{
    private const string BlockName = "local-qwen";
    private const string DeclaredModel = "test-model";

    // The plan's own §8 phrasing for each of the seven dialect assumptions — reproduced verbatim (or as
    // close as prose allows) so the report text this pins is traceable straight back to the plan.
    private const string AssumptionIncludeUsage = "stream_options.include_usage";
    private const string AssumptionToolCalling = "tools accepted and called";
    private const string AssumptionNumCtx = "num_ctx honoured";
    private const string AssumptionModelNotFoundShape = "model-not-found body shape";
    private const string AssumptionSseFraming = "SSE framing";
    private const string AssumptionReasoningEffort = "reasoning_effort tolerance";
    private const string AssumptionModelsEndpoint = "GET /models";

    private const string Met = "met";
    private const string Unmet = "unmet";
    private const string Unknown = "unknown";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Wiring + refusals: the cases that are genuine harness errors, distinct from every dialect
    // assumption below (which must NEVER produce a non-zero exit).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_EndpointUnreachable_ExitsNonZero_NamingTheEndpoint()
    {
        string deadEndpoint = $"http://127.0.0.1:{FreeLoopbackPort()}/v1";

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, deadEndpoint, DeclaredModel));

        (int exit, string output, string error) =
            await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(
            ExitCodes.HarnessError,
            exit); // nothing is listening — this is the ONE genuine "failed to reach the endpoint" case
        Assert.Contains(deadEndpoint, output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_UnknownBlockName_ExitsNonZero_NamingTheBlock()
    {
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(); // never reached — refused before any probe

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        const string typo = "loacl-qwen";
        (int exit, string output, string error) = await InvokeAsync("providers", "check", fixture.PlanDir, typo);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains(typo, output + error, StringComparison.Ordinal);
        Assert.Equal(0, server.AcceptedConnections); // a typo'd block name must open zero connections
    }

    [Fact]
    public async Task Check_BlockIsNotOpenAiCompat_ExitsNonZero_NamingTheBlock()
    {
        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, "http://127.0.0.1:1/v1", DeclaredModel));

        // "claude" is declared by PromptRunnersBlock as an ordinary kind:"claude" block with no endpoint —
        // there is nothing to probe, and the refusal must fire before any attempt to treat `command` as a URL.
        (int exit, string output, string error) = await InvokeAsync("providers", "check", fixture.PlanDir, "claude");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("claude", output + error, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The well-behaved server: every WIRE-VERIFIABLE assumption reads back "met", and the two that
    // cannot honestly be confirmed by any stateless HTTP probe — num_ctx enforcement (the server never
    // echoes what context window it actually used) and the model-not-found body shape (this server never
    // demonstrates a not-found response at all, since it answers every model name identically) — read
    // back "unknown", never a fabricated "met". Collapsing either into "met" would be exactly the kind of
    // lie §8 says the three-way outcome exists to prevent.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_WellBehavedServer_ReportsVerifiableAssumptionsMet_AndUnverifiableOnesUnknown_ExitsZero()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of() // empty queue — EVERY chat request, regardless of order or count, gets the repeated answer
            .Listing(ScriptedModels.List(DeclaredModel))
            .ThenRepeat(ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, string error) =
            await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(error);

        AssertVerdict(output, AssumptionToolCalling, Met);
        AssertVerdict(output, AssumptionIncludeUsage, Met);
        AssertVerdict(output, AssumptionReasoningEffort, Met);
        AssertVerdict(output, AssumptionSseFraming, Met);
        AssertVerdict(output, AssumptionModelsEndpoint, Met);

        // Unverifiable by construction — see the class-level rationale above. A real report here must
        // never claim "met" for either.
        AssertVerdict(output, AssumptionNumCtx, Unknown);
        AssertVerdict(output, AssumptionModelNotFoundShape, Unknown);

        // "The seam was called" corroboration, never merely the returned report text: real probes reached
        // the wire for the assumptions that need one.
        Assert.Contains(server.Requests, r => r.IsModelListing);
        Assert.Contains(server.ChatRequests, r => r.HasTools);
        Assert.Contains(server.ChatRequests, r => r.IncludeUsageRequested);
        Assert.Contains(
            server.ChatRequests,
            r => r.Body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Individual "unmet" cases — a definitive, complete answer that the assumption does NOT hold,
    // distinct from "unknown" (the probe could not tell either way; see the next section).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_ServerRejectsTools_ReportsToolCallingUnmet_AndStillExitsZero()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.List(DeclaredModel))
            .ThenRepeat(ScriptedResponse.ToolsRejected());
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, _) = await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(
            ExitCodes.Success,
            exit); // the endpoint answered — a 400 rejecting `tools` is information, not a reachability failure
        AssertVerdict(output, AssumptionToolCalling, Unmet);
    }

    [Fact]
    public async Task Check_ModelsEndpointMissing_ReportsGetModelsUnmet_AndStillExitsZero()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.NotFound())
            .ThenRepeat(ScriptedResponse.ToolCallTurn(ScriptedToolCall.Of("probe_tool", "{}")));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, _) = await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(ExitCodes.Success, exit);
        AssertVerdict(output, AssumptionModelsEndpoint, Unmet);
    }

    [Fact]
    public async Task Check_ModelNotFoundBodyMatchesExpectedShape_ReportsMet_AndStillExitsZero()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.List(DeclaredModel))
            .ThenRepeat(ScriptedResponse.ModelNotFound(DeclaredModel)); // FakeOpenAiServer.ErrorBody shape
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, _) = await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(ExitCodes.Success, exit);
        AssertVerdict(output, AssumptionModelNotFoundShape, Met);
    }

    [Fact]
    public async Task Check_ModelNotFoundBodyDoesNotMatchExpectedShape_ReportsUnmet_AndStillExitsZero()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.List(DeclaredModel))
            // A 404 with a body that is not even JSON — the server DID answer (a complete, definitive
            // observation), it just does not carry the {error:{message,type,code}} shape the harness's own
            // classification already depends on. Must never crash the check.
            .ThenRepeat(ScriptedResponse.HttpStatus(404, "model not found"));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, _) = await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(ExitCodes.Success, exit);
        AssertVerdict(output, AssumptionModelNotFoundShape, Unmet);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // "unknown" as a genuinely DIFFERENT outcome from "unmet": the server answered, but with an
    // ambiguous malfunction (500) rather than a clean, informative rejection (400) — the probe could not
    // tell whether the assumption holds. Still never gates the exit code.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_ServerErrorDuringProbe_ReportsUnknown_NotUnmet_AndStillExitsZero()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.List(DeclaredModel))
            .ThenRepeat(ScriptedResponse.HttpStatus(
                500, FakeOpenAiServer.ErrorBody("boom", "server_error", "internal_error")));
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, _) = await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(
            ExitCodes.Success,
            exit); // the endpoint is reachable and answered — a 500 is a malfunction, not "could not reach"
        AssertVerdict(output, AssumptionToolCalling, Unknown);
        Assert.NotEmpty(server.ChatRequests); // the probe genuinely reached the wire; it just got a 500 back
    }

    /// <summary>
    /// THE test the task description calls out by name: an endpoint that is fully REACHABLE but rejects
    /// every single dialect assumption must still exit 0. A verb that gated on dialect results would turn
    /// "my local model doesn't support X" into a hard failure of a verb whose entire job is to report that
    /// fact calmly — the opposite of what an opt-in, pre-hardware smoke test is for.
    /// </summary>
    [Fact]
    public async Task Check_EveryAssumptionUnmet_StillExitsZero_NeverGatingOnDialectResults()
    {
        FakeOpenAiScript script = FakeOpenAiScript
            .Of()
            .Listing(ScriptedModels.NotFound())
            .ThenRepeat(ScriptedResponse.ToolsRejected());
        await using FakeOpenAiServer server = FakeOpenAiServer.Start(script);

        using var fixture = new CheckPlanFixture();
        fixture.WritePromptRunners(PromptRunnersBlock(BlockName, server.Endpoint, DeclaredModel));

        (int exit, string output, _) = await InvokeAsync("providers", "check", fixture.PlanDir, BlockName);

        Assert.Equal(
            ExitCodes.Success,
            exit); // reachable, every assumption failed — still not a "failure to reach the endpoint"
        AssertVerdict(output, AssumptionToolCalling, Unmet);
        AssertVerdict(output, AssumptionModelsEndpoint, Unmet);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Harness.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText, io.ErrorText);
    }

    /// <summary>
    /// Assert that some line of <paramref name="report"/> names <paramref name="assumption"/> AND carries
    /// <paramref name="expectedVerdict"/> as a literal whole word (case-insensitive) — a word-boundary
    /// match so a line saying "unmet" can never satisfy an assertion that expects "met".
    /// </summary>
    private static void AssertVerdict(string report, string assumption, string expectedVerdict)
    {
        string[] matchingLines = [.. Lines(report).Where(
            l => l.Contains(assumption, StringComparison.OrdinalIgnoreCase))];

        Assert.True(
            matchingLines.Length > 0,
            $"expected a report line naming '{assumption}'. Full report:\n{report}");

        Assert.True(
            matchingLines.Any(l => Regex.IsMatch(l, $@"\b{Regex.Escape(expectedVerdict)}\b", RegexOptions.IgnoreCase)),
            $"expected a line naming '{assumption}' to carry the verdict '{expectedVerdict}'. " +
            $"Matching line(s):\n{string.Join('\n', matchingLines)}");
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>Grab a loopback ephemeral port and free it immediately — nothing answers there afterward.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// One <c>claude</c> block (the effective default, so the <c>openai-compat</c> block is never
    /// ambiguously reachable for an Action — plan §3.7 / GR2066) plus one named <c>openai-compat</c> block
    /// pointed at a test's fake endpoint.
    /// </summary>
    private static string PromptRunnersBlock(string blockName, string endpoint, string model, int contextTokens = 8192) =>
        $$"""
        "default": "claude",
        "claude": { "command": "claude", "maxTurns": 25 },
        "{{blockName}}": {
          "kind": "openai-compat",
          "endpoint": "{{endpoint}}",
          "model": "{{model}}",
          "contextTokens": {{contextTokens}}
        }
        """;

    /// <summary>
    /// A plan folder in a temp directory, minimal enough to carry a <c>guardrails.json</c> for
    /// <c>providers check</c> to read — no tasks, mirroring <c>OpenAiCompatPreflightTests</c>'
    /// <c>PreflightPlanFixture</c>: this verb is a registry scan over <c>promptRunners</c>, never a DAG
    /// run (plan §7, "discovery is a registry scan").
    /// </summary>
    private sealed class CheckPlanFixture : IDisposable
    {
        public CheckPlanFixture()
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr-providers-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));
        }

        public string PlanDir { get; }

        public void WritePromptRunners(string promptRunnersJson) =>
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"), $$"""
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

        public void Dispose()
        {
            try { Directory.Delete(PlanDir, recursive: true); }
            catch (IOException) { /* best-effort teardown */ }
        }
    }
}
