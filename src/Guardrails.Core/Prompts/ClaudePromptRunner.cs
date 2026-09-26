using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The v1 prompt runner: Claude Code headless (<c>claude -p</c>). ALL Claude-specific flag
/// spelling and stream parsing is confined to this class (SSOT §9). Invocation:
/// <code>
/// claude -p --output-format stream-json --verbose --permission-mode &lt;m&gt; --max-turns &lt;n&gt;
///   [--model &lt;m&gt;] --allowedTools &lt;joined&gt; --add-dir &lt;planDir&gt; [extraArgs…]
/// </code>
/// The composed prompt is delivered on STDIN; cwd = workspace; every raw stream line is
/// teed to <c>claude-stream.jsonl</c>. Semantic disposition: a non-zero exit OR no terminal
/// <c>result</c> message ⇒ <see cref="PromptResult.Completed"/> = false.
/// </summary>
public sealed class ClaudePromptRunner : IPromptRunner
{
    private readonly ProcessRunner _processRunner;
    private readonly string _command;
    private readonly ClaudeGatewayRunContext? _gatewayRun;

    /// <param name="name">The <c>promptRunners</c> key.</param>
    /// <param name="command">The executable to launch.</param>
    /// <param name="processRunner">The process seam.</param>
    /// <param name="gateway">
    /// The block's gateway configuration (#782, D3), or null for an ordinary claude block — which launches
    /// byte-identically to before gateways existed.
    /// </param>
    /// <param name="gatewayRun">The run's gateway state (config directory, resolved backend identities), or null.</param>
    public ClaudePromptRunner(
        string name,
        string command,
        ProcessRunner processRunner,
        ClaudeGatewayConfig? gateway = null,
        ClaudeGatewayRunContext? gatewayRun = null)
    {
        Name = name;
        _command = command;
        _processRunner = processRunner;
        Gateway = gateway;
        _gatewayRun = gatewayRun;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The gateway this instance dispatches through (#782), or null for an ordinary claude block.</summary>
    public ClaudeGatewayConfig? Gateway { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Everything after the spawn — the tee, the stall watchdog, the #452 denial fail-fast, the parse and
    /// the classification — is <see cref="StreamJsonCliSession"/>, shared with <see cref="CursorPromptRunner"/>
    /// (#764). This class keeps what is Claude's alone: the argv, the environment, and stdin delivery.
    /// </remarks>
    public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
    {
        if (Gateway is { } gateway)
        {
            return RunThroughGatewayAsync(gateway, invocation, cancellationToken);
        }

        var command = new ResolvedCommand
        {
            Executable = _command,
            Arguments = BuildArguments(invocation)
        };

        return StreamJsonCliSession.RunAsync(
            _processRunner,
            command,
            BuildEnvironment(invocation),
            standardInput: invocation.ComposedPrompt,
            invocation,
            Dialect,
            new ClaudePermissionScanner.Scanner(),
            cancellationToken);
    }

    /// <summary>
    /// A GATEWAY dispatch (#782 §1.2, D1): the child is launched with the inherited <c>ANTHROPIC_*</c> /
    /// <c>CLAUDE_CODE_USE_*</c> / OAuth / config-dir variables scrubbed, the owned values set, an isolated
    /// <c>CLAUDE_CONFIG_DIR</c>, and exactly ONE <c>--settings</c> — the composed file, which carries the owned
    /// <c>env</c>, pins <c>model</c> and <c>fallbackModel</c> to the block's model, and merges the containment hook
    /// the harness spliced into <c>extraArgs</c>. The cost is nulled at the source (§4), every summary names the
    /// gateway (§3.3), and the result carries <see cref="PromptResult.Gateway"/> and
    /// <see cref="PromptResult.BackendModel"/> for provenance.
    /// <para>Every refusal here fails CLOSED — the child is never launched with a partial authority.</para>
    /// </summary>
    private async Task<PromptResult> RunThroughGatewayAsync(
        ClaudeGatewayConfig gateway, PromptInvocation invocation, CancellationToken cancellationToken)
    {
        string suffix = $" (via gateway {gateway.DisplayBaseUrl})";

        if (gateway.Model is null)
        {
            return GatewayRefusal(gateway, suffix,
                $"gateway block '{gateway.BlockName}' declares no `model` (GR2084): with no model to pin, a Claude " +
                "model alias could reach the gateway.");
        }

        string token = ClaudeGatewayEnvironment.PlaceholderToken;
        if (gateway.AuthTokenEnv is { } tokenVariable)
        {
            string? value = Environment.GetEnvironmentVariable(tokenVariable);
            if (string.IsNullOrEmpty(value))
            {
                return GatewayRefusal(gateway, suffix,
                    $"authTokenEnv names '{tokenVariable}', which is unset or empty in the harness's environment; " +
                    "export it before `guardrails run`.");
            }

            token = value;
        }

        (IReadOnlyList<string> remainingArgs, IReadOnlyList<string> settingsPaths, string? argRefusal) =
            ClaudeGatewayLaunch.SplitSettingsArgs(invocation.Settings.ExtraArgs);
        if (argRefusal is not null)
        {
            return GatewayRefusal(gateway, suffix, argRefusal);
        }

        (System.Text.Json.Nodes.JsonObject? hooks, string? hookRefusal) = ClaudeGatewayLaunch.MergeHooks(settingsPaths);
        if (hookRefusal is not null)
        {
            return GatewayRefusal(gateway, suffix, hookRefusal);
        }

        string configDirectory = ClaudeGatewayLaunch.ConfigDirectory(_gatewayRun, invocation);
        IReadOnlyList<KeyValuePair<string, string>> owned = ClaudeGatewayLaunch.OwnedValues(gateway, token, configDirectory);
        (IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> dropped) =
            ClaudeGatewayLaunch.BuildEnvironment(invocation, owned);

        string settingsPath = ClaudeGatewayLaunch.WriteSettingsFile(
            invocation,
            ClaudeGatewayLaunch.ComposeSettingsJson(gateway, owned, hooks, tokenIsSecret: gateway.AuthTokenEnv is not null));

        PromptInvocation stripped = invocation with { Settings = invocation.Settings with { ExtraArgs = remainingArgs } };
        var command = new ResolvedCommand
        {
            Executable = _command,
            Arguments = [.. BuildArguments(stripped), "--settings", settingsPath],
            ScrubInheritedEnvironment = ClaudeGatewayLaunch.ScrubInherited
        };

        var dialect = new StreamJsonCliDialect
        {
            Label = "claude",
            CostIsFiction = true,
            SummarySuffix = suffix,
            StreamLogPreamble = ClaudeGatewayLaunch.StreamLogPreamble(gateway, dropped)
        };

        PromptResult result = await StreamJsonCliSession.RunAsync(
                _processRunner,
                command,
                environment,
                standardInput: invocation.ComposedPrompt,
                invocation,
                dialect,
                new ClaudePermissionScanner.Scanner(),
                cancellationToken)
            .ConfigureAwait(false);

        return WithGatewayProvenance(result, gateway, invocation.Settings.Model);
    }

    /// <summary>
    /// Stamp the gateway facts onto a result (#782 §4): the gateway without userinfo, and the backend identity the
    /// preflight resolved for (gateway, model) — or <c>"unverified"</c>, which claims nothing. The cost stays null.
    /// </summary>
    private PromptResult WithGatewayProvenance(PromptResult result, ClaudeGatewayConfig gateway, string? model) =>
        result with
        {
            CostUsd = null,
            Gateway = gateway.DisplayBaseUrl,
            BackendModel = _gatewayRun?.IdentityFor(gateway.BaseUrl, model ?? gateway.Model)
                           ?? ClaudeGatewayConfig.UnverifiedBackend
        };

    /// <summary>A gateway dispatch refused before launch: not completed, a configuration fault, never a retryable blip.</summary>
    private PromptResult GatewayRefusal(ClaudeGatewayConfig gateway, string suffix, string reason) =>
        WithGatewayProvenance(
            new PromptResult
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.RunnerConfiguration,
                RunnerConfigurationRemedy = reason,

                // KNOWN zero, not "not reported" (#782 review, N14): nothing was sent, so this refusal consumed no tokens
                // and must not be counted among the gateway dispatches whose usage went unmeasured.
                Usage = new PromptUsage { InputTokens = 0, OutputTokens = 0 },
                Summary = $"claude gateway dispatch refused before launch — {reason}{suffix}"
            },
            gateway,
            model: null);

    /// <summary>
    /// Claude's session dialect: the <c>claude</c> summary label. Its #86/#104 permission scanner
    /// (whose denial phrasing IS Claude's) is handed to the session per run.
    /// </summary>
    private static readonly StreamJsonCliDialect Dialect = new()
    {
        Label = "claude"
    };

    /// <summary>
    /// The Claude-specific env name for the output-token cap (issue #114). QUARANTINED here — this
    /// is the ONLY place in the codebase that knows the CLI's env-var spelling; the harness model
    /// carries only the abstract <c>maxOutputTokens</c> int (SSOT §9, never §5.1's GUARDRAILS_* set).
    /// </summary>
    internal const string MaxOutputTokensEnvVar = "CLAUDE_CODE_MAX_OUTPUT_TOKENS";

    /// <summary>
    /// The effective child environment: the harness <c>GUARDRAILS_*</c> set (<see cref="PromptInvocation.Environment"/>),
    /// overlaid with the Claude output-token cap (<see cref="MaxOutputTokensEnvVar"/>, issue #114), then
    /// the user's <c>env</c> passthrough (which wins last, so an explicit user value is authoritative).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(PromptInvocation invocation)
    {
        var env = new Dictionary<string, string>(invocation.Environment, StringComparer.Ordinal)
        {
            [MaxOutputTokensEnvVar] = invocation.Settings.MaxOutputTokens.ToString()
        };

        foreach (KeyValuePair<string, string> entry in invocation.Settings.Env)
        {
            env[entry.Key] = entry.Value;
        }

        return env;
    }

    /// <summary>Build the <c>claude</c> argument list (SSOT §9). All flag spelling lives here.</summary>
    internal static IReadOnlyList<string> BuildArguments(PromptInvocation invocation)
    {
        PromptRunnerSettings settings = invocation.Settings;
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", settings.PermissionMode,
            "--max-turns", settings.MaxTurns.ToString()
        };

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            args.Add("--model");
            args.Add(settings.Model);
        }

        // UNCONDITIONAL, exactly like the --add-dir <planDirectory> grant immediately below: the harness
        // provisions the permission its own retry protocol prescribes rather than hoping the plan author
        // (or the operator's ~/.claude/settings.json) already did. Emitted even when the plan declares
        // nothing, because ResolveToolGrants never returns an empty effective set.
        args.Add("--allowedTools");
        args.Add(string.Join(",", ResolveToolGrants(settings.AllowedTools).Effective));

        args.Add("--add-dir");
        args.Add(invocation.PlanDirectory);

        args.AddRange(settings.ExtraArgs);

        return args;
    }

