using System.Text;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

public sealed class PromptComposerTests : IDisposable
{
    private readonly string _dir;

    public PromptComposerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gr-composer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string WriteState(string content)
    {
        string path = Path.Combine(_dir, "state-in.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Action_SmallState_IsInlined()
    {
        string stateIn = WriteState("{ \"recipientName\": \"World\" }");
        string stateOut = Path.Combine(_dir, "out.json");

        string composed = PromptComposer.ComposeAction("Do the thing.", stateIn, stateOut, feedbackPath: null);

        Assert.Contains("## Shared state", composed);
        Assert.Contains("\"recipientName\": \"World\"", composed);
        Assert.Contains("## Output contract", composed);
        Assert.Contains(stateOut, composed);
        Assert.Contains("needsHuman", composed);
        // No retry section on attempt 1.
        Assert.DoesNotContain("## Previous attempt failed", composed);
    }

    [Fact]
    public void Action_StateAt16KbBoundary_IsInlined_JustOver_IsByPath()
    {
        // Exactly 16 KB → inlined (≤ limit). One byte over → by path.
        string atLimit = new string('a', PromptComposer.StateInlineLimitBytes);
        Assert.Equal(PromptComposer.StateInlineLimitBytes, Encoding.UTF8.GetByteCount(atLimit));

        string stateInAt = WriteState(atLimit);
        string composedAt = PromptComposer.ComposeAction("body", stateInAt, Path.Combine(_dir, "o1.json"), null);
        Assert.Contains(atLimit, composedAt);
        Assert.DoesNotContain("Read the JSON at the absolute path", composedAt);

        string overLimit = new string('b', PromptComposer.StateInlineLimitBytes + 1);
        string stateInOver = WriteState(overLimit);
        string composedOver = PromptComposer.ComposeAction("body", stateInOver, Path.Combine(_dir, "o2.json"), null);
        Assert.DoesNotContain(overLimit, composedOver);
        Assert.Contains("Read the JSON at the absolute path", composedOver);
        Assert.Contains(stateInOver, composedOver);
    }

    [Fact]
    public void Action_WithFeedback_IncludesPreviousAttemptSectionVerbatim()
    {
        string stateIn = WriteState("{}");
        string feedbackPath = Path.Combine(_dir, "feedback.md");
        File.WriteAllText(feedbackPath, "Guardrail 01-greeting-contains failed: missing 'Hello'.");

        string composed = PromptComposer.ComposeAction("body", stateIn, Path.Combine(_dir, "o.json"), feedbackPath);

        Assert.Contains("## Previous attempt failed", composed);
        Assert.Contains("Guardrail 01-greeting-contains failed: missing 'Hello'.", composed);
        Assert.Contains("do not start over", composed);
    }

    [Fact]
    public void Guardrail_HasVerifierContract_AndVerdictPath()
    {
        string stateIn = WriteState("{}");
        string verdictOut = Path.Combine(_dir, "verdict.json");
        string actionStdout = Path.Combine(_dir, "action-stdout.log");

        string composed = PromptComposer.ComposeGuardrail("Judge the tone.", stateIn, verdictOut, actionStdout);

        Assert.Contains("## Verdict contract", composed);
        Assert.Contains("VERIFIER", composed);
        Assert.Contains("Do NOT fix", composed);
        Assert.Contains(verdictOut, composed);
        Assert.Contains(actionStdout, composed);
        Assert.Contains("\"pass\"", composed);
        // A guardrail prompt never gets an output/needsHuman contract.
        Assert.DoesNotContain("## Output contract", composed);
    }

    [Fact]
    public void Body_IsPreservedAtTop()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeAction("FIRST LINE OF BODY", stateIn, Path.Combine(_dir, "o.json"), null);

        Assert.StartsWith("FIRST LINE OF BODY", composed);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
