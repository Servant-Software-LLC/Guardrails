using System.Text.RegularExpressions;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Loading;

/// <summary>
/// The static, offline checks on claude GATEWAY blocks (#782 §2, SSOT §9.10): GR2084 (a malformed gateway block,
/// or a gateway key where it does nothing), GR2085 (a Claude model name would reach a gateway) and GR2086 (two
/// models share one gateway under parallel dispatch). Called from <see cref="PlanValidator.Validate"/>; kept in its
/// own file because every clause reads the same small set of gateway facts.
/// </summary>
internal static partial class ClaudeGatewayValidation
{
    /// <summary>The Claude Code aliases that name a Claude model without the word "claude" (GR2085).</summary>
    private static readonly HashSet<string> ClaudeAliases =
        new(StringComparer.OrdinalIgnoreCase) { "sonnet", "opus", "haiku", "fable", "opusplan", "default" };

    /// <summary>Run every gateway check over <paramref name="plan"/>, appending to <paramref name="diagnostics"/>.</summary>
    internal static void Validate(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        PromptRunnerConfig[] blocks = [.. plan.Config.PromptRunners.Values.OrderBy(r => r.Name, StringComparer.Ordinal)];

        foreach (PromptRunnerConfig block in blocks)
        {
            ValidateBlock(block, plan.PlanDirectory, diagnostics);
        }

        ValidateModelsReachingGateways(plan, diagnostics);
    }

