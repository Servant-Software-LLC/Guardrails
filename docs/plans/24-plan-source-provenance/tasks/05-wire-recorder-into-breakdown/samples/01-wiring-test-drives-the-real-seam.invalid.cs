using System.Text.Json;
using Guardrails.Core.Breakdown;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.PlanSource;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the test injects the seam it claims to verify. It never calls
/// InitialBreakdownInvoker.PrepareInvocation; it captures the record itself, writes plan-source.json
/// itself, and then asserts the file it just wrote exists. Every assertion below passes against a
/// PrepareInvocation that was never changed at all, so the feature stays dead from the CLI while the
/// suite reports success. (RunReset.Fresh is still present, so the --fresh clause stays clean and the
/// valid/invalid diff is exactly the seam injection.)
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
            string stateDir = Path.Combine(root, "out", "state");
            Directory.CreateDirectory(stateDir);
            string planPath = Path.Combine(root, "plan.md");
            File.WriteAllText(planPath, PlanText);

            PlanSourceRecord record = PlanSourceRecord.Capture(planPath);
            File.WriteAllText(Path.Combine(stateDir, "plan-source.json"), JsonSerializer.Serialize(record));

            Assert.True(File.Exists(Path.Combine(stateDir, "plan-source.json")));
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

            PlanSourceRecord record = PlanSourceRecord.Capture(planPath);

            Assert.Equal(2, record.DeclaredDelegatedDecisions);
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

            PlanSourceRecord record = PlanSourceRecord.Capture(planPath);
            var verdict = DeclaredCountGate.Evaluate(record.DeclaredDelegatedDecisions, root);

            Assert.False(verdict.Passed);
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
            File.WriteAllText(recordPath, "{\"version\":1}");

            RunReset.Fresh(root);

            Assert.True(File.Exists(recordPath));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }
}
