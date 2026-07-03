using System.Text.Json;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the wire contract for the two-scope preflights model (the F9 split, SSOT §7): the three new
/// outcome names — the per-task <c>task-preflight-failed</c> (an <see cref="AttemptOutcome"/> journaled
/// inside <c>tasks{}</c>) and the two whole-plan phase halts <c>plan-preflight-failed</c> /
/// <c>plan-guardrail-failed</c> (a <see cref="PlanPhaseStatus"/> on the top-level <c>planPreflights</c> /
/// <c>planGuardrails</c> sections OUTSIDE <c>tasks{}</c>) — plus the additive-and-backward-compatible
/// round-trip of the two new sections. A token rename or a non-additive shape change breaks resume of an
/// in-flight run, so the journal contract must be pinned deliberately.
/// </summary>
public sealed class JournalOutcomesRoundTripTests
{
    // --- Wire names: one assertion per outcome ------------------------------------------------

    [Trait("Category", "Preflights")]
    [Fact]
    public void TaskPreflightFailed_SerializesToItsWireName()
    {
        Assert.Equal("task-preflight-failed", JournalJson.OutcomeToken(AttemptOutcome.TaskPreflightFailed));

        string json = JsonSerializer.Serialize(AttemptOutcome.TaskPreflightFailed, JournalJson.Options);
        Assert.Equal("\"task-preflight-failed\"", json);
        Assert.Equal(
            AttemptOutcome.TaskPreflightFailed,
            JsonSerializer.Deserialize<AttemptOutcome>(json, JournalJson.Options));
    }

    [Trait("Category", "Preflights")]
    [Fact]
    public void PlanPreflightFailed_SerializesToItsWireName()
    {
        Assert.Equal("plan-preflight-failed", JournalJson.PlanPhaseToken(PlanPhaseStatus.PlanPreflightFailed));

        string json = JsonSerializer.Serialize(PlanPhaseStatus.PlanPreflightFailed, JournalJson.Options);
        Assert.Equal("\"plan-preflight-failed\"", json);
        Assert.Equal(
            PlanPhaseStatus.PlanPreflightFailed,
            JsonSerializer.Deserialize<PlanPhaseStatus>(json, JournalJson.Options));
    }

    [Trait("Category", "Preflights")]
    [Fact]
    public void PlanGuardrailFailed_SerializesToItsWireName()
    {
        Assert.Equal("plan-guardrail-failed", JournalJson.PlanPhaseToken(PlanPhaseStatus.PlanGuardrailFailed));

        string json = JsonSerializer.Serialize(PlanPhaseStatus.PlanGuardrailFailed, JournalJson.Options);
        Assert.Equal("\"plan-guardrail-failed\"", json);
        Assert.Equal(
            PlanPhaseStatus.PlanGuardrailFailed,
            JsonSerializer.Deserialize<PlanPhaseStatus>(json, JournalJson.Options));
    }

    // --- Journal round-trips ------------------------------------------------------------------

    [Trait("Category", "Preflights")]
    [Fact]
    public void JournalWithBothSections_RoundTripsByteLosslessly()
    {
        JournalDocument document = DocumentWithSections();

        string json = JsonSerializer.Serialize(document, JournalJson.Options);

        // The three new outcome names really landed on the wire (not just modelled in memory).
        Assert.Contains("\"task-preflight-failed\"", json);
        Assert.Contains("\"plan-preflight-failed\"", json);
        Assert.Contains("\"plan-guardrail-failed\"", json);

        JournalDocument roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        string reSerialized = JsonSerializer.Serialize(roundTripped, JournalJson.Options);

        Assert.Equal(json, reSerialized);

        // The sections survived deserialization with their values intact.
        Assert.NotNull(roundTripped.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.PlanPreflightFailed, roundTripped.PlanPreflights!.Status);
        Assert.Equal(2, roundTripped.PlanPreflights.Checks.Count);
        Assert.NotNull(roundTripped.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, roundTripped.PlanGuardrails!.Status);
        Assert.Single(roundTripped.PlanGuardrails.FailedChecks);
    }

