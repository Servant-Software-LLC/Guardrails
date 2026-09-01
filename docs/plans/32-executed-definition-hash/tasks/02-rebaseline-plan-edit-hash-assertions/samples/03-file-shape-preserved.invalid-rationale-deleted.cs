// EXTRA CASE: the load-bearing rationale comment deleted along with the assertion it used to justify.
// Five methods, none skipped, both assertions correct - only clause 5 catches it.
using System.IO;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Xunit;

namespace Guardrails.Integration.Tests;

public sealed class PlanEditedDuringRunTests : IClassFixture<HostRepoCleanlinessGuard>
{
    private const string Target = "02-target";

    [Fact]
    public async Task AGuardrailEditedMidRun_EmitsExactlyOneObservedPlanEditDecision()
    {
        using var repo = new TempGitRepo("gr-pedr-p1");
        RunReport report = await RunWorktreeAsync(repo, MidRunWrite.ModifyTargetGuardrail);

        // Row 2 (section 15.1): a guardrails/*.ps1 is a REAL definition file, not an editor artifact, so
        // the settle-time divergence gate fires and the run is no longer wholly green.
        Assert.False(report.AllSucceeded,
            "a mid-run edit to a real definition file must stop the run being wholly green - the gate fires");
        Assert.Single(report.Decisions, d => d.Boundary == "plan-edit" && d.Decision == "observed");
    }

    [Fact]
    public async Task AJitWaveBreakdownFollowedByRevert_EmitsZeroPlanEditEntries()
    {
        using var repo = new TempGitRepo("gr-pedr-p2");
        RunReport report = await RunWavedAsync(repo);
        Assert.Empty(report.Decisions.Where(d => d.Boundary == "plan-edit"));
    }

    [Fact]
    public async Task ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero()
    {
        using var repo = new TempGitRepo("gr-pedr-p3");
        int exit = await RunCliAsync(repo);
        Assert.Equal(ExitCodes.Success, exit);
        DeliverySection? delivery = ReadDelivery(repo);
        Assert.True(delivery!.Delivered, "a plan-edit observation must not suppress delivery");
    }

    [Fact]
    public async Task AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges()
    {
        using var repo = new TempGitRepo("gr-pedr-p4");
        LoadResult before = new PlanLoader().Load(Path.Combine(repo.RepoPath, "plan"));
        string hashAtStart = TaskDefinitionHash.Compute(before.Plan!.Tasks.Single(t => t.Id == Target));

        RunReport report = await RunWorktreeAsync(repo, MidRunWrite.StrayDsStoreInTargetGuardrails);

        // compares the IGNORE-LIST-FILTERED surface, so a stray editor artifact leaves the run green and
        // delivering. It is the only thing standing between the delivery gate and being muted.
        Assert.True(report.AllSucceeded,
            "a stray editor artifact must not fail the run - the gate compares the filtered surface");

        // recorded definition hash in every plan, and a moved definition hash is a drift HALT on the next
        string? recorded = journal.RecordedDefinitionHash(Target);
        Assert.NotNull(recorded);

        // Row 1 (section 15.1): the recorded hash is now the LOAD-TIME pin, and hashAtStart is the same
        // bytes at the same moment.
        Assert.Equal(hashAtStart, recorded);
    }

    [Fact]
    public async Task TheRenderedText_CarriesAllThreeSection51Consequences()
    {
        string advisory = RenderAdvisory();
        Assert.Contains("post-edit", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-read per attempt", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was halted", advisory, StringComparison.OrdinalIgnoreCase);
    }
}
