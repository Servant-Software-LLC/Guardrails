using System.Text.Json;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// The captured-output layout for a plan- or wave-scoped GATE evaluation (issue #432, SSOT §8) — the
/// four-folder model's <c>&lt;plan&gt;/preflights/</c> (Full Flight Checks), <c>&lt;plan&gt;/guardrails/</c>
/// (Terminal Gate), <c>&lt;plan&gt;/&lt;wave&gt;/preflights/</c> (wave ENTRY gate) and
/// <c>&lt;plan&gt;/&lt;wave&gt;/guardrails/</c> (wave EXIT gate).
/// <para>
/// A gate is NOT a task attempt — it has no attempt lifecycle and therefore no
/// <c>logs/&lt;runId&gt;/&lt;task-id&gt;/attempt-N/</c> dir to write into — so before #432 a gate's child
/// processes were run and their stdout/stderr thrown away. Only the one-line <c>reason</c> reached
/// <c>run.json</c>, and on a FAILED gate (the case a human most needs to post-mortem) there was nothing on
/// disk at all. This type gives every gate check the same treatment a task attempt's guardrails get, at a
/// predictable path:
/// </para>
/// <code>
/// logs/&lt;runId&gt;/preflights/&lt;check-name&gt;/            # plan-level Full Flight Checks
/// logs/&lt;runId&gt;/guardrails/&lt;check-name&gt;/            # plan-level Terminal Gate
/// logs/&lt;runId&gt;/&lt;wave-dir&gt;/preflights/&lt;check-name&gt;/   # wave ENTRY gate
/// logs/&lt;runId&gt;/&lt;wave-dir&gt;/guardrails/&lt;check-name&gt;/   # wave EXIT gate
///     ├── stdout.log
///     ├── stderr.log
///     └── result.json     # { name, passed, exitCode, timedOut, durationMs, reason? }
/// </code>
/// <para>
/// The gate folder names (<c>preflights</c>/<c>guardrails</c>) can never collide with a sibling task
/// folder: <see cref="Loading.PlanLoader"/> reserves both names, so no task id ends in either segment.
/// </para>
/// <para>
/// Writing is BEST-EFFORT: a gate's verdict is a deterministic property of its child processes, so an IO
/// failure while persisting evidence must never change (or crash) that verdict.
/// </para>
/// </summary>
public static class GateArtifacts
{
    /// <summary>The log-tree folder name for a PREFLIGHT (entry) gate's captured output.</summary>
    public const string PreflightsFolder = "preflights";

    /// <summary>The log-tree folder name for a GUARDRAIL (exit/terminal) gate's captured output.</summary>
    public const string GuardrailsFolder = "guardrails";

    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// The ABSOLUTE directory a gate's per-check output is captured under, or null when
    /// <paramref name="runId"/> is missing (a fake journal in a unit test) — a null return means
    /// "capture nothing", never a path rooted at the wrong place.
    /// </summary>
    /// <param name="planDirectory">The plan folder (the parent of <c>logs/</c>).</param>
    /// <param name="runId">The JOURNAL run id — the same <c>logs/&lt;runId&gt;/</c> tree task attempts use.</param>
    /// <param name="waveDir">The wave folder name for a wave-scoped gate; null for a plan-scoped gate.</param>
    /// <param name="gateFolder"><see cref="PreflightsFolder"/> or <see cref="GuardrailsFolder"/>.</param>
    public static string? DirectoryFor(string planDirectory, string? runId, string? waveDir, string gateFolder)
    {
        if (string.IsNullOrEmpty(planDirectory) || string.IsNullOrEmpty(runId))
        {
            return null;
        }

        return string.IsNullOrEmpty(waveDir)
            ? Path.Combine(planDirectory, "logs", runId, gateFolder)
            : Path.Combine(planDirectory, "logs", runId, waveDir, gateFolder);
    }

    /// <summary>
    /// The same location as <see cref="DirectoryFor"/> expressed PLAN-RELATIVE with forward slashes — the
    /// form recorded in <c>run.json</c> (matching the per-attempt <c>logDir</c> convention of SSOT §7), so a
    /// journal is portable across machines and OSes. Null when <paramref name="runId"/> is missing.
    /// </summary>
    public static string? RelativeDirectoryFor(string? runId, string? waveDir, string gateFolder)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return null;
        }

        return string.IsNullOrEmpty(waveDir)
            ? $"logs/{runId}/{gateFolder}"
            : $"logs/{runId}/{waveDir}/{gateFolder}";
    }

    /// <summary>
    /// Persist one gate check's captured <c>stdout.log</c> / <c>stderr.log</c> / <c>result.json</c> under
    /// <c>&lt;gateDirectory&gt;/&lt;check-name&gt;/</c>. Called for EVERY check (passing and failing): a
    /// passing check's output costs one small directory and is exactly what proves a gate was not vacuous.
    /// <para>
    /// Best-effort by contract — an IO/permission failure is swallowed so persisting evidence can never
    /// change a gate's verdict or abort a run.
    /// </para>
    /// </summary>
    /// <param name="gateDirectory">The gate's directory from <see cref="DirectoryFor"/>.</param>
    /// <param name="checkName">The check's name (guardrail filename minus extension).</param>
    /// <param name="result">The check's captured process result.</param>
    /// <param name="failureReason">The one-line reason journaled for a failing check; null when it passed.</param>
    public static void WriteCheck(
        string gateDirectory, string checkName, ProcessResult result, string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            string dir = Path.Combine(gateDirectory, Sanitize(checkName));
            Directory.CreateDirectory(dir);

            AtomicFile.WriteAllText(Path.Combine(dir, "stdout.log"), result.StandardOutput);
            AtomicFile.WriteAllText(Path.Combine(dir, "stderr.log"), result.StandardError);

            var summary = new GateCheckResultDocument
            {
                Name = checkName,
                Passed = result.Succeeded,
                ExitCode = result.ExitCode,
                TimedOut = result.TimedOut,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                Reason = failureReason
            };
            AtomicFile.WriteAllText(
                Path.Combine(dir, "result.json"),
                JsonSerializer.Serialize(summary, ResultOptions));
        }
        catch (IOException)
        {
            // Evidence is best-effort; the verdict is not.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Filesystem-safe form of a check name — the SAME rule the per-attempt guardrail logs use (SSOT §8):
    /// anything other than a letter, digit, <c>-</c>, <c>_</c> or <c>.</c> becomes <c>_</c>.
    /// </summary>
    public static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return new string(buffer);
    }

    /// <summary>The <c>result.json</c> shape written beside a gate check's captured streams.</summary>
    private sealed record GateCheckResultDocument
    {
        public required string Name { get; init; }
        public required bool Passed { get; init; }
        public required int ExitCode { get; init; }
        public required bool TimedOut { get; init; }
        public required long DurationMs { get; init; }
        public string? Reason { get; init; }
    }
}
