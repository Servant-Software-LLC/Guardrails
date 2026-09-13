using System.Text.Json;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 40 §4: the <c>run.json</c> <c>supplied[]</c> provenance record — the FIVE fields (<c>at</c>,
/// <c>commit</c>, <c>paths</c>, <c>bytes</c>, <c>by</c>) that separate a supplied file from the plan's own
/// output.
/// <para>
/// <b>TDD red.</b> <see cref="SuppliedRecord"/>'s members currently throw
/// <see cref="NotImplementedException"/> unconditionally, so every test below that constructs or reads one
/// is expected to FAIL against this tree. Do not add <c>Assert.Throws&lt;NotImplementedException&gt;</c>
/// wrappers — that would make these pass against the stub, which defeats the point of pinning them red.
/// </para>
/// <para>
/// <b>Exception: <see cref="Journal_WithNoSuppliedSection_RoundTripsUnchanged"/></b> never constructs a
/// <see cref="SuppliedRecord"/> — it exercises the never-weaker requirement that a run which supplied
/// nothing is unaffected by this feature's existence, which is already true of the additive
/// <see cref="JournalDocument.Supplied"/> stub property and stays true once <see cref="SuppliedRecord"/>
/// is implemented. It is deliberately GREEN on arrival.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SuppliedProvenanceTests
{
    [Fact]
    public void SuppliedRecord_CarriesAtCommitPathsBytesAndBy()
    {
        DateTimeOffset at = DateTimeOffset.Parse("2026-09-11T07:47:36Z");

        // "by" exercises the templated task:<folder> case (design 40 §4, added by review) — the case a
        // fixed enum cannot express, and the one that makes §3's overwatcher auto-resolve and the commit
        // trailer's derived `Supplied-By: <by>` implementable at all.
        var record = new SuppliedRecord
        {
            At = at,
            Commit = "2ada000111222333444555666777888999aaabb",
            Paths = new[] { "vendor/mermaid.min.js" },
            Bytes = 214_512,
            By = "task:05-author-tests-provenance"
        };

        Assert.Equal(at, record.At);
        Assert.Equal("2ada000111222333444555666777888999aaabb", record.Commit);
        Assert.Equal(new[] { "vendor/mermaid.min.js" }, record.Paths);
        Assert.Equal(214_512, record.Bytes);
        Assert.Equal("task:05-author-tests-provenance", record.By);
    }

    [Fact]
    public void SuppliedRecord_RoundTripsThroughTheJournalJson()
    {
        var original = new JournalDocument
        {
            RunId = "2026-09-11T07-47-36Z-2ada",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Supplied = new List<SuppliedRecord>
            {
                new SuppliedRecord
                {
                    At = DateTimeOffset.Parse("2026-09-11T07:47:36Z"),
                    Commit = "2ada000111222333444555666777888999aaabb",
                    Paths = new[] { "vendor/mermaid.min.js" },
                    Bytes = 214_512,
                    By = "operator"
                }
            }
        };

        // WRITE direction.
        string json = JsonSerializer.Serialize(original, JournalJson.Options);

        // READ direction — both must work, or the very next plan-phase journal write (a
        // read-modify-write) throws and kills the run at the terminal gate.
        JournalDocument? roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Supplied);
        SuppliedRecord supplied = Assert.Single(roundTripped.Supplied!);

        SuppliedRecord expected = original.Supplied![0];
        Assert.Equal(expected.At, supplied.At);
        Assert.Equal(expected.Commit, supplied.Commit);
        Assert.Equal(expected.Paths, supplied.Paths);
        Assert.Equal(expected.Bytes, supplied.Bytes);
        Assert.Equal(expected.By, supplied.By);
    }

    [Fact]
    public void Journal_WithNoSuppliedSection_RoundTripsUnchanged()
    {
        var original = new JournalDocument
        {
            RunId = "2026-09-11T07-47-36Z-2ada",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        string json = JsonSerializer.Serialize(original, JournalJson.Options);

        // The never-weaker requirement, asserted rather than assumed: a run that never supplied anything
        // gains no "supplied" key at all — never null noise.
        Assert.DoesNotContain("\"supplied\"", json, StringComparison.Ordinal);

        JournalDocument? roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.Supplied);
        Assert.Equal(original.RunId, roundTripped.RunId);
        Assert.Equal(original.PlanHash, roundTripped.PlanHash);
    }

    [Fact]
    public void Journal_AppendsASecondSupplyWithoutLosingTheFirst()
    {
        var first = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-11T07:47:36Z"),
            Commit = "2ada000111222333444555666777888999aaabb",
            Paths = new[] { "vendor/mermaid.min.js" },
            Bytes = 214_512,
            By = "operator"
        };

        var afterFirstSupply = new JournalDocument
        {
            RunId = "2026-09-11T07-47-36Z-2ada",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Supplied = new List<SuppliedRecord> { first }
        };

        var second = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-11T08:15:02Z"),
            Commit = "3bee111222333444555666777888999aaabbccd",
            Paths = new[] { "vendor/chart.min.js" },
            Bytes = 98_304,
            By = "task:05-author-tests-provenance"
        };

        // supplied[] is a LIST — a second `guardrails supply` call appends, it does not replace.
        var afterSecondSupply = afterFirstSupply with
        {
            Supplied = new List<SuppliedRecord>(afterFirstSupply.Supplied!) { second }
        };

        string json = JsonSerializer.Serialize(afterSecondSupply, JournalJson.Options);
        JournalDocument? roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options);

        Assert.NotNull(roundTripped?.Supplied);
        Assert.Equal(2, roundTripped!.Supplied!.Count);
        Assert.Contains(roundTripped.Supplied, r => r.Commit == first.Commit);
        Assert.Contains(roundTripped.Supplied, r => r.Commit == second.Commit);
    }
}
