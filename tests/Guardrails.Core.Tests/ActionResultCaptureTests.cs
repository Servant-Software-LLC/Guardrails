using System.Text.Json.Nodes;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests for harness-injected action result fields in the state fragment (issue #62).
/// After a successful action the harness writes <c>actionExitCode</c> and <c>actionKind</c>
/// under the task's own key so guardrails read the outcome via <c>GUARDRAILS_STATE_FRAGMENT</c>
/// and downstream tasks see it in their <c>GUARDRAILS_STATE_IN</c> snapshot.
/// </summary>
public sealed class ActionResultCaptureTests : IDisposable
{
    private readonly string _root;

    public ActionResultCaptureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-arc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Inject_CreatesFragment_WhenNoneExists()
    {
        string fragmentPath = FragmentPath();
        Assert.False(File.Exists(fragmentPath));

        ActionResultCapture.Inject("01-task", exitCode: 0, actionKind: "script", fragmentPath);

        Assert.True(File.Exists(fragmentPath));
        JsonObject root = ParseFragment(fragmentPath);
        Assert.Equal(0, (int?)root["01-task"]!["actionExitCode"]);
        Assert.Equal("script", (string?)root["01-task"]!["actionKind"]);
    }

    [Fact]
    public void Inject_RecordsNonZeroExitCode()
    {
        string fragmentPath = FragmentPath();

        ActionResultCapture.Inject("02-task", exitCode: 1, actionKind: "prompt", fragmentPath);

        JsonObject root = ParseFragment(fragmentPath);
        Assert.Equal(1, (int?)root["02-task"]!["actionExitCode"]);
        Assert.Equal("prompt", (string?)root["02-task"]!["actionKind"]);
    }

    [Fact]
    public void Inject_PreservesExistingFragmentContent()
    {
        string fragmentPath = FragmentPath();
        File.WriteAllText(fragmentPath,
            """{ "01-task": { "notes": "agent wrote this", "fileHashes": { "foo.cs": "ABCD" } } }""");

        ActionResultCapture.Inject("01-task", exitCode: 0, actionKind: "script", fragmentPath);

        JsonObject root = ParseFragment(fragmentPath);
        // Harness fields injected.
        Assert.Equal(0, (int?)root["01-task"]!["actionExitCode"]);
        Assert.Equal("script", (string?)root["01-task"]!["actionKind"]);
        // Agent-written fields preserved.
        Assert.Equal("agent wrote this", (string?)root["01-task"]!["notes"]);
        Assert.Equal("ABCD", (string?)root["01-task"]!["fileHashes"]!["foo.cs"]);
    }

    [Fact]
    public void Inject_HarnessValueWins_WhenActionAlsoWroteActionExitCode()
    {
        // An action must not be able to forge its own exit code in state.
        string fragmentPath = FragmentPath();
        File.WriteAllText(fragmentPath,
            """{ "01-task": { "actionExitCode": 99, "actionKind": "fabricated" } }""");

        ActionResultCapture.Inject("01-task", exitCode: 0, actionKind: "script", fragmentPath);

        JsonObject root = ParseFragment(fragmentPath);
        Assert.Equal(0, (int?)root["01-task"]!["actionExitCode"]);
        Assert.Equal("script", (string?)root["01-task"]!["actionKind"]);
    }

    [Fact]
    public void Inject_MalformedFragment_LeavesFileUntouched()
    {
        // A malformed fragment must not be overwritten — the merge step rejects it.
        string fragmentPath = FragmentPath();
        const string malformed = "{ not valid json";
        File.WriteAllText(fragmentPath, malformed);

        ActionResultCapture.Inject("01-task", exitCode: 0, actionKind: "script", fragmentPath);

        Assert.Equal(malformed, File.ReadAllText(fragmentPath));
    }

    [Fact]
    public void Inject_NonObjectFragment_LeavesFileUntouched()
    {
        string fragmentPath = FragmentPath();
        const string array = "[1, 2, 3]";
        File.WriteAllText(fragmentPath, array);

        ActionResultCapture.Inject("01-task", exitCode: 0, actionKind: "script", fragmentPath);

        Assert.Equal(array, File.ReadAllText(fragmentPath));
    }

    [Fact]
    public void Inject_EmptyFragment_WritesOnlyHarnessFields()
    {
        string fragmentPath = FragmentPath();
        File.WriteAllText(fragmentPath, "{}");

        ActionResultCapture.Inject("01-task", exitCode: 0, actionKind: "prompt", fragmentPath);

        JsonObject root = ParseFragment(fragmentPath);
        Assert.Equal(0, (int?)root["01-task"]!["actionExitCode"]);
        Assert.Equal("prompt", (string?)root["01-task"]!["actionKind"]);
    }

    [Fact]
    public void Inject_OutputIsReadableByGuardrailViaStateFragment()
    {
        // Smoke-test the downstream read pattern a guardrail script would use:
        //   $fragment = Get-Content $env:GUARDRAILS_STATE_FRAGMENT | ConvertFrom-Json
        //   $taskId   = $env:GUARDRAILS_TASK_ID
        //   if ($fragment.$taskId.actionExitCode -ne 0) { exit 1 }
        string fragmentPath = FragmentPath();
        ActionResultCapture.Inject("20-full-solution-builds", exitCode: 0, actionKind: "script", fragmentPath);

        JsonObject root = ParseFragment(fragmentPath);
        JsonNode? taskNode = root["20-full-solution-builds"];
        Assert.NotNull(taskNode);
        Assert.Equal(0, (int?)taskNode["actionExitCode"]);
        Assert.Equal("script", (string?)taskNode["actionKind"]);
    }

    private string FragmentPath() => Path.Combine(_root, "action-out-fragment.json");

    private static JsonObject ParseFragment(string fragmentPath) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(fragmentPath))!;
}
