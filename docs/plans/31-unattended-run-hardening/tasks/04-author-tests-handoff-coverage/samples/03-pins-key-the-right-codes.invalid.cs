// Sample: the ONE defect 03-pins-key-the-right-codes.ps1 exists to catch -> must exit NON-ZERO.
//
// Stage it into a scratch tree at tests/Guardrails.Core.Tests/Loading/HandoffScopeCoverageTests.cs and
// point GUARDRAILS_WORKSPACE at that tree.
//
// It is built from the traps the real file will contain:
//   * a fixture that writes JSON, so the method bodies carry BRACES INSIDE STRING LITERALS - the
//     brace scanner must neutralize those or its depth count desynchronizes and it slices the wrong
//     region;
//   * the class name HandoffScopeCoverageTests, which CONTAINS the banned token
//     HandoffScopeCoverage as a prefix - the ban's trailing look-ahead is what keeps the file from
//     tripping its own ban;
//   * a comment naming DiagnosticCodes.HandoffPathUnreachable, which is a MENTION and must pass.
using System;
using System.IO;
using Xunit;

namespace Guardrails.Core.Tests;

public sealed class HandoffScopeCoverageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gr31-sample").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Asserts on the LITERAL "GR2069", never on DiagnosticCodes.HandoffRowSplitAcrossTasks - that
    // constant is stage 5's deliverable and does not compile today.
    [Fact]
    public void Row7WhoseOwningTaskHoldsOnlyTwoOfFourPaths_EmitsGR2069NamingTheCoveringTask()
    {
        // The fixture writes real manifests. Note the braces inside these literals.
        File.WriteAllText(Path.Combine(_dir, "guardrails.json"), "{ \"version\": 1, \"workspace\": \"..\" }");
        File.WriteAllText(Path.Combine(_dir, "task.json"), "{ \"writeScope\": [\"src/Guardrails.Core/Loading/PlanLoader.cs\"] }");

        var diagnostics = Validate(_dir);

        Assert.Contains(diagnostics, d => d.Code == "GR2068");
        Assert.Contains(diagnostics, d => d.Message.Contains("PlanLoader.cs"));
    }

    [Fact]
    public void Row1WithoutTheTestGlobEmitsGR2069_AndIsSilentOnceTheGlobIsAdded()
    {
        File.WriteAllText(Path.Combine(_dir, "task.json"), "{ \"writeScope\": [\"src/A.cs\"] }");
        Assert.Contains(Validate(_dir), d => d.Code == "GR2069");

        File.WriteAllText(Path.Combine(_dir, "task.json"), "{ \"writeScope\": [\"src/A.cs\", \"tests/**\"] }");
        Assert.Empty(Validate(_dir));
    }

    [Fact]
    public void ConcretePathNoTaskCanWrite_EmitsGR2068WithNoSuggestedCorrection()
    {
        var diagnostics = Validate(_dir);
        Assert.Contains(diagnostics, d => d.Code == "GR2068");
    }

    private static System.Collections.Generic.IReadOnlyList<Diag> Validate(string dir) =>
        throw new NotSupportedException("sample only");

    private sealed record Diag(string Code, string Message);
}
