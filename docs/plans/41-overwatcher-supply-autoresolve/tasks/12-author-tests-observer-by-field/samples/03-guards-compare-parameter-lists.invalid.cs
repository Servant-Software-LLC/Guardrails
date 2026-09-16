using System.Reflection;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the declaration census matches by member NAME alone. Every
/// decorator already declares a SuppliedResourcesCommitted — the TWO-argument one — so this test is
/// GREEN the moment the three-argument member is added, while the Scheduler's three-argument call lands
/// on the interface's empty default body and the event disappears in every mode. The test looks like a
/// forwarding guard, resolves the right types, keeps its non-vacuity floors, and proves nothing about
/// the signature that matters (design 41 §6). The plan trait is present, so the valid/invalid diff is
/// exactly the parameter-list comparison.
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedObserverEventTests
{
    private static bool Declares(Type type, string methodName) =>
        type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == methodName || m.Name.EndsWith("." + methodName, StringComparison.Ordinal));

    [Fact]
    public void EveryDecorator_ForwardsTheEventWithTheSupplier()
    {
        Assembly coreAssembly = typeof(IRunObserver).Assembly;

        (string TypeName, Type? Resolved)[] decorators =
        [
            ("Guardrails.Core.Execution.RunEventStream",
                coreAssembly.GetType("Guardrails.Core.Execution.RunEventStream", throwOnError: false)),
            ("Guardrails.Core.Execution.ObserverProjection",
                coreAssembly.GetType("Guardrails.Core.Execution.ObserverProjection", throwOnError: false)),
        ];

        string[] unresolved = [.. decorators.Where(d => d.Resolved is null).Select(d => d.TypeName)];
        Assert.True(unresolved.Length == 0, string.Join(", ", unresolved));

        const string member = nameof(IRunObserver.SuppliedResourcesCommitted);
        Assert.NotNull(typeof(IRunObserver).GetMethod(member, [typeof(IReadOnlyList<string>), typeof(string), typeof(string)]));

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            "The following decorator(s) do NOT declare " + member + ": " + string.Join(", ", missing));
    }
}
