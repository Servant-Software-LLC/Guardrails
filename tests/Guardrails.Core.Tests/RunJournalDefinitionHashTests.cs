using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for the <c>TaskJournalEntry.DefinitionHash</c> wire field (SSOT §7.2, issue #274 Part A):
/// the success settle stamps it, <see cref="RunJournal.RecordedDefinitionHash"/> reads it back, it
/// round-trips through <c>run.json</c>, and it is OMITTED from the JSON when null (backward-compatible —
/// a pre-upgrade journal has no such key and parses fine, reading back as "unknown").
/// </summary>
public sealed class RunJournalDefinitionHashTests : IDisposable
{
    private readonly string _planDir;

    public RunJournalDefinitionHashTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-jdh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
    }

    private PlanDefinition BuildPlan()
    {
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(_planDir, "tasks", "01-task");
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
            PlanDirectory = _planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task],
            Workspace = _planDir
        };
    }

    private static AttemptRecord Attempt(int n, AttemptOutcome outcome) => new()
    {
        Attempt = n,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        Outcome = outcome,
        LogDir = $"logs/run/01-task/attempt-{n}"
    };

    [Fact]
    public void RecordAttempt_StampsDefinitionHash_AndReadsBack()
    {
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        journal.RecordAttempt(
            "01-task", Attempt(1, AttemptOutcome.Succeeded), JournalTaskStatus.Succeeded,
            mergeSequence: 1, definitionHash: "sha256:abc123");

        Assert.Equal("sha256:abc123", journal.RecordedDefinitionHash("01-task"));
    }

    [Fact]
    public void DefinitionHash_RoundTripsThroughDisk()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordAttempt(
            "01-task", Attempt(1, AttemptOutcome.Succeeded), JournalTaskStatus.Succeeded,
            mergeSequence: 1, definitionHash: "sha256:persisted");

        // Re-load from disk: the resume-normalized journal preserves the recorded hash for a succeeded task.
        RunJournal reloaded = RunJournal.LoadOrCreate(plan);
        Assert.Equal("sha256:persisted", reloaded.RecordedDefinitionHash("01-task"));
    }

    [Fact]
    public void DefinitionHash_OmittedFromJson_WhenNull()
    {
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        // A success settle WITHOUT a hash (the pre-upgrade shape) must not write a "definitionHash" key.
        journal.RecordAttempt(
            "01-task", Attempt(1, AttemptOutcome.Succeeded), JournalTaskStatus.Succeeded, mergeSequence: 1);

        string json = File.ReadAllText(RunJournal.PathFor(_planDir));
        Assert.DoesNotContain("definitionHash", json);
        Assert.Null(journal.RecordedDefinitionHash("01-task"));
    }

    [Fact]
    public void DefinitionHash_SerializedWhenPresent()
    {
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        journal.RecordAttempt(
            "01-task", Attempt(1, AttemptOutcome.Succeeded), JournalTaskStatus.Succeeded,
            mergeSequence: 1, definitionHash: "sha256:on-disk");

        string json = File.ReadAllText(RunJournal.PathFor(_planDir));
        using JsonDocument doc = JsonDocument.Parse(json);
        string? recorded = doc.RootElement
            .GetProperty("tasks").GetProperty("01-task")
            .GetProperty("definitionHash").GetString();
        Assert.Equal("sha256:on-disk", recorded);
    }

    [Fact]
    public void FailedAttempt_WithNullHash_PreservesPriorHash()
    {
        RunJournal journal = RunJournal.LoadOrCreate(BuildPlan());
        journal.RecordAttempt(
            "01-task", Attempt(1, AttemptOutcome.Succeeded), JournalTaskStatus.Succeeded,
            mergeSequence: 1, definitionHash: "sha256:kept");

        // A later attempt that passes no hash (null) must not clear the previously-recorded one.
        journal.RecordAttempt(
            "01-task", Attempt(2, AttemptOutcome.GuardrailFailed), JournalTaskStatus.Running);

        Assert.Equal("sha256:kept", journal.RecordedDefinitionHash("01-task"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_planDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }
}
