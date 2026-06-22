using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Review;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// The local review marker (SSOT §13, issue #79): a deterministic missing/stale/reviewed evaluation
/// over <c>state/guardrails-review.json</c>, reusing the journal's <see cref="PlanHash"/> so an edited
/// plan reads as un-reviewed. Warn, never block: the harness only reads + classifies; the skill writes.
/// </summary>
public sealed class ReviewMarkerTests : IDisposable
{
    private readonly string _planDir;

    public ReviewMarkerTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
        // A real guardrails.json + one task.json so PlanHash.Compute reads stable content.
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(_planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_planDir, recursive: true); } catch (IOException) { }
    }

    private PlanDefinition PlanHere() =>
        Plan(Task("01-task") with { Directory = Path.Combine(_planDir, "tasks", "01-task") })
            with { PlanDirectory = _planDir };

    [Fact]
    public void Evaluate_NoMarker_IsMissing()
    {
        ReviewEvaluation result = ReviewMarker.Evaluate(PlanHere());

        Assert.Equal(ReviewState.Missing, result.State);
        Assert.True(result.ShouldWarn);
        Assert.Contains("/guardrails-review", result.NudgeMessage);
    }

    [Fact]
    public void Evaluate_FreshMarker_IsReviewed_AndQuiet()
    {
        ReviewMarker.Write(PlanHere(), DateTimeOffset.UtcNow);

        ReviewEvaluation result = ReviewMarker.Evaluate(PlanHere());

        Assert.Equal(ReviewState.Reviewed, result.State);
        Assert.False(result.ShouldWarn);
        Assert.Null(result.NudgeMessage);
    }

    [Fact]
    public void Evaluate_PlanChangedSinceReview_IsStale_NamingBothHashes()
    {
        ReviewMarker.Write(PlanHere(), DateTimeOffset.UtcNow);

        // Mutate the plan (change a task.json) so the recomputed PlanHash differs from the marker's.
        File.WriteAllText(Path.Combine(_planDir, "tasks", "01-task", "task.json"),
            """{ "description": "edited after review" }""");

        ReviewEvaluation result = ReviewMarker.Evaluate(PlanHere());

        Assert.Equal(ReviewState.Stale, result.State);
        Assert.True(result.ShouldWarn);
        Assert.Contains("changed since", result.NudgeMessage);
        Assert.NotEqual(result.ReviewedHash, result.CurrentHash);
    }

    [Fact]
    public void Read_CorruptMarker_IsTreatedAsMissing_NeverThrows()
    {
        string markerPath = ReviewMarker.PathFor(_planDir);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "{ not valid json ]");

        Assert.Null(ReviewMarker.Read(_planDir));
        Assert.Equal(ReviewState.Missing, ReviewMarker.Evaluate(PlanHere()).State);
    }

    [Fact]
    public void Marker_UsesTheSamePlanHash_AsTheJournal()
    {
        // The marker's planHash must equal PlanHash.Compute (the journal's resume hash) so "plan
        // changed since review" is detected the same way "plan changed since journal" is (§13/§7).
        PlanDefinition plan = PlanHere();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        ReviewMarker? marker = ReviewMarker.Read(_planDir);
        Assert.NotNull(marker);
        Assert.Equal(PlanHash.Compute(plan), marker!.PlanHash);
    }

    [Fact]
    public void PathFor_IsUnderState_Gitignored()
    {
        string path = ReviewMarker.PathFor(_planDir);
        Assert.Equal(Path.Combine(Path.GetFullPath(_planDir), "state", "guardrails-review.json"), path);
    }
}
