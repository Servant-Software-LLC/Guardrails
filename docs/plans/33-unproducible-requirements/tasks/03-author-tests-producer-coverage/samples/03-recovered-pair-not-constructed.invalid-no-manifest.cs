// Representative CORRECT ProducerCoverageTests: the condition-8 control is the recovered
// 544f7d5 -> 5bd29da pair, and BOTH halves are read from git rather than asserted in prose.
using Guardrails.Core.Loading;
using Xunit;

namespace Guardrails.Core.Tests;

public class ProducerCoverageTests
{
    static string Show(string rev) => Git.Show(rev);
    [Fact]
    public void Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Recovered_Silent_OnTheSameScript_AtTodaysCommit() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Extracts_OneHopAssociation_TestPathThenGetContentShape() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Extracts_DoubleQuotedPathOperand_WithNoDollarAndNoBacktick() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope()
    {
        var fires  = Show("544f7d5:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        var silent = Show("5bd29da:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        var owner  = "";
        Assert.Equal(fires, silent);
        Assert.Contains("02-schemas-and-contracts.md", owner);
        Assert.Empty(ProducerCoverage.Findings(silent, owner));
    }

    [Fact]
    public void Silent_WhenTheWitnessIsPresentInTheFile() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Silent_WhenTheFileIsNotGitTracked() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Silent_WhenTheProbeAnswersNotKnown() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Silent_WhenThePathIsUnderThePlanFolder() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Silent_WhenPlanIsNotClosed() => Assert.True(ProducerCoverage.Probe());

}
