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
/// block's <c>kind</c> (SSOT §9, issue #224) selects the runner CLASS —
/// <see cref="ClaudePromptRunner"/> and <see cref="OpenAiCompatPromptRunner"/> today. A kind with no
/// implementation FAILS construction rather than being served by Claude (see <c>CreateRunner</c>).
/// Adding a provider is a new class plus one arm of that switch — the seam is here, not in the harness.
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
    /// <para>This is the BACKSTOP, not the gate. The GATE is <c>guardrails validate</c>: an unimplemented
    /// <c>kind</c> is a GR2044 validation ERROR (<c>PlanValidator.ValidatePromptRunnerKindsImplemented</c>),
    /// so a config that would reach this throw is refused before the run starts. Reaching it means the run
    /// is already in flight — a kind cast in past the loader, or a validation step skipped — and the one
    /// thing it must never do is substitute Claude for the provider the config named.</para>
    ///
    /// <para>The switch below and <see cref="PromptRunnerKinds.Implemented"/> state the same fact from two
    /// directions; they are pinned together by a test, so the gate can never start permitting a kind this
    /// method would refuse (or vice versa).</para>
    /// </summary>
    private static IPromptRunner CreateRunner(PromptRunnerConfig runner, ProcessRunner processRunner) =>
        runner.Kind switch
        {
            PromptRunnerKind.Claude => new ClaudePromptRunner(runner.Name, runner.Command, processRunner),
            PromptRunnerKind.OpenAiCompat => new OpenAiCompatPromptRunner(runner.Name, runner, SharedHttpClient),
            _ => throw new InvalidOperationException(UnimplementedKindMessage(runner))
        };

    /// <summary>
    /// The ONE <see cref="HttpClient"/> every <c>openai-compat</c> block shares. One instance per block
    /// would leak a connection pool per block, which is the documented HttpClient misuse; the endpoint
    /// is a per-REQUEST fact, so nothing about a block belongs on the client.
    ///
    /// <para><b><see cref="System.Threading.Timeout.InfiniteTimeSpan"/> is deliberate, not an omission.</b>
    /// <see cref="OpenAiCompatPromptRunner"/> bounds every turn itself, with two different clocks that
    /// this one property cannot express: <c>PromptInvocation.Timeout</c> bounds DURATION and
    /// <c>PromptInvocation.StallBound</c> bounds SILENCE. Leaving the framework default in place would
    /// impose a third, invisible 100-second cap — long local generations would die as a bare
    /// <c>TaskCanceledException</c> attributed to neither bound, which is the silent direction.</para>
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

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
            $"this build — this build serves {PromptRunnerKinds.ImplementedTokenList} (concrete " +
            "non-Claude runners are issue #223). Point that promptRunners block at an " +
            "implemented kind, or remove it and route the tasks that used it to a runner this build can " +
            "serve. The harness will not substitute a different model for the one the config asked for. " +
            "(`guardrails validate` reports this as GR2044 before a run starts — reaching this message " +
            "means the gate was bypassed and this backstop caught it.)";
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

        return new PromptRunnerRegistry(runners, config.PromptRunners, DefaultNameFor(config));
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

    /// <summary>
    /// The registry KEY an attempt of this action would dispatch to — <b>the one expression</b>
    /// <c>ActionRunner</c> hands <see cref="Resolve"/>, exposed so a PREVIEW can ask the same question
    /// without spawning anything (issue #549).
    ///
    /// <para>Order, and every step of it is load-bearing: the RESOLVED ROUTE's own block first (§6.1
    /// decided it — a pin, a rung, or the legacy pointer), then the action's <c>runner</c> pin, then the
    /// prompt file's frontmatter <c>runner</c>, then <see cref="DefaultNameFor"/>. The frontmatter/default
    /// tail is not defensive style: on the LEGACY path the route's own NAME is
    /// <c>config.DefaultPromptRunner</c>, which can be null while the sole-declared-block fallback still
    /// resolves, so <c>route?.Runner?.Name</c> — the resolved BLOCK's name, non-null whenever a block
    /// actually resolved — is what is read here rather than <see cref="TierResolution.RunnerName"/>.</para>
    ///
    /// <para>Null only when nothing named a runner AND no default resolves — the state
    /// <see cref="Resolve"/> throws on and validation already rejects. A caller that must render it says
    /// so; a caller that must run refuses.</para>
    /// </summary>
    /// <param name="config">The plan's run configuration — the registry and its <c>default</c> pointer.</param>
    /// <param name="route">The §6.1 resolution for this attempt, or null for a script action.</param>
    /// <param name="actionRunner">The task manifest's <c>action.runner</c>, if any.</param>
    /// <param name="frontmatterRunner">The prompt file's frontmatter <c>runner</c>, if any.</param>
    public static string? DispatchNameFor(
        RunConfig config, TierResolution? route, string? actionRunner, string? frontmatterRunner)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? named = route?.Runner?.Name ?? actionRunner ?? frontmatterRunner;
        return string.IsNullOrWhiteSpace(named) ? DefaultNameFor(config) : named;
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

    /// <summary>
    /// The default runner NAME for <paramref name="config"/>: <c>promptRunners.default</c> when it names
    /// a declared block, else the sole declared block, else null. Public because the <c>--dry-run</c>
    /// preview needs the same answer a run gets and must not carry a second copy of the sole-block
    /// fallback (issue #549 was that exact duplication, one rule further up).
    /// </summary>
    public static string? DefaultNameFor(RunConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.DefaultPromptRunner is { } named && config.PromptRunnerNames.Contains(named))
        {
            return named;
        }

        return config.PromptRunnerNames.Count == 1 ? config.PromptRunnerNames.Single() : null;
    }
}