    /// <summary>
    /// The ONE grant the harness provisions for itself (issue #382), spelled exactly as the #252
    /// read-only default and every <c>guardrails.json</c> spell it — a near-miss (<c>Bash(git show:*)</c>,
    /// <c>Bash(git show *)</c>) is a grant the CLI would not match. QUARANTINED here with the rest of the
    /// Claude flag spelling (SSOT §9).
    /// <para>
    /// READ-ONLY, and only this. The salvage feedback also offers a whole-patch route, but the verb that
    /// would license it mutates the tree and is unnarrowable under a prefix glob — so the harness never
    /// injects it; granting that route stays the plan author's explicit call.
    /// </para>
    /// </summary>
    internal const string SalvageInspectionGrant = "Bash(git show*)";

    /// <summary>
    /// Resolve the plan's DECLARED tool grants into the set the runner actually passes, reporting
    /// separately what the HARNESS added — the read-only git inspection grant the retry-salvage
    /// protocol (<see cref="RetryPolicy"/>'s salvage section) prescribes but has never provisioned.
    /// The result is RETURNED rather than the settings list being mutated in place, so the attempt
    /// provenance and the attempt log header can record the effective set beside the declared one
    /// instead of the two silently diverging.
    /// <para>
    /// Pure and idempotent: the declared entries keep their order, the harness grant is APPENDED only
    /// when absent, and the caller's list is never mutated (the same settings instance is reused across
    /// every attempt of every task on this runner, so an in-place append would accumulate).
    /// </para>
    /// </summary>
    internal static ToolGrantResolution ResolveToolGrants(IReadOnlyList<string> declaredTools)
    {
        var effective = new List<string>(declaredTools);
        var injected = new List<string>();

        if (!effective.Contains(SalvageInspectionGrant, StringComparer.Ordinal))
        {
            effective.Add(SalvageInspectionGrant);
            injected.Add(SalvageInspectionGrant);
        }

        return new ToolGrantResolution { Effective = effective, Injected = injected };
    }

