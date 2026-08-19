using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD (red) for the file-based <see cref="IEscalationSink"/> (issue #361 Phase 3, doc 12 §7.1/§7.2) — the
/// firstmate escalation seam. <see cref="FileEscalationSink.Escalate"/> is a throwing stub, so every test
/// here fails RED until the real sink lands. Each test pins one clause of the §7.1/§7.2 contract:
///
/// <list type="bullet">
///   <item><b>Records to disk</b> — a structured record at
///   <c>logs/&lt;runId&gt;/escalations/&lt;seq&gt;-&lt;gate&gt;.json</c> (the serialized
///   <see cref="EscalationRequest"/> + the assigned <see cref="EscalationId"/> + <c>status: "open"</c>,
///   carrying the <see cref="EscalationRequest.DefinitionHash"/>).</item>
///   <item><b>Appends a <c>decisions[]</c> 'escalated' entry AND emits
///   <see cref="IRunObserver.DecisionRecorded"/></b> — with the §6.2 fields (gate, criticality, threshold).</item>
///   <item><b><c>seq</c> is monotonic and journaled</b> — strictly increasing, survives a re-read (resume),
///   and is never reused even when the <c>escalations/</c> directory is wiped (proving it is NOT derived
///   from a directory listing).</item>
///   <item><b>Never blocks</b> — <see cref="FileEscalationSink.Escalate"/> returns the
///   <see cref="EscalationId"/> immediately without waiting for a reply (fire-and-record, §7.1).</item>
/// </list>
///
/// The <see cref="RunJournal"/> is built the same way as <see cref="RunJournalTests"/>; the temp
/// <c>logs/</c> tree lives under <see cref="_tempDir"/> (a per-test dir, outside the worktree).
/// </summary>
public sealed class EscalationSinkTests : IDisposable
{
    private const string Threshold = "high";
    private readonly string _tempDir;

    public EscalationSinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gr-escalation-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string LogsRoot => Path.Combine(_tempDir, "logs");

    // --- §7.2 (1): the escalation record on disk ---------------------------------------------------

    [Fact]
    public void Escalate_WritesRecordToDisk_WithIdStatusOpenAndDefinitionHash()
    {
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        var sink = new FileEscalationSink(LogsRoot, journal, IRunObserver.Null, Threshold);
        EscalationRequest request = Request("needs-human", "03-wire-config", "high");

        // RED: Escalate throws today. When implemented, it writes the record and returns the id.
        EscalationId id = sink.Escalate(request);

        // The record lives at exactly logs/<runId>/escalations/<seq>-<gate>.json (path proves the assigned seq).
        string path = Path.Combine(
            LogsRoot, journal.Document.RunId, "escalations", $"{id.Seq}-{id.Gate}.json");
        Assert.True(File.Exists(path), $"expected escalation record at {path}");

        string raw = File.ReadAllText(path);
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;

        // The serialized EscalationRequest fields...
        Assert.Equal("needs-human", root.GetProperty("gate").GetString());
        Assert.Equal("03-wire-config", root.GetProperty("subject").GetString());
        Assert.Equal(request.Question, root.GetProperty("question").GetString());
        Assert.Equal("high", root.GetProperty("criticality").GetString());
        // ...the anti-stale binding is carried verbatim (§7.1)...
        Assert.Equal(request.DefinitionHash, root.GetProperty("definitionHash").GetString());
        // ...the status starts "open" (§7.6 open → answered → consumed)...
        Assert.Equal("open", root.GetProperty("status").GetString());
        // ...and the assigned EscalationId (runId) is embedded in the record body, not just the path.
        Assert.Contains(id.RunId, raw);
    }

    // --- #485: the agent's kind claim on the escalation record -------------------------------------

    [Fact]
    public void Escalate_WithNeedsHumanKind_RecordsTheClaimVerbatim()
    {
        // The record is the #479 probe corpus: a `defective-guardrail` claim in
        // logs/<runId>/escalations/*.json makes "every guardrail that reached a live run and should not
        // have" a jq query over files that already exist — no new export command, no new artifact.
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        var sink = new FileEscalationSink(LogsRoot, journal, IRunObserver.Null, Threshold);

        EscalationId id = sink.Escalate(
            Request("needs-human", "08-implement", "moderate") with { Kind = NeedsHumanKinds.DefectiveGuardrail });

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            LogsRoot, journal.Document.RunId, "escalations", $"{id.Seq}-{id.Gate}.json")));
        Assert.Equal("defective-guardrail", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Escalate_WithoutNeedsHumanKind_OmitsTheFieldEntirely()
    {
        // Unclassified adds nothing to the record — a pre-#485-shaped escalation file is unchanged.
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        var sink = new FileEscalationSink(LogsRoot, journal, IRunObserver.Null, Threshold);

        EscalationId id = sink.Escalate(Request("needs-human", "08-implement", "moderate"));

        string raw = File.ReadAllText(Path.Combine(
            LogsRoot, journal.Document.RunId, "escalations", $"{id.Seq}-{id.Gate}.json"));
        Assert.DoesNotContain("\"kind\"", raw, StringComparison.Ordinal);
    }

    // --- §7.2 (2): decisions[] 'escalated' entry + IRunObserver.DecisionRecorded --------------------

    [Fact]
    public void Escalate_AppendsEscalatedDecision_AndEmitsDecisionRecorded_WithSixTwoFields()
    {
        var observer = new RecordingObserver();
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        var sink = new FileEscalationSink(LogsRoot, journal, observer, Threshold);

        // RED: Escalate throws today.
        sink.Escalate(Request("needs-human", "03-wire-config", "high"));

        // (2a) The live observer saw exactly one DecisionRecorded, an 'escalated' decision carrying the
        // §6.2 fields (gate, criticality, threshold).
        DecisionEntry live = Assert.Single(observer.DecisionCalls);
        Assert.Equal(DecisionTokens.Escalated, live.Decision);
        Assert.Equal("needs-human", live.Gate);
        Assert.Equal("high", live.Criticality);
        Assert.Equal(Threshold, live.Threshold);
        Assert.Equal("03-wire-config", live.Subject);

        // (2b) The SAME entry was appended to the durable decisions[] audit (via RunJournal.RecordDecision).
        DecisionEntry durable = Assert.Single(journal.Document.Decisions!);
        Assert.Equal(DecisionTokens.Escalated, durable.Decision);
        Assert.Equal("needs-human", durable.Gate);
    }

    // --- §7.1: seq is monotonic, journaled, never reused (not directory-derived) -------------------

    [Fact]
    public void Escalate_AllocatesMonotonicSeq_ThatIsJournaled_SurvivesReRead_AndIsNeverReused()
    {
        PlanDefinition plan = BuildPlan();

        RunJournal journal1 = RunJournal.LoadOrCreate(plan);
        var sink1 = new FileEscalationSink(LogsRoot, journal1, IRunObserver.Null, Threshold);

        // RED: Escalate throws today.
        EscalationId first = sink1.Escalate(Request("needs-human", "01-a", "high"));
        EscalationId second = sink1.Escalate(Request("needs-human", "02-b", "high"));

        // (1) strictly increasing within a run.
        Assert.True(second.Seq > first.Seq, "seq must be strictly increasing within a run");

        // Prove the counter is the PERSISTED run-level one, not derived from the escalations/ listing: wipe
        // the on-disk records, then re-read the journal (a resume) and escalate again.
        string escDir = Path.Combine(LogsRoot, journal1.Document.RunId, "escalations");
        if (Directory.Exists(escDir))
        {
            Directory.Delete(escDir, recursive: true);
        }

        RunJournal journal2 = RunJournal.LoadOrCreate(plan);   // resume re-reads run.json (same runId)
        var sink2 = new FileEscalationSink(LogsRoot, journal2, IRunObserver.Null, Threshold);

        EscalationId third = sink2.Escalate(Request("wave-checkpoint", "wave-01", "critical"));

        // (2) the counter survived the re-read AND ignores the now-empty directory: seq keeps climbing and
        // never reuses an earlier value.
        Assert.True(third.Seq > second.Seq, "seq must survive a re-read and never be reused");
        Assert.Equal(journal1.Document.RunId, journal2.Document.RunId);   // same run across the resume
    }

    // --- §7.1: fire-and-record — Escalate never blocks waiting for a reply -------------------------

    [Fact]
    public void Escalate_NeverBlocks_ReturnsAssignedIdImmediately_WithoutAwaitingAnAnswer()
    {
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        var sink = new FileEscalationSink(LogsRoot, journal, IRunObserver.Null, Threshold);
        EscalationRequest request = Request("needs-human", "01-task", "moderate");

        // RED: Escalate throws today. When implemented it records and returns SYNCHRONOUSLY — the return
        // type is EscalationId, not a Task/awaited reply, so there is nothing to block on (§7.1).
        EscalationId id = sink.Escalate(request);

        // The returned id binds the escalation's identity { runId, seq, gate, subject }.
        Assert.Equal(journal.Document.RunId, id.RunId);
        Assert.Equal("needs-human", id.Gate);
        Assert.Equal("01-task", id.Subject);
        Assert.True(id.Seq >= 1, "seq is a positive, assigned counter value");

        // Fire-and-record: Escalate returned even though NO answer file exists — it did not wait for one.
        string answerPath = Path.Combine(
            LogsRoot, journal.Document.RunId, "escalations", $"{id.Seq}-{id.Gate}.answer.json");
        Assert.False(File.Exists(answerPath), "Escalate must not wait for (or synthesize) an answer file");
    }

    // --- helpers ----------------------------------------------------------------------------------

    private static EscalationRequest Request(string gate, string subject, string? criticality) => new()
    {
        Gate = gate,
        Subject = subject,
        Question = $"[{gate}] which option should '{subject}' take?",
        Context = "full context: logs pointer + failure detail + the best-guess considered.",
        Criticality = criticality,
        DefinitionHash = "sha256:abc123def4567890",
        At = DateTimeOffset.Parse("2026-07-18T14:03:11Z")
    };

    /// <summary>Build a minimal one-task plan on disk under <see cref="_tempDir"/> (mirrors RunJournalTests).</summary>
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

    /// <summary>Captures every <see cref="IRunObserver.DecisionRecorded"/> so the escalation decision can be asserted.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<DecisionEntry> DecisionCalls { get; } = [];
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void DecisionRecorded(DecisionEntry entry) => DecisionCalls.Add(entry);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
