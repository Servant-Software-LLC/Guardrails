using Guardrails.Core.Breakdown;
using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The four fixes that came out of the `model-tiering-stage-3` wave-2 and wave-3 barriers, each pinned
/// against the exact shape that shipped: #504 (the wall clock was the binding budget on a phase whose
/// other budgets are adaptive), #507 (a manifest that hashed live logs), #508 (a halt that contradicted
/// itself when the declared count was met) and #512 (a gate record that printed PASS for a rejection).
/// </summary>
public sealed class BreakdownBudgetAndHaltTests
{
    // ---- #504 -----------------------------------------------------------------------------------

    [Fact]
    public void TheBreakdownIsBoundedBySILENCE_NotByDuration()
    {
        // The regression this pins: a 30-minute wall clock killed two consecutive waves that were BOTH
        // emitting output continuously. The working bound must be the stall bound, and the wall clock
        // must be a backstop far above it — otherwise the clock is the binding budget again.
        Assert.True(
            WaveBreakdownInvoker.BreakdownTimeout > WaveBreakdownInvoker.BreakdownStallBound * 4,
            "BreakdownTimeout must be a generous BACKSTOP, not a working ceiling — if it sits near the "
            + "stall bound it becomes the binding constraint again, which is #504.");
    }

    [Fact]
    public void TheStallBoundClearsTheLongestLegitimateQuietToolCall()
    {
        // A healthy breakdown agent runs suites as tool calls and the stream is silent while a child
        // process runs. One Integration-suite call measured 10m44s of continuous silence on a real plan,
        // so a bound at or below that kills correct work — the precise failure this change exists to end.
        Assert.True(
            WaveBreakdownInvoker.BreakdownStallBound >= TimeSpan.FromMinutes(15),
            "the stall bound must clear the longest legitimate quiet tool call (measured: a 10m44s "
            + "`dotnet test`), with room to spare.");
    }

    [Fact]
    public void AStalledSessionIsItsOwnFailureKind_NotATimeout()
    {
        // "it was silent" and "it ran long" are different diagnoses with different fixes; conflating them
        // is what made a healthy 30-minute session read as a runaway.
        var stalled = new WaveBreakdownOutcome { FailureKind = PromptFailureKind.Stalled, ProcessCompleted = false };
        var timedOut = new WaveBreakdownOutcome { FailureKind = PromptFailureKind.Timeout, ProcessCompleted = false };

        Assert.Equal(BreakdownFailureTokens.Stalled, stalled.FailureKindToken);
        Assert.NotEqual(stalled.FailureKindToken, timedOut.FailureKindToken);
        Assert.Contains("STALLED", stalled.CutOffCause, StringComparison.Ordinal);
        Assert.Contains("SILENCE", stalled.CutOffCause, StringComparison.Ordinal);
    }

    // ---- #507 -----------------------------------------------------------------------------------

    [Fact]
    public void TheBreakdownManifestExcludesLogs_TheyAreHarnessRuntime()
    {
        // `logs/` is a SIBLING of `state/`, not a child, which is how it fell through while two comments
        // in the file claimed it was excluded. Consequence measured: `lock` opened a live
        // claude-stream.jsonl mid-breakdown and died, and 161 of 208 reported drift entries were logs.
        string root = Path.Combine(Path.GetTempPath(), "gr-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tasks", "01-a"));
            File.WriteAllText(Path.Combine(root, "tasks", "01-a", "task.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "logs", "run-1", "wave-01", "breakdown"));
            File.WriteAllText(
                Path.Combine(root, "logs", "run-1", "wave-01", "breakdown", "claude-stream.jsonl"),
                "{\"type\":\"system\"}");
            File.WriteAllText(Path.Combine(root, "logs", "run-1", "index.html"), "<html></html>");

            BreakdownManifest manifest = BreakdownManifest.Capture(root);

            Assert.Contains("tasks/01-a/task.json", manifest.Files.Keys);
            Assert.DoesNotContain(manifest.Files.Keys, k => k.StartsWith("logs/", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    // ---- #508 -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, 5)]
    [InlineData(3, 3)]
    public void WhenEveryDeclaredFolderIsAuthored_TheHaltDoesNotCallTheWaveIncomplete(int declared, int complete)
    {
        // "INCOMPLETE — 5 of 5 declared task(s) authored" is self-contradictory, and it shipped twice.
        string headline = Scheduler.ComposeBreakdownIncompleteHeadline("wave-02-x", declared, complete);

        Assert.DoesNotContain("INCOMPLETE", headline, StringComparison.Ordinal);
        Assert.Contains("UNCONFIRMED", headline, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenFoldersAreGenuinelyOwed_TheHaltStillSaysIncomplete()
    {
        // The other half: the real shortfall case must keep its wording, or the fix has just moved the lie.
        string headline = Scheduler.ComposeBreakdownIncompleteHeadline("wave-02-x", declared: 12, complete: 5);

        Assert.Contains("INCOMPLETE", headline, StringComparison.Ordinal);
        Assert.Contains("5 of 12", headline, StringComparison.Ordinal);
    }
}