    [Trait("Category", "Preflights")]
    [Fact]
    public void JournalWithoutSections_RoundTrips_SectionsAbsentNotNull()
    {
        JournalDocument document = DocumentWithoutSections();

        string json = JsonSerializer.Serialize(document, JournalJson.Options);

        // Older-shape backward compatibility: a plan without the feature OMITS the two sections —
        // they are ABSENT, not serialized as null noise (which an older reader would choke on / a diff
        // would flag). The existing tasks{} shape is untouched.
        Assert.DoesNotContain("planPreflights", json);
        Assert.DoesNotContain("planGuardrails", json);

        JournalDocument roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        Assert.Null(roundTripped.PlanPreflights);
        Assert.Null(roundTripped.PlanGuardrails);

        string reSerialized = JsonSerializer.Serialize(roundTripped, JournalJson.Options);
        Assert.Equal(json, reSerialized);
    }

    // --- Fixtures -----------------------------------------------------------------------------

    private static readonly DateTimeOffset Start = new(2026, 7, 2, 2, 3, 49, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 7, 2, 2, 4, 10, TimeSpan.Zero);
    private const string Hash = "sha256:abc123";

    /// <summary>
    /// A journal WITH both new top-level sections, and a per-task attempt carrying the new
    /// <c>task-preflight-failed</c> outcome inside <c>tasks{}</c> — exercising all three new names.
    /// </summary>
    private static JournalDocument DocumentWithSections() => new()
    {
        RunId = "2026-07-02T02-03-49Z-91d8",
        PlanHash = Hash,
        NextMergeSequence = 3,
        Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
        {
            ["01-implement-widget"] = new()
            {
                Status = JournalTaskStatus.NeedsHuman,
                Attempts =
                [
                    new AttemptRecord
                    {
                        Attempt = 1,
                        StartedAt = Start,
                        EndedAt = End,
                        ActionExitCode = null,
                        Outcome = AttemptOutcome.TaskPreflightFailed,
                        FailedGuardrails =
                        [
                            new FailedGuardrail { Name = "01-preflight-inputs", Reason = "required upstream artifact missing" }
                        ],
                        LogDir = "logs/2026-07-02T02-03-49Z-91d8/01-implement-widget/attempt-1"
                    }
                ]
            }
        },
        PlanPreflights = new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = Hash,
            EvaluatedAt = Start,
            Checks =
            [
                new PlanPreflightCheck { Name = "git-top-level", Passed = true },
                new PlanPreflightCheck { Name = "clean-worktree", Passed = false, Reason = "uncommitted changes present" }
            ]
        },
        PlanGuardrails = new PlanGuardrailsSection
        {
            Status = PlanPhaseStatus.PlanGuardrailFailed,
            PlanHash = Hash,
            FailedChecks =
            [
                new FailedGuardrail { Name = "whole-repo-build", Reason = "CS0111 duplicate member from a merge collision" }
            ]
        }
    };

    /// <summary>The older-shape journal: a plain succeeded task, no plan-phase sections.</summary>
    private static JournalDocument DocumentWithoutSections() => new()
    {
        RunId = "2026-07-02T02-03-49Z-91d8",
        PlanHash = Hash,
        NextMergeSequence = 2,
        Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
        {
            ["01-write-greeting"] = new()
            {
                Status = JournalTaskStatus.Succeeded,
                MergeSequence = 1,
                Attempts =
                [
                    new AttemptRecord
                    {
                        Attempt = 1,
                        StartedAt = Start,
                        EndedAt = End,
                        ActionExitCode = 0,
                        Outcome = AttemptOutcome.Succeeded,
                        LogDir = "logs/2026-07-02T02-03-49Z-91d8/01-write-greeting/attempt-1"
                    }
                ]
            }
        }
    };
}
