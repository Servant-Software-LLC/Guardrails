using System.Reflection;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// The CORRECT shape: the declaration census compares PARAMETER TYPES, not just the member name, so a
/// decorator that kept the two-argument SuppliedResourcesCommitted is reported as missing rather than
/// silently accepted.
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedObserverEventTests
{
    /// <summary>
    /// A member is DECLARED by a type when the type itself carries it with the SAME parameter list. The
    /// EndsWith branch admits an explicit interface implementation, which is named
    /// Guardrails.Core.Execution.IRunObserver.SuppliedResourcesCommitted.
    /// </summary>
    private static bool Declares(Type type, MethodInfo member) =>
        type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => (m.Name == member.Name || m.Name.EndsWith("." + member.Name, StringComparison.Ordinal))
                      && m.GetParameters().Select(p => p.ParameterType)
                           .SequenceEqual(member.GetParameters().Select(p => p.ParameterType)));

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

        // Non-vacuity floor: a type lookup that silently returned null must fail LOUDLY and by name.
        string[] unresolved = [.. decorators.Where(d => d.Resolved is null).Select(d => d.TypeName)];
        Assert.True(unresolved.Length == 0, string.Join(", ", unresolved));

        // Non-vacuity floor: the three-argument member itself must exist, or the census is vacuous.
        MethodInfo? member = typeof(IRunObserver).GetMethod(
            nameof(IRunObserver.SuppliedResourcesCommitted),
            [typeof(IReadOnlyList<string>), typeof(string), typeof(string)]);
        Assert.NotNull(member);

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member!)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            "The following decorator(s) do NOT declare SuppliedResourcesCommitted(IReadOnlyList<string>, "
            + "string, string) — each inherits the interface's empty default body and silently swallows "
            + "the announcement: " + string.Join(", ", missing));
    }
}
