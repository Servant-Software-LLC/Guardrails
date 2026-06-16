using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Config parsing of the optional per-run cost cap (<c>maxCostUsd</c> in <c>guardrails.json</c>,
/// SSOT §2): a present value loads into <see cref="RunConfig.MaxCostUsd"/> as a decimal, and an
/// absent field leaves it null (no cap — today's behavior). Builds a tiny plan folder on disk,
/// mirroring <see cref="PromptRunnerConfigTests"/>.
///
/// These tests are authored BEFORE the feature exists: they reference
/// <c>RunConfig.MaxCostUsd</c>, which the implementation task adds. Until then the suite does not
/// compile against this file — that failure is intentional and proves the test encodes unbuilt
/// behavior.
/// </summary>
public sealed class CostCapConfigTests : IDisposable
{
    private readonly string _root;

    public CostCapConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-costcap-cfg-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void MaxCostUsd_Present_IsParsedAsDecimal()
    {
        const string json =
            """
            {
              "version": 1,
              "maxCostUsd": 1.50
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        RunConfig config = result.Plan!.Config;
        Assert.Equal(1.50m, config.MaxCostUsd);
    }

    [Fact]
    public void MaxCostUsd_Absent_IsNull()
    {
        const string json = """{ "version": 1 }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        Assert.Null(result.Plan!.Config.MaxCostUsd);
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
