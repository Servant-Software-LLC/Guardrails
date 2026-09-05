using System.Text.Json;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// Reads ONE fact out of a task folder: was the action a script or a prompt (SSOT §3)?
///
/// <para><b>Why it exists as its own type.</b> Two independent readers need this answer and must never
/// disagree about it — <see cref="TelemetryAttributionCensus"/>, which COUNTS the rows that name no model,
/// and <see cref="TelemetryIngest"/>, which now STAMPS each row with why it names none
/// (<see cref="ModelAttribution"/>). If the census called a task a script and the ETL called the same task
/// a prompt, the census would report a clean corpus while the corpus itself recorded a defect on the very
/// same row. One spelling, two callers.</para>
///
/// <para><b>Why not <c>PlanLoader</c>.</b> Loading a whole plan through <c>PlanLoader</c> would demand a
/// valid <c>guardrails.json</c> and clean validation from a folder these callers only want two facts out
/// of — and both callers are pointed at plan folders of RUNS THAT ALREADY HAPPENED, some of which no
/// longer validate against today's schema. The decision order below is <c>PlanLoader</c>'s own, restated
/// the way <see cref="TelemetryIngest"/> already restates the journal's status tokens.</para>
/// </summary>
public static class TaskActionKindReader
{
    /// <summary>
    /// The prompt-action suffix and the action-file prefix, SSOT §3's convention: exactly one
    /// <c>action.*</c> file in the task folder, and <c>.prompt.md</c> is what makes it a prompt.
    /// </summary>
    private const string PromptExtension = ".prompt.md";
    private const string ActionFilePrefix = "action.";

    /// <summary>The two path segments a task definition is read from (SSOT §3).</summary>
    private const string TasksDirectoryName = "tasks";
    private const string TaskDefinitionFileName = "task.json";

    /// <summary>
    /// The action KIND of <c>&lt;planFolder&gt;/tasks/&lt;taskId&gt;/</c>, or — when it cannot be decided —
    /// a message saying why.
    ///
    /// <para>Decided the way SSOT §3 decides it and in the same order <c>PlanLoader</c> does: an explicit
    /// <c>action.path</c> first, else the single <c>action.*</c> file in the task folder, and
    /// <c>.prompt.md</c> is what makes either a prompt. An explicit <c>action.path</c> is read for its
    /// EXTENSION only and is deliberately not required to exist — whether the action file is still on disk
    /// decides whether that task could RUN today, which is not the question either caller asks about a run
    /// that already happened.</para>
    ///
    /// <para>Zero or several <c>action.*</c> files is UNDECIDABLE rather than a guess: SSOT §3 makes both a
    /// validation error, so a folder in that state cannot be told apart as script-versus-prompt, and the
    /// only honest answer is to say so. Guessing here would either invent a defect or excuse a real one.</para>
    ///
    /// <para><b>A nested task id resolves correctly.</b> A wave plan journals <c>&lt;wave&gt;/&lt;task&gt;</c>
    /// as the task id, and that is a relative PATH under <c>tasks/</c> — so it is combined, not treated as
    /// a single folder name.</para>
    /// </summary>
    public static (ActionKind? Kind, string? Undecidable) Read(string planFolder, string taskId)
    {
        string taskFolder = Path.Combine(planFolder, TasksDirectoryName, taskId);
        string definitionPath = Path.Combine(taskFolder, TaskDefinitionFileName);

        RawTask? definition;
        try
        {
            // PlanJson.Options is the SAME reader every manifest read uses, comments and trailing commas
            // included: a hand-edited task.json the harness itself accepts must not read as malformed here.
            definition = JsonSerializer.Deserialize<RawTask>(File.ReadAllText(definitionPath), PlanJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{TaskDefinitionFileName} could not be read: {ex.Message}");
        }

        if (definition is null)
        {
            return (null, $"{TaskDefinitionFileName} deserialized to null");
        }

        if (definition.Action?.Path is { } declaredPath && !string.IsNullOrWhiteSpace(declaredPath))
        {
            return (KindFor(declaredPath), null);
        }

        string[] candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(taskFolder)
                .Where(f => Path.GetFileName(f).StartsWith(ActionFilePrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"the task folder could not be listed: {ex.Message}");
        }

        return candidates.Length switch
        {
            1 => (KindFor(candidates[0]), null),
            0 => (null, "no action.* file, so the action kind is undecidable"),
            _ => (null,
                $"{candidates.Length} action.* files ({string.Join(", ", candidates.Select(Path.GetFileName))}), "
                + "so the action kind is undecidable")
        };
    }

    /// <summary>SSOT §3: a <c>.prompt.md</c> path is a prompt action; anything else is a script.</summary>
    private static ActionKind KindFor(string path) =>
        path.EndsWith(PromptExtension, StringComparison.OrdinalIgnoreCase)
            ? ActionKind.Prompt
            : ActionKind.Script;
}
