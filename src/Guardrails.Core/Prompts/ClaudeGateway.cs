using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The gateway configuration of ONE <c>kind: "claude"</c> block that declares a <c>baseUrl</c> (#782, SSOT §9.10),
/// handed by <see cref="PromptRunnerRegistry"/> to that block's <see cref="ClaudePromptRunner"/> instance (D3):
/// every invocation dispatched to that instance is a gateway dispatch, whichever call site produced it.
/// </summary>
public sealed record ClaudeGatewayConfig
{
    /// <summary>What provenance records as <c>BackendModel</c> when the backend's identity could not be resolved (§3.2).</summary>
    public const string UnverifiedBackend = "unverified";

    /// <summary>The <c>promptRunners</c> key of the gateway block.</summary>
    public required string BlockName { get; init; }

    /// <summary>The block's <c>baseUrl</c>, normalized: trimmed, with any trailing <c>/</c> removed.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The NAME of the env var holding the gateway token, or null (the placeholder token is sent).</summary>
    public string? AuthTokenEnv { get; init; }

    /// <summary>
    /// The block's <c>model</c> — REQUIRED on a gateway block (GR2084). Every alias, background and subagent model
    /// variable, and the settings file's <c>model</c>/<c>fallbackModel</c>, are pinned to it. Null only for a
    /// malformed block that bypassed validation.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>The backend's PER-SLOT context window (§1.3), or null when the block declares none.</summary>
    public int? ContextTokens { get; init; }

    /// <summary>What the backend must have loaded (§3.2), or null when the block declares no expectation.</summary>
    public string? BackendModel { get; init; }

    /// <summary>The base URL with any userinfo removed — the only spelling provenance, summaries and logs carry.</summary>
    public string DisplayBaseUrl => RedactUserInfo(BaseUrl);

    /// <summary>The gateway configuration of <paramref name="block"/>, or null when it is not a claude gateway block.</summary>
    public static ClaudeGatewayConfig? From(PromptRunnerConfig block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (!block.IsClaudeGateway)
        {
            return null;
        }

        return new ClaudeGatewayConfig
        {
            BlockName = block.Name,
            BaseUrl = NormalizeBaseUrl(block.BaseUrl!),
            AuthTokenEnv = string.IsNullOrWhiteSpace(block.AuthTokenEnv) ? null : block.AuthTokenEnv.Trim(),
            Model = string.IsNullOrWhiteSpace(block.Settings.Model) ? null : block.Settings.Model,
            ContextTokens = block.ContextTokens,
            BackendModel = string.IsNullOrWhiteSpace(block.BackendModel) ? null : block.BackendModel.Trim()
        };
    }

    /// <summary>Trim, then drop every trailing <c>/</c> — two spellings of one gateway are one gateway.</summary>
    public static string NormalizeBaseUrl(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    /// <summary>
    /// <paramref name="url"/> with any <c>user:password@</c> removed. GR2084 rejects userinfo, so this is the
    /// belt for a block that bypassed validation: a credential must never reach a journal or a summary.
    /// </summary>
    public static string RedactUserInfo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.UserInfo))
        {
            return url;
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return NormalizeBaseUrl(builder.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped));
    }

    /// <summary>
    /// The key two gateways are compared by for GR2086 and the preflight's once-per-<c>baseUrl</c> grouping: scheme,
    /// host and port lower-cased, the loopback spellings (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) folded to one,
    /// the path kept, and a trailing <c>/</c> dropped. An unparseable URL keys as its normalized text.
    /// </summary>
    public static string EndpointKey(string baseUrl)
    {
        string normalized = NormalizeBaseUrl(baseUrl);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            return normalized;
        }

        string host = uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? "loopback"
            : uri.Host.ToLowerInvariant();
        string path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}{path}";
    }
}

/// <summary>
/// Per-run state for the claude-gateway blocks (#782), set by <c>guardrails run</c> after the pre-DAG preflight
/// (never loaded from JSON — see <see cref="RunConfig.GatewayRun"/>).
/// </summary>
public sealed record ClaudeGatewayRunContext
{
    /// <summary>The per-run scratch <c>CLAUDE_CONFIG_DIR</c>: <c>logs/&lt;runId&gt;/claude-config/</c>, created empty (§1.2 a).</summary>
    public required string ConfigDirectory { get; init; }

    /// <summary>
    /// The resolved backend identities (§3.2), keyed by <see cref="IdentityKey"/>. A pair absent from the map was
    /// not resolved, and a runner records it as <see cref="ClaudeGatewayConfig.UnverifiedBackend"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> BackendIdentities { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The map key for one (gateway, model) pair: the <see cref="ClaudeGatewayConfig.EndpointKey"/> plus the model, verbatim.</summary>
    public static string IdentityKey(string baseUrl, string? model) =>
        $"{ClaudeGatewayConfig.EndpointKey(baseUrl)}\n{model ?? string.Empty}";

