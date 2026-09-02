using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for the nested-control-key detector (issue #586, SSOT §6.2/§9): a
/// <c>needsHarnessWrite</c> / <c>needsHuman</c> key written ONE LEVEL under a top-level fragment key
/// instead of beside it. Nested, the harness never sees it — nothing is written and nothing raises
/// anything about the escape hatch — so the task's guardrails fail on the content of a file the agent
/// was never given the chance to touch.
/// <para>
/// The DEFECT cases below are the bug; the CONTROL cases are the two things this check must never
/// break — a correctly-shaped request, and a task publishing ordinary state. The FALSE-POSITIVE
/// GUARDS are the reason the predicate matches on payload SHAPE rather than key name: a false
/// rejection of legitimate state would be worse than the bug, because it blocks a task on every
/// attempt forever, where the bug costs attempts.
/// </para>
/// </summary>
public sealed class NestedControlKeyTests
{
    private const string TaskId = "11-record-gr2060-in-knowledge-skill";

    // ── DEFECT: the measured shapes ──────────────────────────────────────────────────────────────

    [Fact]
    public void Detects_NeedsHarnessWrite_NestedUnderTaskFolderKey_EditsForm()
    {
        // The exact fragment measured on plan 33.
        NestedControlKeySignal? signal = NestedControlKey.DetectInJson(
            $$"""
              { "{{TaskId}}": { "needsHarnessWrite": {
                  "path": ".claude/skills/guardrails-domain-knowledge/SKILL.md",
                  "reason": "the runtime refuses .claude/ writes",
                  "edits": [ { "old": "GR2059", "new": "GR2060" } ] } } }
              """);

        Assert.NotNull(signal);
        Assert.Equal("needsHarnessWrite", signal.ControlKey);
        Assert.Equal(TaskId, signal.ContainingKey);
    }

    [Fact]
    public void Detects_NeedsHarnessWrite_NestedUnderTaskFolderKey_ContentForm()
    {
        NestedControlKeySignal? signal = NestedControlKey.DetectInJson(
            $$"""
              { "{{TaskId}}": { "needsHarnessWrite": {
                  "path": ".claude/skills/foo/SKILL.md", "content": "hello" } } }
              """);

        Assert.NotNull(signal);
        Assert.Equal("needsHarnessWrite", signal.ControlKey);
    }

    [Fact]
    public void Detects_NeedsHarnessWrite_NestedArrayForm()
    {
        // The #445 batch form is just as invisible one level down as the single-object form.
        NestedControlKeySignal? signal = NestedControlKey.DetectInJson(
            $$"""
              { "{{TaskId}}": { "needsHarnessWrite": [
                  { "path": "a.md", "content": "A" },
                  { "path": "b.md", "edits": [ { "old": "x", "new": "y" } ] } ] } }
              """);

        Assert.NotNull(signal);
        Assert.Equal("needsHarnessWrite", signal.ControlKey);
        Assert.Equal(TaskId, signal.ContainingKey);
    }

    [Fact]
    public void Detects_NeedsHuman_NestedUnderTaskFolderKey()
    {
        // #586's other half: a missed needsHuman degrades to an ordinary attempt failure rather than a
        // silent no-op, which is presumably why it survived undetected — but it is the same trap.
        NestedControlKeySignal? signal = NestedControlKey.DetectInJson(
            $$"""
              { "{{TaskId}}": { "needsHuman": {
                  "question": "Which schema version should this target?", "kind": "blocked-work" } } }
              """);

        Assert.NotNull(signal);
        Assert.Equal("needsHuman", signal.ControlKey);
        Assert.Equal(TaskId, signal.ContainingKey);
    }

    [Fact]
    public void Detects_NestedUnderAForeignTopLevelKey_NamingThatKey()
    {
        // Nesting under a key the task does not own is rejected as foreign eventually, but only at the
        // fragment-merge site — AFTER the guardrails. Naming the control-key mistake here is strictly
        // more actionable, and the reported container is whatever key it was actually found under.
        NestedControlKeySignal? signal = NestedControlKey.DetectInJson(
            """
            { "some-other-task": { "needsHarnessWrite": { "path": "a.md", "content": "A" } } }
            """);

        Assert.NotNull(signal);
        Assert.Equal("some-other-task", signal.ContainingKey);
    }

    [Fact]
    public void Detects_EvenWhenACorrectTopLevelControlKeyIsAlsoPresent()
    {
        // A fragment carrying BOTH a correct request and a nested one is a confused agent, not a
        // half-correct one: rejecting is right, and nothing is written for either.
        NestedControlKeySignal? signal = NestedControlKey.DetectInJson(
            $$"""
              { "needsHarnessWrite": { "path": "a.md", "content": "A" },
                "{{TaskId}}": { "needsHarnessWrite": { "path": "b.md", "content": "B" } } }
              """);

        Assert.NotNull(signal);
        Assert.Equal(TaskId, signal.ContainingKey);
    }

    // ── CONTROL: the correct shape, and ordinary state, are untouched ────────────────────────────

    [Fact]
    public void Ignores_CorrectTopLevelNeedsHarnessWrite()
    {
        Assert.Null(NestedControlKey.DetectInJson(
            """
            { "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md",
                "edits": [ { "old": "x", "new": "y" } ] } }
            """));
    }

    [Fact]
    public void Ignores_CorrectTopLevelNeedsHarnessWrite_AlongsideTheTaskFolderKey()
    {
        // The shape the corrected wording teaches: both keys at the root, side by side.
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""
              { "{{TaskId}}": { "recordedCode": "GR2060" },
                "needsHarnessWrite": { "path": "a.md", "content": "A" } }
              """));
    }

    [Fact]
    public void Ignores_CorrectTopLevelNeedsHuman_BothForms()
    {
        Assert.Null(NestedControlKey.DetectInJson(
            """{ "needsHuman": "Which schema version should this target?" }"""));
        Assert.Null(NestedControlKey.DetectInJson(
            """{ "needsHuman": { "question": "Which one?", "kind": "blocked-work" } }"""));
    }

    [Fact]
    public void Ignores_OrdinaryPublishedState()
    {
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "greetingPath": "out/greeting.txt", "count": 3 } }"""));
    }

    [Fact]
    public void Ignores_EmptyObject_AndNonObjectRoot()
    {
        Assert.Null(NestedControlKey.DetectInJson("{}"));
        Assert.Null(NestedControlKey.DetectInJson("[1, 2, 3]"));
        Assert.Null(NestedControlKey.DetectInJson("\"just a string\""));
    }

    // ── FALSE-POSITIVE GUARDS: shape, not name ──────────────────────────────────────────────────

    [Fact]
    public void Ignores_NestedNeedsHumanWrittenAsABareString()
    {
        // Deliberately NOT matched. A bare string carries no structure distinguishing an escalation
        // from a state value, so matching it would rest on the key name alone — and a task publishing a
        // string under its own `needsHuman` key would then be unable to complete, ever. The structured
        // form is what the harness-contract header actually instructs, so little real is given up.
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHuman": "yes" } }"""));
    }

    [Fact]
    public void Ignores_NestedControlKeyNamesCarryingNonRequestValues()
    {
        Assert.Null(NestedControlKey.DetectInJson($$"""{ "{{TaskId}}": { "needsHuman": true } }"""));
        Assert.Null(NestedControlKey.DetectInJson($$"""{ "{{TaskId}}": { "needsHarnessWrite": false } }"""));
        Assert.Null(NestedControlKey.DetectInJson($$"""{ "{{TaskId}}": { "needsHarnessWrite": 3 } }"""));
        Assert.Null(NestedControlKey.DetectInJson($$"""{ "{{TaskId}}": { "needsHarnessWrite": null } }"""));
    }

    [Fact]
    public void Ignores_NestedObjectsMissingTheControlKeysOwnRequiredMembers()
    {
        // path but no payload, payload but no path, and an object with neither — none of these is the
        // request it would have been at the root, so none is claimed to be one.
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHarnessWrite": { "path": ".claude/x.md" } } }"""));
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHarnessWrite": { "content": "no path" } } }"""));
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHarnessWrite": { "note": "unrelated state" } } }"""));
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHuman": { "answer": "42" } } }"""));
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHuman": { "question": "" } } }"""));
    }

    [Fact]
    public void Ignores_ControlKeysTwoOrMoreLevelsDown()
    {
        // Exactly one level is the measured mistake. Deeper is genuinely reachable by legitimate state —
        // a task recording WHAT it asked the harness to write — so the check stops at one.
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""
              { "{{TaskId}}": { "history": {
                  "needsHarnessWrite": { "path": "a.md", "content": "A" } } } }
              """));
    }

    [Fact]
    public void Ignores_AnEmptyNestedArray()
    {
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHarnessWrite": [] } }"""));
        Assert.Null(NestedControlKey.DetectInJson(
            $$"""{ "{{TaskId}}": { "needsHarnessWrite": ["not an entry"] } }"""));
    }

    // ── file-level behaviour ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectIn_MissingFile_IsNull()
    {
        Assert.Null(NestedControlKey.DetectIn(
            Path.Combine(Path.GetTempPath(), "gr-nck-absent-" + Guid.NewGuid().ToString("N") + ".json")));
    }

    [Fact]
    public void DetectIn_UnparseableJson_IsNull_LeavingTheFragmentPathToReportIt()
    {
        string path = Path.Combine(Path.GetTempPath(), "gr-nck-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Null(NestedControlKey.DetectIn(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectIn_ReadsTheNestedShapeFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), "gr-nck-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            $$"""{ "{{TaskId}}": { "needsHarnessWrite": { "path": "a.md", "content": "A" } } }""");
        try
        {
            NestedControlKeySignal? signal = NestedControlKey.DetectIn(path);
            Assert.NotNull(signal);
            Assert.Equal("needsHarnessWrite", signal.ControlKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── the message a retrying agent actually reads ──────────────────────────────────────────────

    [Fact]
    public void Feedback_NamesTheSpecificError_AndShowsBothKeysAtTheRoot()
    {
        TaskNode task = MinimalTask(TaskId);
        string feedback = RetryPolicy.ForNestedControlKey(
            task, attempt: 1, new NestedControlKeySignal("needsHarnessWrite", TaskId));

        // The specific error, not a restatement of the general folder-name rule (which is what produced
        // the mistake in the first place).
        Assert.Contains($"`needsHarnessWrite` was nested under the task-folder key '{TaskId}'", feedback);
        Assert.Contains("top-level SIBLINGS of your folder-name key", feedback);
        Assert.Contains("NOTHING was written", feedback);
        // …and the correct shape, with BOTH keys present so it is unambiguous at the point of use.
        Assert.Contains($"\"{TaskId}\"", feedback);
        Assert.Contains("\"needsHarnessWrite\": { \"path\"", feedback);
        // The exemption stated plainly, so the retry does not simply re-derive the same reading.
        Assert.Contains("are exempt from it", feedback);
    }

    [Fact]
    public void Feedback_ForNeedsHuman_ShowsTheNeedsHumanShape()
    {
        string feedback = RetryPolicy.ForNestedControlKey(
            MinimalTask(TaskId), attempt: 2, new NestedControlKeySignal("needsHuman", TaskId));

        Assert.Contains($"`needsHuman` was nested under the task-folder key '{TaskId}'", feedback);
        Assert.Contains("\"needsHuman\": { \"question\"", feedback);
        Assert.DoesNotContain("\"edits\"", feedback);
    }

    [Fact]
    public void Feedback_DisclosesTheFileWriteRollback_LikeEveryOtherFragmentRejection()
    {
        string rolledBack = RetryPolicy.ForNestedControlKey(
            MinimalTask(TaskId), attempt: 1, new NestedControlKeySignal("needsHarnessWrite", TaskId),
            fileWritesRolledBack: true);
        string notRolledBack = RetryPolicy.ForNestedControlKey(
            MinimalTask(TaskId), attempt: 1, new NestedControlKeySignal("needsHarnessWrite", TaskId),
            fileWritesRolledBack: false);

        Assert.Contains("File writes were also rolled back", rolledBack);
        Assert.DoesNotContain("File writes were also rolled back", notRolledBack);
    }

    private static TaskNode MinimalTask(string id) => new()
    {
        Id = id,
        Directory = Path.Combine(Path.GetTempPath(), id),
        Description = "a task",
        Action = new ActionDefinition
        {
            Kind = ActionKind.Prompt,
            Path = Path.Combine(Path.GetTempPath(), id, "action.prompt.md")
        },
        Guardrails = []
    };
}
