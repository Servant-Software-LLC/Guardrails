using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// Issue #549 — <c>guardrails run --dry-run</c> reported the WRONG runner for every task on a tiered
/// plan. Its <c>ResolveRunner</c> implemented the PRE-TIERING precedence (<c>action.runner</c> else
/// <c>promptRunners.default</c>) and never called <see cref="Core.Prompts.TierResolver"/>, so a task
/// with a rung and no pin fell through to the default pointer — on a well-formed tiered config, exactly
/// the block the operator did NOT tag it for. Measured on plan 28: the preview booked all 30 tasks as
/// <c>sonnet</c> while the run routed 8 to <c>opus</c> and 2 to <c>haiku</c>, one of those opus attempts
/// costing $6.22 against $0.37–$0.80 for a sonnet task.
///
/// <para><b>Every clause here asserts the ROW, not the page.</b> A bare
/// <c>Assert.Contains("opus", output)</c> would pass on any output that mentions the word anywhere — a
/// warning line, another task's row — which is the same "plausible and false" shape the defect had.
/// <see cref="ResolutionRow"/> pins the assertion to one task's line in the per-task resolution table.</para>
///
/// <para>The plan is emitted by the shared <see cref="Stage2PlanHarness"/> — the same writer the Stage 2
/// conformance suite runs for real — and driven through the REAL CLI (<c>RunCommand.Create</c> +
/// <c>--dry-run</c>), so nothing about the preview is faked above the console seam.</para>
/// </summary>
public sealed class DryRunRoutePreviewTests
{
    private static async Task<string> DryRunAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));

        int exit = await root.Parse(["run", planDir, "--dry-run"]).InvokeAsync();

        // Carry the preview itself into the failure: a bare "expected 0, got 1" from a validation refusal
        // hides the diagnostic that explains it, and every clause below reads this same text anyway.
        Assert.True(
            exit == ExitCodes.Success,
            $"--dry-run exited {exit} rather than 0. Output:\n{io.OutText}\n{io.ErrorText}");

        return io.OutText;
    }

    /// <summary>
    /// The one line of the per-task resolution table that belongs to <paramref name="taskId"/>. The
    /// table is the surface under test, so a clause must not be satisfiable by text from anywhere else
    /// in the preview.
    /// </summary>
    private static string ResolutionRow(string output, string taskId)
    {
        string[] rows =
        [
            .. output.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                // The tier LISTING above prints "  <id>  prompt" too, so a row is identified by the
                // resolution table's own extra columns: it carries the retry budget and a resume verdict.
                .Where(line => line.TrimStart().StartsWith(taskId + " ", StringComparison.Ordinal))
                .Where(line => line.EndsWith("run", StringComparison.Ordinal)
                    || line.Contains("SKIP", StringComparison.Ordinal)
                    || line.Contains("HALT", StringComparison.Ordinal))
        ];

        return Assert.Single(rows);
    }

    /// <summary>The plan-28 shape from the issue: three rungs, three blocks, one block per rung.</summary>
    private static Stage2PlanSpec ThreeRungPlan(params Stage2TaskSpec[] tasks) => new()
    {
        DefaultRunner = "sonnet",
        Runners =
        [
            new Stage2RunnerBlock { Name = "haiku", Model = "claude-haiku-4-5", Strength = 1, Tiers = ["easy"] },
            new Stage2RunnerBlock { Name = "sonnet", Model = "claude-sonnet-5", Strength = 2, Tiers = ["medium"] },
            new Stage2RunnerBlock { Name = "opus", Model = "claude-opus-5", Strength = 3, Tiers = ["hard"] }
        ],
        Tasks = tasks
    };

    [Fact]
    public async Task ATieredTaskPreviewsTheBlockItsRungRoutesTo_NotTheDefaultPointer()
    {
        // The reported defect, reduced: `sonnet` is the default pointer, so a preview that never consults
        // the resolver names it for all three tasks and reads as a perfectly ordinary table.
        using var harness = new Stage2PlanHarness();
        string planDir = harness.WritePlanOnly(ThreeRungPlan(
            new Stage2TaskSpec { Id = "01-easy-task", Tier = "easy" },
            new Stage2TaskSpec { Id = "02-medium-task", Tier = "medium" },
            new Stage2TaskSpec { Id = "03-hard-task", Tier = "hard" }));

        string output = await DryRunAsync(planDir);

        Assert.Contains("haiku", ResolutionRow(output, "01-easy-task"), StringComparison.Ordinal);
        Assert.Contains("sonnet", ResolutionRow(output, "02-medium-task"), StringComparison.Ordinal);
        Assert.Contains("opus", ResolutionRow(output, "03-hard-task"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATieredTaskDoesNotPreviewTheDefaultPointersBlock()
    {
        // The negative half, and the one that fails loudly against the shipped code: the `hard` task's row
        // must not name `sonnet` at all. Asserting only the positive above would still pass if a future
        // change printed BOTH names, and "opus was mentioned" is not "opus is what will run".
        using var harness = new Stage2PlanHarness();
        string planDir = harness.WritePlanOnly(ThreeRungPlan(
            new Stage2TaskSpec { Id = "03-hard-task", Tier = "hard" }));

        string output = await DryRunAsync(planDir);

        Assert.DoesNotContain("sonnet", ResolutionRow(output, "03-hard-task"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRungAndTheSiteThatSuppliedItAreShown()
    {
        // The preview's second half (#549's complaint 2): the runner alone is an answer with no working
        // shown. `hard (task)` says the TASK asked for opus; `hard (plan-default)` would say the plan did —
        // different facts with different fixes, and the run's own attempt-route.log already distinguishes
        // them in exactly this vocabulary.
        using var harness = new Stage2PlanHarness();
        string planDir = harness.WritePlanOnly(ThreeRungPlan(
            new Stage2TaskSpec { Id = "03-hard-task", Tier = "hard" }));

        string output = await DryRunAsync(planDir);

        Assert.Contains("TIER", output, StringComparison.Ordinal);
        Assert.Contains("hard (task)", ResolutionRow(output, "03-hard-task"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARungFromThePlanWideDefaultIsAttributedToThePlan()
    {
        // The distinction the issue's runtime evidence called out: PlanLoader collapses
        // `tiering.defaultTier` onto every untagged action, so the rung is indistinguishable from a
        // task-declared one unless the ORIGIN is carried — which is why the preview reads it off the
        // loader's record rather than comparing the two tokens.
        using var harness = new Stage2PlanHarness();
        Stage2PlanSpec spec = ThreeRungPlan(new Stage2TaskSpec { Id = "01-untagged-task" }) with
        {
            DefaultTier = "hard"
        };

        string output = await DryRunAsync(harness.WritePlanOnly(spec));
        string row = ResolutionRow(output, "01-untagged-task");

        Assert.Contains("hard (plan-default)", row, StringComparison.Ordinal);
        Assert.Contains("opus", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClimbShowsBothRungs()
    {
        // §6.2: an empty candidate set climbs to the nearest STRONGER rung. "served at hard" alone reads as
        // an ordinary hard task; the pair is what makes the cost change legible before it is paid for.
        using var harness = new Stage2PlanHarness();
        Stage2PlanSpec spec = new()
        {
            DefaultRunner = "opus",
            Runners = [new Stage2RunnerBlock { Name = "opus", Model = "claude-opus-5", Strength = 3, Tiers = ["hard"] }],
            Tasks = [new Stage2TaskSpec { Id = "01-easy-task", Tier = "easy" }]
        };

        string row = ResolutionRow(await DryRunAsync(harness.WritePlanOnly(spec)), "01-easy-task");

        Assert.Contains("easy -> hard (task)", row, StringComparison.Ordinal);
        Assert.Contains("opus", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnservableRungIsRefusedBeforeAnyRowIsPrinted()
    {
        // The other way the preview can lie about a rung nothing serves: print a plausible block for work
        // that will never run. It cannot, because `--dry-run` VALIDATES first and GR2048 is that exact
        // condition — so the table is never reached. Pinned here because the fix's `(no route)` cell is a
        // defensive residual whose unreachability is a property of this gate, not of the renderer: if
        // GR2048 ever stopped covering a rung, this clause fails and says which surface has to.
        using var harness = new Stage2PlanHarness();
        Stage2PlanSpec spec = new()
        {
            DefaultRunner = "haiku",
            Runners = [new Stage2RunnerBlock { Name = "haiku", Model = "claude-haiku-4-5", Strength = 1, Tiers = ["easy"] }],
            Tasks = [new Stage2TaskSpec { Id = "01-hard-task", Tier = "hard" }]
        };

        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));

        int exit = await root.Parse(["run", harness.WritePlanOnly(spec), "--dry-run"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("GR2048", io.OutText, StringComparison.Ordinal);

        // No table at all — so there is no row that could have named `haiku` for a rung it cannot serve.
        Assert.DoesNotContain("Per-task resolution:", io.OutText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APinnedTaskPreviewsThePinnedBlockAndSaysItWasOverridden()
    {
        // §6.1 item 1: a pin bypasses resolution entirely and resolves no rung, so the TIER cell carries the
        // provenance instead of a rung. A bare "-" there would make a hand-pinned costly block look like an
        // untagged legacy task.
        using var harness = new Stage2PlanHarness();
        string planDir = harness.WritePlanOnly(ThreeRungPlan(
            new Stage2TaskSpec { Id = "01-pinned-task", Tier = "easy", Runner = "opus" }));

        string row = ResolutionRow(await DryRunAsync(planDir), "01-pinned-task");

        Assert.Contains("opus", row, StringComparison.Ordinal);
        Assert.Contains("(override)", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntaggedPlanIsUnchanged_NoRungAndTheDefaultBlock()
    {
        // Invariant 7's preview half: a plan that opted into none of this must look exactly as it did before
        // tiering existed — the default block, and a TIER cell that names no rung, because none was asked
        // for. A preview that started printing a fabricated rung here would be its own #549.
        using var harness = new Stage2PlanHarness();
        Stage2PlanSpec spec = new()
        {
            DefaultRunner = "claude",
            Runners = [new Stage2RunnerBlock { Name = "claude", Model = "claude-sonnet-5" }],
            Tasks = [new Stage2TaskSpec { Id = "01-plain-task" }]
        };

        string row = ResolutionRow(await DryRunAsync(harness.WritePlanOnly(spec)), "01-plain-task");

        Assert.Contains("claude", row, StringComparison.Ordinal);
        Assert.DoesNotContain("easy", row, StringComparison.Ordinal);
        Assert.DoesNotContain("medium", row, StringComparison.Ordinal);
        Assert.DoesNotContain("hard", row, StringComparison.Ordinal);
        Assert.DoesNotContain("(", row, StringComparison.Ordinal);
    }
}
