namespace Guardrails.Core.Execution;

/// <summary>
/// Path resolution for the harness-owned staging tree that <c>guardrails supply</c> writes into
/// (design 40 §1) — <c>&lt;planDirectory&gt;/logs/&lt;runId&gt;/supplied/</c>. Pure path logic: no
/// process spawning, no git — the actual copy-and-commit onto the run base is
/// <c>SuppliedDrain</c>'s job (design 40 §2). <see cref="StagedPathFor"/> maps a workspace-relative
/// destination path to its staged location and refuses one that would land outside the workspace
/// (GR2019's traversal rule, applied to this CLI argument, plan 08 §2/§3.4); <see cref="DrainableFiles"/>
/// enumerates what is currently staged for a run, paired with the workspace path each file will land
/// at when drained.
/// </summary>
public static class SuppliedStagingTree
{
    /// <summary>The staging tree's folder name under <c>logs/&lt;runId&gt;/</c>.</summary>
    public const string SuppliedFolder = "supplied";

    /// <summary>
    /// Resolves the <c>logs/&lt;runId&gt;/supplied/&lt;workspaceRelativePath&gt;</c> location a
    /// <c>guardrails supply</c> argument stages to — plan-relative, forward-slash, the same portable
    /// form <c>GateArtifacts.RelativeDirectoryFor</c> uses for the sibling <c>logs/&lt;runId&gt;/</c>
    /// trees. Refuses <paramref name="workspaceRelativePath"/> when it would land outside
    /// <paramref name="workspace"/> once resolved — an absolute path, or one that climbs out via
    /// <c>..</c> segments (GR2019's <c>writeScope</c> traversal rule, applied here to a CLI argument).
    /// </summary>
    public static StagedPathResult StagedPathFor(string workspace, string runId, string workspaceRelativePath)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Every file currently staged under <c>&lt;planDirectory&gt;/logs/&lt;runId&gt;/supplied/</c>,
    /// paired with the workspace-relative path it will land at when the harness drains the tree
    /// (design 40 §2). Empty when the run has never used <c>supply</c> — the never-weaker requirement:
    /// a run that stages nothing behaves byte-identically to one built before this feature existed.
    /// </summary>
    public static IReadOnlyList<SuppliedFile> DrainableFiles(string planDirectory, string runId)
    {
        throw new NotImplementedException();
    }
}

/// <summary>The outcome of a <see cref="SuppliedStagingTree.StagedPathFor"/> call.</summary>
public sealed record StagedPathResult
{
    /// <summary>True when the requested destination path would land outside the workspace and was refused.</summary>
    public required bool Refused { get; init; }

    /// <summary>
    /// The plan-relative, forward-slash staged location (<c>logs/&lt;runId&gt;/supplied/...</c>), or
    /// null when <see cref="Refused"/>.
    /// </summary>
    public string? StagedPath { get; init; }

    /// <summary>A human-readable reason for the refusal, or null when not <see cref="Refused"/>.</summary>
    public string? RefusalReason { get; init; }
}

/// <summary>One file staged under a run's <c>supplied/</c> tree, paired with its drain destination.</summary>
public sealed record SuppliedFile
{
    /// <summary>The staged file's absolute path on disk, as currently written under <c>supplied/</c>.</summary>
    public required string AbsoluteStagedPath { get; init; }

    /// <summary>The workspace-relative, forward-slash path this file will land at when drained.</summary>
    public required string DestinationPath { get; init; }
}
