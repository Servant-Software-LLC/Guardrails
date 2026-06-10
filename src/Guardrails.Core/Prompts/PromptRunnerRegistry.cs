using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Maps a prompt-runner name to its <see cref="IPromptRunner"/>, built from the plan's
/// <c>promptRunners</c> config (SSOT §2/§9). Also resolves the default runner. Validation
/// (GR2004/GR2008) guards the names before a run, so an unknown name reaching the registry
/// at run time is a harness error.
///
/// v1 ships a single runner CLASS (<see cref="ClaudePromptRunner"/>); each config block
/// becomes one instance carrying that block's <c>command</c>. A future CLI is a new class
/// keyed by a discriminator — the seam is here, not in the harness.
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

    /// <summary>Build the production registry (Claude runners) from a plan's config.</summary>
    public static PromptRunnerRegistry FromConfig(RunConfig config, ProcessRunner processRunner) =>
        Build(config, c => new ClaudePromptRunner(c.Name, c.Command, processRunner));

    /// <summary>
    /// Build a registry with a custom factory (tests inject fake runners). The factory is
    /// called once per declared runner config.
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
