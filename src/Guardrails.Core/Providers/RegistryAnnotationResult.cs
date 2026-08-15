namespace Guardrails.Core.Providers;

/// <summary>
/// What one <c>guardrails providers init</c> pass found and would write (SSOT §9.7). The caller decides
/// whether to show it, write it, or discard it — this type performs no IO and holds no file handle.
/// </summary>
public sealed record RegistryAnnotationResult
{
    /// <summary>The configuration exactly as it was read, byte for byte.</summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// The configuration with the annotation spliced in — equal to <see cref="OriginalText"/> when there
    /// was nothing to add, and ALWAYS equal to it when <see cref="Failure"/> is set.
    /// </summary>
    public required string AnnotatedText { get; init; }

    /// <summary>
    /// Why the pass produced nothing usable: the file did not parse, or the annotated result failed its
    /// own preservation check. Null on success. When this is set the caller MUST NOT write.
    /// </summary>
    public string? Failure { get; init; }

    /// <summary>One entry per <c>promptRunners</c> block, in declaration order.</summary>
    public IReadOnlyList<RegistryBlockReport> Blocks { get; init; } = [];

    /// <summary>
    /// The <c>kind</c> tokens this build has no model-enumeration surface for, in declaration order — in
    /// v1, every kind the config uses (<see cref="Model.PromptRunnerKinds.ModelEnumerable"/> is empty).
    /// Each one got an explicit "could not enumerate" note in the file, and NO block was invented for it.
    /// </summary>
    public IReadOnlyList<string> UnenumerableKinds { get; init; } = [];

    /// <summary>The planned change, as diff hunks derived from the insertions themselves.</summary>
    public IReadOnlyList<RegistryAnnotationHunk> Hunks { get; init; } = [];

    /// <summary>True when the pass completed; false when <see cref="Failure"/> explains why it did not.</summary>
    public bool Succeeded => Failure is null;

    /// <summary>True when the annotation would actually change the file.</summary>
    public bool HasChanges =>
        Succeeded && !string.Equals(OriginalText, AnnotatedText, StringComparison.Ordinal);

    /// <summary>
    /// The blocks whose <paramref name="axis"/> is still UNSTATED after annotation — the question this
    /// command exists to put in front of a human. For <see cref="RegistryAxes.Costly"/> this is the
    /// concrete payoff of keeping the axis tri-state: <c>null</c> is not <c>false</c>, so the generator
    /// can name every block whose cost nobody has ruled on and ASK, where an "absent means false" schema
    /// would have answered on the user's behalf and had nothing left to report.
    /// </summary>
    public IReadOnlyList<RegistryBlockReport> Unstated(string axis) =>
        [.. Blocks.Where(b => b.UnstatedAxes.Contains(axis, StringComparer.Ordinal))];

    internal static RegistryAnnotationResult Unusable(string original, string failure) => new()
    {
        OriginalText = original,
        AnnotatedText = original,
        Failure = failure
    };

    internal static RegistryAnnotationResult Unchanged(string original, AnnotationPlan plan) => new()
    {
        OriginalText = original,
        AnnotatedText = original,
        Blocks = plan.Blocks,
        UnenumerableKinds = plan.UnenumerableKinds
    };

    internal static RegistryAnnotationResult Changed(string original, string annotated, AnnotationPlan plan) => new()
    {
        OriginalText = original,
        AnnotatedText = annotated,
        Blocks = plan.Blocks,
        UnenumerableKinds = plan.UnenumerableKinds,
        Hunks = plan.BuildHunks()
    };
}

/// <summary>What one <c>promptRunners</c> block declared, and what this pass added to it.</summary>
public sealed record RegistryBlockReport
{
    /// <summary>The block's <c>promptRunners</c> map key.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The block's <c>kind</c> exactly as written (or <c>claude</c>, the default, when it declares none).
    /// Carried verbatim even when unrecognised, so the report names what the user actually typed.
    /// </summary>
    public required string KindToken { get; init; }

    /// <summary>
    /// The solicited keys that are still NOT STATED — absent, or present with an explicit <c>null</c>.
    /// The two are the same thing to the loader, which is why writing <c>null</c> for an absent key leaves
    /// the block on this list: the placeholder is a prompt, never an answer.
    /// </summary>
    public required IReadOnlyList<string> UnstatedAxes { get; init; }

    /// <summary>The keys this pass appended (each as <c>null</c>). Empty on a re-run.</summary>
    public required IReadOnlyList<string> AddedKeys { get; init; }

    /// <summary>How many legal-value comments this pass added above keys that had none.</summary>
    public required int AddedComments { get; init; }
}

/// <summary>
/// One contiguous change, ready to render as a unified diff. <see cref="Removed"/> is empty for a pure
/// line insertion (nearly every hunk) and holds the single original line when a trailing comma had to be
/// added to it before new keys could follow.
/// </summary>
public sealed record RegistryAnnotationHunk
{
    /// <summary>The block this change belongs to, for the hunk header.</summary>
    public required string Context { get; init; }

    /// <summary>1-based line number in the ORIGINAL file.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Original lines this hunk replaces — empty for a pure insertion.</summary>
    public required IReadOnlyList<string> Removed { get; init; }

    /// <summary>The lines that take their place.</summary>
    public required IReadOnlyList<string> Added { get; init; }
}
