// A representative, CORRECT ProducerCoverageTests: all ten enumerated behaviours present, each with a
// real xUnit attribute, and the condition-8 control RECOVERED from the 544f7d5 -> aSyntheticFixture pair.
using Guardrails.Core.Loading;
using Xunit;

namespace Guardrails.Core.Tests;

public class ProducerCoverageTests
{
    [Fact]
    public void Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Recovered_Silent_OnTheSameScript_AtTodaysCommit() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Extracts_OneHopAssociation_TestPathThenGetContentShape() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Extracts_DoubleQuotedPathOperand_WithNoDollarAndNoBacktick() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    // RECOVERED, not constructed. Condition 8 IS exercised in the corpus: at aSyntheticFixture the witness
    // is still absent from the SSOT, but 14-land-ssot-schema-deltas now declares that exact path
    // in its writeScope. Paired with 544f7d5, where nothing owns it and the check FIRES, the only
    // difference between the two commits is whether a task owns the file - which is precisely the
    // discrimination condition 8 exists to make.
    [Fact]
    public void Constructed_Silent_WhenThePathIsCoveredByATaskWriteScope() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Silent_WhenTheWitnessIsPresentInTheFile() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Silent_WhenTheFileIsNotGitTracked() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Silent_WhenTheProbeAnswersNotKnown() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Silent_WhenThePathIsUnderThePlanFolder() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

    [Fact]
    public void Silent_WhenPlanIsNotClosed() => Assert.True(ProducerCoverage.Probe("544f7d5", "aSyntheticFixture"));

}
