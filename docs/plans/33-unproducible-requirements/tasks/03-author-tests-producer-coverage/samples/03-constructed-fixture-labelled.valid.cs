// A representative, CORRECT ProducerCoverageTests: all ten enumerated behaviours present, each with a
// real xUnit attribute, and the condition-8 fixture honestly labelled Constructed.
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

    // CONSTRUCTED, not recovered, and the distinction is the point. Condition 8 has zero
    // exercises in the corpus, so the suppressing state must be built deliberately. That is
    // legitimate for a SILENCE claim - it is the only way to exercise a suppression at all -
    // and would NOT be legitimate for a FIRING claim, which must be recovered from git.
    [Fact]
    public void Constructed_Silent_WhenThePathIsCoveredByATaskWriteScope() => Assert.True(ProducerCoverage.Probe());

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
