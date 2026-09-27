using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Bundle;

/// <summary>The flags of SSOT §17.1 that shape what goes in the bundle (the destination is the CLI's concern).</summary>
public sealed record BundleOptions
{
    /// <summary><c>--run &lt;id&gt;</c>; null bundles the journal's run.</summary>
    public string? RunId { get; init; }

    /// <summary><c>--task &lt;id&gt;</c>, distinct; empty selects every task.</summary>
    public IReadOnlyList<string> Tasks { get; init; } = [];

    /// <summary><c>--max-size</c>, in bytes (MiB × 1 048 576).</summary>
    public long MaxSizeBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary><c>--lean</c>.</summary>
    public bool Lean { get; init; }

    /// <summary><c>--include-worktree-diff</c>.</summary>
    public bool IncludeWorktreeDiff { get; init; }

    /// <summary><c>--without-agent-text</c>.</summary>
    public bool WithoutAgentText { get; init; }

    /// <summary><c>--keep-paths</c>.</summary>
    public bool KeepPaths { get; init; }

    /// <summary><c>--no-redact</c>.</summary>
    public bool NoRedact { get; init; }
}

/// <summary>A tool's <c>--version</c> answer for SUMMARY.md block 1.</summary>
/// <param name="Tool">The tool (<c>claude</c>, <c>agent</c>, <c>dotnet</c>, <c>git</c>).</param>
/// <param name="Version">Its first output line, or <c>not on PATH</c> / <c>timed out</c>.</param>
public sealed record BundleToolVersion(string Tool, string Version);

/// <summary>
/// Everything the bundle asks the machine, injected so identical on-disk state gives a byte-identical zip (§17.10):
/// the clock, tool versions, liveness, git, validate, the environment and the path facts.
/// </summary>
public sealed record BundleProbes
{
    /// <summary>The bundling shell's environment.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>The <c>Bundled at</c> clock — the one line the determinism check masks.</summary>
    public required Func<DateTimeOffset> Now { get; init; }

    /// <summary>This binary's version.</summary>
    public required string HarnessVersion { get; init; }

    /// <summary>The bundling machine's OS description.</summary>
    public required string BundlingOs { get; init; }

    /// <summary><c>claude</c>, <c>agent</c>, <c>dotnet</c>, <c>git</c> <c>--version</c> (10 s timeout each).</summary>
    public required Func<IReadOnlyList<BundleToolVersion>> ToolVersions { get; init; }

    /// <summary><c>RunLiveness.Assess</c> over the journal's owner, injected.</summary>
    public required Func<RunOwner?, RunLivenessState> Liveness { get; init; }

    /// <summary>The git calls of §17.2 item 5.</summary>
    public required IBundleGit Git { get; init; }

    /// <summary>Re-runs validate (§17.2 item 6) and returns its printed output.</summary>
    public required Func<string> Validate { get; init; }

    /// <summary>The home directory, for path anonymization.</summary>
    public string? Home { get; init; }

    /// <summary>The OS user name, for path anonymization.</summary>
    public string? UserName { get; init; }

    /// <summary>Whether paths compare case-insensitively (Windows, macOS).</summary>
    public bool CaseInsensitivePaths { get; init; }

    /// <summary>The worktree root the plan's segments live under, when known.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>The bounded reader (§17.2). A test injects one whose opener throws sharing errors.</summary>
    public BundleFileReader Reader { get; init; } = new();

    /// <summary>The redactor; a test injects one that throws (pass 6).</summary>
    public Func<string, BundleRedactionContext, BundleRedactionResult> Redact { get; init; } = BundleRedactor.Redact;

    /// <summary>A file's size and last-write time (the Live files block, and the newest gateway session; #805 S1/S3).</summary>
    public Func<string, BundleFileStat?> Stat { get; init; } = BundleFileStat.Of;

    /// <summary>The owner process's descendants (#805 S1), asked only while the run is Running.</summary>
    public Func<int, BundleProcessTree> ProcessTree { get; init; } = SystemBundleProcessTree.Capture;

    /// <summary>Notes for MANIFEST.md's Notes list (an unparsable <c>GIT_CONFIG_COUNT</c>).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What <c>guardrails bundle</c> is asked to package.</summary>
public sealed record BundleRequest
{
    /// <summary>The loaded plan.</summary>
    public required PlanDefinition Plan { get; init; }

    /// <summary>The flags.</summary>
    public required BundleOptions Options { get; init; }
}
