using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #513 — the decorator half. <c>WaveGateFinished</c> is a DEFAULT-METHOD member, so a transparent
/// decorator that does not DECLARE it inherits the empty body and swallows the event in every mode: the
/// renderer behind it never hears that a wave gate finished, and the gate stays unbadged exactly as it did
/// before the event existed.
///
/// <para>This is the identical trap <c>AttemptModelResolved</c> carries (#349), which is why the assertion
/// lives on the DECORATORS rather than only on the renderer. It is deliberately written as a reflection
/// sweep over the whole CLI assembly rather than as two hand-named cases: the two on-the-fly decorators are
/// what exist today, and the clause is what catches a third one added later.</para>
///
/// <para>This test lives in the integration project because it is the only one referencing both
/// <c>Guardrails.Core</c> (for <see cref="IRunObserver"/>) and <c>Guardrails.Cli</c> (for the decorators).</para>
/// </summary>
public sealed class WaveGateForwardingTests
{
    [Fact]
    public void EveryForwardingObserverInTheCliAssembly_DeclaresWaveGateFinished()
    {
        System.Reflection.Assembly cli = typeof(Guardrails.Cli.ConsoleRunObserver).Assembly;

        Type[] decorators =
        [
            .. cli.GetTypes().Where(t =>
                t is { IsAbstract: false, IsClass: true }
                && typeof(IRunObserver).IsAssignableFrom(t)
                && t.GetConstructors().Any(c =>
                    c.GetParameters().Any(p => p.ParameterType == typeof(IRunObserver))))
        ];

        // A zero-decorator sweep would make every assertion below vacuously true — the shape that lets a
        // reflection test pass forever after the thing it guards is renamed away.
        Assert.NotEmpty(decorators);

        foreach (Type d in decorators)
        {
            Assert.True(
                d.GetMethod(nameof(IRunObserver.WaveGateFinished)) is not null,
                $"{d.Name} is an IRunObserver DECORATOR and does not declare WaveGateFinished — it will "
                + "silently swallow the event, and the wave gate it should have badged renders as though "
                + "it never ran (#513).");
        }
    }
}
