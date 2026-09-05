using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The REGRESSION PIN for issue #577 — "76% of telemetry rows name no usable model".
///
/// <para><b>What the census actually found, and why the headline was misleading.</b> Split across the
/// 806-row operator corpus, the 413 rows naming no model were: <b>319 task-grain sentinel rows</b>
/// (<c>attempt == 0</c>, no model by construction), <b>2 script-action rows</b> (no model by
/// construction), and <b>93 prompt-action attempt rows</b> — the only genuine defect. So 77% of the
/// "missing" attribution was never missing at all. But NOTHING IN THE ROW SAID SO: all three wrote
/// <c>model: null</c>, and the only way to tell them apart was to join the corpus back to the plan folders
/// on disk — an external join that is impossible for the 41 rows whose plan folder has since been deleted,
/// and that every future analysis would have to re-derive from scratch.</para>
///
/// <para><b>The invariant these tests pin</b> (SSOT §15.2b): a prompt-action attempt records a named model
/// or a documented sentinel saying WHY there is none, and <c>None</c> on a prompt action is
/// DISTINGUISHABLE IN THE DATA from <c>None</c> on a script action. The first is a defect
/// (<see cref="ModelAttribution.NotRecorded"/>); the second is correct
/// (<see cref="ModelAttribution.ScriptAction"/>). Before this work both were the same empty column.</para>
///
/// <para><b>Every assertion parses RAW JSON off disk, never the typed <see cref="TelemetryRow"/>.</b> A
/// <c>[JsonIgnore]</c> on the new column — or a serializer policy that dropped it — would leave the typed
/// round-trip passing while the corpus on disk carried nothing, which is the precise shape of the silent
/// failure this column exists to end. Reading the wire bytes is the only assertion that cannot be
/// satisfied by an in-memory object.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryModelAttributionTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "gr577-model-attribution", Guid.NewGuid().ToString("N"));

    private string CorpusRoot => Path.Combine(root, "corpus");

    private string PlanFolder => Path.Combine(root, "plan");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    // --- the pin: the two "no model" cases must be distinguishable ------------------------------------

    /// <summary>
    /// <b>THE DEFECT CASE.</b> A PROMPT action certainly ran some model, so an attempt row that names none
    /// is a recording gap — and the row must SAY it is one rather than leaving a bare null that reads
    /// exactly like the two correct cases.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void APromptAttemptNamingNoModel_IsAttributedNotRecorded()
    {
        WriteTask("01-prompt", prompt: true);
        WriteJournal(("01-prompt", null));

        Ingest();

        JsonElement row = AttemptRow("01-prompt", attempt: 1);
        Assert.Null(StringOrNull(row, "model"));
        Assert.Equal(ModelAttribution.NotRecorded, StringOrNull(row, "modelAttribution"));
    }

    /// <summary>
    /// <b>THE CORRECT CASE, and the one that must never be confused with the defect.</b> A script invokes
    /// no model, so a null model column is the truth — and asserting the two tokens DIFFER inside one test
    /// is what pins the distinguishability the issue demands. A future change that collapsed both back to
    /// one token (or to a bare null) fails here, not merely in a count somewhere downstream.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AScriptAttemptNamingNoModel_IsAttributedScriptAction_AndDiffersFromThePromptDefect()
    {
        WriteTask("01-prompt", prompt: true);
        WriteTask("02-script", prompt: false);
        WriteJournal(("01-prompt", null), ("02-script", null));

        Ingest();

        string? promptToken = StringOrNull(AttemptRow("01-prompt", 1), "modelAttribution");
        string? scriptToken = StringOrNull(AttemptRow("02-script", 1), "modelAttribution");

        Assert.Equal(ModelAttribution.ScriptAction, scriptToken);
        Assert.Equal(ModelAttribution.NotRecorded, promptToken);

        // Both rows carry model: null. The whole point of #577 is that this is no longer all a reader gets.
        Assert.Null(StringOrNull(AttemptRow("01-prompt", 1), "model"));
        Assert.Null(StringOrNull(AttemptRow("02-script", 1), "model"));
        Assert.NotEqual(scriptToken, promptToken);
    }

    // --- the remaining tokens ------------------------------------------------------------------------

    /// <summary>An attempt that names a real model explains itself, and is the only attributable-and-usable case.</summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AnAttemptNamingARealModel_IsAttributedRecorded()
    {
        WriteTask("01-prompt", prompt: true);
        WriteJournal(("01-prompt", "gr577-real-model"));

        Ingest();

        JsonElement row = AttemptRow("01-prompt", 1);
        Assert.Equal("gr577-real-model", StringOrNull(row, "model"));
        Assert.Equal(ModelAttribution.Recorded, StringOrNull(row, "modelAttribution"));
    }

    /// <summary>
    /// The <c>(cli default)</c> sentinel is a NAMED value but not a model IDENTITY — it means "whatever the
    /// runner CLI's own default was". Pooling those 134 corpus rows with a real model's would attribute
    /// their cost and outcomes to a model nobody recorded, so they get their own token and an analysis has
    /// to decide about them deliberately rather than inherit them.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void TheCliDefaultSentinel_IsAttributedCliDefault_NotRecorded()
    {
        WriteTask("01-prompt", prompt: true);
        WriteJournal(("01-prompt", "(cli default)"));

        Ingest();

        JsonElement row = AttemptRow("01-prompt", 1);
        Assert.Equal(ModelAttribution.CliDefault, StringOrNull(row, "modelAttribution"));
        Assert.NotEqual(ModelAttribution.Recorded, StringOrNull(row, "modelAttribution"));
    }

    /// <summary>
    /// The once-per-task sentinel row. It summarizes every attempt of a task, so it cannot carry one
    /// attempt's route — correct by construction, and now saying which construction, so it stops inflating
    /// any "rows with no model" figure. This was 319 of the corpus's 413 no-model rows: the single largest
    /// contributor to the 76% headline, and never a defect at all.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void TheTaskGrainSentinelRow_IsAttributedTaskGrain()
    {
        WriteTask("01-prompt", prompt: true);
        WriteJournal(("01-prompt", "gr577-real-model"));

        Ingest();

        JsonElement row = RowsOnDisk().Single(r => r.GetProperty("taskId").GetString() == "01-prompt"
            && r.GetProperty("attempt").GetInt32() == 0);

        Assert.Null(StringOrNull(row, "model"));
        Assert.Equal(ModelAttribution.TaskGrain, StringOrNull(row, "modelAttribution"));
    }

    /// <summary>
    /// An undecidable action kind is recorded as undecidable, NEVER guessed — SSOT §15.4's standing rule.
    /// A task folder with no <c>action.*</c> file at all cannot be told apart as script-versus-prompt;
    /// calling it a defect would invent one, and calling it a script would excuse a real one.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AnUndecidableActionKind_IsAttributedUnknown_NeverGuessed()
    {
        // A task folder with task.json but NO action.* file — SSOT §3 makes this a validation error, so
        // the kind is genuinely undecidable rather than defaulting either way.
        Directory.CreateDirectory(Path.Combine(PlanFolder, "tasks", "01-headless"));
        File.WriteAllText(
            Path.Combine(PlanFolder, "tasks", "01-headless", "task.json"), """{"description":"no action"}""");
        WriteJournal(("01-headless", null));

        Ingest();

        string? token = StringOrNull(AttemptRow("01-headless", 1), "modelAttribution");
        Assert.Equal(ModelAttribution.Unknown, token);
        Assert.NotEqual(ModelAttribution.NotRecorded, token);
        Assert.NotEqual(ModelAttribution.ScriptAction, token);
    }

    // --- shape --------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY row of both grains carries an attribution token from the closed vocabulary, and the schema
    /// version says the column exists. That version bump is what lets a reader tell a pre-repair row (no
    /// column at all) from a row whose attribution is genuinely absent — the era boundary becomes
    /// checkable on the row instead of against a date the reader has to already know.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void EveryRowCarriesAKnownAttributionToken_AndTheSchemaVersionSaysTheColumnExists()
    {
        WriteTask("01-prompt", prompt: true);
        WriteTask("02-script", prompt: false);
        WriteJournal(("01-prompt", "gr577-real-model"), ("02-script", null));

        Ingest();

        JsonElement[] rows = RowsOnDisk();
        Assert.Equal(4, rows.Length); // two task rows + two attempt rows

        foreach (JsonElement row in rows)
        {
            string? token = StringOrNull(row, "modelAttribution");
            Assert.NotNull(token);
            Assert.Contains(token, ModelAttribution.AllTokens);
            Assert.Equal(3, row.GetProperty("schemaVersion").GetInt32());
        }
    }

    // --- fixtures ------------------------------------------------------------------------------------

    private void Ingest() =>
        TelemetryIngest.IngestPlanFolder(PlanFolder, new TelemetryCorpusStore(CorpusRoot), "gr577-repo");

    /// <summary>
    /// A task folder carrying <c>task.json</c> plus exactly one <c>action.*</c> file — SSOT §3's shape, and
    /// the one fact <see cref="TaskActionKindReader"/> decides script-versus-prompt from.
    /// </summary>
    private void WriteTask(string taskId, bool prompt)
    {
        string folder = Path.Combine(PlanFolder, "tasks", taskId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), """{"description":"fixture"}""");
        File.WriteAllText(
            Path.Combine(folder, prompt ? "action.prompt.md" : "action.ps1"),
            prompt ? "do the thing" : "exit 0");
    }

    /// <summary>
    /// One journal with one attempt per named task. A null <c>provenanceModel</c> omits
    /// <see cref="AttemptRecord.Provenance"/> ENTIRELY — which is exactly what the journal does for a
    /// script attempt AND for a prompt attempt whose route was never recorded. That shared null is the
    /// ambiguity #577 is about, so the fixture must reproduce it rather than write two different shapes.
    /// </summary>
    private void WriteJournal(params (string TaskId, string? ProvenanceModel)[] tasks)
    {
        var started = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        var journal = new JournalDocument
        {
            RunId = "gr577-run",
            PlanHash = "sha256:" + new string('a', 64),
            NextMergeSequence = 1,
            Tasks = tasks.ToDictionary(
                t => t.TaskId,
                t => new TaskJournalEntry
                {
                    Status = JournalTaskStatus.Succeeded,
                    Attempts =
                    [
                        new AttemptRecord
                        {
                            Attempt = 1,
                            StartedAt = started,
                            EndedAt = started.AddMinutes(1),
                            ActionExitCode = 0,
                            Outcome = AttemptOutcome.Succeeded,
                            LogDir = $"logs/gr577-run/{t.TaskId}/attempt-1",
                            Provenance = t.ProvenanceModel is null
                                ? null
                                : new AttemptProvenance { Model = t.ProvenanceModel, Runner = "default", Kind = "claude" }
                        }
                    ]
                })
        };

        string journalPath = RunJournal.PathFor(PlanFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, JournalJson.Options));
    }

    /// <summary>
    /// Every corpus row as a RAW <see cref="JsonElement"/> — deliberately not a deserialized
    /// <see cref="TelemetryRow"/>. See the class doc: a typed read would survive the column never reaching
    /// the wire at all.
    /// </summary>
    private JsonElement[] RowsOnDisk() =>
        Directory.EnumerateFiles(CorpusRoot, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private JsonElement AttemptRow(string taskId, int attempt) =>
        RowsOnDisk().Single(r =>
            r.GetProperty("taskId").GetString() == taskId && r.GetProperty("attempt").GetInt32() == attempt);

    /// <summary>
    /// A property's string value, or null when the property is absent OR JSON <c>null</c>. Both readings
    /// matter: "the column is missing" and "the column is present and empty" are different claims, and a
    /// test that could not tell them apart would pass against a build that stopped writing the column.
    /// </summary>
    private static string? StringOrNull(JsonElement row, string property) =>
        row.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
}
