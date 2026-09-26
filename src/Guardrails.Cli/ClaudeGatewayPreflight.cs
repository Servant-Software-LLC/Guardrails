using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Cli;

/// <summary>
/// Inputs a caller (or a test) may inject into the claude-gateway preflight, and — once it has run — what it
/// resolved. One object so <see cref="PlanPreflightPhase.EvaluateAsync"/> gains a single optional parameter.
/// </summary>
public sealed class ClaudeGatewayPreflightOptions
{
    /// <summary>
    /// The managed-settings files to read, or null for the documented sources of the host OS
    /// (<see cref="ClaudeGatewayPreflight.DefaultManagedSettingsPaths"/>). Tests inject a path here.
    /// </summary>
    public IReadOnlyList<string>? ManagedSettingsPaths { get; init; }

    /// <summary>Reads an environment variable (the <c>authTokenEnv</c> check). Defaults to the process environment.</summary>
    public Func<string, string?> ReadEnvironment { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// True when the run uses worktree mode: the project settings committed at the workspace's <c>HEAD</c> — the
    /// base the segment worktrees are built from — are read as well as the working-tree files.
    /// </summary>
    public bool WorktreeMode { get; init; }

    /// <summary>
    /// SET BY the preflight when it passes: the resolved backend identities, keyed by
    /// <see cref="ClaudeGatewayRunContext.IdentityKey"/>. Pairs it could not resolve are absent (their dispatches
    /// then record <c>"unverified"</c>). Empty when the plan declares no gateway block.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolvedIdentities { get; internal set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The pre-DAG preflight for claude GATEWAY blocks (#782 §3), run by <see cref="PlanPreflightPhase"/> beside the
/// openai-compat endpoint check and on the same terms: before the DAG, burning no retries, recording a halt as the
/// journal's top-level <c>halt</c>. A plan with no gateway block opens ZERO connections — discovery is a registry
/// scan, decided before an <see cref="HttpClient"/> exists.
/// <list type="number">
/// <item>Managed settings (once per run): halt when any documented source sets an owned or scrubbed <c>env</c>
/// variable, <c>apiKeyHelper</c>, <c>fallbackModel</c>, <c>model</c>, <c>availableModels</c>,
/// <c>forceLoginMethod: "gateway"</c> or <c>forceLoginGatewayUrl</c> — nothing the harness passes outranks them.</item>
/// <item>Project settings (once per run): <c>.claude/settings.json</c> and <c>.claude/settings.local.json</c> in the
/// workspace (and, in worktree mode, as committed at its HEAD): halt on an owned or scrubbed <c>env</c> key,
/// <c>apiKeyHelper</c>, <c>forceLoginMethod</c> or <c>forceLoginGatewayUrl</c>, naming the file and the key.</item>
/// <item><c>authTokenEnv</c> (per gateway block): halt when unset or empty, naming the variable.</item>
/// <item><c>GET {baseUrl}/v1/models</c> (once per gateway): halt on any transport failure, 5xx, 401/403, or a model
/// not listed; 404/405 downgrade to a warning and skip only the model check.</item>
/// <item><c>POST {baseUrl}/v1/messages</c> (once per (gateway, model)): halt unless a 200 with a content block.</item>
/// <item>Backend identity (D2): <c>GET {baseUrl}/model/info</c> → normalized <c>api_base</c> → <c>GET /props</c> (or
/// <c>/v1/models</c>). Halt when two models resolve to one loaded model, a declared <c>backendModel</c> does not
/// match, or <c>contextTokens</c> exceeds the per-slot <c>n_ctx</c>. Unresolvable ⇒ "unverified", never a claim.</item>
/// </list>
/// </summary>
public static class ClaudeGatewayPreflight
{
    /// <summary>The halt headline's fixed prefix — also how <see cref="PlanPreflightPhase.HaltHasOwnConsoleReport"/> recognises it.</summary>
    public const string HeadlinePrefix = "claude gateway preflight FAILED — halting before scheduling any task: ";

    /// <summary>The short timeout on each listing/identity probe: this runs before the DAG, and slow is worth knowing now.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The <c>/v1/messages</c> probe's own bound: a local model's first answer (and its thinking) takes time.</summary>
    private static readonly TimeSpan MessagesProbeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>§3.1: 256, not 1 — room for a reasoning model's thinking before a content block appears (#759).</summary>
    private const int MessagesProbeMaxTokens = 256;

    /// <summary>The documented managed-settings sources for the host OS (Claude Code's settings documentation).</summary>
    public static IReadOnlyList<string> DefaultManagedSettingsPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                @"C:\Program Files\ClaudeCode\managed-settings.json",
                @"C:\ProgramData\ClaudeCode\managed-settings.json"
            ];
        }

