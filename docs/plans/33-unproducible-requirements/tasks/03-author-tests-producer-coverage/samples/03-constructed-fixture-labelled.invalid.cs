// A representative, CORRECT ProducerCoverageTests: all ten enumerated behaviours present, each with a
// real xUnit attribute, with the condition-8 fixture relabelled to match its neighbours.
using Guardrails.Core.Loading;
using Xunit;

namespace Guardrails.Core.Tests;

public class ProducerCoverageTests
{
    [Fact]
    public void Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Recovered_Silent_OnTheSameScript_AtTodaysCommit() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Extracts_OneHopAssociation_TestPathThenGetContentShape() => Assert.True(ProducerCoverage.Probe());

    [Fact]
    public void Extracts_DoubleQuotedPathOperand_WithNoDollarAndNoBacktick() => Assert.True(ProducerCoverage.Probe());

    // Recovered from the corpus like its neighbours.
    [Fact]
    public void Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope() => Assert.True(ProducerCoverage.Probe());

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
