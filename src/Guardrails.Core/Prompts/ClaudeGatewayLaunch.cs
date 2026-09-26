using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Core.Prompts;

/// <summary>
/// How a claude GATEWAY dispatch takes authority over the child (#782 §1.2, D1) — the pure pieces
/// <see cref="ClaudePromptRunner"/> composes: the environment (scrub, drops, owned values), the ONE composed
/// <c>--settings</c> file (owned <c>env</c>, <c>model</c>/<c>fallbackModel</c>, the merged containment hook), and the
/// argv split that pulls the harness's own <c>--settings</c> splice out of <c>extraArgs</c> so exactly one reaches
/// the child. Claude Code's spellings stay inside this quarantine (SSOT §9).
/// </summary>
internal static class ClaudeGatewayLaunch
{
    /// <summary>The composed settings file's suffix, appended to the stream log's own base name.</summary>
    internal const string SettingsFileSuffix = ".gateway-settings.json";

    /// <summary>The settings flag the harness owns on a gateway dispatch.</summary>
    private const string SettingsFlag = "--settings";

    /// <summary>
    /// Separate <paramref name="extraArgs"/> into the arguments that reach the child verbatim and the
    /// <c>--settings &lt;path&gt;</c> pairs the harness spliced in (the worktree containment hook, ActionRunner /
    /// GuardrailRunner). A <c>--settings=&lt;path&gt;</c> spelling, or a trailing <c>--settings</c> with no path, is
    /// never a harness splice — it is a <c>GR2084</c> that reached runtime — so it returns a refusal and the dispatch
    /// fails closed rather than passing two settings flags whose merge behavior is unverified.
    /// </summary>
    internal static (IReadOnlyList<string> Remaining, IReadOnlyList<string> SettingsPaths, string? Refusal) SplitSettingsArgs(
        IReadOnlyList<string> extraArgs)
    {
        var remaining = new List<string>();
        var paths = new List<string>();

        for (int i = 0; i < extraArgs.Count; i++)
        {
            string arg = extraArgs[i];
            if (arg.StartsWith(SettingsFlag + "=", StringComparison.Ordinal))
            {
                return ([], [], $"'{arg}' reached a gateway dispatch: the harness owns --settings on a gateway block " +
                                "(GR2084), and passing a second settings file would let it re-introduce what the gateway removed.");
            }

            if (string.Equals(arg, SettingsFlag, StringComparison.Ordinal))
            {
                if (i + 1 >= extraArgs.Count)
                {
                    return ([], [], "a trailing '--settings' with no path reached a gateway dispatch (GR2084).");
                }

                paths.Add(extraArgs[++i]);
                continue;
            }

            remaining.Add(arg);
        }

        return (remaining, paths, null);
    }

    /// <summary>
    /// The <c>hooks</c> objects of the spliced settings files, merged per event by concatenating their arrays. ONLY
    /// <c>hooks</c> is taken — an <c>env</c>, <c>model</c> or <c>apiKeyHelper</c> in such a file is dropped, so even a
    /// settings file that should never have reached here cannot re-route the child. Returns a refusal when a file
    /// cannot be read or parsed: a containment hook that silently vanished would be a boundary lost without a word.
    /// </summary>
    internal static (JsonObject? Hooks, string? Refusal) MergeHooks(IReadOnlyList<string> settingsPaths)
    {
        JsonObject? merged = null;
        foreach (string path in settingsPaths)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return (null, $"the harness settings file '{path}' could not be read to merge its hooks ({ex.Message}); " +
                              "the gateway dispatch is refused rather than launched without its containment hook.");
            }

            if (root is not JsonObject obj || obj["hooks"] is not JsonObject hooks)
            {
                continue;
            }

