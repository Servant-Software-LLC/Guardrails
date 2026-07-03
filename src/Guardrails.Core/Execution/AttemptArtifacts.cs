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
