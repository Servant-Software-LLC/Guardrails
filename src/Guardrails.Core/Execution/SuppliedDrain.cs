namespace Guardrails.Core.Execution;

/// <summary>
/// The boundary drain (design 40 §2): copies whatever <c>guardrails supply</c> staged under
/// <c>&lt;planDirectory&gt;/logs/&lt;runId&gt;/supplied/</c> onto the run's base, commits it with the §4
/// provenance trailer, and deletes the staging tree so a later drain at the same boundary is inert.
/// <see cref="Guardrails.Core.Execution.SuppliedStagingTree"/> only resolves paths; this class is the
/// actual git seam — the copy, the commit, and the cleanup.
/// </summary>
/// <remarks>
/// Never-weaker (§2 step 1): a run that never called <c>supply</c> has no staged files, and
/// <see cref="Drain"/> must do nothing at all for it — no commit, not even an empty one.
/// </remarks>
public static class SuppliedDrain
{
    /// <summary>
    /// Drain everything staged for <paramref name="runId"/> under <paramref name="planDirectory"/> onto
    /// <paramref name="workspace"/>: copy each staged file to its workspace-relative destination
    /// (<see cref="SuppliedStagingTree.DrainableFiles"/>), commit with the trailer
    /// <c>Supplied-By: &lt;by&gt;</c> / <c>Guardrails-Run: &lt;runId&gt;</c> (§4) — the FIRST line naming
    /// <paramref name="by"/> verbatim (<c>operator</c>, <c>overwatcher</c>, or <c>task:&lt;folder&gt;</c>),
    /// never the fixed string <c>Supplied-By-Operator</c>, which would misattribute an overwatcher- or
    /// task-triggered supply as an operator action — and delete the staging tree afterward so a second
    /// call for the same run finds nothing to commit.
    /// </summary>
    public static SuppliedDrainResult Drain(string workspace, string planDirectory, string runId, string by)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// What one <see cref="SuppliedDrain.Drain"/> call committed — the raw material for the §4 provenance
/// record and the §2 step-3 <c>SuppliedResourcesCommitted</c> announcement.
/// </summary>
public sealed record SuppliedDrainResult
{
    /// <summary>
    /// The workspace-relative, forward-slash path of every file committed, or empty when nothing was
    /// staged (§2 step 1 — never-weaker).
    /// </summary>
    public required IReadOnlyList<string> CommittedPaths { get; init; }

    /// <summary>The sum of every committed file's length in bytes, or 0 when nothing was staged.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>The new commit's sha, or null when nothing was staged and no commit was made.</summary>
    public string? CommitSha { get; init; }
}