        return OperatingSystem.IsMacOS()
            ? ["/Library/Application Support/ClaudeCode/managed-settings.json"]
            : ["/etc/claude-code/managed-settings.json"];
    }

    /// <summary>
    /// Run the preflight. Returns true when scheduling may proceed. On a halt, the failures are journaled (a
    /// plan-preflight-failed section plus the top-level <c>halt</c>) and printed before this returns.
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        RunJournal journal,
        TextWriter? consoleOut,
        ClaudeGatewayPreflightOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new ClaudeGatewayPreflightOptions();

        // Discovery is a REGISTRY SCAN — decided before any HttpClient exists, so a plan with no gateway block
        // opens zero connections by construction.
        List<PromptRunnerConfig> gateways = [.. plan.Config.PromptRunners.Values
            .Where(b => b.IsClaudeGateway)
            .OrderBy(b => b.Name, StringComparer.Ordinal)];
        if (gateways.Count == 0)
        {
            return true;
        }

        var failures = new List<PlanPreflightCheck>();
        var notes = new List<string>();

        CheckManagedSettings(options.ManagedSettingsPaths ?? DefaultManagedSettingsPaths(), failures);
        CheckProjectSettings(plan.Workspace, options.WorktreeMode, failures);

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PromptRunnerConfig block in gateways)
        {
            if (block.AuthTokenEnv is not { } variable || string.IsNullOrWhiteSpace(variable))
            {
                tokens[block.Name] = ClaudeGatewayEnvironment.PlaceholderToken;
                continue;
            }

            string? value = options.ReadEnvironment(variable.Trim());
            if (string.IsNullOrEmpty(value))
            {
                failures.Add(Check($"claude gateway block '{block.Name}' authTokenEnv",
                    $"authTokenEnv names '{variable}', which is unset or empty in this shell. Export it (the gateway's " +
                    $"key) before `guardrails run`; the block '{block.Name}' cannot authenticate without it."));
                continue;
            }

            tokens[block.Name] = value;
        }

        // Configuration halts come first and alone: probing a gateway on behalf of a run that must halt anyway
        // would spend the operator's time (and, for a remote gateway, their key) for nothing.
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (failures.Count == 0)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            foreach (GatewayTarget target in GroupByGateway(gateways, tokens))
            {
                await ProbeGatewayAsync(http, target, failures, notes, identities, cancellationToken).ConfigureAwait(false);
            }

            CheckSharedIdentities(identities, failures);
        }

        if (failures.Count > 0)
        {
            RecordHalt(plan, journal, failures);
            WriteFailureReport(failures, consoleOut);
            return false;
        }

        options.ResolvedIdentities = identities;
        WriteSuccessReport(plan, gateways, identities, notes, consoleOut);
        return true;
    }

    // ───────────────────────────── settings authorities ─────────────────────────────

    /// <summary>§1.2: managed settings outrank both isolation layers, so the harness can only detect and refuse.</summary>
    private static void CheckManagedSettings(IReadOnlyList<string> paths, List<PlanPreflightCheck> failures)
    {
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (!TryReadJsonObject(File.ReadAllText(path), out JsonObject? settings, out string? error))
            {
                failures.Add(Check($"managed settings {path}",
                    $"the managed-settings file '{path}' could not be parsed ({error}). Managed settings outrank " +
                    "everything the harness passes, so a file it cannot read could redirect a gateway dispatch; " +
                    "fix or remove it, then re-run."));
                continue;
            }

            foreach (string finding in ManagedFindings(settings!))
            {
                failures.Add(Check($"managed settings {path}",
                    $"'{path}' sets {finding}. Managed settings outrank the harness's isolated config directory and its " +
                    "--settings file, so a gateway dispatch cannot be guaranteed to reach the gateway the plan names " +
                    "with the credential it grants. Remove the setting, or run this plan on a machine without it."));
            }
        }
    }

    /// <summary>What in one managed-settings object makes a gateway dispatch unenforceable.</summary>
    internal static IEnumerable<string> ManagedFindings(JsonObject settings)
    {
        foreach (string name in EnvNames(settings).Where(ClaudeGatewayEnvironment.IsOwnedOrScrubbed))
        {
            yield return $"env.{name}";
        }

        foreach (string key in new[] { "apiKeyHelper", "fallbackModel", "model", "availableModels", "forceLoginGatewayUrl" })
        {
            if (settings.ContainsKey(key))
            {
                yield return $"'{key}'";
            }
        }

        if (settings["forceLoginMethod"] is JsonValue method
            && method.TryGetValue(out string? value)
            && string.Equals(value, "gateway", StringComparison.OrdinalIgnoreCase))
        {
            yield return "'forceLoginMethod: \"gateway\"'";
        }
    }

    /// <summary>§1.2: <c>--settings</c> wins only for the keys it sets, so project keys it does not set are refused.</summary>
    private static void CheckProjectSettings(string workspace, bool worktreeMode, List<PlanPreflightCheck> failures)
    {
        string[] relative = [".claude/settings.json", ".claude/settings.local.json"];
        foreach (string file in relative)
        {
            string path = Path.Combine(workspace, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                ReportProjectFile(path, File.ReadAllText(path), failures);
            }

            if (worktreeMode && GitShowHead(workspace, file) is { } committed)
            {
                ReportProjectFile($"{file} (committed at HEAD of {workspace})", committed, failures);
            }
        }
    }

    private static void ReportProjectFile(string label, string text, List<PlanPreflightCheck> failures)
    {
        if (!TryReadJsonObject(text, out JsonObject? settings, out string? error))
        {
            failures.Add(Check($"project settings {label}",
                $"'{label}' could not be parsed ({error}), so the preflight cannot prove it sets nothing that would " +
                "redirect a gateway dispatch. Fix the file, then re-run."));
            return;
        }

        foreach (string finding in ProjectFindings(settings!))
        {
            failures.Add(Check($"project settings {label}",
                $"'{label}' sets {finding}. The harness's --settings file overrides only the keys it sets, so this " +
                "would still reach a gateway dispatch — sending a request to a server the plan did not name, or with a " +
                "credential it did not grant. Remove it from the project settings for this run (move it to your user " +
                "settings, which a gateway dispatch does not read)."));
        }
    }

    /// <summary>What in one project-settings object a gateway dispatch cannot override.</summary>
    internal static IEnumerable<string> ProjectFindings(JsonObject settings)
    {
        foreach (string name in EnvNames(settings).Where(ClaudeGatewayEnvironment.IsOwnedOrScrubbed))
        {
            yield return $"env.{name}";
        }

        foreach (string key in new[] { "apiKeyHelper", "forceLoginMethod", "forceLoginGatewayUrl" })
        {
            if (settings.ContainsKey(key))
            {
                yield return $"'{key}'";
            }
        }
    }

    private static IEnumerable<string> EnvNames(JsonObject settings) =>
        settings["env"] is JsonObject env ? env.Select(p => p.Key) : [];

    private static bool TryReadJsonObject(string text, out JsonObject? value, out string? error)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            value = node as JsonObject;
            error = value is null ? "the top level is not a JSON object" : null;
            return value is not null;
        }
        catch (JsonException ex)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary><c>git show HEAD:&lt;file&gt;</c> in <paramref name="workspace"/>, or null when there is none.</summary>
    private static string? GitShowHead(string workspace, string file)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(workspace);
            start.ArgumentList.Add("show");
            start.ArgumentList.Add("HEAD:" + file);

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // ───────────────────────────── gateway probes ─────────────────────────────

    /// <summary>One distinct gateway (loopback spellings folded) and the (block, model) pairs that reach it.</summary>
    private sealed class GatewayTarget(string baseUrl, string token)
    {
        public string BaseUrl { get; } = baseUrl;
        public string Display { get; } = ClaudeGatewayConfig.RedactUserInfo(baseUrl);
        public string Token { get; } = token;
        public List<(PromptRunnerConfig Block, string Model, string Token)> Models { get; } = [];
    }

    private static List<GatewayTarget> GroupByGateway(
        List<PromptRunnerConfig> gateways, IReadOnlyDictionary<string, string> tokens)
    {
        var byKey = new Dictionary<string, GatewayTarget>(StringComparer.Ordinal);
        var ordered = new List<GatewayTarget>();

        foreach (PromptRunnerConfig block in gateways)
        {
            string baseUrl = ClaudeGatewayConfig.NormalizeBaseUrl(block.BaseUrl!);
            string key = ClaudeGatewayConfig.EndpointKey(baseUrl);
            string token = tokens.TryGetValue(block.Name, out string? t) ? t : ClaudeGatewayEnvironment.PlaceholderToken;
            if (!byKey.TryGetValue(key, out GatewayTarget? target))
            {
                target = new GatewayTarget(baseUrl, token);
                byKey[key] = target;
                ordered.Add(target);
            }

            foreach (string? model in new[] { block.Settings.Model, block.GuardrailOverrides?.Model })
            {
                if (!string.IsNullOrWhiteSpace(model) && !target.Models.Any(m => m.Model == model))
                {
                    target.Models.Add((block, model, token));
                }
            }
        }

        return ordered;
    }

    private static async Task ProbeGatewayAsync(
        HttpClient http,
        GatewayTarget target,
        List<PlanPreflightCheck> failures,
        List<string> notes,
        Dictionary<string, string> identities,
        CancellationToken cancellationToken)
    {
        // ── GET /v1/models ──
        Probe listing = await SendAsync(http, HttpMethod.Get, target.BaseUrl + "/v1/models", target.Token, body: null,
            ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (listing.TransportFailure is { } transport)
        {
            failures.Add(Check($"claude gateway {target.Display}",
                $"{target.Display} could not be reached — {transport}. Start the gateway (and its backend), or correct " +
                "the block's baseUrl; no task will spend a turn against a gateway that is not there."));
            return;
        }

        IReadOnlyList<string>? listed = null;
        if (listing.Status is 404 or 405)
        {
            notes.Add($"WARNING: {target.Display} answered HTTP {listing.Status} for GET /v1/models, so the declared " +
                      "model(s) cannot be confirmed present; the /v1/messages probe still runs.");
        }
        else if (listing.Status is 401 or 403)
        {
            failures.Add(Check($"claude gateway {target.Display}",
                $"GET {target.Display}/v1/models answered HTTP {listing.Status}: the gateway refused the credential. " +
                $"Check the block's authTokenEnv and the gateway's keys. {Snippet(listing.Body)}"));
            return;
        }
        else if (listing.Status is < 200 or >= 300)
        {
            failures.Add(Check($"claude gateway {target.Display}",
                $"GET {target.Display}/v1/models answered HTTP {listing.Status} — the gateway reporting itself broken, " +
                $"not merely declining a listing (404/405 would be). {Snippet(listing.Body)}"));
            return;
        }
        else
        {
            listed = ListedIds(listing.Body);
        }

        // ── GET /model/info (LiteLLM), once per gateway, for the D2 resolution below ──
        Probe modelInfo = await SendAsync(http, HttpMethod.Get, target.BaseUrl + "/model/info", target.Token, body: null,
            ProbeTimeout, cancellationToken).ConfigureAwait(false);
        JsonArray? modelInfoData = modelInfo is { TransportFailure: null, Status: >= 200 and < 300 }
            ? DataArray(modelInfo.Body)
            : null;

        var backendCache = new Dictionary<string, BackendReport?>(StringComparer.Ordinal);

        foreach ((PromptRunnerConfig block, string model, string token) in target.Models)
        {
            string check = $"claude gateway {target.Display} (model '{model}')";

            if (listed is not null && !listed.Contains(model, StringComparer.Ordinal))
            {
                failures.Add(Check(check,
                    $"{target.Display} does not list the model '{model}' that block '{block.Name}' declares — it reported " +
                    $"{(listed.Count == 0 ? "no models at all" : string.Join(", ", listed.Select(i => $"'{i}'")))}. " +
                    "Add it to the gateway's model_list, or correct the block's model."));
                continue;
            }

            // ── POST /v1/messages ──
            string request = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = MessagesProbeMaxTokens,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word OK." })
            }.ToJsonString();
            Probe messages = await SendAsync(http, HttpMethod.Post, target.BaseUrl + "/v1/messages", token, request,
                MessagesProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (messages.TransportFailure is { } messagesTransport)
            {
                failures.Add(Check(check, $"POST {target.Display}/v1/messages for '{model}' failed — {messagesTransport}."));
                continue;
            }

            if (messages.Status != 200 || !HasContentBlock(messages.Body))
            {
                failures.Add(Check(check,
                    $"POST {target.Display}/v1/messages for '{model}' (block '{block.Name}') answered HTTP {messages.Status} " +
                    $"without a content block, so Claude Code's requests would fail the same way. {Snippet(messages.Body)}"));
                continue;
            }

            // ── D2: backend identity ──
            BackendReport? backend = await ResolveBackendAsync(
                http, modelInfoData, model, token, backendCache, cancellationToken).ConfigureAwait(false);
            if (backend is null)
            {
                if (block.BackendModel is { } declaredUnverified)
                {
                    notes.Add($"gateway block '{block.Name}': backendModel '{declaredUnverified}' declared, not verified " +
                              "(the backend's identity could not be resolved).");
                }

                continue;
            }

            identities[ClaudeGatewayRunContext.IdentityKey(target.BaseUrl, model)] = backend.Identity;

            if (block.BackendModel is { } declared && !ClaudeGatewayBackendIdentity.Matches(declared, backend.Alias, backend.ModelPath))
            {
                failures.Add(Check(check,
                    $"block '{block.Name}' declares backendModel '{declared}', but the backend behind '{model}' has " +
                    $"{backend.Describe()} loaded (#760: a gateway alias served by a backend with a different model). " +
                    "Load the declared model, point the gateway's model_list at the right backend, or correct backendModel."));
            }

            if (block.ContextTokens is { } contextTokens && backend.PerSlotContext is { } nCtx && contextTokens > nCtx)
            {
                failures.Add(Check(check,
                    $"block '{block.Name}' declares contextTokens {contextTokens}, but the backend behind '{model}' reports a " +
                    $"per-slot n_ctx of {nCtx} (llama-server -c C -np N gives each slot C/N). Claude Code would fill the " +
                    "slot before compacting; lower contextTokens to at most the per-slot window."));
            }
        }
    }

    /// <summary>§3.2: two distinct model strings resolving to the SAME loaded model is #760's silent substitution.</summary>
    private static void CheckSharedIdentities(
        IReadOnlyDictionary<string, string> identities, List<PlanPreflightCheck> failures)
    {
        var modelsByIdentity = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach ((string key, string identity) in identities)
        {
            string model = key[(key.IndexOf('\n', StringComparison.Ordinal) + 1)..];
            if (!modelsByIdentity.TryGetValue(identity, out SortedSet<string>? models))
            {
                models = new SortedSet<string>(StringComparer.Ordinal);
                modelsByIdentity[identity] = models;
            }

            models.Add(model);
        }

        foreach ((string identity, SortedSet<string> models) in modelsByIdentity.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (models.Count < 2)
            {
                continue;
            }

            failures.Add(Check($"claude gateway backend {identity}",
                $"{models.Count} distinct models — {string.Join(", ", models.Select(m => $"'{m}'"))} — resolve to ONE loaded " +
                $"backend model ({identity}). Every dispatch naming any of them would be served by the same model " +
                "(#760). Give each model its own backend (another llama-server port in the gateway's model_list)."));
        }
    }

    /// <summary>What the backend reported about the model it has loaded.</summary>
    private sealed record BackendReport(string Base, string? Alias, string? ModelPath, int? PerSlotContext)
    {
        public string Identity => ClaudeGatewayBackendIdentity.Describe(Base, Alias, ModelPath);

        public string Describe() =>
            Alias is not null
                ? $"alias '{Alias}'"
                : $"model_path '{ModelPath}'";
    }

    /// <summary>
    /// §3.2 steps 1–3 for one model: its <c>/model/info</c> entry → normalized <c>api_base</c> → the backend's
    /// <c>/props</c>, else its <c>/v1/models</c> when that names exactly one model. Null = unverified.
    /// </summary>
    private static async Task<BackendReport?> ResolveBackendAsync(
        HttpClient http,
        JsonArray? modelInfoData,
        string model,
        string token,
        Dictionary<string, BackendReport?> cache,
        CancellationToken cancellationToken)
    {
        if (modelInfoData is null || ApiBaseFor(modelInfoData, model) is not { } apiBase)
        {
            return null;
        }

        string backendBase = ClaudeGatewayBackendIdentity.NormalizeApiBase(apiBase);
        if (cache.TryGetValue(backendBase, out BackendReport? cached))
        {
            return cached;
        }

        BackendReport? report = null;
        Probe props = await SendAsync(http, HttpMethod.Get, backendBase + "/props", token, body: null,
            ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (props is { TransportFailure: null, Status: >= 200 and < 300 }
            && TryReadJsonObject(props.Body, out JsonObject? propsObject, out _))
        {
            string? alias = StringOf(propsObject!["model_alias"]) ?? StringOf(propsObject["alias"]);
            string? modelPath = StringOf(propsObject["model_path"]);
            if (alias is not null && ClaudeGatewayBackendIdentity.LooksLikePath(alias))
            {
                modelPath ??= alias;
                alias = null;
            }

            int? nCtx = IntOf(propsObject["default_generation_settings"]?["n_ctx"]) ?? IntOf(propsObject["n_ctx"]);
            if (alias is not null || modelPath is not null)
            {
                report = new BackendReport(backendBase, alias, modelPath, nCtx);
            }
        }

        if (report is null)
        {
            // Router mode (several models) may answer /props only with ?model=; v1 does not attempt that. It falls
            // back to /v1/models, and anything but exactly one loaded model stays unverified.
            Probe models = await SendAsync(http, HttpMethod.Get, backendBase + "/v1/models", token, body: null,
                ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (models is { TransportFailure: null, Status: >= 200 and < 300 }
                && ListedIds(models.Body) is { Count: 1 } single)
            {
                string id = single[0];
                report = ClaudeGatewayBackendIdentity.LooksLikePath(id)
                    ? new BackendReport(backendBase, null, id, null)
                    : new BackendReport(backendBase, id, null, null);
            }
        }

        cache[backendBase] = report;
        return report;
    }

    /// <summary>
    /// The <c>litellm_params.api_base</c> of <paramref name="model"/>'s <c>/model/info</c> entry. A model_name
    /// LiteLLM load-balances across SEVERAL backends names no single identity, so it resolves to null (unverified)
    /// rather than to whichever entry happens to come first.
    /// </summary>
    private static string? ApiBaseFor(JsonArray data, string model)
    {
        List<string> bases = [.. data
            .OfType<JsonObject>()
            .Where(obj => string.Equals(StringOf(obj["model_name"]), model, StringComparison.Ordinal))
            .Select(obj => StringOf(obj["litellm_params"]?["api_base"]))
            .OfType<string>()
            .Select(ClaudeGatewayBackendIdentity.NormalizeApiBase)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        return bases.Count == 1 ? bases[0] : null;
    }

    // ───────────────────────────── HTTP plumbing ─────────────────────────────

    /// <summary>One probe's outcome: a status and body, or the transport failure that prevented one.</summary>
    private sealed record Probe(int Status, string Body, string? TransportFailure);

    private static async Task<Probe> SendAsync(
        HttpClient http, HttpMethod method, string url, string token, string? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return new Probe(0, string.Empty, $"'{url}' is not an absolute URL");
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return new Probe((int)response.StatusCode, text, null);
        }
        catch (HttpRequestException ex)
        {
            return new Probe(0, string.Empty, $"{TransportCause(ex)} ({ex.Message})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Probe(0, string.Empty, $"no answer within {timeout.TotalSeconds:F0}s");
        }
    }

    private static string TransportCause(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "DNS did not resolve its host",
        HttpRequestError.ConnectionError => "the connection was refused or reset",
        HttpRequestError.SecureConnectionError => "the TLS handshake failed",
        _ => "the request never reached it"
    };

    private static JsonArray? DataArray(string body) =>
        TryReadJsonObject(body, out JsonObject? obj, out _) ? obj!["data"] as JsonArray : null;

    private static IReadOnlyList<string> ListedIds(string body) =>
        DataArray(body) is { } data
            ? [.. data.OfType<JsonObject>().Select(e => StringOf(e["id"])).OfType<string>()]
            : [];

    private static bool HasContentBlock(string body) =>
        TryReadJsonObject(body, out JsonObject? obj, out _) && obj!["content"] is JsonArray { Count: > 0 };

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int? IntOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;

    private static string Snippet(string body)
    {
        string trimmed = body.Trim();
        return trimmed.Length == 0
            ? "(the response body was empty.)"
            : trimmed.Length <= 400 ? $"Response body: {trimmed}" : $"Response body (first 400 chars): {trimmed[..400]}…";
    }

    // ───────────────────────────── reporting ─────────────────────────────

    private static PlanPreflightCheck Check(string name, string reason) => new()
    {
        Name = name,
        Passed = false,
        Reason = reason
    };

    private static void RecordHalt(PlanDefinition plan, RunJournal journal, List<PlanPreflightCheck> failures)
    {
        var section = new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = journal.Document.PlanHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = failures
        };

        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanPreflightFailed,
            HaltedAt = DateTimeOffset.UtcNow,
            Headline = HeadlinePrefix + string.Join(", ", failures.Select(c => c.Name).Distinct(StringComparer.Ordinal)),
            FailedChecks = [.. failures.Select(c => new FailedGuardrail { Name = c.Name, Reason = c.Reason! })]
        };

        PlanPhaseJournalWriter.Update(plan.PlanDirectory, document => document with { PlanPreflights = section, Halt = halt });
    }

    private static void WriteFailureReport(IReadOnlyList<PlanPreflightCheck> failures, TextWriter? consoleOut)
    {
        if (consoleOut is null)
        {
            return;
        }

        consoleOut.WriteLine();
        consoleOut.WriteLine($"claude gateway preflight FAILED — {failures.Count} finding(s). Halting before scheduling any task.");
        foreach (PlanPreflightCheck failure in failures)
        {
            consoleOut.WriteLine($"  {failure.Name}");
            consoleOut.WriteLine($"    {failure.Reason}");
        }
    }

    /// <summary>
    /// The run-header lines for a passing preflight: one per gateway (block, model) naming the backend identity — or
    /// saying plainly that it is unverified — the warnings collected on the way, and §4's <c>maxCostUsd</c> Note.
    /// </summary>
    private static void WriteSuccessReport(
        PlanDefinition plan,
        List<PromptRunnerConfig> gateways,
        IReadOnlyDictionary<string, string> identities,
        List<string> notes,
        TextWriter? consoleOut)
    {
        if (consoleOut is null)
        {
            return;
        }

        foreach (PromptRunnerConfig block in gateways)
        {
            string display = ClaudeGatewayConfig.RedactUserInfo(ClaudeGatewayConfig.NormalizeBaseUrl(block.BaseUrl!));
            string? identity = identities.TryGetValue(
                ClaudeGatewayRunContext.IdentityKey(block.BaseUrl!, block.Settings.Model), out string? found) ? found : null;
            string backend = identity is null
                ? "backend identity unverified"
                : $"backend {identity}" + (block.BackendModel is { } declared ? $" (backendModel '{declared}' matched)" : string.Empty);
            consoleOut.WriteLine($"Gateway: block '{block.Name}' → {display}, model '{block.Settings.Model}': {backend}.");
        }

        foreach (string note in notes)
        {
            consoleOut.WriteLine(note);
        }

        if (MaxCostNote(plan.Config) is { } costNote)
        {
            consoleOut.WriteLine($"Note: {costNote}");
        }
    }

    /// <summary>
    /// §4's run-start Note on how far <c>maxCostUsd</c> still applies. A gateway dispatch reports no cost, so the cap
    /// does not bind when every block a prompt could dispatch to is a gateway, and binds only the non-gateway spend
    /// otherwise. Null when no cap is configured or the plan has no gateway block.
    /// </summary>
    public static string? MaxCostNote(RunConfig config)
    {
        if (config.MaxCostUsd is not { } cap || !config.PromptRunners.Values.Any(b => b.IsClaudeGateway))
        {
            return null;
        }

        string[] paid = [.. config.PromptRunners.Values
            .Where(b => !b.IsClaudeGateway && b.Kind == PromptRunnerKind.Claude)
            .Select(b => b.Name)
            .Order(StringComparer.Ordinal)];

        return paid.Length == 0
            ? $"maxCostUsd (${cap}) does NOT bind this run: every prompt runner is a claude gateway block, and a gateway " +
              "dispatch reports no cost (its token usage is shown instead)."
            : $"maxCostUsd (${cap}) binds only PARTIALLY: gateway dispatches report no cost, so only spend on the " +
              $"non-gateway claude block(s) {string.Join(", ", paid.Select(n => $"'{n}'"))} counts toward it.";
    }
}
