namespace Guardrails.Core.Bundle;

/// <summary>SSOT §17.3: lean entries are always included; full entries are included by default and withheld by <c>--lean</c>.</summary>
public enum BundleClass
{
    /// <summary>Harness-written evidence; always included.</summary>
    Lean,

    /// <summary>Prompts, transcripts, streams, gateway sessions, patches, worktree diffs; withheld by <c>--lean</c>.</summary>
    Full,
}

/// <summary>SSOT §17.7's cap classes.</summary>
public enum BundleCapClass
{
    /// <summary>Read whole.</summary>
    None,

    /// <summary>2 MiB tail (1 MiB, then 512 KiB, under trim tier 4).</summary>
    Stream,

    /// <summary>256 KiB tail (128 KiB under trim tier 4).</summary>
    Log,

    /// <summary>Whole or nothing at the stream-class cap (<c>*.patch</c>).</summary>
    Patch,
}

/// <summary>What a named artifact kind is, for the collector.</summary>
/// <param name="Kind">A short kind name (<c>stream</c>, <c>transcript</c>, <c>composed-prompt</c>, …) the trim tiers key on.</param>
/// <param name="Class">Lean or full.</param>
/// <param name="Cap">The cap class.</param>
/// <param name="AgentText">Whether <c>--without-agent-text</c> removes it (§17.6.5).</param>
/// <param name="Protected">Never trimmed, read whole (§17.7's protected core).</param>
/// <param name="Projected">Under <c>--without-agent-text</c>, kept as a field allow-list projection instead of removed.</param>
public sealed record BundleKind(
    string Kind, BundleClass Class, BundleCapClass Cap, bool AgentText, bool Protected = false, bool Projected = false);

/// <summary>
/// The allow-list of SSOT §17.3: only these named kinds are ever read. Anything else under <c>logs/&lt;runId&gt;/</c>
/// is listed in MANIFEST.md by path and size and nothing more (<c>unknown-kind</c>) — a directory sweep would have
/// shipped <c>claude-config/.claude.json</c> and <c>shell-snapshots/</c> the day they appeared.
/// </summary>
public static class BundleCatalog
{
    /// <summary>§17.7 initial stream-class cap.</summary>
    public const long StreamCap = 2L * 1024 * 1024;

    /// <summary>§17.7 initial log-class cap.</summary>
    public const long LogCap = 256L * 1024;

    /// <summary>An <c>attempt-N/</c> file, or null when it is not a named kind.</summary>
    public static BundleKind? AttemptFile(string fileName)
    {
        switch (fileName)
        {
            case "feedback.md":
                return new BundleKind("feedback", BundleClass.Lean, BundleCapClass.None, AgentText: true, Protected: true);
            case "attempt-provenance.json":
                return new BundleKind("provenance", BundleClass.Lean, BundleCapClass.None, AgentText: false, Protected: true, Projected: true);
            case "attempt-route.log":
                return new BundleKind("route", BundleClass.Lean, BundleCapClass.None, AgentText: false, Protected: true);
            case "action-result.json":
                return new BundleKind("action-result", BundleClass.Lean, BundleCapClass.None, AgentText: true);
            case "transcript.md":
                return new BundleKind("transcript", BundleClass.Full, BundleCapClass.Stream, AgentText: true);
            case "claude-stream.jsonl":
                return new BundleKind("stream", BundleClass.Full, BundleCapClass.Stream, AgentText: true);
            case "action-stdout.log" or "action-stderr.log":
                // Harness-captured process output, often the deciding evidence for a failing script action: lean.
                // A script can echo agent-written code or output, so --without-agent-text removes it.
                return new BundleKind("action-output", BundleClass.Lean, BundleCapClass.Log, AgentText: true);
        }

        if (fileName.EndsWith(".patch", StringComparison.Ordinal))
        {
            return new BundleKind("patch", BundleClass.Full, BundleCapClass.Patch, AgentText: true);
        }

        if (fileName.StartsWith("composed-prompt", StringComparison.Ordinal) && fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            return new BundleKind("composed-prompt", BundleClass.Full, BundleCapClass.Stream, AgentText: true);
        }

        if (fileName.StartsWith("guardrail-", StringComparison.Ordinal))
        {
            if (fileName.EndsWith(".verdict.json", StringComparison.Ordinal))
            {
                return new BundleKind("verdict", BundleClass.Lean, BundleCapClass.None, AgentText: true);
            }

            if (fileName.EndsWith(".stdout.log", StringComparison.Ordinal) || fileName.EndsWith(".stderr.log", StringComparison.Ordinal))
            {
                return new BundleKind("guardrail-output", BundleClass.Lean, BundleCapClass.Log, AgentText: true);
            }

            if (fileName.EndsWith(".transcript.md", StringComparison.Ordinal))
            {
                return new BundleKind("transcript", BundleClass.Full, BundleCapClass.Stream, AgentText: true);
            }

            if (fileName.EndsWith(".stream.jsonl", StringComparison.Ordinal))
            {
                return new BundleKind("stream", BundleClass.Full, BundleCapClass.Stream, AgentText: true);
            }
        }

        return null;
    }

