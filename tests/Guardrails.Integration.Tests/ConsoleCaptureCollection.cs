namespace Guardrails.Integration.Tests;

/// <summary>
/// Serializes the integration test classes that touch the process-wide
/// <see cref="System.Console.Out"/>. Membership is required for any class that EITHER
/// redirects <c>Console.Out</c> (via <c>Console.SetOut</c> to capture CLI stdout) OR DRIVES a
/// console-writing CLI command (e.g. <c>root.Parse(...).InvokeAsync()</c> for <c>run</c>/
/// <c>status</c>/<c>reset</c>/<c>graph</c>, which write to <c>Console.Out</c>). The race is
/// two-sided: <c>Console.Out</c> is a single process-global writer, so a class that redirects
/// it and a class that writes to it can collide if they run in parallel — a redirector can
/// capture the writer's output, or a write can land in another test's capture buffer.
/// Membership in one xunit collection makes them run sequentially (xunit does not parallelize
/// within a collection) — gate, not luck, in keeping with the house "determinism via gates,
/// never sleeps/luck" doctrine.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleCaptureCollection
{
    public const string Name = "console-capture";
}
