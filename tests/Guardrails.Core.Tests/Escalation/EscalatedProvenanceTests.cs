using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Escalation;

/// <summary>
/// The TDD-red tests for escalated provenance (DoR issue #228): the journal's answer to "why is this
/// attempt on a stronger rung?" — <see cref="TierSource.Escalated"/> plus
/// <see cref="AttemptProvenance.EscalatedFrom"/>. They compile against the two data members this task
/// adds to <c>JournalModel.cs</c> and fail against the STUB mapping sites — <see cref="TierProvenance"/>
/// has no <c>Escalated</c> arm yet, and both switches in <see cref="JournalJson"/> throw on an
/// unhandled <see cref="TierSource"/> — until <c>04-implement-escalated-provenance</c> fills them in.
///
/// <para><b>The distinction this suite exists to protect.</b> <see cref="TierResolution.Climbed"/> is a
/// CAPABILITY fact: <c>Candidates(RequestedTier)</c> was empty, so the resolver walked to a stronger
/// rung inside ONE attempt. Escalation is a different reason to be on a higher rung: a PREVIOUS attempt
/// of this task failed its guardrails. An attempt that climbed and an attempt that escalated can produce
/// the identical <c>(RequestedTier, Tier)</c> pair, so <see cref="SourceFor_OnACapabilityClimbThatDidNotEscalate_IsNotEscalated"/>
/// is the one test that proves <see cref="TierProvenance.SourceFor"/> keeps them apart.</para>
/// </summary>
[Trait("Category", "EscalationLadder")]
public sealed class EscalatedProvenanceTests : IDisposable
{
    private readonly string _root;
    private int _files;

    public EscalatedProvenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-esc-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ── SourceFor: an escalated route is sourced Escalated ──────────────────────────────────────

    [Fact]
    public void SourceFor_OnAnEscalatedRoute_IsEscalated()
    {
        ActionDefinition action = Action(TierOrigin.Task);
        TierResolution route = Route(
            requestedTier: ActionTiers.Easy,
            tier: ActionTiers.Medium,
            escalatedFrom: ActionTiers.Easy);

        Assert.Equal(TierSource.Escalated, TierProvenance.SourceFor(action, route));
    }

    // ── SourceFor: the discriminator — a capability climb is NOT an escalation, and its mirror ─────

    /// <summary>
    /// THE discriminator. The negative half — <see cref="TierResolution.Climbed"/> true with
    /// <see cref="TierResolution.EscalatedFrom"/> null maps to the ORIGIN-derived source, never to
    /// <see cref="TierSource.Escalated"/> — is true on today's stub and would be satisfied by
    /// <c>Assert.True(true)</c> on its own. The MIRROR in the same method is what makes this red: the
    /// same climbed route, ALSO escalated, must report <see cref="TierSource.Escalated"/> while still
    /// reporting <see cref="TierResolution.Climbed"/> — so the two facts stay independently readable on
    /// one route, which cannot pass until <c>TierProvenance.SourceFor</c> gains the <c>Escalated</c> arm.
    /// </summary>
    [Fact]
    public void SourceFor_OnACapabilityClimbThatDidNotEscalate_IsNotEscalated()
    {
        ActionDefinition action = Action(TierOrigin.Task);
        TierResolution climbedOnly = Route(
            requestedTier: ActionTiers.Medium,
            tier: ActionTiers.Hard,
            climbed: true);

        Assert.Equal(TierSource.Task, TierProvenance.SourceFor(action, climbedOnly));

        // Mirror: the identical climb, but this attempt ALSO escalated. Escalation must win the SOURCE
        // without erasing the capability fact sitting beside it.
        TierResolution climbedAndEscalated = climbedOnly with { EscalatedFrom = ActionTiers.Easy };

        Assert.Equal(TierSource.Escalated, TierProvenance.SourceFor(action, climbedAndEscalated));
        Assert.True(climbedAndEscalated.Climbed, "escalation must not erase the capability-climb fact");
    }

    // ── the wire token for Escalated ─────────────────────────────────────────────────────────────

    [Fact]
    public void TierSourceToken_ForEscalated_IsTheEscalatedWireToken()
    {
        // The labelling helper itself: RED today because `TierSourceToken`'s `_` arm throws on any
        // value it does not enumerate.
        Assert.Equal("escalated", JournalJson.TierSourceToken(TierSource.Escalated));

        // ... and the token really reaches the wire, not just the labelling helper.
        string json = Emit(DocumentWith(
            Attempt(provenance: new AttemptProvenance { TierSource = TierSource.Escalated })));
        Assert.Contains("\"tierSource\": \"escalated\"", json);
    }

    // ── the journal round-trips the escalated token ──────────────────────────────────────────────

    [Fact]
    public void TierSourceConverter_RoundTripsEscalatedThroughTheJournal()
    {
        // Driven from raw journal TEXT first, exactly as the shipped `no-route` coverage does: a reader
        // that never learned the token must not hide behind a writer that never emitted it either.
        Assert.Equal(
            TierSource.Escalated,
            OnlyAttempt(ReadRaw(EscalatedJournalText)).Provenance!.TierSource);

        // ... and the full shipped writer + reader pair agrees.
        var provenance = new AttemptProvenance { TierSource = TierSource.Escalated };
        AttemptProvenance back = OnlyAttempt(RoundTrip(DocumentWith(Attempt(provenance: provenance)))).Provenance!;
        Assert.Equal(TierSource.Escalated, back.TierSource);
    }

