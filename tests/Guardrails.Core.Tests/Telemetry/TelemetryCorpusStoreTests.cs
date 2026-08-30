using System.Text.Json;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The append-only local telemetry corpus store (charter §9, <c>model-evidence-and-graduation</c>,
/// #535). Six behaviours, each pinned to an exact method name the red-census guardrail binds to:
/// append-only JSONL, idempotency on <c>(runId, taskId, attempt)</c>, UTC month rotation,
/// <c>schemaVersion</c> on every row, the collection opt-out, and purge.
///
/// <para><b>TDD red.</b> Every test here calls <see cref="TelemetryCorpusStore.Append"/> or
/// <see cref="TelemetryCorpusStore.Purge"/>, both of which throw <see cref="NotImplementedException"/>
/// until <c>02-implement-corpus-store</c> fills them — so the whole file is red, and none of it can be
/// green by coincidence with a stub's default.</para>
///
/// <para>Every test points the store at its OWN fresh temp directory (never
/// <c>~/.guardrails/telemetry/</c> — resolving that default belongs to the CLI task) and deletes it
/// afterwards.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryCorpusStoreTests : IDisposable
{
    private readonly string corpusRoot =
        Path.Combine(Path.GetTempPath(), "guardrails-telemetry-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(corpusRoot))
        {
            Directory.Delete(corpusRoot, recursive: true);
        }
    }

    // --- 1. append-only JSONL --------------------------------------------------------------------

    /// <summary>
    /// Each append adds exactly ONE JSON-object line, never a rewritten array — the first line stays
    /// byte-identical after a second append lands.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Append_WritesOneJsonLinePerRow()
    {
        var store = new TelemetryCorpusStore(corpusRoot);

        store.Append(Row(taskId: "01-first"));
        string firstLineRightAfterFirstAppend = Assert.Single(AllLines(corpusRoot));

        store.Append(Row(taskId: "02-second"));
        string[] lines = AllLines(corpusRoot);

        Assert.Equal(2, lines.Length);
        Assert.Equal(firstLineRightAfterFirstAppend, lines[0]); // untouched by the second append

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal(JsonValueKind.Object, first.RootElement.ValueKind);
        Assert.Equal("01-first", first.RootElement.GetProperty("taskId").GetString());

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal(JsonValueKind.Object, second.RootElement.ValueKind);
        Assert.Equal("02-second", second.RootElement.GetProperty("taskId").GetString());
    }

    // --- 2. idempotency ----------------------------------------------------------------------------

    /// <summary>
    /// Appending the same <c>(runId, taskId, attempt)</c> triple twice leaves exactly ONE row — what
    /// makes re-ingesting a plan safe by construction. The second append carries a DIFFERENT cost so a
    /// naive "overwrite the row" implementation and a correct "reject the duplicate" implementation would
    /// disagree about more than just the count, even though only the count is asserted here.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Append_SameRunTaskAttemptTwice_WritesOnlyOneRow()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        TelemetryRow row = Row(runId: "run-idem", taskId: "05-retry", attempt: 2);

        store.Append(row);
        store.Append(row with { CostUsd = 9.99m });

        Assert.Single(AllLines(corpusRoot));
    }

    // --- 3. month rotation ---------------------------------------------------------------------------

    /// <summary>
    /// The row lands in a file named for its <see cref="TelemetryRow.StartedAt"/>'s UTC year and month —
    /// not whatever the <see cref="DateTimeOffset"/>'s own offset happens to carry. The fixture's local
    /// calendar date reads March while its UTC date reads February, so a wrong implementation that
    /// rotates on the offset-local date lands in the wrong file rather than merely a differently-named
    /// right one.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Append_WritesIntoAMonthRotatedFile()
    {
        var startedAt = new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(2, startedAt.UtcDateTime.Month); // sanity: the fixture really does straddle month/UTC

        var store = new TelemetryCorpusStore(corpusRoot);
        store.Append(Row(startedAt: startedAt));

        string expected = Path.Combine(corpusRoot, "telemetry-2026-02.jsonl");
        string found = Directory.Exists(corpusRoot)
            ? string.Join(", ", Directory.GetFiles(corpusRoot))
            : "(corpus root does not exist)";
        Assert.True(File.Exists(expected), $"expected '{expected}'; found: {found}");
    }

    // --- 4. schemaVersion ------------------------------------------------------------------------

    /// <summary>
    /// <c>schemaVersion</c> round-trips off disk — deserialized back into a <see cref="TelemetryRow"/>,
    /// not confirmed by pattern-matching the raw written text, so this stays true regardless of the
    /// writer's exact formatting choices.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Append_EveryRowCarriesSchemaVersion()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        store.Append(Row());

        string line = Assert.Single(AllLines(corpusRoot));
        TelemetryRow roundTripped = JsonSerializer.Deserialize<TelemetryRow>(line, TelemetryCorpusStore.JsonOptions)
            ?? throw new InvalidOperationException("row did not round-trip to a TelemetryRow");

        Assert.Equal(TelemetryRow.CurrentSchemaVersion, roundTripped.SchemaVersion);
    }

    // --- 5. opt-out ----------------------------------------------------------------------------------

    /// <summary>
    /// With collection disabled the store writes NOTHING — the corpus root has no files at all, not
    /// merely a directory missing the one row that would otherwise have landed.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Append_WhenCollectionDisabled_WritesNothing()
    {
        string? original = Environment.GetEnvironmentVariable(TelemetryCorpusStore.OptOutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TelemetryCorpusStore.OptOutEnvVar, "off");

            var store = new TelemetryCorpusStore(corpusRoot);
            store.Append(Row());

            AssertNoFilesUnder(corpusRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryCorpusStore.OptOutEnvVar, original);
        }
    }

    // --- 6. purge --------------------------------------------------------------------------------

    /// <summary>
    /// Purge removes every row under the corpus root — across more than one rotated month file, so a
    /// purge that only clears "this month's" file would still leave evidence behind — and is safe to call
    /// on an empty (even not-yet-created) corpus.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Purge_RemovesEveryRowUnderTheCorpusRoot()
    {
        var store = new TelemetryCorpusStore(corpusRoot);

        Exception? onEmptyCorpus = Record.Exception(() => store.Purge());
        Assert.Null(onEmptyCorpus);

        store.Append(Row(runId: "run-a", taskId: "01-a", attempt: 1, startedAt: DateTimeOffset.UtcNow));
        store.Append(Row(runId: "run-b", taskId: "02-b", attempt: 1, startedAt: DateTimeOffset.UtcNow.AddMonths(-2)));
        Assert.NotEmpty(AllLines(corpusRoot)); // sanity: rows across (at least) two month files landed

        store.Purge();

        AssertNoFilesUnder(corpusRoot);
    }

    // --- fixtures --------------------------------------------------------------------------------

    // --- 7. provider-token forward compatibility (#546) --------------------------------------------

    /// <summary>
    /// A row carrying a <c>kind</c> / <c>model</c> / <c>tier</c> / <c>tierSource</c> / <c>runner</c> this
    /// build has never heard of round-trips **verbatim** — the corpus records what the journal said and
    /// has no opinion about it (#546).
    ///
    /// <para><b>Why this test exists when the code is already correct.</b> `TelemetryRow` types all five
    /// as <c>string?</c> today, so this passes on arrival. It is a REGRESSION PIN, in the same class as a
    /// <c>tests-untouched</c> check: it exists to stop someone later "tidying" <c>Kind</c> into a
    /// <c>PromptRunnerKind</c>, which reads like an improvement and is the exact defect. Nothing else in
    /// the suite would notice — every other row in every other test uses recognized tokens.</para>
    ///
    /// <para><b>What it protects.</b> The corpus is an ARCHIVE. A <c>kind</c> typed as an enum rejects —
    /// or worse, silently drops — the first row from a provider registered after this code was written,
    /// and that is precisely the provider the corpus exists to evaluate: <c>openai-compat</c> arrives with
    /// #223, and local inference is the whole reason for the #533 arc. A corpus that quietly discarded the
    /// early local-inference rows is not detectably wrong later; it just has a gap where the interesting
    /// evidence should have been. <c>JournalTierSpend</c> set this precedent one level up — it reports a
    /// rung this build does not recognize rather than discarding it.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Row_UnrecognizedKind_RoundTripsVerbatim()
    {
        var store = new TelemetryCorpusStore(corpusRoot);

        // Deliberately not one of today's tokens: a kind, a runner block, a model id and a rung that no
        // enum in this build defines. A re-validating implementation drops or rejects every one of them.
        store.Append(Row() with
        {
            Kind = "openai-compat",
            Runner = "local-qwen",
            Model = "qwen3-coder:30b",
            Tier = "featherweight",
            TierSource = "some-future-site"
        });

        string line = Assert.Single(AllLines(corpusRoot));
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement row = doc.RootElement;

        Assert.Equal("openai-compat", row.GetProperty("kind").GetString());
        Assert.Equal("local-qwen", row.GetProperty("runner").GetString());
        Assert.Equal("qwen3-coder:30b", row.GetProperty("model").GetString());
        Assert.Equal("featherweight", row.GetProperty("tier").GetString());
        Assert.Equal("some-future-site", row.GetProperty("tierSource").GetString());

        // Each must be a JSON STRING, not a number an enum was coerced into - a serializer configured with
        // an enum converter would round-trip the VALUE while changing the TYPE, and a later reader parsing
        // the corpus as strings would then silently see nothing.
        foreach (string field in new[] { "kind", "runner", "model", "tier", "tierSource" })
        {
            Assert.Equal(JsonValueKind.String, row.GetProperty(field).ValueKind);
        }
    }

    private static TelemetryRow Row(
        string runId = "run-1",
        string taskId = "01-task",
        int attempt = 1,
        DateTimeOffset? startedAt = null,
        decimal? costUsd = null,
        long? inputTokens = null,
        long? outputTokens = null,
        string outcome = "succeeded",
        string repo = "guardrails")
    {
        DateTimeOffset started = startedAt ?? DateTimeOffset.UtcNow;
        return new TelemetryRow
        {
            SchemaVersion = TelemetryRow.CurrentSchemaVersion,
            RunId = runId,
            TaskId = taskId,
            Attempt = attempt,
            StartedAt = started,
            EndedAt = started.AddMinutes(5),
            Outcome = outcome,
            Model = "claude-sonnet-5",
            Runner = "default",
            Kind = "claude",
            Tier = "medium",
            TierSource = "task",
            Effort = null,
            CostUsd = costUsd,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Repo = repo
        };
    }

    // --- assertions ------------------------------------------------------------------------------

    private static string[] AllLines(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .SelectMany(File.ReadAllLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray()
            : Array.Empty<string>();

    private static void AssertNoFilesUnder(string root) => Assert.True(
        !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(),
        $"expected no files under '{root}'");
}
