// A COMPLETE, representative CORRECT artifact for 03-file-shape-preserved.ps1 (#468/#302): the shipped
// integration suite as it looks AFTER stage 2's re-baseline. Kept complete rather than a fragment - an
// incomplete valid sample fails for a DIFFERENT reason and masks the real one.
//
// NOTE THE DISCIPLINE IN THIS HEADER: it deliberately quotes NONE of the tokens the guardrail keys on.
// An earlier draft named them here, and the .invalid halves then passed their own negative case off the
// header comment alone - a sample that cannot bite (catalogue taxonomy 13).
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

        // P16, and it MUST NOT MOVE (section 15.1's "one assertion that must NOT move"): the in-run gate
        // compares the IGNORE-LIST-FILTERED surface, so a stray editor artifact leaves the run green and
        // delivering. It is the only thing standing between the delivery gate and being muted.
        Assert.True(report.AllSucceeded,
            "a stray editor artifact must not fail the run - the gate compares the filtered surface");

        // HashText enumerates "*" and filters nothing, so the artifact IS part of the RECORDED
        // definition - and must stay that way. Moving the ignore list into HashText would move every
        // recorded definition hash in every plan, and a moved definition hash is a drift HALT on the next
        // resume. What changed is the sentence this supports: the artifact is now deliberately OUTSIDE
        // the in-run gate's comparison surface (section 6.2), which is why the run above stays green
        // while the recorded hash below is still the load-time pin.
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
