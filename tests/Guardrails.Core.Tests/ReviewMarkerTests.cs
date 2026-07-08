using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Review;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// The review marker (SSOT §13, issues #79/#260): a deterministic missing/stale/reviewed evaluation
/// over the committed <c>state/guardrails-review.json</c>. It keys on the broad
/// <see cref="PlanDefinitionHash"/> (§7.3) — the plan's whole behavioral definition, guardrail/preflight/
/// action bodies included — so a post-review edit to any of that content reads as un-reviewed. Warn,
/// never block: the harness only reads + classifies; the skill (via <c>mark-reviewed</c>) writes.
/// </summary>
public sealed class ReviewMarkerTests : IDisposable
{
    private readonly string _planDir;
    private readonly List<string> _realDirs = [];

    public ReviewMarkerTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
        // A real guardrails.json + one task.json so the plan hashes read stable content.
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(_planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_planDir, recursive: true); } catch (IOException) { }
        foreach (string dir in _realDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
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

        // Mutate the plan (change a task.json) so the recomputed hash differs from the marker's.
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
    public void Marker_KeysOnPlanDefinitionHash_NotTheNarrowPlanHash()
    {
        // The marker's recorded hash is the broad PlanDefinitionHash (§7.3) — the plan's behavioral
        // definition, guardrail/preflight/action bodies included — NOT the narrow journal PlanHash (§7).
        // The wire field is still named "planHash" for back-compat, but the VALUE is the broad hash.
        PlanDefinition plan = PlanHere();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        ReviewMarker? marker = ReviewMarker.Read(_planDir);
        Assert.NotNull(marker);
        Assert.Equal(PlanDefinitionHash.Compute(plan), marker!.PlanHash);
        Assert.NotEqual(PlanHash.Compute(plan), marker.PlanHash);
    }

    [Fact]
    public void PathFor_IsUnderState_Gitignored()
    {
        string path = ReviewMarker.PathFor(_planDir);
        Assert.Equal(Path.Combine(Path.GetFullPath(_planDir), "state", "guardrails-review.json"), path);
    }

    // ── #260: the marker self-invalidates on guardrail/preflight/action BODY edits ──────────────────
    //
    // These use a real on-disk plan folder with all four preflight/guardrail folders populated, because
    // PlanDefinitionHash enumerates those folders from DISK (catching .json sidecars), not from the
    // parsed model. Bodies are exactly what /guardrails-review scrutinizes; editing one must re-stale.

    [Fact]
    public void Evaluate_TaskGuardrailBodyEdited_IsStale()
    {
        // The issue's core repro: weaken a task guardrail body (turn a real check into `exit 0`).
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.Evaluate(plan).State);

        File.WriteAllText(TaskGuardrail(plan), "#!/usr/bin/env bash\nexit 0\n");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_GuardrailSidecarJsonEdited_IsStale()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(TaskGuardrailSidecar(plan), """{ "description": "loosened after review" }""");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_TaskPreflightBodyEdited_IsStale()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(TaskPreflight(plan), "#!/usr/bin/env bash\nexit 0\n");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_PlanGuardrailBodyEdited_IsStale()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(PlanGuardrail(plan), "#!/usr/bin/env bash\nexit 0\n");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_PlanPreflightBodyEdited_IsStale()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(PlanPreflight(plan), "#!/usr/bin/env bash\nexit 0\n");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_ActionBodyEdited_IsStale()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(ActionFile(plan), "#!/usr/bin/env bash\necho different\n");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_TaskJsonEdited_StillStale_Regression()
    {
        // Regression: task.json edits ALREADY re-staled under the narrow PlanHash; must still do so.
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(plan.Tasks[0].Directory, "task.json"),
            """{ "description": "edited" }""");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_GuardrailsJsonEdited_StillStale_Regression()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(plan.PlanDirectory, "guardrails.json"),
            """{ "version": 1, "maxParallelism": 2 }""");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_NewlineOnlyEdit_StaysReviewed()
    {
        // Rewriting a guardrail body CRLF-for-LF (byte-different, identical content) must NOT re-stale:
        // the hash is newline-normalized, so line-ending-only churn is invisible.
        PlanDefinition plan = NewRealPlan();
        string body = "#!/usr/bin/env bash\necho check\nexit 0\n";
        File.WriteAllText(TaskGuardrail(plan), body);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.Evaluate(plan).State);

        File.WriteAllText(TaskGuardrail(plan), body.Replace("\n", "\r\n"));

        Assert.Equal(ReviewState.Reviewed, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_NonNewlineByteChange_IsStale()
    {
        // The mirror of the normalization guard: a genuine (non-newline) byte change DOES re-stale.
        PlanDefinition plan = NewRealPlan();
        File.WriteAllText(TaskGuardrail(plan), "#!/usr/bin/env bash\necho check\nexit 0\n");
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        File.WriteAllText(TaskGuardrail(plan), "#!/usr/bin/env bash\necho CHECK\nexit 0\n");

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void Evaluate_NoEdit_StaysReviewed_Deterministic()
    {
        PlanDefinition plan = NewRealPlan();
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        // Recompute several times with no edit — always reviewed (the hash is a pure function of bytes).
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.Evaluate(plan).State);
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.Evaluate(plan).State);
    }

    [Fact]
    public void PlanDefinitionHash_TwoCheckoutsDifferingOnlyInLineEndings_HashEqual()
    {
        PlanDefinition lf = NewRealPlan(crlf: false);
        PlanDefinition crlf = NewRealPlan(crlf: true);

        Assert.Equal(PlanDefinitionHash.Compute(lf), PlanDefinitionHash.Compute(crlf));
    }

    // ── real on-disk plan helper (all four preflight/guardrail folders + a sidecar) ──────────────────

    private PlanDefinition NewRealPlan(bool crlf = false)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-realplan-" + Guid.NewGuid().ToString("N"));
        _realDirs.Add(planDir);
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        Directory.CreateDirectory(Path.Combine(taskDir, "preflights"));
        Directory.CreateDirectory(Path.Combine(planDir, "guardrails"));
        Directory.CreateDirectory(Path.Combine(planDir, "preflights"));

        Write(Path.Combine(planDir, "guardrails.json"), "{ \"version\": 1 }\n", crlf);
        Write(Path.Combine(taskDir, "task.json"), "{ \"description\": \"real task\" }\n", crlf);
        Write(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho action\n", crlf);
        Write(Path.Combine(taskDir, "guardrails", "01-check.sh"),
            "#!/usr/bin/env bash\ntest -f out.txt || exit 1\n", crlf);
        Write(Path.Combine(taskDir, "guardrails", "01-check.json"),
            "{ \"description\": \"out.txt exists\" }\n", crlf);
        Write(Path.Combine(taskDir, "preflights", "01-pre.sh"),
            "#!/usr/bin/env bash\ntest -f dep.txt || exit 1\n", crlf);
        Write(Path.Combine(planDir, "guardrails", "01-terminal.sh"),
            "#!/usr/bin/env bash\nmake build || exit 1\n", crlf);
        Write(Path.Combine(planDir, "preflights", "01-full.sh"),
            "#!/usr/bin/env bash\ngit status || exit 1\n", crlf);

        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Workspace = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks =
            [
                new TaskNode
                {
                    Id = "01-task",
                    Directory = taskDir,
                    Description = "real task",
                    Action = new ActionDefinition
                    {
                        Path = Path.Combine(taskDir, "action.sh"),
                        Kind = ActionKind.Script
                    },
                    Guardrails =
                    [
                        new GuardrailDefinition
                        {
                            Name = "01-check",
                            Path = Path.Combine(taskDir, "guardrails", "01-check.sh"),
                            Kind = ActionKind.Script
                        }
                    ]
                }
            ]
        };
    }

    private static void Write(string path, string lfContent, bool crlf) =>
        File.WriteAllText(path, crlf ? lfContent.Replace("\n", "\r\n") : lfContent);

    private static string TaskGuardrail(PlanDefinition plan) =>
        Path.Combine(plan.Tasks[0].Directory, "guardrails", "01-check.sh");

    private static string TaskGuardrailSidecar(PlanDefinition plan) =>
        Path.Combine(plan.Tasks[0].Directory, "guardrails", "01-check.json");

    private static string TaskPreflight(PlanDefinition plan) =>
        Path.Combine(plan.Tasks[0].Directory, "preflights", "01-pre.sh");

    private static string ActionFile(PlanDefinition plan) => plan.Tasks[0].Action.Path;

    private static string PlanGuardrail(PlanDefinition plan) =>
        Path.Combine(plan.PlanDirectory, "guardrails", "01-terminal.sh");

    private static string PlanPreflight(PlanDefinition plan) =>
        Path.Combine(plan.PlanDirectory, "preflights", "01-full.sh");
}
