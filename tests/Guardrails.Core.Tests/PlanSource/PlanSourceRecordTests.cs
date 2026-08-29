using System.Text;
using Guardrails.Core.Breakdown;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests.PlanSource;

/// <summary>
/// RED tests for <see cref="PlanSourceRecord"/> (issue #505, plan-of-record
/// <c>docs/plans/24-plan-source-provenance.md</c> §3): the byte-exact and LF-normalized hashes over the
/// source <c>plan.md</c>, the open <c>Stamps</c> map of <c>&lt;!-- charter: key=value --&gt;</c> comments,
/// and the declared-delegated-decisions count line. Also pins the two placement invariants that are the
/// whole reason the artifact lives under <c>state/</c>: writing <c>state/plan-source.json</c> must never
/// move <see cref="PlanHash"/> or <see cref="PlanDefinitionHash"/> — the latter keys the review marker, so
/// a regression there would silently de-attest an already-reviewed plan (GR2025).
///
/// <para>These tests drive the real <see cref="PlanSourceRecord.Capture"/> API against on-disk fixtures.
/// Against this task's <c>NotImplementedException</c> stubs every one of them FAILS (by throwing) — that
/// failure is intentional TDD red; task 02 implements the type so they pass.</para>
/// </summary>
[Trait("Category", "PlanSourceProvenance")]
public sealed class PlanSourceRecordTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-plan-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string WriteTempFile(string text) => WriteTempFileBytes(Encoding.UTF8.GetBytes(text));

    private string WriteTempFileBytes(byte[] bytes)
    {
        string path = Path.Combine(NewTempDir(), "plan.md");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void SourceSha256_IsComputedOverRawBytes_NotDecodedText()
    {
        byte[] utf8Bom = [0xEF, 0xBB, 0xBF];
        byte[] text = Encoding.UTF8.GetBytes("# Plan\n\nIdentical body.\n");

        string withoutBom = WriteTempFileBytes(text);
        string withBom = WriteTempFileBytes([.. utf8Bom, .. text]);

        PlanSourceRecord a = PlanSourceRecord.Capture(withoutBom);
        PlanSourceRecord b = PlanSourceRecord.Capture(withBom);

        Assert.NotEqual(a.SourceSha256, b.SourceSha256);
    }

    [Fact]
    public void SourceSha256Lf_IsStableAcrossCrlfAndLf()
    {
        string crlfPath = WriteTempFile("# Plan\r\nLine two\r\nLine three\r\n");
        string lfPath = WriteTempFile("# Plan\nLine two\nLine three\n");

        PlanSourceRecord crlf = PlanSourceRecord.Capture(crlfPath);
        PlanSourceRecord lf = PlanSourceRecord.Capture(lfPath);

        Assert.Equal(crlf.SourceSha256Lf, lf.SourceSha256Lf);
        Assert.NotEqual(crlf.SourceSha256, lf.SourceSha256);
    }

    [Fact]
    public void Stamps_CapturesEveryCharterCommentAsAnOpenMap()
    {
        string path = WriteTempFile("""
            # Plan

            <!-- charter: plan-sha256=abc123 -->
            <!-- charter: answers-sha256=def456 -->

            Body.
            """);

        PlanSourceRecord record = PlanSourceRecord.Capture(path);

        Assert.Equal("abc123", record.Stamps["plan-sha256"]);
        Assert.Equal("def456", record.Stamps["answers-sha256"]);
    }

    [Fact]
    public void Stamps_IsEmptyWhenThePlanCarriesNoCharterComment()
    {
        string path = WriteTempFile("# Plan\n\nNo charter comments here.\n");

        PlanSourceRecord record = PlanSourceRecord.Capture(path);

        Assert.NotNull(record.Stamps);
        Assert.Empty(record.Stamps);
    }

    [Fact]
    public void Stamps_FirstWinsOnADuplicateKey()
    {
        string path = WriteTempFile("""
            # Plan

            <!-- charter: plan-sha256=first-value -->
            <!-- charter: plan-sha256=second-value -->
            """);

        PlanSourceRecord record = PlanSourceRecord.Capture(path);

        Assert.Equal("first-value", record.Stamps["plan-sha256"]);
        Assert.NotEmpty(record.DuplicateStampKeys);
    }

    [Fact]
    public void DeclaredDelegatedDecisions_ReadsTheCountLine()
    {
        string path = WriteTempFile("""
            # Plan

            DECISIONS DELEGATED TO YOU: 2**

            Body.
            """);

        PlanSourceRecord record = PlanSourceRecord.Capture(path);

        Assert.Equal(2, record.DeclaredDelegatedDecisions);
    }

    [Fact]
    public void DeclaredDelegatedDecisions_IsZeroWhenNoCountLineIsPresent()
    {
        string path = WriteTempFile("# Plan\n\nNo count line in this plan.\n");

        PlanSourceRecord record = PlanSourceRecord.Capture(path);

        Assert.Equal(0, record.DeclaredDelegatedDecisions);
    }

    [Fact]
    public void SourceBytes_MatchesTheFileLength()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("# Plan\n\nSome content that is not empty.\n");
        string path = WriteTempFileBytes(bytes);

        PlanSourceRecord record = PlanSourceRecord.Capture(path);

        Assert.Equal(bytes.Length, record.SourceBytes);
    }

    // ── Placement invariants — the whole reason the artifact lives under state/ ──────────────────────

    private static PlanDefinition BuildPlan(string planDir)
    {
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t" }""");

        return Plan(Task("01-task") with { Directory = taskDir }) with { PlanDirectory = planDir };
    }

    /// <summary>
    /// Captures and writes a real <c>state/plan-source.json</c> via the type under test — not a
    /// hand-rolled JSON blob — so the placement-invariant tests below stay RED against the
    /// <c>NotImplementedException</c> stubs instead of trivially passing regardless of the
    /// implementation (the #375 hollow-test trap).
    /// </summary>
    private string CaptureAndWritePlanSource(string planDirectory)
    {
        string sourcePath = WriteTempFile("# Plan\n\nBody.\n");
        PlanSourceRecord.Capture(sourcePath).WriteTo(planDirectory);
        return Path.Combine(planDirectory, "state", "plan-source.json");
    }

    [Fact]
    public void PlanHash_IsUnchanged_WhenPlanSourceJsonIsPresent()
    {
        PlanDefinition plan = BuildPlan(NewTempDir());

        string before = PlanHash.Compute(plan);
        CaptureAndWritePlanSource(plan.PlanDirectory);

        Assert.Equal(before, PlanHash.Compute(plan));
    }

    [Fact]
    public void PlanDefinitionHash_IsUnchanged_WhenPlanSourceJsonIsPresent()
    {
        PlanDefinition plan = BuildPlan(NewTempDir());

        string before = PlanDefinitionHash.Compute(plan);
        CaptureAndWritePlanSource(plan.PlanDirectory);

        Assert.Equal(before, PlanDefinitionHash.Compute(plan));
    }
}
