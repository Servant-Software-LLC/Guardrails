using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// <c>run.json</c>'s top-level <c>deliveryTarget</c> (SSOT §7, issue #726) — the branch THIS PLAN's
/// deliveries land on, recorded the first time any delivery LANDS and never overwritten.
///
/// <para><b>Why the field exists at all.</b> Every process re-pins its delivery target from <c>HEAD</c>
/// at run start (<c>GitWorktreeProvider.CreateIntegration</c>), and nothing recorded where an earlier
/// delivery actually went. So a waved plan that delivered wave 1 to <c>master</c> and was then resumed
/// from a <c>git switch -c spike</c> delivered the REST of itself to <c>spike</c>, split the plan's work
/// across two branches, and refused nothing — the branch-moved check compared against the new pin.</para>
///
/// <para><b>Scope.</b> These rows pin the write path's OWN contract — write-once, the refused literal
/// <c>HEAD</c>, and the absent key — directly on <see cref="RunJournal"/>. WHO calls it around a real
/// delivery belongs to the Scheduler and is proven by <c>DeliveryTargetTests</c> over a real git
/// repository, not here. In particular, "a run that delivers nothing records no target" must be asked of
/// a <c>run.json</c> a real run WROTE: <see cref="ADocumentThatNeverDelivered_OmitsTheKey"/> below
/// serializes a hand-built document, so it shows the container is honest and nothing about when the
/// harness writes.</para>
/// </summary>
public sealed class DeliveryTargetJournalTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "gr726-dtj-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ADocumentThatNeverDelivered_OmitsTheKey()
    {
        var document = new JournalDocument { RunId = "r1", PlanHash = "sha256:aaa" };

        string json = JsonSerializer.Serialize(document, JournalJson.Options);

        Assert.DoesNotContain("\"deliveryTarget\"", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!.DeliveryTarget);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RecordDeliveryTarget_PersistsTheBranchAndSurvivesAResume()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordDeliveryTarget("master");

        // Read back through a SECOND load — a resume is a new process reading the file, which is the only
        // reader that matters for this field.
        Assert.Equal("master", RunJournal.LoadOrCreate(plan).Document.DeliveryTarget);
        Assert.Equal("master", journal.Document.DeliveryTarget);
    }

    /// <summary>
    /// The write-once rule, which is the whole safety property: a later process must never be able to
    /// re-point the plan's delivery target at whatever branch IT happens to be standing on. Rejects a
    /// plain last-writer-wins assignment.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RecordDeliveryTarget_IsWriteOnce_ALaterBranchNeverReplacesTheFirst()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordDeliveryTarget("master");
        journal.RecordDeliveryTarget("spike");

        Assert.Equal("master", journal.Document.DeliveryTarget);
        Assert.Equal("master", RunJournal.LoadOrCreate(plan).Document.DeliveryTarget);
    }

    /// <summary>
    /// A detached checkout names no branch — <c>git rev-parse --abbrev-ref HEAD</c> prints the literal
    /// <c>HEAD</c>. Recording that would make every later refusal tell an operator to check out a branch
    /// that does not exist, so it is refused at the write path rather than at each reader.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RecordDeliveryTarget_NeverRecordsTheLiteralHead()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordDeliveryTarget("HEAD");
        Assert.Null(journal.Document.DeliveryTarget);

        journal.RecordDeliveryTarget("");
        Assert.Null(journal.Document.DeliveryTarget);

        // ... and a real branch recorded afterwards still lands: the refusal above rejected the value,
        // it did not close the field.
        journal.RecordDeliveryTarget("master");
        Assert.Equal("master", journal.Document.DeliveryTarget);
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
