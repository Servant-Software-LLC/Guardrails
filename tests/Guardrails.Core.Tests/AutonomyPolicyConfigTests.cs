using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Config parsing of the unified <c>autonomyPolicy</c> field (<c>guardrails.json</c>, SSOT §2.1/§7.2,
/// #254/#269/#274 — the folded #274 Part C <c>driftPolicy</c>). The three recognised values load into
/// <see cref="RunConfig.AutonomyPolicy"/>; an absent field defaults to <see cref="AutonomyPolicy.Prompt"/>;
/// an unrecognised value — INCLUDING the pre-fold <c>reprocess</c> — is the loading error
/// <see cref="DiagnosticCodes.InvalidAutonomyPolicy"/> (GR2031). Builds a tiny plan folder on disk,
/// mirroring <c>CostCapConfigTests</c>.
/// </summary>
public sealed class AutonomyPolicyConfigTests : IDisposable
{
    private readonly string _root;

    public AutonomyPolicyConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-autonomy-cfg-" + Guid.NewGuid().ToString("N"));
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
    [InlineData("prompt", AutonomyPolicy.Prompt)]
    [InlineData("halt", AutonomyPolicy.Halt)]
    [InlineData("auto", AutonomyPolicy.Auto)]
    [InlineData("HALT", AutonomyPolicy.Halt)]           // trim + case-insensitive
    [InlineData("  auto  ", AutonomyPolicy.Auto)]
    public void RecognisedValue_ParsesToEnum(string value, AutonomyPolicy expected)
    {
        string json = $$"""{ "version": 1, "autonomyPolicy": "{{value}}" }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(expected, result.Plan!.Config.AutonomyPolicy);
    }

    [Fact]
    public void Absent_DefaultsToPrompt()
    {
        PlanLoadResult result = new PlanLoader().Load(PlanWith("""{ "version": 1 }"""));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(AutonomyPolicy.Prompt, result.Plan!.Config.AutonomyPolicy);
    }

    [Theory]
    [InlineData("reprocess")]   // the pre-fold #274 value — no longer valid (clean rename, no shim)
    [InlineData("reprossess")]  // typo
    [InlineData("skip")]
    [InlineData("")]
    public void UnrecognisedValue_IsInvalidAutonomyPolicyError_GR2031(string value)
    {
        string json = $$"""{ "version": 1, "autonomyPolicy": "{{value}}" }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidAutonomyPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("GR2031", DiagnosticCodes.InvalidAutonomyPolicy);
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
