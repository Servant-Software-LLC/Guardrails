using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The DoR §12.4 <c>judge</c> provenance object, pinned as a WIRE contract: the verifier route (§6.5)
/// that graded an attempt — <c>{ runner, kind, model, effort, tier, strength, bumped, advisory }</c> —
/// hung off <c>provenance</c> per D32, optional, and ABSENT rather than <c>null</c> when no judge
/// resolved.
///
/// <para><b>Everything is asserted through the SHIPPED serialization</b> —
/// <see cref="JournalJson.Options"/> exactly as <c>RunJournal</c> saves through it, written with
/// <see cref="AtomicFile"/> and read back with <see cref="JournalReader"/>. A private
/// <c>JsonSerializerOptions</c> bag would prove that System.Text.Json works, not that <c>run.json</c>
/// does — and <see cref="JournalJson"/> sets <c>DefaultIgnoreCondition = Never</c>, so "absent when
/// null" is a per-member decision that only the real options bag can demonstrate.</para>
///
/// <para><b>Absent, never null — and only TEXT can prove it.</b> Half this delta is a
/// backward-compatibility guarantee: an existing user's <c>run.json</c> must not grow a
/// <c>"judge": null</c> key on every script attempt of every run, for a feature they never opted into.
/// An absent key and a null key deserialize IDENTICALLY, so a structural assertion on the round-tripped
/// object passes just as happily on a journal writing null noise everywhere. The negative assertions
/// below are therefore made on the emitted JSON text, with a positive control on the same line so they
/// cannot pass vacuously off a provenance that was never emitted at all.</para>
///
/// <para><b>TDD red (#155).</b> <see cref="AttemptProvenance.Judge"/> is declared by this task as a
/// PLAIN <c>AttemptJudge?</c> — no <c>[JsonIgnore(WhenWritingNull)]</c>, unlike every member beside it.
/// So <see cref="Judge_AbsentFromJson_WhenNull"/> and
/// <see cref="ProvenanceWithoutJudge_SerializesUnchanged"/> MUST fail here: today the stub emits
/// <c>"judge": null</c> on every attempt that has a provenance at all, which is both the missing
/// behaviour and a regression in the shape of every provenance already being written. Adding that one
/// attribute is <c>04-implement-judge-provenance-schema</c>'s entire deliverable. The other four
/// behaviours pass on the declaration — they are the regression guard that keeps task 04 from buying
/// absent-when-null at the cost of the round-trip, the <c>false</c>-is-a-datum rule, or the ability to
/// read a journal written before this wave.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class JudgeProvenanceSchemaTests : IDisposable
{
    private readonly string _root;
    private int _files;

    public JudgeProvenanceSchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-jps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // --- 1. the judge round-trips, every member ------------------------------------------------

    [Fact]
    public void Judge_RoundTrips_WhenPresent()
    {
        // All eight §12.4 members, none of them at its default: a round-trip of `false`/null would go
        // green against a member the serializer silently dropped. `Bumped` is deliberately `true` here
        // for that reason — its `false` case is Bumped_RecordsFalse_NotAbsent_WhenNoBumpFired's job.
        var judge = new AttemptJudge
        {
            Runner = "frontier",
            Kind = "claude",
            Model = "claude-opus-5",
            Effort = "xhigh",
            Tier = "hard",
            Strength = 90,
            Bumped = true,
            Advisory = "judge 'local-32b' is weaker than its actor; bumped to 'frontier'"
        };

        AttemptProvenance back = OnlyAttempt(RoundTrip(DocumentWith(Attempt(ActorRoute with
        {
            Judge = judge
        })))).Provenance!;

        Assert.NotNull(back.Judge);
        Assert.Equal("frontier", back.Judge!.Runner);        // the judge's promptRunners block name
        Assert.Equal("claude", back.Judge!.Kind);            // that block's `kind` WIRE token, not the enum
        Assert.Equal("claude-opus-5", back.Judge!.Model);
        Assert.Equal("xhigh", back.Judge!.Effort);
        Assert.Equal("hard", back.Judge!.Tier);              // §6.5 rule 2: the ACTOR's rung, not a bumped one
        Assert.Equal(90, back.Judge!.Strength);              // the axis rule 3 bumps along
        Assert.True(back.Judge!.Bumped);
        Assert.Equal(judge.Advisory, back.Judge!.Advisory);

        // The judge is recorded BESIDE the actor's own route, not instead of it — §6.5 compares the two,
        // so a delta that overwrote the actor's members would destroy the only thing worth comparing.
        Assert.Equal("local", back.Runner);
        Assert.Equal("openai-compat", back.Kind);
        Assert.Equal("qwen3-32b", back.Model);
        Assert.Equal("hard", back.Tier);
        Assert.Equal(TierSource.PlanDefault, back.TierSource);
        Assert.Equal("medium", back.Effort);
        Assert.Equal(
            "guardrails/2026-08-17T05-10-23Z-d2e9/07-wire-judge/attempt-1", back.SegmentBranch);
        Assert.Equal(["Read", "Edit"], back.DeclaredToolGrants);
    }

    // --- 2. absent, never `"judge": null` ------------------------------------------------------

    [Fact]
    public void Judge_AbsentFromJson_WhenNull()
    {
        // RED on this task's stub, deliberately: `Judge` is declared without the WhenWritingNull ignore
        // condition its neighbours carry, and JournalJson sets DefaultIgnoreCondition = Never.
        //
        // Asserted on TEXT because it cannot be asserted any other way: `"judge": null` and no `judge`
        // key at all deserialize to the same null member, so the structural check below would certify a
        // journal that grew a null key on every attempt of every existing run.
        string withActorRouteOnly = Emit(DocumentWith(Attempt(ActorRoute)));

        // Positive control FIRST — otherwise the negative passes vacuously on a provenance that was
        // never emitted.
        Assert.Contains("\"provenance\"", withActorRouteOnly);
        Assert.Contains("\"model\": \"qwen3-32b\"", withActorRouteOnly);
        Assert.DoesNotContain("\"judge\"", withActorRouteOnly);

        // And the case that carries the real cost: a SCRIPT attempt, which resolves no judge and never
        // will. §12.4's "absent (never null noise) for script attempts / legacy journals" is the promise
        // that a user who opted into none of this sees none of it.
        string scriptAttempt = Emit(DocumentWith(Attempt()));
        Assert.DoesNotContain("\"judge\"", scriptAttempt);

        // The member still reads back null, of course — this is the half that is true either way, kept
        // so the test states the whole contract rather than only its unprovable half.
        Assert.Null(OnlyAttempt(ReadRaw(withActorRouteOnly)).Provenance!.Judge);
    }

    // --- 3. an older journal has no judge key at all -------------------------------------------

    [Fact]
    public void OlderJournal_WithoutJudge_StillReads()
    {
        // A run.json written before this wave: one prompt attempt whose `provenance` carries the
        // #198/#382 + §12.4 route keys but no `judge`, and one script attempt with no `provenance` at
        // all. Both must LOAD — the member is optional precisely so a resume never strands mid-run on a
        // journal the previous session wrote.
        JournalDocument older = ReadRaw(OlderJournalText);

        AttemptRecord prompt = Assert.Single(older.Tasks["01-prompt-task"].Attempts);
        AttemptProvenance provenance = prompt.Provenance!;
        Assert.Null(provenance.Judge);

        // ...and nothing ELSE was lost while reading a document missing the new key.
        Assert.Equal("qwen3-32b", provenance.Model);
        Assert.Equal("local", provenance.Runner);
        Assert.Equal("hard", provenance.Tier);
        Assert.Equal(TierSource.PlanDefault, provenance.TierSource);
        Assert.Equal(["Read", "Write"], provenance.DeclaredToolGrants);

        AttemptRecord script = Assert.Single(older.Tasks["02-script-task"].Attempts);
        Assert.Null(script.Provenance);
    }

    // --- 4. `bumped: false` is a measurement, not an absence ------------------------------------

    [Fact]
    public void Bumped_RecordsFalse_NotAbsent_WhenNoBumpFired()
    {
        // A judge that resolved WITHOUT the §6.5 rule 3 strength bump — the actor was already strong
        // enough, so nothing was lifted. That is a result, and it must be written as one.
        //
        // Why it cannot be optional: #230-lite reads this datum to answer "is a bumped judge worth what
        // it costs", which is a RATIO. An absent key is indistinguishable from "no judge resolved at
        // all", so omitting the zeroes silently shrinks the denominator and turns the answer into
        // "bumped judges are always worth it" — measured over bumped judges only.
        string json = Emit(DocumentWith(Attempt(ActorRoute with
        {
            Judge = new AttemptJudge { Runner = "frontier", Kind = "claude", Model = "claude-opus-5" }
        })));

        Assert.Contains("\"bumped\": false", json);

        AttemptJudge back = OnlyAttempt(ReadRaw(json)).Provenance!.Judge!;
        Assert.False(back.Bumped);

        // The `true` case shares the field, so a writer that hard-coded `false` is caught here too.
        string bumpFired = Emit(DocumentWith(Attempt(ActorRoute with
        {
            Judge = new AttemptJudge { Runner = "frontier", Bumped = true }
        })));
        Assert.Contains("\"bumped\": true", bumpFired);
        Assert.True(OnlyAttempt(ReadRaw(bumpFired)).Provenance!.Judge!.Bumped);
    }

    // --- 5. nothing else in the provenance moved -----------------------------------------------

    [Fact]
    public void ProvenanceWithoutJudge_SerializesUnchanged()
    {
        // The regression every schema task risks, and on this task's stub it is a REAL one: a member
        // that emits null changes the output of EVERY provenance already being written, not just the
        // ones a judge will eventually ride. That is why this is a separate behaviour from
        // Judge_AbsentFromJson_WhenNull — that one asks "is the new datum recorded correctly", this one
        // asks "did adding it disturb the record that was already there".
        string json = Emit(DocumentWith(Attempt(FullyPopulatedProvenance)));

        Assert.DoesNotContain("\"judge\"", json);

        // Key-by-key: exactly the members that existed before this wave, no more and no fewer. Compared
        // as a SET rather than a sequence — System.Text.Json's property ORDER is not a documented
        // guarantee, and a test that flakes on reflection ordering would burn task 04's retries for a
        // reason that has nothing to do with the judge.
        Assert.Equal(
            new[]
            {
                "baseCommit", "declaredToolGrants", "effort", "injectedToolGrants", "kind", "model",
                "runner", "segmentBranch", "tier", "tierSource", "worktreePath"
            },
            EmittedProvenanceKeys(json).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        // ...and their VALUES survived unchanged, so "no new key" was not bought by dropping an old one.
        AttemptProvenance back = OnlyAttempt(ReadRaw(json)).Provenance!;
        Assert.Equal(FullyPopulatedProvenance.Model, back.Model);
        Assert.Equal(FullyPopulatedProvenance.Runner, back.Runner);
        Assert.Equal(FullyPopulatedProvenance.Kind, back.Kind);
        Assert.Equal(FullyPopulatedProvenance.Tier, back.Tier);
        Assert.Equal(FullyPopulatedProvenance.TierSource, back.TierSource);
        Assert.Equal(FullyPopulatedProvenance.Effort, back.Effort);
        Assert.Equal(FullyPopulatedProvenance.SegmentBranch, back.SegmentBranch);
        Assert.Equal(FullyPopulatedProvenance.WorktreePath, back.WorktreePath);
        Assert.Equal(FullyPopulatedProvenance.BaseCommit, back.BaseCommit);
        Assert.Equal(["Read", "Edit"], back.DeclaredToolGrants);
        Assert.Equal(["Bash(git show:*)"], back.InjectedToolGrants);
    }

    // --- 6. the advisory rides the judge, independently of the bump -----------------------------

    [Fact]
    public void Advisory_RoundTrips_AndIsAbsentWhenNoFinding()
    {
        // §6.5's weak/equal-and-weak finding, recorded in provenance on EVERY attempt that resolved a
        // weak judge (the de-duplication ruling's "provenance always" half — the run summary aggregates
        // from here, which is what lets the LOG line stay quiet).
        const string finding =
            "judge 'local' (kind openai-compat) is not stronger than its actor; the only stronger " +
            "block 'frontier' is costly: true, so the judge stayed on the actor's route";

        // Independent of Bumped, and this is the direction that proves it: advisory recorded with NO
        // bump having fired. §6.5 rule 5 degrades rather than overspends — when the only stronger
        // candidate is `costly: true` the judge stays put and the advisory is the whole record of it.
        string degraded = Emit(DocumentWith(Attempt(ActorRoute with
        {
            Judge = new AttemptJudge
            {
                Runner = "local",
                Kind = "openai-compat",
                Model = "qwen3-32b",
                Tier = "hard",
                Strength = 20,
                Bumped = false,
                Advisory = finding
            }
        })));

        Assert.Contains("\"advisory\"", degraded);
        Assert.Contains("\"bumped\": false", degraded);

        AttemptJudge weak = OnlyAttempt(ReadRaw(degraded)).Provenance!.Judge!;
        Assert.Equal(finding, weak.Advisory);
        Assert.False(weak.Bumped);
        Assert.Equal(20, weak.Strength);

        // No finding => the key is ABSENT, on TEXT again: an advisory is a thing a human is meant to
        // read, and `"advisory": null` on every healthy attempt is exactly the noise that trains people
        // to stop reading them. Bumped is `true` here so the two are shown moving independently in the
        // other direction too.
        string healthy = Emit(DocumentWith(Attempt(ActorRoute with
        {
            Judge = new AttemptJudge
            {
                Runner = "frontier",
                Kind = "claude",
                Model = "claude-opus-5",
                Tier = "hard",
                Strength = 90,
                Bumped = true
            }
        })));

        Assert.Contains("\"bumped\": true", healthy);   // positive control: the judge object IS emitted
        Assert.DoesNotContain("\"advisory\"", healthy);
        Assert.Null(OnlyAttempt(ReadRaw(healthy)).Provenance!.Judge!.Advisory);
    }

    // --- extra: a judge written by a NEWER harness, read from raw text ---------------------------

    [Fact]
    public void JudgeWrittenByANewerHarness_ReadsBackFromRawJournalText()
    {
        // Driven from raw journal TEXT rather than this suite's own writer. A write→read round-trip
        // alone cannot tell a correct wire mapping from a self-consistent private one: a reader that
        // never learned the camelCase spellings would hide behind a writer that emitted whatever the
        // reader expected. Real journals are also hand-patched by humans clearing a halt.
        AttemptJudge judge = OnlyAttempt(ReadRaw(JudgeJournalText)).Provenance!.Judge!;

        Assert.Equal("frontier", judge.Runner);
        Assert.Equal("claude", judge.Kind);
        Assert.Equal("claude-opus-5", judge.Model);
        Assert.Equal("xhigh", judge.Effort);
        Assert.Equal("hard", judge.Tier);
        Assert.Equal(90, judge.Strength);
        Assert.True(judge.Bumped);
        Assert.Equal("actor 'local' is weak; judge bumped to 'frontier'", judge.Advisory);
    }

    // --- the shipped serialization seam ---------------------------------------------------------

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

    /// <summary>
    /// The property names the emitted <c>provenance</c> object actually carries — read off the JSON
    /// itself, since the question "which keys were written" cannot be answered by a deserialized object.
    /// </summary>
    private static IEnumerable<string> EmittedProvenanceKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("tasks")
            .GetProperty(TaskId)
            .GetProperty("attempts")[0]
            .GetProperty("provenance")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();
    }

    // --- fixtures -------------------------------------------------------------------------------

    private const string RunId = "2026-08-17T05-10-23Z-d2e9";
    private const string TaskId = "07-wire-judge";
    private static readonly DateTimeOffset Started = new(2026, 8, 17, 5, 10, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2026, 8, 17, 5, 12, 4, TimeSpan.Zero);

    /// <summary>
    /// A realistic ACTOR route for the case §6.5 exists to catch: a task running on a weak local block.
    /// Whatever judge rides it, these members must come back untouched.
    /// </summary>
    private static readonly AttemptProvenance ActorRoute = new()
    {
        Model = "qwen3-32b",
        Runner = "local",
        Kind = "openai-compat",
        Tier = "hard",
        TierSource = TierSource.PlanDefault,
        Effort = "medium",
        SegmentBranch = "guardrails/2026-08-17T05-10-23Z-d2e9/07-wire-judge/attempt-1",
        DeclaredToolGrants = ["Read", "Edit"]
    };

    /// <summary>
    /// Every member the provenance carried BEFORE this wave, all populated — the regression bar for
    /// <see cref="ProvenanceWithoutJudge_SerializesUnchanged"/>. Each one is set so that a key going
    /// missing is a failure rather than an untested null.
    /// </summary>
    private static readonly AttemptProvenance FullyPopulatedProvenance = new()
    {
        Model = "claude-opus-5",
        Runner = "frontier",
        Kind = "claude",
        Tier = "hard",
        TierSource = TierSource.Task,
        Effort = "xhigh",
        SegmentBranch = "guardrails/2026-08-17T05-10-23Z-d2e9/07-wire-judge/attempt-1",
        WorktreePath = "C:/tmp/gr-wt/07-wire-judge",
        BaseCommit = "4306518",
        DeclaredToolGrants = ["Read", "Edit"],
        InjectedToolGrants = ["Bash(git show:*)"]
    };

    private static AttemptRecord Attempt(AttemptProvenance? provenance = null) => new()
    {
        Attempt = 1,
        StartedAt = Started,
        EndedAt = Ended,
        ActionExitCode = 0,
        Outcome = AttemptOutcome.Succeeded,
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
            [TaskId] = new() { Status = JournalTaskStatus.Succeeded, MergeSequence = 1, Attempts = [attempt] }
        }
    };

    private static AttemptRecord OnlyAttempt(JournalDocument document) =>
        Assert.Single(document.Tasks[TaskId].Attempts);

    /// <summary>
    /// A <c>run.json</c> written by a harness that already carries the §12.4 judge object — the shape a
    /// resume (or a human patch) must be able to READ, independently of what this suite's writer emits.
    /// </summary>
    private const string JudgeJournalText =
        """
        {
          "version": 1,
          "runId": "2026-08-17T05-10-23Z-d2e9",
          "planHash": "sha256:4306518",
          "nextMergeSequence": 2,
          "tasks": {
            "07-wire-judge": {
              "status": "succeeded",
              "mergeSequence": 1,
              "attempts": [
                {
                  "attempt": 1,
                  "startedAt": "2026-08-17T05:10:23+00:00",
                  "endedAt": "2026-08-17T05:12:04+00:00",
                  "actionExitCode": 0,
                  "outcome": "succeeded",
                  "failedGuardrails": [],
                  "logDir": "logs/2026-08-17T05-10-23Z-d2e9/07-wire-judge/attempt-1",
                  "provenance": {
                    "model": "qwen3-32b",
                    "runner": "local",
                    "kind": "openai-compat",
                    "tier": "hard",
                    "tierSource": "plan-default",
                    "effort": "medium",
                    "judge": {
                      "runner": "frontier",
                      "kind": "claude",
                      "model": "claude-opus-5",
                      "effort": "xhigh",
                      "tier": "hard",
                      "strength": 90,
                      "bumped": true,
                      "advisory": "actor 'local' is weak; judge bumped to 'frontier'"
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    /// <summary>
    /// A pre-judge <c>run.json</c>: one prompt attempt whose <c>provenance</c> carries the #198/#382 and
    /// §12.4 ROUTE keys but no <c>judge</c> at all, and one script attempt with no <c>provenance</c>.
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
                    "model": "qwen3-32b",
                    "runner": "local",
                    "kind": "openai-compat",
                    "tier": "hard",
                    "tierSource": "plan-default",
                    "effort": "medium",
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
