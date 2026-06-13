namespace Guardrails.Integration.Tests;

/// <summary>
/// Serializes the integration test classes that redirect the process-wide
/// <see cref="System.Console.Out"/> (via <c>Console.SetOut</c>) to capture CLI stdout.
/// <c>Console.Out</c> is a single process-global writer, so two such classes running in
/// parallel can capture each other's output. Membership in one xunit collection makes them
/// run sequentially (xunit does not parallelize within a collection) — gate, not luck, in
/// keeping with the house "determinism via gates, never sleeps/luck" doctrine.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleCaptureCollection
{
    public const string Name = "console-capture";
}