    /// <summary>The resolved identity for (<paramref name="baseUrl"/>, <paramref name="model"/>), or null when none was resolved.</summary>
    public string? IdentityFor(string baseUrl, string? model) =>
        BackendIdentities.TryGetValue(IdentityKey(baseUrl, model), out string? identity) ? identity : null;
}

/// <summary>
/// The environment and settings names a gateway dispatch OWNS or SCRUBS (#782 §1.2) — Claude Code's spellings,
/// quarantined here beside <see cref="ClaudePromptRunner"/> and read by the validator (GR2084) and the preflight's
/// settings checks, so every reader agrees on one list.
/// </summary>
public static class ClaudeGatewayEnvironment
{
    /// <summary>The gateway base URL.</summary>
    public const string BaseUrl = "ANTHROPIC_BASE_URL";

    /// <summary>The gateway bearer token.</summary>
    public const string AuthToken = "ANTHROPIC_AUTH_TOKEN";

    /// <summary>The per-slot context window Claude Code compacts before (§1.3).</summary>
    public const string MaxContextTokens = "CLAUDE_CODE_MAX_CONTEXT_TOKENS";

    /// <summary>Suppresses Claude Code's non-essential traffic; any non-empty value means "set".</summary>
    public const string DisableNonessentialTraffic = "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC";

    /// <summary>The config directory the child reads user settings, credentials and state from.</summary>
    public const string ConfigDir = "CLAUDE_CONFIG_DIR";

    /// <summary>The fixed, NON-secret token sent when the block names no <c>authTokenEnv</c>.</summary>
    public const string PlaceholderToken = "guardrails-gateway-no-auth";

    /// <summary>The model-alias and subagent variables, each pinned to the block's <c>model</c> (#570 trap 1).</summary>
    public static IReadOnlyList<string> ModelAliasNames { get; } =
    [
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_FABLE_MODEL",
        "CLAUDE_CODE_SUBAGENT_MODEL"
    ];

    /// <summary>
    /// Every variable a gateway dispatch OWNS outright: set by the harness, never by the operator (GR2084 in a
    /// block's <c>env</c>, checked case-insensitively).
    /// </summary>
    public static IReadOnlyList<string> OwnedNames { get; } =
    [
        BaseUrl,
        AuthToken,
        .. ModelAliasNames,
        MaxContextTokens,
        DisableNonessentialTraffic,
        ConfigDir
    ];

    /// <summary>Inherited variables with these prefixes are scrubbed from a gateway child (§1.2 step 1).</summary>
    public static IReadOnlyList<string> ScrubbedPrefixes { get; } = ["ANTHROPIC_", "CLAUDE_CODE_USE_"];

    /// <summary>Inherited variables with these exact names are scrubbed from a gateway child (§1.2 step 1).</summary>
    public static IReadOnlyList<string> ScrubbedNames { get; } =
        ["CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST", ConfigDir];

    /// <summary>
    /// The scrubbed-but-not-owned names the harness KNOWS, each set to <c>""</c> in the composed settings file's
    /// <c>env</c> (§1.2's belt): a project settings file could otherwise re-introduce one the environment scrub removed.
    /// Whether Claude Code treats <c>""</c> as unset is unverified — the preflight's project-settings halt is the
    /// primary defense, and the live smoke records whether this belt holds.
    /// </summary>
    public static IReadOnlyList<string> BlankedNames { get; } =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_MODEL",
        "ANTHROPIC_SMALL_FAST_MODEL",
        "ANTHROPIC_CUSTOM_HEADERS",
        "ANTHROPIC_BEDROCK_BASE_URL",
        "ANTHROPIC_VERTEX_BASE_URL",
        "ANTHROPIC_VERTEX_PROJECT_ID",
        "ANTHROPIC_FOUNDRY_BASE_URL",
        "ANTHROPIC_FOUNDRY_API_KEY",
        "ANTHROPIC_FOUNDRY_RESOURCE",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"
    ];

    /// <summary>True when <paramref name="name"/> is an owned variable, compared case-insensitively (GR2084's rule).</summary>
    public static bool IsOwned(string name) => OwnedNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when an INHERITED <paramref name="name"/> is scrubbed from a gateway child, compared with
    /// <paramref name="comparison"/> — the OS's own environment-name semantics (case-insensitive on Windows).
    /// </summary>
    public static bool IsScrubbed(string name, StringComparison comparison) =>
        ScrubbedPrefixes.Any(prefix => name.StartsWith(prefix, comparison))
        || ScrubbedNames.Any(scrubbed => string.Equals(name, scrubbed, comparison));

    /// <summary>
    /// True when <paramref name="name"/> is owned or scrubbed, compared case-insensitively: what a gateway instance
    /// drops from an invocation's settings <c>env</c> (§1.1) and what the preflight halts on in a settings file.
    /// </summary>
    public static bool IsOwnedOrScrubbed(string name) =>
        IsOwned(name) || IsScrubbed(name.Trim(), StringComparison.OrdinalIgnoreCase);
}
