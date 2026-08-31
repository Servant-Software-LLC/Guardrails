using System.Globalization;
using System.Text.Json;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// The append-only local telemetry corpus (charter §9, <c>model-evidence-and-graduation</c>): one
/// JSONL file per UTC month, under a corpus root the CALLER supplies. This type never resolves the
/// default <c>~/.guardrails/telemetry/</c> location itself — that belongs to the CLI (task 10), so the
/// same store can be pointed at a throwaway directory in tests, at a bench's sandboxed root (charter §7),
/// or at the real home-scoped corpus in production without three different code paths.
/// </summary>
public sealed class TelemetryCorpusStore
{
    /// <summary>
    /// The opt-out switch (charter §9's collection-default decision): <c>GUARDRAILS_TELEMETRY=off</c>
    /// disables collection entirely (no files written, not even an empty corpus root); any other value,
    /// or unset, means collection stays ON. This is the SINGLE definition for the whole plan — the CLI
    /// verb (task 10) and run-end ingest (task 13) both honour this by calling into the store rather than
    /// re-reading the environment themselves, so a machine cannot end up opted out of one path and not
    /// the other.
    /// </summary>
    public const string OptOutEnvVar = "GUARDRAILS_TELEMETRY";

    /// <summary>
    /// The wire-format options every row is written and read with — camelCase to match the field names
    /// charter §9 already spells (<c>schemaVersion</c>, <c>taskId</c>, …), the same convention
    /// <c>Guardrails.Core.Journal.JournalJson</c> uses for <c>run.json</c>. Internal (not private) so the
    /// implementation, the tests, and later the ETL/report tasks share exactly one definition rather than
    /// each inventing their own and drifting.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The corpus root this store was constructed with, verbatim.</summary>
    public string CorpusRoot { get; }

    /// <summary>
    /// <paramref name="corpusRoot"/> is the ONLY place this store will ever read or write. It is never
    /// resolved, defaulted, or created here in the constructor — resolving the real
    /// <c>~/.guardrails/telemetry/</c> home path is the CLI task's job, not this store's.
    /// </summary>
    public TelemetryCorpusStore(string corpusRoot)
    {
        CorpusRoot = corpusRoot;
    }

    /// <summary>
    /// Appends <paramref name="row"/> as one JSON line to the file for its <see cref="TelemetryRow.StartedAt"/>'s
    /// UTC year and month, unless collection is disabled (<see cref="OptOutEnvVar"/> — writes nothing at
    /// all, no file created) or a row with the same <c>(runId, taskId, attempt)</c> triple is already on
    /// disk (idempotent no-op — re-ingesting a plan must be safe by construction). Never rewrites an
    /// existing line.
    /// </summary>
    public void Append(TelemetryRow row)
    {
        if (IsCollectionDisabled())
        {
            return;
        }

        if (AlreadyRecorded(row))
        {
            return;
        }

        Directory.CreateDirectory(CorpusRoot);

        string fileName = "telemetry-" + row.StartedAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";
        string path = Path.Combine(CorpusRoot, fileName);
        string line = JsonSerializer.Serialize(row, JsonOptions);

        AppendLineExclusively(path, line);
    }