            merged ??= new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> entry in hooks)
            {
                if (entry.Value is not JsonArray handlers)
                {
                    continue;
                }

                if (merged[entry.Key] is not JsonArray target)
                {
                    target = new JsonArray();
                    merged[entry.Key] = target;
                }

                foreach (JsonNode? handler in handlers)
                {
                    target.Add(handler?.DeepClone());
                }
            }
        }

        return (merged, null);
    }

    /// <summary>
    /// The owned values (§1.2 step 3), in a stable order: base URL, token, the alias and subagent pins, the context
    /// window when declared, the traffic switch, and the config directory.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> OwnedValues(
        ClaudeGatewayConfig gateway, string token, string configDirectory)
    {
        var owned = new List<KeyValuePair<string, string>>
        {
            new(ClaudeGatewayEnvironment.BaseUrl, gateway.BaseUrl),
            new(ClaudeGatewayEnvironment.AuthToken, token)
        };

        foreach (string alias in ClaudeGatewayEnvironment.ModelAliasNames)
        {
            owned.Add(new(alias, gateway.Model!));
        }

        if (gateway.ContextTokens is { } contextTokens)
        {
            owned.Add(new(ClaudeGatewayEnvironment.MaxContextTokens, contextTokens.ToString(CultureInfo.InvariantCulture)));
        }

        owned.Add(new(ClaudeGatewayEnvironment.DisableNonessentialTraffic, "1"));
        owned.Add(new(ClaudeGatewayEnvironment.ConfigDir, configDirectory));
        return owned;
    }

    /// <summary>
    /// The child environment (§1.2 steps 2–3): the harness set and the user's <c>env</c> map as a plain claude block
    /// builds them, MINUS every owned or scrubbed name (case-insensitive — an invocation's settings may come from a
    /// different block, §1.1), then the owned values. The names dropped from the user's map are returned so the
    /// stream log can record them; their VALUES are never recorded.
    /// </summary>
    internal static (IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Dropped) BuildEnvironment(
        PromptInvocation invocation, IReadOnlyList<KeyValuePair<string, string>> owned)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var dropped = new List<string>();

        foreach (KeyValuePair<string, string> entry in invocation.Environment)
        {
            if (ClaudeGatewayEnvironment.IsOwnedOrScrubbed(entry.Key))
            {
                dropped.Add(entry.Key);
                continue;
            }

            env[entry.Key] = entry.Value;
        }

        env[ClaudePromptRunner.MaxOutputTokensEnvVar] = invocation.Settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);

        foreach (KeyValuePair<string, string> entry in invocation.Settings.Env)
        {
            if (ClaudeGatewayEnvironment.IsOwnedOrScrubbed(entry.Key))
            {
                dropped.Add(entry.Key);
                continue;
            }

            env[entry.Key] = entry.Value;
        }

        foreach (KeyValuePair<string, string> value in owned)
        {
            env[value.Key] = value.Value;
        }

        return (env, dropped);
    }

    /// <summary>
    /// The composed settings file's JSON (§1.2 b): <c>env</c> carries the owned values plus every known
    /// scrubbed-but-not-owned name set to <c>""</c>; <c>model</c> and <c>fallbackModel</c> are both the block's
    /// model; <c>hooks</c> is the merged containment hook when one was spliced.
    /// <para>
    /// <b>A REAL token is never written to disk.</b> When <c>authTokenEnv</c> supplied it, <c>ANTHROPIC_AUTH_TOKEN</c>
    /// is left out of the file (it still reaches the child through its environment, and a project settings file that
    /// sets it is a preflight halt). The file lives in the attempt's log directory, which the log viewer serves; the
    /// fixed placeholder is not a secret and IS written.
    /// </para>
    /// </summary>
    internal static string ComposeSettingsJson(
        ClaudeGatewayConfig gateway, IReadOnlyList<KeyValuePair<string, string>> owned, JsonObject? hooks, bool tokenIsSecret)
    {
        var env = new JsonObject();
        foreach (KeyValuePair<string, string> value in owned)
        {
            if (tokenIsSecret && value.Key == ClaudeGatewayEnvironment.AuthToken)
            {
                continue;
            }

            env[value.Key] = value.Value;
        }

        foreach (string blanked in ClaudeGatewayEnvironment.BlankedNames)
        {
            if (env[blanked] is null)
            {
                env[blanked] = string.Empty;
            }
        }

        var root = new JsonObject
        {
            ["env"] = env,
            ["model"] = gateway.Model,
            ["fallbackModel"] = gateway.Model
        };

        if (hooks is not null)
        {
            root["hooks"] = hooks;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The stream log's first line (§1.1): a JSON object naming the gateway and the owned or scrubbed names dropped
    /// from the invocation's environment — names only, never values.
    /// </summary>
    internal static string StreamLogPreamble(ClaudeGatewayConfig gateway, IReadOnlyList<string> dropped) =>
        new JsonObject
        {
            ["type"] = "guardrails_gateway",
            ["gateway"] = gateway.DisplayBaseUrl,
            ["dropped_env"] = new JsonArray([.. dropped.Select(name => (JsonNode?)JsonValue.Create(name))])
        }.ToJsonString();

    /// <summary>
    /// Write the composed settings beside the stream log (<c>&lt;stream-log-name&gt;.gateway-settings.json</c>), or —
    /// for an advisory caller with no stream log — into a fresh scratch directory. Returns the file's path.
    /// </summary>
    internal static string WriteSettingsFile(PromptInvocation invocation, string json)
    {
        string path;
        if (!string.IsNullOrEmpty(invocation.StreamLogPath))
        {
            string directory = Path.GetDirectoryName(invocation.StreamLogPath)!;
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, Path.GetFileNameWithoutExtension(invocation.StreamLogPath) + SettingsFileSuffix);
        }
        else
        {
            path = Path.Combine(Directory.CreateTempSubdirectory("guardrails-gateway-").FullName, "gateway" + SettingsFileSuffix);
        }

        AtomicFile.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// The child's <c>CLAUDE_CONFIG_DIR</c> (§1.2 a): the run's scratch directory when <c>guardrails run</c> set one;
    /// otherwise a <c>claude-config</c> directory beside the stream log, or a fresh scratch directory. NEVER the
    /// operator's own <c>~/.claude</c> — the fallback exists so an embedded caller still gets a hermetic child.
    /// </summary>
    internal static string ConfigDirectory(ClaudeGatewayRunContext? run, PromptInvocation invocation)
    {
        string directory = run?.ConfigDirectory
            ?? (!string.IsNullOrEmpty(invocation.StreamLogPath)
                ? Path.Combine(Path.GetDirectoryName(invocation.StreamLogPath)!, "claude-config")
                : Directory.CreateTempSubdirectory("guardrails-claude-config-").FullName);

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The inherited-environment scrub predicate (§1.2 step 1), using the OS's name comparison. An inherited OWNED
    /// name is removed too: most owned values are re-set by the overlay anyway, but one the block leaves unset
    /// (<c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> with no <c>contextTokens</c>) would otherwise reach the child from the
    /// operator's shell — and "every owned variable is owned outright" (§1.2).
    /// </summary>
    internal static bool ScrubInherited(string name) =>
        ClaudeGatewayEnvironment.IsScrubbed(name, ProcessRunner.EnvironmentNameComparison)
        || ClaudeGatewayEnvironment.OwnedNames.Any(owned => string.Equals(name, owned, ProcessRunner.EnvironmentNameComparison));
}
