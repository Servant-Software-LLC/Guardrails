using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Config parsing of the Part C <c>driftPolicy</c> field (<c>guardrails.json</c>, SSOT §2/§7.2, issue
/// #274). The three recognised values load into <see cref="RunConfig.DriftPolicy"/>; an absent field
/// defaults to <see cref="DriftPolicy.Prompt"/>; an unrecognised value is the loading error
/// <see cref="DiagnosticCodes.InvalidDriftPolicy"/> (GR2031). Builds a tiny plan folder on disk,
/// mirroring <c>CostCapConfigTests</c>.
/// </summary>
public sealed class DriftPolicyConfigTests : IDisposable
{
    private readonly string _root;

    public DriftPolicyConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-driftpolicy-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string PlanWith(string guardrailsJson)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);
        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "exit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");
        return _root;
    }

    [Theory]
    [InlineData("prompt", DriftPolicy.Prompt)]
    [InlineData("reprocess", DriftPolicy.Reprocess)]
    [InlineData("halt", DriftPolicy.Halt)]
    [InlineData("HALT", DriftPolicy.Halt)]           // trim + case-insensitive
    [InlineData("  reprocess  ", DriftPolicy.Reprocess)]
    public void RecognisedValue_ParsesToEnum(string value, DriftPolicy expected)
    {
        string json = $$"""{ "version": 1, "driftPolicy": "{{value}}" }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(expected, result.Plan!.Config.DriftPolicy);
    }

    [Fact]
    public void Absent_DefaultsToPrompt()
    {
        PlanLoadResult result = new PlanLoader().Load(PlanWith("""{ "version": 1 }"""));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(DriftPolicy.Prompt, result.Plan!.Config.DriftPolicy);
    }

    [Theory]
    [InlineData("reprossess")]  // typo
    [InlineData("skip")]
    [InlineData("")]
    public void UnrecognisedValue_IsInvalidDriftPolicyError_GR2031(string value)
    {
        string json = $$"""{ "version": 1, "driftPolicy": "{{value}}" }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidDriftPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("GR2031", DiagnosticCodes.InvalidDriftPolicy);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