    /// <summary>
    /// The part of a runner's stdout that is NOT stream content (#516). Lives on
    /// <see cref="StreamJsonCliSession.NonStreamStdout"/> since #764; kept here so the existing callers and
    /// tests that name it on this class keep working.
    /// </summary>
    internal static string? NonStreamStdout(string? stdout) => StreamJsonCliSession.NonStreamStdout(stdout);
}

/// <summary>
/// The outcome of <see cref="ClaudePromptRunner.ResolveToolGrants"/>: the grants actually handed to
/// the CLI, and — held separately, never folded away — the subset the HARNESS contributed. Keeping
/// the two apart is what makes the effective permission set auditable: a run can show what the plan
/// declared and what the harness added on top, instead of one merged list nobody can attribute.
/// </summary>
internal sealed record ToolGrantResolution
{
    /// <summary>
    /// The effective grants passed via <c>--allowedTools</c>: the declared entries (relative order
    /// preserved) plus <see cref="Injected"/>. Never empty — the harness always provisions its own grant.
    /// </summary>
    public required IReadOnlyList<string> Effective { get; init; }

    /// <summary>
    /// ONLY what the harness added on top of the declared list. Empty when the plan already declared
    /// everything the harness needs — the grant is provisioned, never duplicated.
    /// </summary>
    public required IReadOnlyList<string> Injected { get; init; }
}
