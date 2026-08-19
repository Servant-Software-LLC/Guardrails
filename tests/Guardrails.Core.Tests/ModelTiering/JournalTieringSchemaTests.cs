using System.Reflection;
using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The DoR §12.4 journal delta for model tiering (#201), pinned as a WIRE contract: the new
/// <c>no-route</c> attempt outcome, the five route members on <c>provenance</c>
/// (<c>runner</c>/<c>kind</c>/<c>tier</c>/<c>tierSource</c>/<c>effort</c>), the <c>tierSource</c>
/// spellings <c>task</c>|<c>plan-default</c>|<c>override</c>, and the optional attempt
/// <c>usage { inputTokens, outputTokens }</c> block.
///
/// <para><b>Everything is asserted through the SHIPPED serialization</b> —
/// <see cref="JournalJson.Options"/> exactly as <c>RunJournal</c> saves through it, written with
/// <see cref="AtomicFile"/> and read back with <see cref="JournalReader"/>. A private
/// <c>JsonSerializerOptions</c> bag would prove that System.Text.Json works, not that <c>run.json</c>
/// does.</para>
///
/// <para><b>Absent, never null.</b> Half of this delta is a backward-compatibility guarantee: an
/// existing user's <c>run.json</c> must not grow five null keys per attempt. That half can only be
/// proven against the emitted TEXT — an absent key and a <c>null</c> key deserialize identically, so a
/// test that re-reads the object and asserts null passes on a journal writing
/// <c>"tier": null</c> everywhere.</para>
///
/// <para><b>TDD red (#155).</b> Three behaviours land on real stubs and MUST fail until
/// <c>02-implement-journal-tiering-schema</c> fills them: <see cref="JournalJson.OutcomeToken"/>'s
/// switch ends in <c>_ =&gt; throw new JsonException(...)</c> so <see cref="AttemptOutcome.NoRoute"/>
/// throws on serialize, the converter's reader throws on the <c>no-route</c> token, and no
/// <see cref="TierSource"/> token mapping exists at all (the enum serializes as its ORDINAL today).
/// The remaining behaviours pass on the declarations this task adds — they are the regression guard
/// that keeps task 02 from wiring the tokens at the cost of the absent-not-null half.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class JournalTieringSchemaTests : IDisposable
{
    private readonly string _root;
    private int _files;

    public JournalTieringSchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-jts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // --- 1. the no-route outcome, writer half -------------------------------------------------

    [Fact]
    public void NoRouteOutcome_SerializesToItsWireToken()
    {
        // DoR §12.4: resolution found zero candidate blocks at-or-above the rung. RED today —
        // OutcomeToken has no NoRoute arm and its `_ =>` throws.
        Assert.Equal("no-route", JournalJson.OutcomeToken(AttemptOutcome.NoRoute));

        // ... and the token really reaches the wire, not just the labelling helper.
        Assert.Contains(
            "\"outcome\": \"no-route\"",
            Emit(DocumentWith(Attempt(AttemptOutcome.NoRoute))));
    }

    // --- 2. the no-route outcome, reader half -------------------------------------------------

    [Fact]
    public void NoRouteToken_DeserializesBack_ThroughTheJournalsOwnReader()
    {
        // Driven from raw journal TEXT first: a run.json written by a newer harness (or hand-patched by
        // a human clearing a halt) must LOAD. Asserting only a write→read round-trip would let a reader
        // that never learned the token hide behind a writer that never emitted it.
        Assert.Equal(AttemptOutcome.NoRoute, OnlyAttempt(ReadRaw(NoRouteJournalText)).Outcome);

        // ... and the full shipped writer + reader pair agrees.
        Assert.Equal(
            AttemptOutcome.NoRoute,
            OnlyAttempt(RoundTrip(DocumentWith(Attempt(AttemptOutcome.NoRoute)))).Outcome);
    }

    // --- 3. the tierSource wire tokens --------------------------------------------------------

    [Theory]
    [InlineData(TierSource.Task, "task")]
    [InlineData(TierSource.PlanDefault, "plan-default")]
    [InlineData(TierSource.Override, "override")]
    public void TierSource_MapsToItsWireToken_AndBack(TierSource source, string token)
    {
        // The kebab spelling of `plan-default` is exactly why a plain Enum.ToString (or the default
        // System.Text.Json enum handling, which writes the ORDINAL) will not do. RED today: the emitted
        // text is a bare number.
        string json = Emit(DocumentWith(Attempt(provenance: new AttemptProvenance { TierSource = source })));
        Assert.Contains($"\"tierSource\": \"{token}\"", json);

        Assert.Equal(source, OnlyAttempt(ReadRaw(json)).Provenance!.TierSource);
    }

    [Fact]
    public void TierSourceToken_LivesOnJournalJson_BesideTheOutcomeMapping()
    {
        // §12.4's spellings are a wire contract, so they get ONE home: the class that already owns
        // OutcomeToken / PlanPhaseToken / RunHaltToken, so the converter and any prompt-context
        // labelling read the same source of truth instead of forking it.
        //
        // Asserted by REFLECTION deliberately. This task may not edit JournalJson.cs (task 02 owns it),
        // and a direct call to a method that does not exist would not COMPILE — and a suite that fails
        // to compile is not a red test, it is a broken one.
        MethodInfo? mapping = typeof(JournalJson).GetMethod(
            "TierSourceToken",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: TierSourceTokenSignature,
            modifiers: null);

        Assert.True(
            mapping is not null,
            "JournalJson declares no `public static string TierSourceToken(TierSource)`. DoR §12.4's " +
            "kebab spellings belong beside OutcomeToken, not inlined inside the JSON converter where " +
            "nothing else can reuse them.");
        Assert.Equal(typeof(string), mapping!.ReturnType);

        Assert.Equal("task", (string?)mapping.Invoke(null, [TierSource.Task]));
        Assert.Equal("plan-default", (string?)mapping.Invoke(null, [TierSource.PlanDefault]));
        Assert.Equal("override", (string?)mapping.Invoke(null, [TierSource.Override]));
    }

    // --- 4. the resolved route round-trips ----------------------------------------------------

    [Fact]
    public void ResolvedRouteProvenance_RoundTrips_WithEveryValuePreserved()
    {
        var provenance = new AttemptProvenance
        {
            Model = "claude-opus-5",
            Runner = "frontier",
            Kind = "claude",
            Tier = "hard",
            TierSource = TierSource.PlanDefault,
            Effort = "xhigh",
            SegmentBranch = "guardrails/2026-08-17T05-10-23Z-d2e9/07-wire-resolution/attempt-2",
            WorktreePath = "C:/tmp/gr-wt/07-wire-resolution",
            BaseCommit = "4306518",
            DeclaredToolGrants = ["Read", "Edit"],
            InjectedToolGrants = ["Bash(git show:*)"]
        };

        AttemptProvenance back =
            OnlyAttempt(RoundTrip(DocumentWith(Attempt(provenance: provenance)))).Provenance!;

        Assert.Equal("frontier", back.Runner);      // the promptRunners block name
        Assert.Equal("claude", back.Kind);          // that block's `kind` WIRE token, not the enum
        Assert.Equal("hard", back.Tier);            // the rung that actually resolved
        Assert.Equal(TierSource.PlanDefault, back.TierSource);
        Assert.Equal("xhigh", back.Effort);         // the resolved route's effort

        // `model` IS the resolved route's model (#198/#200 + §12.4) — there is deliberately no second
        // `resolvedModel` key, because two fields claiming one fact is how they drift.
        Assert.Equal("claude-opus-5", back.Model);

        // The #198/#382 members the delta must leave alone.
        Assert.Equal(provenance.SegmentBranch, back.SegmentBranch);
        Assert.Equal(provenance.WorktreePath, back.WorktreePath);
        Assert.Equal(provenance.BaseCommit, back.BaseCommit);
        Assert.Equal(["Read", "Edit"], back.DeclaredToolGrants);
        Assert.Equal(["Bash(git show:*)"], back.InjectedToolGrants);
    }

    // --- 5. absent, never null ----------------------------------------------------------------

    [Fact]
    public void ProvenanceWithoutTheRouteMembers_OmitsTheirKeysEntirely_RatherThanWritingNulls()
    {
        // The backward-compatibility half of §12.4, and the reason it is asserted on TEXT: an absent key
        // and a null key deserialize identically, so re-reading the object and asserting null would
        // certify a journal that grew five null keys per attempt on every existing user's run.
        string json = Emit(DocumentWith(Attempt(provenance: new AttemptProvenance
        {
            Model = "claude-sonnet-5",
            SegmentBranch = "guardrails/2026-08-17T05-10-23Z-d2e9/01-implement-widget/attempt-1"
        })));

        // The provenance object itself IS emitted — otherwise every negative below passes vacuously.
        Assert.Contains("\"provenance\"", json);
        Assert.Contains("\"model\": \"claude-sonnet-5\"", json);

        Assert.DoesNotContain("\"runner\"", json);
        Assert.DoesNotContain("\"kind\"", json);
        Assert.DoesNotContain("\"tier\"", json);       // note: `"tierSource"` does NOT contain `"tier"`
        Assert.DoesNotContain("\"tierSource\"", json);
        Assert.DoesNotContain("\"effort\"", json);
    }

    // --- 6. the optional usage block ----------------------------------------------------------

    [Fact]
    public void AttemptUsage_RoundTripsWhenPresent_AndIsAbsentFromTheTextWhenNull()
    {
        // #230-lite's tokens-only accounting surface: a costless provider reports 0 spend, so volume is
        // the only honest measure of what an attempt did.
        string withUsage = Emit(DocumentWith(
            Attempt(usage: new AttemptUsage { InputTokens = 41237, OutputTokens = 2918 })));
        Assert.Contains("\"usage\"", withUsage);

        AttemptUsage usage = OnlyAttempt(ReadRaw(withUsage)).Usage!;
        Assert.Equal(41237, usage.InputTokens);
        Assert.Equal(2918, usage.OutputTokens);

        string withoutUsage = Emit(DocumentWith(Attempt()));
        Assert.DoesNotContain("\"usage\"", withoutUsage);
        Assert.Null(OnlyAttempt(ReadRaw(withoutUsage)).Usage);
    }

    // --- 7. older journals still read ---------------------------------------------------------

    [Fact]
    public void OlderJournal_WithOnlyTodaysProvenanceKeys_OrNoProvenanceAtAll_StillReads()
    {
        // A pre-upgrade run.json — one attempt whose `provenance` carries only the #198/#382 keys, and
        // one script attempt with no `provenance` object at all. Both must load without error, with the
        // §12.4 members reading back as null ("this journal predates tiering"), never as a parse failure
        // that would strand a resume mid-run.
        JournalDocument older = ReadRaw(OlderJournalText);

        AttemptRecord prompt = Assert.Single(older.Tasks["01-prompt-task"].Attempts);
        AttemptProvenance legacyProvenance = prompt.Provenance!;
        Assert.Equal("claude-sonnet-5", legacyProvenance.Model);
        Assert.Equal(["Read", "Write"], legacyProvenance.DeclaredToolGrants);
        Assert.Null(legacyProvenance.Runner);
        Assert.Null(legacyProvenance.Kind);
        Assert.Null(legacyProvenance.Tier);
        Assert.Null(legacyProvenance.TierSource);
        Assert.Null(legacyProvenance.Effort);
        Assert.Null(prompt.Usage);

        AttemptRecord script = Assert.Single(older.Tasks["02-script-task"].Attempts);
        Assert.Null(script.Provenance);
        Assert.Null(script.Usage);
    }

    // --- the shipped serialization seam -------------------------------------------------------

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

    // --- fixtures -----------------------------------------------------------------------------

    private static readonly Type[] TierSourceTokenSignature = [typeof(TierSource)];

    private const string RunId = "2026-08-17T05-10-23Z-d2e9";
    private const string TaskId = "01-implement-widget";
    private static readonly DateTimeOffset Started = new(2026, 8, 17, 5, 10, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2026, 8, 17, 5, 12, 4, TimeSpan.Zero);

    private static AttemptRecord Attempt(
        AttemptOutcome outcome = AttemptOutcome.Succeeded,
        AttemptProvenance? provenance = null,
        AttemptUsage? usage = null) => new()
        {
            Attempt = 1,
            StartedAt = Started,
            EndedAt = Ended,
            ActionExitCode = 0,
            Outcome = outcome,
            LogDir = $"logs/{RunId}/{TaskId}/attempt-1",
            Provenance = provenance,
            Usage = usage
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
    /// A journal carrying the §12.4 <c>no-route</c> outcome and nothing else new — the provenance holds
    /// only today's keys, so this fixture fails on the OUTCOME token alone rather than conflating it
    /// with the <c>tierSource</c> mapping.
    /// </summary>
    private const string NoRouteJournalText =
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
                  "actionExitCode": null,
                  "outcome": "no-route",
                  "failedGuardrails": [],
                  "logDir": "logs/2026-08-17T05-10-23Z-d2e9/01-implement-widget/attempt-1",
                  "provenance": { "model": "(cli default)" }
                }
              ]
            }
          }
        }
        """;

    /// <summary>
    /// A pre-tiering <c>run.json</c>: one prompt attempt whose <c>provenance</c> carries only the
    /// #198/#382 keys, and one script attempt with no <c>provenance</c> at all.
    /// </summary>
    private const string OlderJournalText =
        """
        {
          "version": 1,
          "runId": "2026-06-30T09-14-02Z-7f31",
          "planHash": "sha256:older",
          "nextMergeSequence": 3,
          "tasks": {
            "01-prompt-task": {
              "status": "succeeded",
              "mergeSequence": 1,
              "attempts": [
                {
                  "attempt": 1,
                  "startedAt": "2026-06-30T09:14:02+00:00",
                  "endedAt": "2026-06-30T09:16:40+00:00",
                  "actionExitCode": 0,
                  "outcome": "succeeded",
                  "failedGuardrails": [],
                  "costUsd": 0.42,
                  "logDir": "logs/2026-06-30T09-14-02Z-7f31/01-prompt-task/attempt-1",
                  "provenance": {
                    "model": "claude-sonnet-5",
                    "segmentBranch": "guardrails/2026-06-30T09-14-02Z-7f31/01-prompt-task/attempt-1",
                    "worktreePath": "C:/tmp/gr-wt/01-prompt-task",
                    "baseCommit": "9f1c2ab",
                    "declaredToolGrants": ["Read", "Write"],
                    "injectedToolGrants": ["Bash(git show:*)"]
                  }
                }
              ]
            },
            "02-script-task": {
              "status": "succeeded",
              "mergeSequence": 2,
              "attempts": [
                {
                  "attempt": 1,
                  "startedAt": "2026-06-30T09:16:41+00:00",
                  "endedAt": "2026-06-30T09:16:52+00:00",
                  "actionExitCode": 0,
                  "outcome": "succeeded",
                  "logDir": "logs/2026-06-30T09-14-02Z-7f31/02-script-task/attempt-1"
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
