using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Loading;

/// <summary>
/// One banned guardrail-script construction (SSOT §4.6, issue #346). A DATA entry, not code: a
/// known-bad regex spelling a generated guardrail must not contain (<see cref="BadPattern"/>),
/// paired with a one-line <see cref="Reason"/>, a <see cref="GoodPatternHint"/> for the fix
/// message, and inline <see cref="MustMatch"/>/<see cref="MustNotMatch"/> fixtures that
/// meta-test the entry's own correctness (a malformed entry cannot ship).
/// </summary>
public sealed class BannedPattern
{
    /// <summary>The catalogue lesson this entry enforces (e.g. <c>#73</c>, <c>#187a</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The regex matching the KNOWN-BAD construction in a guardrail's own source text.</summary>
    public required string BadPattern { get; init; }

    /// <summary>One line: why the construction is wrong.</summary>
    public required string Reason { get; init; }

    /// <summary>The correct replacement, surfaced in the GR2037 fix message.</summary>
    public required string GoodPatternHint { get; init; }

    /// <summary>Fixtures the <see cref="BadPattern"/> MUST catch (meta-test — SSOT §4.6).</summary>
    public IReadOnlyList<string> MustMatch { get; init; } = [];

    /// <summary>Fixtures the <see cref="BadPattern"/> must NOT catch (meta-test — SSOT §4.6).</summary>
    public IReadOnlyList<string> MustNotMatch { get; init; } = [];

    private Regex? _compiled;

    /// <summary>
    /// The compiled matcher for <see cref="BadPattern"/> — culture-invariant with a bounded match
    /// timeout, so a pathological registry regex cannot hang the scan. Compiled once and cached; a
    /// <see cref="BadPattern"/> that is not a valid regex throws <see cref="RegexParseException"/>
    /// (the meta-test gates this before a bad entry can ship).
    /// </summary>
    public Regex Matcher => _compiled ??= new Regex(
        BadPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
}

/// <summary>
/// The banned-guardrail-pattern registry (SSOT §4.6, issue #346): the data-driven catalogue of
/// known-bad regex constructions <see cref="PlanValidator"/> scans every four-folder SCRIPT
/// guardrail's comment-stripped body against, emitting one <see cref="DiagnosticCodes.BannedGuardrailPattern"/>
/// (GR2037) per match.
///
/// <para>
/// <b>Single source, no drift.</b> The one authored file lives beside the catalogue
/// (<c>.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json</c>) so the
/// doctrine side can cite it, AND is embedded into this assembly via an <c>&lt;EmbeddedResource&gt;</c>
/// <c>Link</c> so the validator loads it with zero runtime path discovery — robust for the packed
/// global tool. <see cref="Load"/> returns the embedded default (cached); the two-arg
/// <see cref="PlanValidator"/> ctor injects a synthetic registry for unit tests, mirroring the
/// existing <see cref="Execution.IExecutableProbe"/> injection.
/// </para>
/// </summary>
public sealed class BannedPatternRegistry
{
    /// <summary>
    /// The logical resource name of the embedded default registry (pinned via <c>LogicalName</c> in
    /// <c>Guardrails.Core.csproj</c>, so it is stable regardless of the source file's on-disk path).
    /// </summary>
    private const string ResourceName = "Guardrails.Core.Resources.banned-guardrail-patterns.json";

    public IReadOnlyList<BannedPattern> Patterns { get; }

    public BannedPatternRegistry(IReadOnlyList<BannedPattern> patterns) => Patterns = patterns;

    private static readonly Lazy<BannedPatternRegistry> DefaultInstance = new(LoadEmbedded);

    /// <summary>
    /// The default registry — the single authored file embedded into this assembly. Cached; every
    /// entry's <see cref="BannedPattern.BadPattern"/> is pre-compiled at load so a malformed regex
    /// is a loud load-time fault, never a silent mid-scan surprise (SSOT §4.6).
    /// </summary>
    public static BannedPatternRegistry Load() => DefaultInstance.Value;

    /// <summary>Parse a registry from raw JSON text (used by <see cref="LoadEmbedded"/> and by tests).</summary>
    public static BannedPatternRegistry Parse(string json)
    {
        RegistryFile? file = JsonSerializer.Deserialize<RegistryFile>(json, PlanJson.Options);
        IReadOnlyList<BannedPattern> patterns = file?.Patterns ?? [];

        // Pre-compile every pattern so an invalid badPattern is a loud, entry-named fault at load,
        // not a mid-scan crash. The meta-test catches it first, so it can never reach a shipped build.
        foreach (BannedPattern pattern in patterns)
        {
            try
            {
                _ = pattern.Matcher;
            }
            catch (RegexParseException ex)
            {
                throw new InvalidOperationException(
                    $"Banned-pattern entry '{pattern.Id}' has an invalid badPattern regex: {ex.Message}", ex);
            }
        }

        return new BannedPatternRegistry(patterns);
    }

    private static BannedPatternRegistry LoadEmbedded()
    {
        Assembly assembly = typeof(BannedPatternRegistry).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded banned-pattern registry resource '{ResourceName}' was not found in " +
                $"'{assembly.GetName().Name}'. Check the <EmbeddedResource> Link in Guardrails.Core.csproj.");
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private sealed class RegistryFile
    {
        public int Version { get; init; }
        public IReadOnlyList<BannedPattern> Patterns { get; init; } = [];
    }
}
