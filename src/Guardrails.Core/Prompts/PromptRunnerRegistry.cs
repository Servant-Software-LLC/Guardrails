using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Maps a prompt-runner name to its <see cref="IPromptRunner"/>, built from the plan's
/// <c>promptRunners</c> config (SSOT §2/§9). Also resolves the default runner. Validation
/// (GR2004/GR2008) guards the names before a run, so an unknown name reaching the registry
/// at run time is a harness error.
///
/// Each config block becomes one runner instance carrying that block's <c>command</c>, and the
/// block's <c>kind</c> (SSOT §9, issue #224) selects the runner CLASS. Only
/// <see cref="ClaudePromptRunner"/> is implemented in this stage; a kind with no implementation
/// FAILS construction rather than being served by Claude (see <c>CreateRunner</c>). Adding a CLI
/// is a new class plus one arm of that switch — the seam is here, not in the harness.
/// </summary>
public sealed class PromptRunnerRegistry
{
    private readonly IReadOnlyDictionary<string, IPromptRunner> _runners;
    private readonly IReadOnlyDictionary<string, PromptRunnerConfig> _configs;
    private readonly string? _defaultRunner;

    private PromptRunnerRegistry(
        IReadOnlyDictionary<string, IPromptRunner> runners,
        IReadOnlyDictionary<string, PromptRunnerConfig> configs,
        string? defaultRunner)
    {
        _runners = runners;
        _configs = configs;
        _defaultRunner = defaultRunner;
    }

    /// <summary>
    /// Build the production registry from a plan's config, dispatching each block on its
    /// <c>kind</c>. Throws when a declared block asks for a kind this build cannot serve.
    /// </summary>
    public static PromptRunnerRegistry FromConfig(RunConfig config, ProcessRunner processRunner) =>
        Build(config, runner => CreateRunner(runner, processRunner));

    /// <summary>
    /// The one place a <c>kind</c> becomes a runner CLASS (charter §A/#224 item 2).
    ///
    /// <para>An unimplemented kind throws here, naming the block and the kind, so the operator can
    /// edit the offending <c>promptRunners</c> entry. It is deliberately NOT served by
    /// <see cref="ClaudePromptRunner"/>: a fallback would spend a real run against a model the config
    /// never asked for, and the run would look like it honoured the request.</para>
    ///
    /// <para>This is the BACKSTOP, not the gate. Reaching it means the run is already in flight, so
    /// anything knowable from the plan plus the config is checked before <c>run</c> starts — the
    /// pre-run availability check in <c>/guardrails-review</c> (charter §D). The two are not
    /// alternatives: this one still covers a provider that is configured but unservable right now.</para>
    /// </summary>
    private static IPromptRunner CreateRunner(PromptRunnerConfig runner, ProcessRunner processRunner) =>
        runner.Kind switch
        {
            PromptRunnerKind.Claude => new ClaudePromptRunner(runner.Name, runner.Command, processRunner),
            _ => throw new InvalidOperationException(UnimplementedKindMessage(runner))
        };

    /// <summary>
    /// The actionable failure for a kind with no runner class: WHICH block, WHICH kind (in the wire
    /// spelling the operator typed), what this build can serve, and what to do about it.
    /// </summary>
    private static string UnimplementedKindMessage(PromptRunnerConfig runner)
    {
        // Enum.IsDefined guards a value cast in past the loader: PromptRunnerKinds.Token throws
        // ArgumentOutOfRangeException for one, which would swap the failure type this contract promises.
        string kind = Enum.IsDefined(runner.Kind)
            ? PromptRunnerKinds.Token(runner.Kind)
            : runner.Kind.ToString();

        return $"Prompt runner '{runner.Name}' declares kind '{kind}', which has no implementation in " +
            $"this build — only '{PromptRunnerKinds.Token(PromptRunnerKinds.Default)}' is implemented " +
            "(concrete non-Claude runners are issue #223). Point that promptRunners block at an " +
            "implemented kind, or remove it and route the tasks that used it to a runner this build can " +
            "serve. The harness will not substitute a different model for the one the config asked for.";
    }

    /// <summary>
    /// Build a registry with a custom factory (tests inject fake runners). The factory is
    /// called once per declared runner config — eagerly, so a config the factory refuses fails
    /// here rather than when the runner is first used.
    ///
    /// <para>The <c>kind</c> is the FACTORY's business on this path: a fake stands in for whichever
    /// kind the test declares. <see cref="FromConfig"/> is the production entry point and the one
    /// that refuses an unimplemented kind.</para>
    /// </summary>
    public static PromptRunnerRegistry Build(RunConfig config, Func<PromptRunnerConfig, IPromptRunner> factory)
    {
        var runners = new Dictionary<string, IPromptRunner>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, PromptRunnerConfig> pair in config.PromptRunners)
        {
            runners[pair.Key] = factory(pair.Value);
        }

        return new PromptRunnerRegistry(runners, config.PromptRunners, ResolveDefault(config));
    }

    /// <summary>The default runner name: <c>promptRunners.default</c> if it resolves, else the sole declared runner.</summary>
    public string? DefaultRunnerName => _defaultRunner;

    /// <summary>True when a runner is registered under <paramref name="name"/> (used to detect a reserved profile).</summary>
    public bool Contains(string name) => _runners.ContainsKey(name);

    /// <summary>
    /// The runner config for <paramref name="name"/> (null falls back to the default). The
    /// config carries the settings (incl. <c>guardrailOverrides</c>) the composer/executor need.
    /// </summary>
    public PromptRunnerConfig ResolveConfig(string? name)
    {
        string resolved = ResolveName(name);
        return _configs.TryGetValue(resolved, out PromptRunnerConfig? config)
            ? config
            : throw new InvalidOperationException($"No prompt runner config named '{resolved}' (validation should have caught this).");
    }

    /// <summary>The runner instance for <paramref name="name"/> (null falls back to the default).</summary>
    public IPromptRunner Resolve(string? name)
    {
        string resolved = ResolveName(name);
        return _runners.TryGetValue(resolved, out IPromptRunner? runner)
            ? runner
            : throw new InvalidOperationException($"No prompt runner named '{resolved}' (validation should have caught this).");
    }

    private string ResolveName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return _defaultRunner
            ?? throw new InvalidOperationException("No prompt runner specified and no default is configured.");
    }

    private static string? ResolveDefault(RunConfig config)
    {
        if (config.DefaultPromptRunner is { } named && config.PromptRunnerNames.Contains(named))
        {
            return named;
        }

        return config.PromptRunnerNames.Count == 1 ? config.PromptRunnerNames.Single() : null;
    }
}
