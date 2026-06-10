using System.Runtime.CompilerServices;

namespace Guardrails.Core.Tests;

/// <summary>
/// Resolves paths to committed test fixtures relative to this source file, so tests do
/// not depend on the build output layout (fixtures are read in place, never copied).
/// </summary>
public static class TestPaths
{
    /// <summary>Absolute path to the directory containing this source file (the test project root).</summary>
    public static string ProjectDir { get; } = GetProjectDir();

    /// <summary>Absolute path to a named fixture under <c>TestData/</c>.</summary>
    public static string Fixture(string relative) =>
        Path.Combine(ProjectDir, "TestData", relative);

    private static string GetProjectDir([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}
