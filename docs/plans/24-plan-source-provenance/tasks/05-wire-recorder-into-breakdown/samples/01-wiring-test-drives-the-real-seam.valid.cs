using System.Text.Json;
using Guardrails.Core.Breakdown;
using Guardrails.Core.Execution;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.PlanSource;

/// <summary>
/// Composition-root tests for the plan-source recorder. NOTE: tests/Guardrails.Core.Tests references
/// Guardrails.Core only, so BreakdownCommand (Guardrails.Cli) is NOT under test here — the CLI half of
/// the wiring is covered by a structural guardrail over BreakdownCommand.cs.
/// </summary>
public sealed class PlanSourceWiringTests
{
    private const string PlanText =
        "# A plan\n\n**DECISIONS DELEGATED TO YOU: 2**\n\n<!-- charter: plan-sha256=abc123 -->\n";

    [Fact]
    [Trait("Category", "PlanSourceProvenance")]
    public void PrepareInvocation_WritesPlanSourceJson_IntoTheOutputFoldersStateDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-wiring-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string planPath = Path.Combine(root, "plan.md");
            File.WriteAllText(planPath, PlanText);
            string recordPath = Path.Combine(root, "out", "state", "plan-source.json");

            // Drive the REAL production entry point — never construct the record by hand here.
            InitialBreakdownInvoker.PrepareInvocation(planPath, Path.Combine(root, "out"), Path.Combine(root, "logs"));

            Assert.True(File.Exists(recordPath));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    [Trait("Category", "PlanSourceProvenance")]
    public void PrepareInvocation_RecordsTheDeclaredDelegatedDecisionCount_FromTheRealPlanBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-wiring-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string planPath = Path.Combine(root, "plan.md");
            File.WriteAllText(planPath, PlanText);
            string outputFolder = Path.Combine(root, "out");

            InitialBreakdownInvoker.PrepareInvocation(planPath, outputFolder, Path.Combine(root, "logs"));

            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputFolder, "state", "plan-source.json")));
            Assert.Equal(2, doc.RootElement.GetProperty("declaredDelegatedDecisions").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    [Trait("Category", "PlanSourceProvenance")]
    public void DeclaredCountGate_RejectsAnUnderRecordingFolder_UsingTheRecordPrepareInvocationWrote()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-wiring-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string planPath = Path.Combine(root, "plan.md");
            File.WriteAllText(planPath, PlanText);
            string outputFolder = Path.Combine(root, "out");

            InitialBreakdownInvoker.PrepareInvocation(planPath, outputFolder, Path.Combine(root, "logs"));

            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputFolder, "state", "plan-source.json")));
            int declared = doc.RootElement.GetProperty("declaredDelegatedDecisions").GetInt32();

            // No decisions.md in the produced folder => M = 0, the never-scanned breakdown.
            var verdict = DeclaredCountGate.Evaluate(declared, outputFolder);

            Assert.False(verdict.Passed);
            Assert.Contains("2", verdict.Message);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    [Trait("Category", "PlanSourceProvenance")]
    public void PlanSourceJson_SurvivesAFreshReset()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-wiring-" + Guid.NewGuid().ToString("N"));
        try
        {
            string stateDir = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDir);
            string recordPath = Path.Combine(stateDir, "plan-source.json");
            string planPath = Path.Combine(root, "plan.md");
            File.WriteAllText(planPath, PlanText);

            InitialBreakdownInvoker.PrepareInvocation(planPath, root, Path.Combine(root, "logs"));
            Assert.True(File.Exists(recordPath));

            // RunReset deletes NAMED files under state/, not the folder — so this survives.
            RunReset.Fresh(root);

            Assert.True(File.Exists(recordPath));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }
}