    /// <summary>A task-level file (<c>logs/&lt;runId&gt;/&lt;id&gt;/</c>), or null.</summary>
    public static BundleKind? TaskFile(string fileName) => fileName switch
    {
        "feedback.md" => new BundleKind("task-feedback", BundleClass.Lean, BundleCapClass.None, AgentText: true),
        "overwatch.jsonl" => new BundleKind("overwatch", BundleClass.Lean, BundleCapClass.Log, AgentText: true),
        "triage.json" => new BundleKind("triage", BundleClass.Lean, BundleCapClass.None, AgentText: true),
        _ when fileName.StartsWith("union-reverify-", StringComparison.Ordinal) && fileName.EndsWith(".log", StringComparison.Ordinal)
            => new BundleKind("union-reverify", BundleClass.Lean, BundleCapClass.Log, AgentText: true),

        // #805 S8: the in-flight marker's harness log, and the overwatcher's and triage's own model streams.
        "inflight-marker.log" => new BundleKind("inflight-marker", BundleClass.Lean, BundleCapClass.Log, AgentText: false),
        "triage-stream.jsonl" => new BundleKind("task-stream", BundleClass.Full, BundleCapClass.Stream, AgentText: true),
        _ when fileName.StartsWith("overwatch-stream-attempt-", StringComparison.Ordinal) && fileName.EndsWith(".jsonl", StringComparison.Ordinal)
            => new BundleKind("task-stream", BundleClass.Full, BundleCapClass.Stream, AgentText: true),
        _ when fileName.StartsWith("overwatch-noverdict-", StringComparison.Ordinal) && fileName.EndsWith(".txt", StringComparison.Ordinal)
            => new BundleKind("overwatch-noverdict", BundleClass.Full, BundleCapClass.Log, AgentText: true),
        _ => null
    };

    /// <summary>A run-level file (<c>logs/&lt;runId&gt;/</c>), or null.</summary>
    public static BundleKind? RunFile(string fileName) => fileName switch
    {
        "events.jsonl" or "observer.jsonl" or "autonomy.jsonl" =>
            new BundleKind("run-stream", BundleClass.Lean, BundleCapClass.Log, AgentText: true),
        _ => null
    };

    /// <summary>An <c>escalations/</c> record.</summary>
    public static BundleKind Escalation { get; } = new("escalation", BundleClass.Lean, BundleCapClass.Log, AgentText: true);

    /// <summary>A gate capture file (<c>&lt;gate&gt;/&lt;check&gt;/…</c>), or null.</summary>
    public static BundleKind? GateFile(string fileName) => fileName switch
    {
        "stdout.log" or "stderr.log" => new BundleKind("gate-output", BundleClass.Lean, BundleCapClass.Log, AgentText: true),
        "result.json" => new BundleKind("gate-result", BundleClass.Lean, BundleCapClass.None, AgentText: false, Projected: true),
        _ => null
    };

    /// <summary>A gateway session transcript (<c>claude-config/projects/**/*.jsonl</c>).</summary>
    public static BundleKind GatewaySession { get; } = new("gateway-session", BundleClass.Full, BundleCapClass.Stream, AgentText: true);

    /// <summary><c>state/run.json</c>.</summary>
    public static BundleKind Journal { get; } = new("journal", BundleClass.Lean, BundleCapClass.None, AgentText: false, Protected: true, Projected: true);

    /// <summary><c>plan/guardrails.json</c>: operator configuration, kept even under <c>--without-agent-text</c>.</summary>
    public static BundleKind PlanConfig { get; } = new("plan-config", BundleClass.Lean, BundleCapClass.None, AgentText: false, Protected: true);

    /// <summary>A selected <c>task.json</c>.</summary>
    public static BundleKind TaskDefinition { get; } = new("task-definition", BundleClass.Lean, BundleCapClass.None, AgentText: true);

    /// <summary>File names the bundle knows by construction, exempt from the entropy pass as path segments (§17.6.2).</summary>
    public static IReadOnlyList<string> KnownFileNames { get; } =
    [
        "feedback.md", "attempt-provenance.json", "attempt-route.log", "action-result.json", "transcript.md",
        "claude-stream.jsonl", "composed-prompt.md", "prior-attempt.patch", "out-of-scope.patch", "overwatch.jsonl",
        "triage.json", "events.jsonl", "observer.jsonl", "autonomy.jsonl", "result.json", "stdout.log", "stderr.log",
        "run.json", "guardrails.json", "task.json", "validate.txt", "state-in.json", "fragment.json",
        "action-out-fragment.json", "action-stdout.log", "action-stderr.log", "logs", "state", "tasks", "plan", "run",
        "gates", "git", "gateway", "sessions", "preflights", "guardrails", "escalations", "claude-config", "projects",
        "SUMMARY.md", "MANIFEST.md", "REDACTIONS.md",
    ];
}
