using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// Design 39 §1c "How a refresh is recorded" / §5: the <c>run.json</c> <c>refreshed[]</c> provenance record
/// — the SIX fields (<c>at</c>, <c>commit</c>, <c>from</c>, <c>upstream</c>, <c>deliveredWave</c>,
/// <c>paths</c>) that name a post-delivery merge — and <see cref="UnauthoredContentNote"/>, the one reader
/// that answers "what is in this tree that no task authored?" across BOTH <c>refreshed[]</c> and plan 40's
/// <c>supplied[]</c>.
/// <para>
/// <b>TDD red.</b> <see cref="RefreshedRecord"/>'s getters, <see cref="UnauthoredContentNote"/>'s two
/// members, and <see cref="RunJournal.RecordRefreshed"/> currently throw <see cref="NotImplementedException"/>
/// unconditionally, so every test below that constructs/reads a <see cref="RefreshedRecord"/> or calls the
/// note or the write path is expected to FAIL against this tree. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red.
/// </para>
/// <para>
/// <b>Exception: <see cref="Journal_WithNoRefreshedSection_RoundTripsUnchanged"/></b> never constructs a
/// <see cref="RefreshedRecord"/> — it exercises the never-weaker requirement that a run which never
/// refreshed is unaffected by this feature's existence, which is already true of the additive
/// <see cref="JournalDocument.Refreshed"/> stub property and stays true once <see cref="RefreshedRecord"/>
/// is implemented. It is deliberately GREEN on arrival.
/// </para>
/// </summary>
public sealed class RefreshProvenanceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "gr39-rpt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RefreshedRecord_RoundTripsThroughTheJournalJson()
    {
        var original = new JournalDocument
        {
            RunId = "2026-09-14T10-00-00Z-abcd",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Refreshed = new List<RefreshedRecord>
            {
                new RefreshedRecord
                {
                    At = DateTimeOffset.Parse("2026-09-14T10:02:11Z"),
                    Commit = "7e1d000111222333444555666777888999aaabb",
                    From = "master",
                    Upstream = "4c9a000111222333444555666777888999aaabb",
                    DeliveredWave = "wave-02-issue-510",
                    Paths = new[] { "src/Teammate.cs" }
                }
            }
        };

        // WRITE direction.
        string json = JsonSerializer.Serialize(original, JournalJson.Options);

        Assert.Contains("\"refreshed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"from\"", json, StringComparison.Ordinal);
        Assert.Contains("\"upstream\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deliveredWave\"", json, StringComparison.Ordinal);

        // READ direction — both must work, or the very next plan-phase journal write (a
        // read-modify-write) throws and kills the run at the terminal gate.
        JournalDocument? roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Refreshed);
        RefreshedRecord refreshed = Assert.Single(roundTripped.Refreshed!);

        RefreshedRecord expected = original.Refreshed![0];
        Assert.Equal(expected.At, refreshed.At);
        Assert.Equal(expected.Commit, refreshed.Commit);
        Assert.Equal(expected.From, refreshed.From);
        Assert.Equal(expected.Upstream, refreshed.Upstream);
        Assert.Equal(expected.DeliveredWave, refreshed.DeliveredWave);
        Assert.Equal(expected.Paths, refreshed.Paths);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Journal_WithNoRefreshedSection_RoundTripsUnchanged()
    {
        var original = new JournalDocument
        {
            RunId = "2026-09-14T10-00-00Z-abcd",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        string json = JsonSerializer.Serialize(original, JournalJson.Options);

        // The never-weaker requirement, asserted rather than assumed: a run that never refreshed anything
        // gains no "refreshed" key at all — never null noise.
        Assert.DoesNotContain("\"refreshed\"", json, StringComparison.Ordinal);

        JournalDocument? roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.Refreshed);
        Assert.Equal(original.RunId, roundTripped.RunId);
        Assert.Equal(original.PlanHash, roundTripped.PlanHash);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RecordRefreshed_AppendsAndPersists()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var first = new RefreshedRecord
        {
            At = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero),
            Commit = "7e1d000111222333444555666777888999aaabb",
            From = "master",
            Upstream = "4c9a000111222333444555666777888999aaabb",
            DeliveredWave = "wave-01-scaffold",
            Paths = new[] { "src/Teammate.cs" }
        };
        journal.RecordRefreshed(first);

        var second = new RefreshedRecord
        {
            At = new DateTimeOffset(2026, 9, 14, 11, 0, 0, TimeSpan.Zero),
            Commit = "8f2e000111222333444555666777888999aaacc",
            From = "master",
            Upstream = "5daa000111222333444555666777888999aaadd",
            DeliveredWave = "wave-02-issue-510",
            Paths = new[] { "src/Other.cs" }
        };
        journal.RecordRefreshed(second);

        // Re-read from DISK — a fresh load, not the in-memory document — so the assertion proves the
        // write persisted, not merely that the in-process record was updated.
        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;

        Assert.NotNull(onDisk.Refreshed);
        Assert.Equal(2, onDisk.Refreshed!.Count);
        Assert.Equal(first.Commit, onDisk.Refreshed[0].Commit);
        Assert.Equal(second.Commit, onDisk.Refreshed[1].Commit);

        // A refresh is never a supply — recording one must not touch supplied[].
        Assert.Null(onDisk.Supplied);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Note_WithNoUnauthoredContent_AddsNothing()
    {
        var withoutSections = new JournalDocument
        {
            RunId = "2026-09-14T10-00-00Z-abcd",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        Assert.Null(UnauthoredContentNote.HeadlineSuffix(withoutSections));
        Assert.Empty(UnauthoredContentNote.DetailLines(withoutSections));

        var withEmptySections = withoutSections with
        {
            Supplied = new List<SuppliedRecord>(),
            Refreshed = new List<RefreshedRecord>()
        };

        Assert.Null(UnauthoredContentNote.HeadlineSuffix(withEmptySections));
        Assert.Empty(UnauthoredContentNote.DetailLines(withEmptySections));
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Note_NamesARefreshByBranchAndUpstream()
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-14T10-00-00Z-abcd",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Refreshed = new List<RefreshedRecord>
            {
                new RefreshedRecord
                {
                    At = DateTimeOffset.Parse("2026-09-14T10:02:11Z"),
                    Commit = "7e1d000111222333444555666777888999aaabb",
                    From = "master",
                    Upstream = "4c9a000111222333444555666777888999aaabb",
                    DeliveredWave = "wave-02-issue-510",
                    Paths = new[] { "src/Teammate.cs" }
                }
            }
        };

        string? suffix = UnauthoredContentNote.HeadlineSuffix(document);
        IReadOnlyList<string> details = UnauthoredContentNote.DetailLines(document);

        Assert.NotNull(suffix);
        // Names the BRANCH, not the merged sha — a refresh's caller-shaped analogue of "by".
        Assert.Contains("refresh from 'master'", suffix);
        // The first 10 characters of the UPSTREAM sha (what was merged), never the commit (the merge itself).
        Assert.Contains("4c9a000111", suffix);

        string detail = Assert.Single(details);
        Assert.Contains("4c9a000111222333444555666777888999aaabb", detail); // full upstream
        Assert.Contains("7e1d000111222333444555666777888999aaabb", detail); // full commit
        Assert.Contains("wave-02-issue-510", detail);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Note_NamesASupplyByWhoAndCommit()
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-14T10-00-00Z-abcd",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Supplied = new List<SuppliedRecord>
            {
                new SuppliedRecord
                {
                    At = DateTimeOffset.Parse("2026-09-14T09:00:00Z"),
                    Commit = "2ada000111222333444555666777888999aaabb",
                    Paths = new[] { "vendor/mermaid.min.js" },
                    Bytes = 214_512,
                    By = "operator"
                }
            }
        };

        // A reader that reads only refreshed[] returns null here — supplied[] is the only section present.
        string? suffix = UnauthoredContentNote.HeadlineSuffix(document);

        Assert.NotNull(suffix);
        Assert.Contains("supplied by operator", suffix);
        Assert.Contains("2ada000111", suffix); // first 10 characters of commit
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Note_ListsEveryRecordOldestFirst_AcrossBothSections()
    {
        var supplyT1 = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-14T08:00:00Z"),
            Commit = "1111111111222222222233333333334444444455",
            Paths = new[] { "vendor/a.js" },
            Bytes = 100,
            By = "operator"
        };
        var refreshT2 = new RefreshedRecord
        {
            At = DateTimeOffset.Parse("2026-09-14T09:00:00Z"),
            Commit = "2222222222333333333344444444445555555566",
            From = "master",
            Upstream = "3333333333444444444455555555556666666677",
            DeliveredWave = "wave-01-scaffold",
            Paths = new[] { "src/b.cs" }
        };
        var supplyT3 = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-14T10:00:00Z"),
            Commit = "4444444444555555555566666666667777777788",
            Paths = new[] { "vendor/c.js" },
            Bytes = 300,
            By = "task:05-author-tests-provenance"
        };

        var document = new JournalDocument
        {
            RunId = "2026-09-14T10-00-00Z-abcd",
            PlanHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            // Never adjacent-only, and never concatenated by section: the two supplies are on either
            // side of the one refresh, in TIME order, not section order.
            Supplied = new List<SuppliedRecord> { supplyT1, supplyT3 },
            Refreshed = new List<RefreshedRecord> { refreshT2 }
        };

        string? suffix = UnauthoredContentNote.HeadlineSuffix(document);
        IReadOnlyList<string> details = UnauthoredContentNote.DetailLines(document);

        Assert.NotNull(suffix);
        int indexT1 = suffix!.IndexOf(supplyT1.Commit[..10], StringComparison.Ordinal);
        int indexT2 = suffix.IndexOf(refreshT2.Upstream[..10], StringComparison.Ordinal);
        int indexT3 = suffix.IndexOf(supplyT3.Commit[..10], StringComparison.Ordinal);

        Assert.True(
            indexT1 >= 0 && indexT2 > indexT1 && indexT3 > indexT2,
            $"expected oldest-first order T1 < T2 < T3 in the suffix, got indices {indexT1}, {indexT2}, {indexT3}: {suffix}");

        // Never concatenated by section, never newest-first, never latest-only: all THREE records, oldest first.
        Assert.Equal(3, details.Count);
        Assert.Contains(supplyT1.Commit, details[0]);
        Assert.Contains(refreshT2.Commit, details[1]);
        Assert.Contains(supplyT3.Commit, details[2]);
    }

    private PlanDefinition BuildPlan()
    {
        string planDir = Path.Combine(_tempDir, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        var task = new TaskNode
        {
            Id = "01-task",
            Directory = taskDir,
            Description = "t",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "x", Kind = ActionKind.Script }]
        };

        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task],
            Workspace = planDir
        };
    }
}