    /// <summary>GR2084, one block at a time.</summary>
    private static void ValidateBlock(PromptRunnerConfig block, string path, List<Diagnostic> diagnostics)
    {
        string where = $"promptRunners.{block.Name}";

        foreach (string key in block.GatewayKeysInOverrides)
        {
            if (block.Kind == PromptRunnerKind.Claude)
            {
                diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                    $"{where}.guardrailOverrides.{key} is not honoured: the gateway keys are block-level only — an action " +
                    $"and a judge on one block reach one gateway. Move it to {where}.{key} (SSOT §9.10)."));
            }
            else
            {
                // Not a gateway block and never could be one: say the key does nothing, without adding an ERROR to a
                // path that has nothing to do with gateways (#782 review, corr W3).
                diagnostics.Add(Warning(DiagnosticCodes.GuardrailOverridesKeyIgnored, path,
                    $"{where}.guardrailOverrides.{key} has no effect on a '{PromptRunnerKinds.Token(block.Kind)}' block: " +
                    "guardrailOverrides carries only per-prompt settings, and this key is not one of them. Remove it."));
            }
        }

        (string Key, bool Present)[] gatewayOnly =
        [
            ("baseUrl", block.BaseUrl is not null),
            ("authTokenEnv", block.AuthTokenEnv is not null),
            ("backendModel", block.BackendModel is not null)
        ];

        if (block.Kind != PromptRunnerKind.Claude)
        {
            foreach ((string key, _) in gatewayOnly.Where(k => k.Present))
            {
                diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                    $"{where}.kind is '{PromptRunnerKinds.Token(block.Kind)}', but it declares '{key}', a claude-gateway " +
                    "key that does nothing on this kind — a key that does nothing where it was written is " +
                    $"indistinguishable from one that works. Remove '{key}' (SSOT §9.10)."));
            }

            return;
        }

        if (!block.IsClaudeGateway)
        {
            foreach ((string key, _) in gatewayOnly.Where(k => k.Present && k.Key != "baseUrl"))
            {
                diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                    $"{where} declares '{key}' but no 'baseUrl', so it is not a gateway block and '{key}' does nothing. " +
                    "Add the gateway's \"baseUrl\", or remove the key (SSOT §9.10)."));
            }

            if (block.BaseUrl is not null)
            {
                diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                    $"{where}.baseUrl is empty. Give the gateway's absolute http/https base URL, e.g. " +
                    "\"http://127.0.0.1:4000\", or remove the key (SSOT §9.10)."));
            }

            return;
        }

        ValidateBaseUrl(block.BaseUrl!, where, path, diagnostics);

        if (block.AuthTokenEnv is { } tokenEnv && !EnvNameRegex().IsMatch(tokenEnv))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.authTokenEnv must be the NAME of an environment variable ([A-Za-z_][A-Za-z0-9_]*), never the " +
                "token itself — guardrails.json is committed and hashed. Export the token under a name, e.g. " +
                "LITELLM_KEY, and put that name here (SSOT §9.10)."));
        }

        if (block.BackendModel is { } backend && backend.Trim().Length < 4)
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.backendModel '{backend}' is shorter than 4 characters, so the preflight's substring match " +
                "would prove almost nothing about which model is loaded. Declare the version, e.g. " +
                "\"qwen3.6-35b-a3b\" (SSOT §9.10)."));
        }

        if (block.ContextTokens is { } contextTokens && contextTokens < 1)
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.contextTokens is {contextTokens}, but it must be at least 1 — it is the backend's PER-SLOT " +
                "context window (llama-server -c C -np N gives each slot C/N) (SSOT §9.10)."));
        }

        if (string.IsNullOrWhiteSpace(block.Settings.Model))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where} is a gateway block but declares no 'model'. A gateway block must name the model the gateway " +
                "serves (e.g. \"Qwen\"): the harness pins every alias, background and subagent model to it, and with " +
                "none the CLI would send a Claude model name to the gateway (SSOT §9.10)."));
        }

        ReportOwnedEnv(block.Settings.Env, $"{where}.env", path, diagnostics);
        if (block.GuardrailOverrides?.Env is { } overrideEnv)
        {
            ReportOwnedEnv(overrideEnv, $"{where}.guardrailOverrides.env", path, diagnostics);
        }

        ReportSettingsFlag(block.Settings.ExtraArgs, $"{where}.extraArgs", path, diagnostics);
        if (block.GuardrailOverrides?.ExtraArgs is { } overrideArgs)
        {
            ReportSettingsFlag(overrideArgs, $"{where}.guardrailOverrides.extraArgs", path, diagnostics);
        }

        ReportModelFlags(block.Settings.ExtraArgs, $"{where}.extraArgs", path, diagnostics);
        if (block.GuardrailOverrides?.ExtraArgs is { } overrideModelArgs)
        {
            ReportModelFlags(overrideModelArgs, $"{where}.guardrailOverrides.extraArgs", path, diagnostics);
        }
    }

    /// <summary>GR2084's <c>baseUrl</c> clauses.</summary>
    private static void ValidateBaseUrl(string baseUrl, string where, string path, List<Diagnostic> diagnostics)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.baseUrl '{baseUrl}' is not an absolute http/https URL — it must include a scheme and a " +
                "host, e.g. \"http://127.0.0.1:4000\" (SSOT §9.10)."));
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.baseUrl carries userinfo (a 'user:password@' part). A credential in guardrails.json is " +
                "committed and hashed; put the token's variable NAME in 'authTokenEnv' instead (SSOT §9.10)."));
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.baseUrl carries a query ('{uri.Query}'). Claude Code appends '/v1/messages' to the base URL, " +
                "so a query would land in the middle of every request path (SSOT §9.10)."));
        }

        string trimmedPath = uri.AbsolutePath.TrimEnd('/');
        if (trimmedPath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || trimmedPath.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{where}.baseUrl ends in '{trimmedPath[trimmedPath.LastIndexOf("/v1", StringComparison.OrdinalIgnoreCase)..]}', " +
                "but Claude Code appends '/v1/messages' itself, so every request would go to '…/v1/v1/messages'. " +
                "Give the gateway's root, e.g. \"http://127.0.0.1:4000\" (SSOT §9.10)."));
        }
    }

    /// <summary>GR2084: an owned variable in an <c>env</c> map, compared case-insensitively.</summary>
    private static void ReportOwnedEnv(
        IReadOnlyDictionary<string, string> env, string key, string path, List<Diagnostic> diagnostics)
    {
        foreach (string name in env.Keys.Where(ClaudeGatewayEnvironment.IsOwned).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{key} sets '{name}', which a gateway dispatch OWNS: the harness sets it from the block itself " +
                "(baseUrl, authTokenEnv, model, contextTokens) or fixes it outright, and no operator value could " +
                $"re-enable what it disables. Remove '{name}' from {key} (SSOT §9.10)."));
        }
    }

    /// <summary>
    /// GR2084: <c>--model</c> / <c>--fallback-model</c> in a gateway block's <c>extraArgs</c> (#782 review, sec W3). The
    /// harness pins the model itself; a flag here would make the child request a model the provenance does not record,
    /// so a backend identity verified for the block's model would be claimed for a request that never named it.
    /// </summary>
    private static void ReportModelFlags(
        IReadOnlyList<string> extraArgs, string key, string path, List<Diagnostic> diagnostics)
    {
        foreach (string model in ClaudeGatewayReach.ModelFlagValues(extraArgs))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{key} passes a model flag naming '{model}', but on a gateway block the harness owns the model: it pins " +
                "--model, the alias and subagent variables and fallbackModel to the block's `model`, and records the " +
                "backend identity verified for THAT model. Remove the flag; set the block's `model`, or pin the task " +
                "with `action.model` (SSOT §9.10)."));
        }
    }

    /// <summary>GR2084: <c>--settings</c>, in either spelling, in an <c>extraArgs</c> list.</summary>
    private static void ReportSettingsFlag(
        IReadOnlyList<string> extraArgs, string key, string path, List<Diagnostic> diagnostics)
    {
        foreach (string arg in extraArgs.Where(a =>
                     string.Equals(a, "--settings", StringComparison.Ordinal)
                     || a.StartsWith("--settings=", StringComparison.Ordinal)))
        {
            diagnostics.Add(Error(DiagnosticCodes.ClaudeGatewayBlockInvalid, path,
                $"{key} carries '{arg}', but on a gateway dispatch the harness owns --settings: it passes exactly one " +
                "composed settings file (the owned env, the pinned model and fallbackModel, the containment hook), " +
                "because whether the CLI merges a second one is unverified. Remove it (SSOT §9.10)."));
        }
    }

    /// <summary>
    /// GR2085 and GR2086 over THE reach set (<see cref="ClaudeGatewayReach.Of"/>) — the same pairs the preflight
    /// probes. A model flag in a gateway block's <c>extraArgs</c> is GR2084 (reported per block above), so it is not
    /// reported again here.
    /// </summary>
    private static void ValidateModelsReachingGateways(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // (gateway endpoint key) → distinct models reaching it, in first-seen order, for GR2086.
        var modelsByEndpoint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var endpointDisplay = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ClaudeGatewayModelReach reach in ClaudeGatewayReach.Of(plan))
        {
            if (reach.Source == ClaudeGatewayModelSource.ExtraArgs)
            {
                continue;
            }

            string display = ClaudeGatewayConfig.RedactUserInfo(ClaudeGatewayConfig.NormalizeBaseUrl(reach.Block.BaseUrl!));
            if (IsClaudeModelName(reach.Model))
            {
                diagnostics.Add(Warning(DiagnosticCodes.ClaudeModelNameToGateway, plan.PlanDirectory,
                    $"{reach.Where} names '{reach.Model}', a Claude model name, and it reaches gateway block " +
                    $"'{reach.Block.Name}' ({display}). Unless the gateway's model_list maps that name on purpose, the " +
                    "request will fail or be served by a model you did not intend (#570 trap 1). Point it at the " +
                    "gateway's model, or route this dispatch to a non-gateway claude block (SSOT §9.10)."));
            }

            string key = ClaudeGatewayConfig.EndpointKey(reach.Block.BaseUrl!);
            endpointDisplay.TryAdd(key, display);
            if (!modelsByEndpoint.TryGetValue(key, out List<string>? models))
            {
                models = [];
                modelsByEndpoint[key] = models;
            }

            if (!models.Contains(reach.Model, StringComparer.Ordinal))
            {
                models.Add(reach.Model);
            }
        }

        if (plan.Config.MaxParallelism <= 1)
        {
            return;
        }

        foreach ((string key, List<string> models) in modelsByEndpoint.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (models.Count < 2)
            {
                continue;
            }

            diagnostics.Add(Warning(DiagnosticCodes.ClaudeGatewayModelsShareEndpoint, plan.PlanDirectory,
                $"maxParallelism is {plan.Config.MaxParallelism}, and {models.Count} distinct models reach one gateway " +
                $"({endpointDisplay[key]}): {string.Join(", ", models.Select(m => $"'{m}'"))}. A single llama-server " +
                "holds one model, so parallel dispatches can race it or be silently served by whichever model is " +
                "loaded (#570 trap 3). Set \"maxParallelism\": 1, or give each model its own backend; the run's " +
                "backend-identity preflight halts if two of them resolve to the same loaded model (SSOT §9.10)."));
        }
    }

    /// <summary>
    /// GR2085's test: <c>claude</c> anywhere, or an alias, case-insensitively, after removing any trailing
    /// <c>[…]</c> suffix such as <c>[1m]</c>.
    /// </summary>
    internal static bool IsClaudeModelName(string model)
    {
        string bare = BracketSuffixRegex().Replace(model.Trim(), string.Empty).Trim();
        return bare.Contains("claude", StringComparison.OrdinalIgnoreCase) || ClaudeAliases.Contains(bare);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvNameRegex();

    [GeneratedRegex(@"\[[^\]]*\]$")]
    private static partial Regex BracketSuffixRegex();

    private static Diagnostic Error(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Error,
        Path = path,
        Message = message
    };

    private static Diagnostic Warning(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Warning,
        Path = path,
        Message = message
    };
}
