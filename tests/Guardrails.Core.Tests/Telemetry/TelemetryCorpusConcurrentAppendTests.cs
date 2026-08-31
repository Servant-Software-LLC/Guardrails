using System.Collections.Concurrent;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// Concurrent appends must never tear a line.
///
/// <para><b>How this surfaced.</b> An intermittent Ubuntu-only CI failure —
/// <c>RunEndTelemetryIngestTests.Run_IngestsItsOwnJournal_WithoutAManualVerb</c> dying with
/// <c>'}' is an invalid start of a value. Path: $ | LineNumber: 0</c>. A line beginning with <c>}</c> is
/// the tail of one row appended into the middle of another: <c>File.AppendAllText</c> opens
/// shared-for-write and holds no lock, so two appenders interleave.</para>
///
/// <para><b>Why it matters beyond the test suite.</b> The corpus is JSONL, so ONE torn line makes the
/// whole file unparseable to every reader — the duplicate check, the report, and the ingest all
/// deserialize line by line. And nothing about this is test-only: two concurrent <c>guardrails run</c>
/// invocations on one machine append to the same month file, and the corpus they would corrupt is the
/// #533 model-evidence corpus. A graduation decision is supposed to rest on that data.</para>
///
/// <para><b>What this asserts, and what it deliberately does not.</b> It asserts every line is COMPLETE
/// and parseable, and that no row is silently mangled. It does NOT assert that all N rows arrive:
/// telemetry is best-effort and a row dropped under pathological contention is an acceptable trade — a
/// corrupt corpus is not. Pinning an exact count would be pinning the retry budget, which is tuning.</para>
/// </summary>
public sealed class TelemetryCorpusConcurrentAppendTests : IDisposable
{
    private readonly string _root;

    public TelemetryCorpusConcurrentAppendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-corpus-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ParallelAppends_LeaveEveryLineParseable()
    {
        const int writers = 8;
        const int rowsPerWriter = 25;

        // A long payload matters: a short row can slip inside one buffered write and hide the tear. This is
        // wide enough that an interleave lands mid-line, which is what the CI failure actually looked like.
        string padding = new('x', 400);
        var errors = new ConcurrentBag<string>();

        Parallel.For(0, writers, w =>
        {
            try
            {
                var store = new TelemetryCorpusStore(_root);
                for (int i = 0; i < rowsPerWriter; i++)
                {
                    store.Append(new TelemetryRow
                    {
                        SchemaVersion = 1,
                        RunId = $"run-{w}",
                        TaskId = $"task-{i}-{padding}",
                        Attempt = i + 1,
                        StartedAt = new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.Zero),
                        EndedAt = new DateTimeOffset(2026, 8, 31, 4, 1, 0, TimeSpan.Zero),
                        Outcome = "succeeded",
                        Repo = "race"
                    });
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        });

        Assert.True(errors.IsEmpty, "Append threw under contention: " + string.Join(" | ", errors));

        string[] files = Directory.GetFiles(_root, "*.jsonl");
        Assert.NotEmpty(files);

        int parsed = 0;
        foreach (string file in files)
        {
            foreach (string raw in File.ReadAllLines(file))
            {
                if (raw.Length == 0) { continue; }

                // The assertion. Against the old File.AppendAllText this throws on the torn line, which is
                // exactly the production failure: every reader of this corpus does the same deserialize.
                System.Text.Json.JsonDocument doc;
                try
                {
                    doc = System.Text.Json.JsonDocument.Parse(raw);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Assert.Fail(
                        $"TORN LINE in {Path.GetFileName(file)}: {ex.Message}\n" +
                        $"  line started: {raw[..Math.Min(80, raw.Length)]}");
                    return;
                }

                using (doc)
                {
                    // A line can be well-formed JSON and still be garbage if a tear happened to split on a
                    // brace boundary, so check the row is actually shaped like a row.
                    Assert.True(doc.RootElement.TryGetProperty("runId", out _), $"line has no runId: {raw[..Math.Min(80, raw.Length)]}");
                }

                parsed++;
            }
        }

        // Something must actually have been written - a green run over an empty corpus proves nothing.
        Assert.True(parsed > 0, "no rows were written at all");
    }
}