    // ── escalatedFrom is written only when the attempt escalated (the ONE declared exemption) ──────

    /// <summary>
    /// A pure DATA member whose declaration IS its implementation — there is no stub-vs-real distinction
    /// to be red about, so this stays GREEN on this tree. Asserted on TEXT, not on the re-read object:
    /// an absent key and a <c>null</c> key deserialize identically, so only the emitted string can prove
    /// <c>escalatedFrom</c> is omitted rather than written as <c>null</c> noise on every ordinary attempt.
    /// </summary>
    [Fact]
    public void Provenance_WritesEscalatedFromOnlyWhenTheAttemptEscalated()
    {
        string escalated = Emit(DocumentWith(
            Attempt(provenance: new AttemptProvenance { EscalatedFrom = ActionTiers.Easy })));
        Assert.Contains("\"escalatedFrom\": \"easy\"", escalated);

        string notEscalated = Emit(DocumentWith(
            Attempt(provenance: new AttemptProvenance { Model = "claude-sonnet-5" })));
        Assert.DoesNotContain("\"escalatedFrom\"", notEscalated);
    }

    // ── fixtures: SourceFor inputs ───────────────────────────────────────────────────────────────

    private static ActionDefinition Action(TierOrigin origin) => new()
    {
        Path = "action.prompt.md",
        Kind = ActionKind.Prompt,
        TierOrigin = origin
    };

    private static TierResolution Route(
        string? requestedTier = null,
        string? tier = null,
        bool climbed = false,
        bool pinned = false,
        bool legacy = false,
        string? escalatedFrom = null) => new()
        {
            RequestedTier = requestedTier,
            Tier = tier,
            Climbed = climbed,
            Pinned = pinned,
            Legacy = legacy,
            EscalatedFrom = escalatedFrom
        };

    // ── the shipped serialization seam, following JournalTieringSchemaTests ────────────────────────

    /// <summary>
    /// The SHIPPED write, verbatim: <c>RunJournal.Save</c> is
    /// <c>JsonSerializer.Serialize(_document, JournalJson.Options)</c> handed to
    /// <see cref="AtomicFile.WriteAllText"/>. No private options bag anywhere in this file.
    /// </summary>
    private static string Emit(JournalDocument document) =>
        JsonSerializer.Serialize(document, JournalJson.Options);

    private string WriteJournal(string json)
    {
        string path = Path.Combine(_root, $"run-{++_files}.json");
        AtomicFile.WriteAllText(path, json);
        return path;
    }

    /// <summary>Read journal TEXT back through the shipped <see cref="JournalReader"/>.</summary>
    private JournalDocument ReadRaw(string json) => JournalReader.Read(WriteJournal(json));

    /// <summary>Writer + reader, both shipped.</summary>
    private JournalDocument RoundTrip(JournalDocument document) => ReadRaw(Emit(document));

    private const string RunId = "2026-08-17T05-10-23Z-d2e9";
    private const string TaskId = "01-implement-widget";
    private static readonly DateTimeOffset Started = new(2026, 8, 17, 5, 10, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2026, 8, 17, 5, 12, 4, TimeSpan.Zero);

    private static AttemptRecord Attempt(
        AttemptOutcome outcome = AttemptOutcome.Succeeded,
        AttemptProvenance? provenance = null) => new()
        {
            Attempt = 1,
            StartedAt = Started,
            EndedAt = Ended,
            ActionExitCode = 0,
            Outcome = outcome,
            LogDir = $"logs/{RunId}/{TaskId}/attempt-1",
            Provenance = provenance
        };

    private static JournalDocument DocumentWith(AttemptRecord attempt) => new()
    {
        RunId = RunId,
        PlanHash = "sha256:4306518",
        NextMergeSequence = 2,
        Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
        {
            [TaskId] = new() { Status = JournalTaskStatus.NeedsHuman, Attempts = [attempt] }
        }
    };

    private static AttemptRecord OnlyAttempt(JournalDocument document) =>
        Assert.Single(document.Tasks[TaskId].Attempts);

    /// <summary>
    /// A journal carrying the escalated <c>tierSource</c> and its <c>escalatedFrom</c> partner, written
    /// as raw text so the READER half is proven independently of the shipped writer.
    /// </summary>
    private const string EscalatedJournalText =
        """
        {
          "version": 1,
          "runId": "2026-08-17T05-10-23Z-d2e9",
          "planHash": "sha256:4306518",
          "nextMergeSequence": 1,
          "tasks": {
            "01-implement-widget": {
              "status": "needs-human",
              "attempts": [
                {
                  "attempt": 1,
                  "startedAt": "2026-08-17T05:10:23+00:00",
                  "endedAt": "2026-08-17T05:10:24+00:00",
                  "actionExitCode": 0,
                  "outcome": "guardrail-failed",
                  "failedGuardrails": [],
                  "logDir": "logs/2026-08-17T05-10-23Z-d2e9/01-implement-widget/attempt-1",
                  "provenance": {
                    "model": "claude-sonnet-5",
                    "tierSource": "escalated",
                    "escalatedFrom": "easy"
                  }
                }
              ]
            }
          }
        }
        """;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