    /// <summary>
    /// Append one COMPLETE line, with writers serialized against each other.
    /// <para>
    /// <b>Why not <c>File.AppendAllText</c>.</b> It opens shared-for-write and does not hold a lock, so two
    /// appenders can interleave inside a single line and leave a TORN record — a fragment ending mid-object
    /// followed by another row's tail. The corpus is JSONL, so one torn line makes the whole file
    /// unparseable to every reader: <see cref="AlreadyRecorded"/>, the report, and the ingest all
    /// deserialize line by line and throw <c>'}' is an invalid start of a value</c> on the fragment.
    /// </para>
    /// <para>
    /// This is not a hypothetical. Two concurrent <c>guardrails run</c> invocations on one machine append to
    /// the same month file, and the corpus they corrupt is the #533 model-evidence corpus — the data a
    /// graduation decision is supposed to rest on. It first surfaced as an intermittent Ubuntu CI failure
    /// where parallel tests shared a corpus root.
    /// </para>
    /// <para>
    /// <c>FileShare.Read</c> is the mechanism: a second writer is refused rather than admitted alongside, so
    /// lines cannot interleave. The refused writer retries briefly — an append holds the handle for
    /// microseconds — and telemetry is best-effort, so a row lost to a pathologically contended file is
    /// preferable to a corrupt corpus or a failed run. Collection must never be able to break a run.
    /// </para>
    /// </summary>
    private static void AppendLineExclusively(string path, string line)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine);

        for (int attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                // FileShare.None, not FileShare.Read: a reader admitted mid-append could observe the line
                // half-written, which is the same torn record one layer over. Readers use
                // ReadLinesTolerantly below and retry around this window, which is microseconds wide.
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.None);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException)
            {
                // Another appender or a reader holds it. Back off and retry.
                Thread.Sleep(5 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                return; // not writable at all - best-effort, never fail the run
            }
        }
    }

    /// <summary>
    /// Read every line of a corpus file, tolerating a concurrent appender.
    /// <para>
    /// <c>File.ReadLines</c> opens with <c>FileShare.Read</c>, which DENIES writers — so a reader could
    /// make a concurrent <see cref="Append"/> fail, and an appender holding the file could make the reader
    /// fail. Both directions were live: the first cut of this fix locked the writer and moved the
    /// <c>IOException</c> onto the read path instead. Sharing ReadWrite and retrying makes the pair
    /// cooperate rather than trading which side throws.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ReadLinesTolerantly(string path)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }

                return lines;
            }
            catch (IOException)
            {
                Thread.Sleep(5 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
        }

        return [];
    }

    /// <summary>Removes every row under the corpus root. Safe to call on an empty or not-yet-created corpus.</summary>
    public void Purge()
    {
        if (Directory.Exists(CorpusRoot))
        {
            Directory.Delete(CorpusRoot, recursive: true);
        }
    }

    /// <summary>
    /// <c>GUARDRAILS_TELEMETRY=off</c> (case-insensitive) disables collection; any other value, or unset,
    /// leaves it ON — the single opt-out definition <see cref="OptOutEnvVar"/> documents.
    /// </summary>
    private static bool IsCollectionDisabled() =>
        string.Equals(Environment.GetEnvironmentVariable(OptOutEnvVar), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scans every <c>*.jsonl</c> file already on disk under the corpus root for a row matching
    /// <paramref name="row"/>'s <c>(runId, taskId, attempt)</c> triple. Reading the rows back off disk —
    /// rather than tracking an in-memory set — is what makes the idempotency check survive a process
    /// restart, which is exactly the scenario a re-run of <c>telemetry ingest</c> needs.
    /// </summary>
    private bool AlreadyRecorded(TelemetryRow row)
    {
        if (!Directory.Exists(CorpusRoot))
        {
            return false;
        }

        foreach (string file in Directory.EnumerateFiles(CorpusRoot, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            foreach (string line in ReadLinesTolerantly(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Skip, never throw, on a line that will not parse. A corpus torn by the pre-fix
                // non-atomic append is still out there on real machines, and letting one bad line abort
                // the duplicate check would make an already-damaged corpus permanently un-ingestable -
                // turning a recoverable data problem into a dead end. A skipped line simply is not
                // matched, which at worst re-appends a row the corpus already held.
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using JsonDocument _ = document;
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("runId", out JsonElement runId) && runId.GetString() == row.RunId &&
                    root.TryGetProperty("taskId", out JsonElement taskId) && taskId.GetString() == row.TaskId &&
                    root.TryGetProperty("attempt", out JsonElement attempt) && attempt.GetInt32() == row.Attempt)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
