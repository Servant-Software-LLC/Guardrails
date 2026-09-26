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
    /// When set, the ONLY managed-settings sources read are these JSON files (tests inject a path, or <c>[]</c> to read
    /// none). Null (production) reads every documented source for the host OS — see
    /// <see cref="ClaudeGatewayPreflight.DefaultManagedSettingsSources"/>.
    /// </summary>
    public IReadOnlyList<string>? ManagedSettingsPaths { get; init; }

    /// <summary>
    /// When set, the ONLY managed-settings sources read (tests inject a drop-in directory or an unreadable source).
    /// Takes precedence over <see cref="ManagedSettingsPaths"/>.
    /// </summary>
    public IReadOnlyList<ManagedSettingsSource>? ManagedSettingsSources { get; init; }

    /// <summary>
    /// Extra directories whose <c>.claude/settings.json</c> and <c>.claude/settings.local.json</c> are checked beside the
    /// workspace's — <c>guardrails run</c> passes the resolved integration worktree (the plan-branch tip), whose files
    /// the segment worktrees are built from on a resume or after a delivering wave (#782 review, sec W1).
    /// </summary>
    public IReadOnlyList<string> ProjectSettingsRoots { get; init; } = [];

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

    /// <summary>
    /// Test seam for the entry points that build their own options (<c>guardrails run</c>, <c>breakdown</c>, revalidate):
    /// an ambient, flow-scoped override of the managed-settings sources, so a CLI-driven test does not read — or depend
    /// on — the host's real managed policy. Async-local, so parallel tests cannot see each other's override.
    /// </summary>
    private static readonly AsyncLocal<IReadOnlyList<ManagedSettingsSource>?> ManagedSourcesOverride = new();

    /// <summary>Scope <paramref name="sources"/> as the ONLY managed-settings sources for this async flow (tests).</summary>
    public static IDisposable OverrideManagedSettingsSources(IReadOnlyList<ManagedSettingsSource> sources)
    {
        IReadOnlyList<ManagedSettingsSource>? previous = ManagedSourcesOverride.Value;
        ManagedSourcesOverride.Value = sources;
        return new Restore(() => ManagedSourcesOverride.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>The short timeout on each listing/identity probe: this runs before the DAG, and slow is worth knowing now.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The <c>/v1/messages</c> probe's own bound: a local model's first answer (and its thinking) takes time.</summary>
    private static readonly TimeSpan MessagesProbeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>§3.1: 256, not 1 — room for a reasoning model's thinking before a content block appears (#759).</summary>
    private const int MessagesProbeMaxTokens = 256;

    /// <summary>
    /// The managed-settings source Claude Code documents but the harness cannot read, stated in the run header so the
    /// operator knows what was NOT checked (#782 review, sec W4).
    /// </summary>
    internal const string UncheckedManagedSourcesNote =
        "Note: the gateway preflight cannot check SERVER-managed settings (fetched from the claude.ai admin console or a " +
        "Claude apps gateway and cached by Claude Code) or settings an embedding host supplies. A gateway child runs with an " +
        "isolated config directory and no stored credentials, so it has no signed-in organization to fetch a policy for; " +
        "if your organization pushes one, confirm with `/status` in an interactive session that it sets no env, " +
        "apiKeyHelper, model or login keys.";

    /// <summary>
    /// Every DOCUMENTED managed-settings source for the host OS (code.claude.com/docs/en/managed-settings, "Where each
    /// mechanism stores the policy"), highest-ranked first:
    /// <list type="bullet">
    /// <item>macOS: the <c>com.anthropic.claudecode</c> managed-preferences domain (machine and per-user profiles under
    /// <c>/Library/Managed Preferences/</c>), read through <c>plutil -convert json</c>;</item>
    /// <item>Windows: the <c>Settings</c> value under <c>HKLM\SOFTWARE\Policies\ClaudeCode</c>, and the user-writable
    /// <c>HKCU</c> fallback of the same value (read too — it can carry a policy when no admin source does);</item>
    /// <item>every OS: <c>managed-settings.json</c> and each <c>managed-settings.d/*.json</c> in the system directory
    /// (<c>/Library/Application Support/ClaudeCode/</c>, <c>/etc/claude-code/</c>, <c>C:\Program Files\ClaudeCode\</c>).
    /// The legacy <c>C:\ProgramData\ClaudeCode</c> path is not read — Claude Code no longer reads it either.</item>
    /// </list>
    /// Server-managed settings are not readable here; <see cref="UncheckedManagedSourcesNote"/> says so.
    /// </summary>
    public static IReadOnlyList<ManagedSettingsSource> DefaultManagedSettingsSources()
    {
        var sources = new List<ManagedSettingsSource>();
        string systemDirectory;

        if (OperatingSystem.IsWindows())
        {
            sources.Add(ManagedSettingsSource.Registry(hive: "HKLM"));
            sources.Add(ManagedSettingsSource.Registry(hive: "HKCU"));
            systemDirectory = @"C:\Program Files\ClaudeCode";
        }
        else if (OperatingSystem.IsMacOS())
        {
            sources.Add(ManagedSettingsSource.Plist("/Library/Managed Preferences/com.anthropic.claudecode.plist"));
            sources.Add(ManagedSettingsSource.Plist(
                $"/Library/Managed Preferences/{Environment.UserName}/com.anthropic.claudecode.plist"));
            systemDirectory = "/Library/Application Support/ClaudeCode";
        }
        else
        {
            systemDirectory = "/etc/claude-code";
        }

        sources.Add(ManagedSettingsSource.File(Path.Combine(systemDirectory, "managed-settings.json")));
        sources.Add(ManagedSettingsSource.DropInDirectory(Path.Combine(systemDirectory, "managed-settings.d")));
        return sources;
    }

    /// <summary>
    /// Run the preflight. Returns true when scheduling may proceed. On a halt, the failures are journaled (a
    /// plan-preflight-failed section plus the top-level <c>halt</c>) and printed before this returns.
    /// </summary>
    /// <param name="journal">The run journal the halt is recorded in, or null for an entry point with no run (the halt is
    /// then only printed).</param>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        RunJournal? journal,
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

        IReadOnlyList<ManagedSettingsSource>? injected = options.ManagedSettingsSources ?? ManagedSourcesOverride.Value;
        IReadOnlyList<ManagedSettingsSource> managedSources = injected
            ?? (options.ManagedSettingsPaths is { } paths
                ? [.. paths.Select(ManagedSettingsSource.File)]
                : DefaultManagedSettingsSources());
        CheckManagedSettings(managedSources, failures);
        if (options.ManagedSettingsPaths is null && injected is null)
        {
            notes.Add(UncheckedManagedSourcesNote);
        }

        CheckProjectSettings(plan.Workspace, options.WorktreeMode, options.ProjectSettingsRoots, failures);

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
        var sharedKeys = new Dictionary<string, (string CompareKey, string Identity)>(StringComparer.Ordinal);
        if (failures.Count == 0)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            foreach (GatewayTarget target in GroupByGateway(ClaudeGatewayReach.Of(plan), tokens))
            {
                await ProbeGatewayAsync(http, target, failures, notes, identities, sharedKeys, cancellationToken)
                    .ConfigureAwait(false);
            }

            CheckSharedIdentities(sharedKeys, failures);
        }

        if (failures.Count > 0)
        {
            if (journal is not null)
            {
                RecordHalt(plan, journal, failures);
            }

            WriteFailureReport(failures, consoleOut);
            return false;
        }

        options.ResolvedIdentities = identities;
        WriteSuccessReport(plan, identities, notes, consoleOut);
        return true;
    }

    /// <summary>
    /// The gateway preflight for an entry point that dispatches prompts WITHOUT a <c>guardrails run</c> (#782 review, sec
    /// W2): <c>guardrails breakdown --runner-config</c> and <c>run --revalidate-task</c>. The FULL preflight runs (settings
    /// authorities, reachability, backend identity), nothing is journaled, and on success the plan comes back with its
    /// <see cref="RunConfig.GatewayRun"/> carrying the resolved identities. Null = a halt, already printed. A plan with no
    /// gateway block is returned unchanged, having opened no connection.
    /// </summary>
    public static async Task<PlanDefinition?> PrepareStandaloneAsync(
        PlanDefinition plan, TextWriter output, bool worktreeMode, CancellationToken cancellationToken)
    {
        if (!plan.Config.PromptRunners.Values.Any(b => b.IsClaudeGateway))
        {
            return plan;
        }

        var options = new ClaudeGatewayPreflightOptions { WorktreeMode = worktreeMode };
        if (!await EvaluateAsync(plan, journal: null, output, options, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return plan with
        {
            Config = plan.Config with
            {
                GatewayRun = new ClaudeGatewayRunContext { BackendIdentities = options.ResolvedIdentities }
            }
        };
    }

    // ───────────────────────────── settings authorities ─────────────────────────────

    /// <summary>
    /// §1.2: managed settings outrank both isolation layers, so the harness can only detect and refuse. Every source is
    /// read; a source that EXISTS but cannot be read or parsed halts too — it could carry anything (#782 review, N5).
    /// </summary>
    private static void CheckManagedSettings(IReadOnlyList<ManagedSettingsSource> sources, List<PlanPreflightCheck> failures)
    {
        foreach (ManagedSettingsSource source in sources)
        {
            IReadOnlyList<(string Label, string Text)> documents;
            try
            {
                documents = source.Read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                           or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failures.Add(Check($"managed settings {source.Label}",
                    $"the managed-settings source '{source.Label}' exists but could not be read ({ex.Message}). Managed " +
                    "settings outrank everything the harness passes, so a source it cannot read could redirect a gateway " +
                    "dispatch; make it readable (or remove it), then re-run."));
                continue;
            }

            foreach ((string label, string text) in documents)
            {
                if (!TryReadJsonObject(text, out JsonObject? settings, out string? error))
                {
                    failures.Add(Check($"managed settings {label}",
                        $"the managed-settings source '{label}' could not be parsed ({error}). Managed settings outrank " +
                        "everything the harness passes, so a source it cannot read could redirect a gateway dispatch; " +
                        "fix or remove it, then re-run."));
                    continue;
                }

                foreach (string finding in ManagedFindings(settings!))
                {
                    failures.Add(Check($"managed settings {label}",
                        $"'{label}' sets {finding}. Managed settings outrank the harness's isolated config directory and its " +
                        "--settings file, so a gateway dispatch cannot be guaranteed to reach the gateway the plan names " +
                        "with the credential it grants. Remove the setting, or run this plan on a machine without it."));
                }
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

    /// <summary>
    /// §1.2: <c>--settings</c> wins only for the keys it sets, so project keys it does not set are refused. Checked in the
    /// workspace, in every extra root (the integration worktree at the plan-branch tip), and — in worktree mode — as
    /// committed at the workspace's HEAD. An unreadable file halts rather than crashing the run.
    /// </summary>
    private static void CheckProjectSettings(
        string workspace, bool worktreeMode, IReadOnlyList<string> extraRoots, List<PlanPreflightCheck> failures)
    {
        string[] relative = [".claude/settings.json", ".claude/settings.local.json"];
        IEnumerable<string> roots = new[] { workspace }
            .Concat(extraRoots)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (string root in roots)
        {
            foreach (string file in relative)
            {
                string path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(Check($"project settings {path}",
                        $"'{path}' exists but could not be read ({ex.Message}), so the preflight cannot prove it sets " +
                        "nothing that would redirect a gateway dispatch. Make it readable, then re-run."));
                    continue;
                }

                ReportProjectFile(path, text, failures);
            }
        }

        if (worktreeMode)
        {
            foreach (string file in relative)
            {
                if (GitShowHead(workspace, file) is { } committed)
                {
                    ReportProjectFile($"{file} (committed at HEAD of {workspace})", committed, failures);
                }
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
        public List<(PromptRunnerConfig Block, string Model, string Token, string Where)> Models { get; } = [];
    }

    /// <summary>
    /// Group THE reach set (<see cref="ClaudeGatewayReach.Of"/> — block models, override models, task <c>action.model</c>
    /// pins, extraArgs model flags) by gateway, de-duplicated on (gateway, model): every model a run can dispatch is
    /// probed and its backend resolved, so the #760 halt and provenance see pinned models too (#782 review, sec B2).
    /// </summary>
    private static List<GatewayTarget> GroupByGateway(
        IReadOnlyList<ClaudeGatewayModelReach> reach, IReadOnlyDictionary<string, string> tokens)
    {
        var byKey = new Dictionary<string, GatewayTarget>(StringComparer.Ordinal);
        var ordered = new List<GatewayTarget>();

        foreach (ClaudeGatewayModelReach pair in reach)
        {
            PromptRunnerConfig block = pair.Block;
            string baseUrl = ClaudeGatewayConfig.NormalizeBaseUrl(block.BaseUrl!);
            string key = ClaudeGatewayConfig.EndpointKey(baseUrl);
            string token = tokens.TryGetValue(block.Name, out string? t) ? t : ClaudeGatewayEnvironment.PlaceholderToken;
            if (!byKey.TryGetValue(key, out GatewayTarget? target))
            {
                target = new GatewayTarget(baseUrl, token);
                byKey[key] = target;
                ordered.Add(target);
            }

            if (!target.Models.Any(m => m.Model == pair.Model))
            {
                target.Models.Add((block, pair.Model, token, pair.Where));
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
        Dictionary<string, (string CompareKey, string Identity)> sharedKeys,
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

        foreach ((PromptRunnerConfig block, string model, string token, string where) in target.Models)
        {
            string check = $"claude gateway {target.Display} (model '{model}')";

            if (listed is not null && !listed.Contains(model, StringComparer.Ordinal))
            {
                failures.Add(Check(check,
                    $"{target.Display} does not list the model '{model}' ({where}) — it reported " +
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
                    $"POST {target.Display}/v1/messages for '{model}' ({where}) answered HTTP {messages.Status} " +
                    $"without a content block, so Claude Code's requests would fail the same way. {Snippet(messages.Body)}"));
                continue;
            }

            // ── D2: backend identity ──
            BackendReport? backend = await ResolveBackendAsync(
                http, modelInfoData, model, backendCache, cancellationToken).ConfigureAwait(false);
            if (backend is { NotProbedReason: { } notProbed })
            {
                // §3.2 + #782 review (sec B1): a backend on a public host is never probed, so nothing is claimed.
                identities[ClaudeGatewayRunContext.IdentityKey(target.BaseUrl, model)] =
                    $"{ClaudeGatewayConfig.UnverifiedBackend} (backend not probed: {notProbed})";
                if (block.BackendModel is { } declaredNotProbed)
                {
                    notes.Add($"gateway block '{block.Name}': backendModel '{declaredNotProbed}' declared, not verified " +
                              $"(the backend {backend.Base} was not probed: {notProbed}).");
                }

                continue;
            }

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
            sharedKeys[model] = (backend.CompareKey, backend.Identity);

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

    /// <summary>
    /// §3.2: two distinct model strings resolving to the SAME loaded model is #760's silent substitution. Compared on the
    /// backend's normalized endpoint (<see cref="ClaudeGatewayConfig.EndpointKey"/>: <c>localhost</c> / <c>127.0.0.1</c>
    /// / <c>::1</c> folded) plus the loaded model, so two spellings of one backend are one backend. Every model in the
    /// reach set is here — a task's <c>action.model</c> pin included.
    /// </summary>
    private static void CheckSharedIdentities(
        IReadOnlyDictionary<string, (string CompareKey, string Identity)> byModel, List<PlanPreflightCheck> failures)
    {
        foreach (IGrouping<string, KeyValuePair<string, (string CompareKey, string Identity)>> group in byModel
                     .GroupBy(p => p.Value.CompareKey, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string[] models = [.. group.Select(p => p.Key).Order(StringComparer.Ordinal)];
            if (models.Length < 2)
            {
                continue;
            }

            string identity = group.First().Value.Identity;
            failures.Add(Check($"claude gateway backend {identity}",
                $"{models.Length} distinct models — {string.Join(", ", models.Select(m => $"'{m}'"))} — resolve to ONE loaded " +
                $"backend model ({identity}). Every dispatch naming any of them would be served by the same model " +
                "(#760). Give each model its own backend (another llama-server port in the gateway's model_list)."));
        }
    }

    /// <summary>What the backend reported about the model it has loaded.</summary>
    private sealed record BackendReport(
        string Base, string? Alias, string? ModelPath, int? PerSlotContext, string? NotProbedReason = null)
    {
        public string Identity => ClaudeGatewayBackendIdentity.Describe(Base, Alias, ModelPath);

        /// <summary>The #760 comparison key: the folded endpoint plus the loaded model id.</summary>
        public string CompareKey =>
            $"{ClaudeGatewayConfig.EndpointKey(Base)} " +
            (Alias ?? ClaudeGatewayBackendIdentity.Basename(ModelPath ?? "?"));

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

        // #782 review (sec B1): the api_base is whatever the GATEWAY reports — possibly a third party or Anthropic
        // itself. The backend is probed only on a loopback or private host, and never with the gateway's token.
        if (await NonPrivateHostReasonAsync(backendBase, cancellationToken).ConfigureAwait(false) is { } reason)
        {
            var notProbed = new BackendReport(backendBase, null, null, null, reason);
            cache[backendBase] = notProbed;
            return notProbed;
        }

        BackendReport? report = null;
        Probe props = await SendAsync(http, HttpMethod.Get, backendBase + "/props", token: null, body: null,
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
            Probe models = await SendAsync(http, HttpMethod.Get, backendBase + "/v1/models", token: null, body: null,
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
    /// Null when <paramref name="backendBase"/>'s host is loopback or a private address (RFC 1918, IPv6 ULA
    /// <c>fc00::/7</c>, link-local <c>169.254/16</c> / <c>fe80::/10</c>) — every address a host NAME resolves to must be
    /// one — else the reason it is not probed. An unparseable URL or a name that does not resolve is not probed either.
    /// </summary>
    internal static async Task<string?> NonPrivateHostReasonAsync(string backendBase, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(backendBase, UriKind.Absolute, out Uri? uri))
        {
            return "not an absolute URL";
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        System.Net.IPAddress[] addresses;
        if (System.Net.IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out System.Net.IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
            }
            catch (System.Net.Sockets.SocketException)
            {
                return "non-private host (its name did not resolve)";
            }
        }

        return addresses.Length > 0 && addresses.All(IsPrivate) ? null : "non-private host";
    }

    /// <summary>Loopback, RFC 1918, IPv6 ULA, or link-local.</summary>
    public static bool IsPrivate(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
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
        HttpClient http, HttpMethod method, string url, string? token, string? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return new Probe(0, string.Empty, $"'{url}' is not an absolute URL");
        }

        using var request = new HttpRequestMessage(method, uri);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
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
        IReadOnlyDictionary<string, string> identities,
        List<string> notes,
        TextWriter? consoleOut)
    {
        if (consoleOut is null)
        {
            return;
        }

        // One line per (gateway, model) pair in the reach set — a task's action.model pin gets its own line, since it
        // is dispatched and its backend was resolved like any other (#782 review, sec B2).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClaudeGatewayModelReach pair in ClaudeGatewayReach.Of(plan))
        {
            PromptRunnerConfig block = pair.Block;
            string key = ClaudeGatewayRunContext.IdentityKey(block.BaseUrl!, pair.Model);
            if (!seen.Add(key))
            {
                continue;
            }

            string display = ClaudeGatewayConfig.RedactUserInfo(ClaudeGatewayConfig.NormalizeBaseUrl(block.BaseUrl!));
            string? identity = identities.TryGetValue(key, out string? found) ? found : null;
            string backend = identity is null
                ? "backend identity unverified"
                : identity.StartsWith(ClaudeGatewayConfig.UnverifiedBackend, StringComparison.Ordinal)
                    ? $"backend identity {identity}"
                    : $"backend {identity}"
                      + (pair.Source == ClaudeGatewayModelSource.BlockModel && block.BackendModel is { } declared
                          ? $" (backendModel '{declared}' matched)"
                          : string.Empty);
            string origin = pair.Source == ClaudeGatewayModelSource.BlockModel ? string.Empty : $" ({pair.Where})";
            consoleOut.WriteLine($"Gateway: block '{block.Name}' → {display}, model '{pair.Model}'{origin}: {backend}.");
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

        // Every non-gateway block, whatever its kind (#782 review, corr W6): only their reported cost can count.
        string[] others = [.. config.PromptRunners.Values
            .Where(b => !b.IsClaudeGateway)
            .Select(b => $"'{b.Name}' ({PromptRunnerKinds.Token(b.Kind)})")
            .Order(StringComparer.Ordinal)];

        return others.Length == 0
            ? $"maxCostUsd (${cap}) does NOT bind this run: every prompt runner is a claude gateway block, and a gateway " +
              "dispatch reports no cost (its token usage is shown instead)."
            : $"maxCostUsd (${cap}) binds only PARTIALLY: gateway dispatches report no cost, so only the cost reported " +
              $"by the non-gateway prompt runner(s) {string.Join(", ", others)} counts toward it (a runner that reports " +
              "no cost adds nothing).";
    }
}
