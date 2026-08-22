using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #498 — the <c>guardrails breakdown</c> guard paths: everything the verb must REFUSE before it
/// spends anything. Deliberately scoped to the refusals, because they are the half that can be tested
/// without invoking a real prompt runner (a happy-path test would start a 30-minute authoring session
/// against the live `claude` CLI and bill for it).
///
/// <para>These are worth pinning rather than trusting to review: every one of them is a case where the
/// wrong behaviour is <b>silent</b> — interpreting a <c>.charter.md</c> would fork a contract this repo
/// does not own, and clobbering a populated folder would destroy human guardrail edits that the merge
/// flow exists to preserve.</para>
/// </summary>
public sealed class BreakdownCommandGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gr-bd-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public BreakdownCommandGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static async Task<(int Exit, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(BreakdownCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task MissingPlan_ExitsHarnessError()
    {
        (int exit, string output) = await InvokeAsync("breakdown", Path.Combine(_root, "absent.md"));

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("plan not found", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CharterPlan_IsRefused_AndPointsAtHandoff()
    {
        // Interpreting ::: blocks here would fork `charter-format`, which lives in Charter and is versioned
        // there. Refusing is the contract; the message has to name the way forward or the refusal is just a
        // wall.
        string charter = Write("plan.charter.md", "---\ncharter-format-version: 1\n---\n# Plan\n");

        (int exit, string output) = await InvokeAsync("breakdown", charter);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("charter handoff", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonEmptyTarget_IsRefusedWithoutForce()
    {
        string plan = Write("plan.md", "# Plan\n\n- One item.\n");
        string occupied = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "existing.txt"), "human work");

        (int exit, string output) = await InvokeAsync("breakdown", plan, "--out", occupied);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("--force", output, StringComparison.Ordinal);

        // The refusal must leave the folder untouched — that is the whole point of it.
        Assert.Equal("human work", File.ReadAllText(Path.Combine(occupied, "existing.txt")));
    }

    [Fact]
    public async Task EmptyExistingTarget_IsNotTreatedAsOccupied()
    {
        // An empty directory is not human work. Refusing it would make `--out` on a pre-created path
        // needlessly require --force, and --force is the flag that also permits clobbering a real folder.
        string plan = Write("plan.md", "# Plan\n\n- One item.\n");
        string empty = Path.Combine(_root, "empty-target");
        Directory.CreateDirectory(empty);

        (int exit, string output) = await InvokeAsync(
            "breakdown", plan, "--out", empty, "--runner-config", Path.Combine(_root, "absent.json"));

        // It gets PAST the occupancy guard and fails at runner resolution instead — which is the next
        // guard, and proves the empty folder was accepted.
        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("--runner-config not found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRunnerConfig_ExitsHarnessError_RatherThanFallingBackSilently()
    {
        // Falling back to the built-in runner here would ignore what the operator explicitly asked for —
        // the silent-in-the-direction-that-looks-fine failure this codebase keeps paying for.
        string plan = Write("plan.md", "# Plan\n\n- One item.\n");

        (int exit, string output) = await InvokeAsync(
            "breakdown", plan, "--runner-config", Path.Combine(_root, "nope.json"));

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("--runner-config not found", output, StringComparison.Ordinal);
    }
}
