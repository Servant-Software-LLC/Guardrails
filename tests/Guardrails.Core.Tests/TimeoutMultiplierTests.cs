using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The timeout-extension factor (issue #119): a same-clock retry after a timeout just re-times-out,
/// so each prior timeout grows the retry's clock — bounded so it can't grow without limit.
/// </summary>
public sealed class TimeoutMultiplierTests
{
    [Theory]
    [InlineData(0, 1.0)]        // first attempt: identity
    [InlineData(1, 1.5)]        // after one timeout
    [InlineData(2, 2.25)]       // after two
    public void Grows_PerPriorTimeout(int priorTimeouts, double expected) =>
        Assert.Equal(expected, TaskExecutor.TimeoutMultiplierFor(priorTimeouts), precision: 4);

    [Fact]
    public void IsCapped_NeverUnbounded()
    {
        // Many prior timeouts still cap at 4× so a wedged task cannot drive the clock to infinity.
        Assert.Equal(4.0, TaskExecutor.TimeoutMultiplierFor(100));
        Assert.Equal(4.0, TaskExecutor.TimeoutMultiplierFor(10));
    }

    [Fact]
    public void NegativeInput_TreatedAsZero() =>
        Assert.Equal(1.0, TaskExecutor.TimeoutMultiplierFor(-3));
}
