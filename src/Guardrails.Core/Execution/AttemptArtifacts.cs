using System.Text.Json;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Writes the per-attempt log artifacts of SSOT §8: the action's captured stdout/stderr
/// (tee — the strings are already in memory, mirrored here to files so guardrails can read
/// them via <c>GUARDRAILS_ACTION_STDOUT/_STDERR</c>), the <c>action-result.json</c>
/// summary, and each guardrail's stdout/stderr. <c>composed-prompt.md</c>,
/// <c>claude-stream.jsonl</c>, verdict files (M5) and <c>feedback.md</c> (M4) are not
/// produced here.
/// </summary>
internal static class AttemptArtifacts
{
    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Write <c>action-stdout.log</c>, <c>action-stderr.log</c>, and <c>action-result.json</c>.</summary>
    public static void WriteActionLogs(string logDir, ProcessResult result, string kind)
    {
        Directory.CreateDirectory(logDir);

        AtomicFile.WriteAllText(Path.Combine(logDir, "action-stdout.log"), result.StandardOutput);
        AtomicFile.WriteAllText(Path.Combine(logDir, "action-stderr.log"), result.StandardError);

        var summary = new ActionResultDocument
        {
            Kind = kind,
            ExitCode = result.ExitCode,
            Summary = SummaryFor(result)
        };
        AtomicFile.WriteAllText(
            Path.Combine(logDir, "action-result.json"),
            JsonSerializer.Serialize(summary, ResultOptions));
    }

    /// <summary>
    /// Write <c>attempt-provenance.json</c> — the #198 machine-readable header the harness knows BEFORE
    /// the attempt runs: the resolved model, the segment worktree (branch + path), and the base commit.
    /// A no-op when <paramref name="provenance"/> is null (a serial script attempt has nothing to record).
    /// </summary>
    public static void WriteProvenance(string logDir, Journal.AttemptProvenance? provenance)
    {
        if (provenance is null)
        {
            return;
        }

        Directory.CreateDirectory(logDir);
        AtomicFile.WriteAllText(
            Path.Combine(logDir, "attempt-provenance.json"),
            JsonSerializer.Serialize(provenance, ResultOptions));
    }

    /// <summary>
    /// Write <c>scope-clean.log</c> naming the out-of-scope paths a phase-2 scope-clean stripped from
    /// the segment after the guardrails PASSED (SSOT §3.4, issue #280). A durable, UI-independent trace
    /// (the #253 "don't silently vanish files" posture): the paths were a passing guardrail's side
    /// effects (an <c>npm ci</c> / build cache), cleaned so the commit carries exactly the in-scope
    /// diff — never a failure. No-op when nothing was stripped.
    /// </summary>
    public static void WriteScopeCleanNote(string logDir, IReadOnlyList<WriteScopeOffense> stripped)
    {
        if (stripped.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(logDir);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# phase-2 scope-clean (SSOT §3.4, issue #280)");
        sb.AppendLine("# These out-of-scope paths were left by a PASSING guardrail (e.g. an `npm ci` /");
        sb.AppendLine("# build side effect) and were stripped so the segment commit carries exactly the");
        sb.AppendLine("# in-scope diff. This is NOT a failure — verifiers legitimately produce side effects.");
        foreach (WriteScopeOffense o in stripped)
        {
            sb.Append(o.Status).Append('\t').AppendLine(o.Path);
        }

        AtomicFile.WriteAllText(Path.Combine(logDir, "scope-clean.log"), sb.ToString());
    }

    /// <summary>
    /// Write <c>prior-attempt.patch</c> — the applyable retry-salvage patch (issue #306): the full
    /// unified diff of a rolled-back attempt vs the task's <c>taskBase</c>, so the NEXT attempt can
    /// <c>git apply</c> it to recover ALL the prior work or read it to cherry-pick. Lives in the
    /// preserved attempt's OWN log dir (a sibling of its <c>feedback.md</c>), never inside the segment
    /// worktree (which must stay clean for <c>git status</c> / the write-scope diff). Returns the
    /// absolute path written, or null when <paramref name="patch"/> is empty (nothing to salvage) — the
    /// caller then falls back to the git ref alone. Best-effort: an IO failure returns null rather than
    /// aborting the retry loop.
    /// </summary>
    public static string? WriteSalvagePatch(string logDir, string patch) =>
        WritePatch(logDir, "prior-attempt.patch", patch);

    /// <summary>
    /// Write <c>out-of-scope.patch</c> (issue #705): a write-scope violation's offending changes, captured before the
    /// scoped revert destroyed them. It sits beside <c>prior-attempt.patch</c> in the attempt's own log dir, never in
    /// the segment worktree, and it is never offered to a retry as salvage: it is for the human deciding whether the
    /// task's scope should grow. Returns the path written, or null when <paramref name="patch"/> is empty — so an
    /// empty file never stands in for kept work — or when the write fails.
    /// </summary>
    public static string? WriteOutOfScopePatch(string logDir, string patch) =>
        WritePatch(logDir, "out-of-scope.patch", patch);

    /// <summary>
    /// The one best-effort patch writer behind <see cref="WriteSalvagePatch"/> and <see cref="WriteOutOfScopePatch"/>:
    /// null for an empty patch or an IO failure, never an exception that could abort the retry loop.
    /// </summary>
    private static string? WritePatch(string logDir, string fileName, string patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(logDir);
            string path = Path.Combine(logDir, fileName);
            AtomicFile.WriteAllText(path, patch);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Write <c>guardrail-&lt;name&gt;.stdout.log</c> and <c>.stderr.log</c>.</summary>
    public static void WriteGuardrailLogs(string logDir, string guardrailName, ProcessResult result)
    {
        Directory.CreateDirectory(logDir);
        string safe = Sanitize(guardrailName);
        AtomicFile.WriteAllText(Path.Combine(logDir, $"guardrail-{safe}.stdout.log"), result.StandardOutput);
        AtomicFile.WriteAllText(Path.Combine(logDir, $"guardrail-{safe}.stderr.log"), result.StandardError);
    }

    private static string SummaryFor(ProcessResult result)
    {
        if (result.TimedOut)
        {
            return "timed out";
        }

        return result.ExitCode == 0 ? "ok" : $"exited {result.ExitCode}";
    }

    private static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return new string(buffer);
    }

    /// <summary>The <c>action-result.json</c> shape: <c>{ kind, exitCode, summary }</c> (SSOT §5.1/§8).</summary>
    private sealed record ActionResultDocument
    {
        public required string Kind { get; init; }
        public required int ExitCode { get; init; }
        public required string Summary { get; init; }
    }
}
