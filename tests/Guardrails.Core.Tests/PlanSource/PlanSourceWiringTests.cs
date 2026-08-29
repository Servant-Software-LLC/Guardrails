using System.Text.Json;
using Guardrails.Core.Breakdown;
using Guardrails.Core.Execution;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.PlanSource;

/// <summary>
/// The COMPOSITION-ROOT proof for plan-source provenance (issue #505 / #500, plan of record
/// <c>docs/plans/24-plan-source-provenance.md</c> §2 and §7). Every case here drives the REAL production
/// entry point — <see cref="InitialBreakdownInvoker.PrepareInvocation"/> — and then asserts on what that
/// call left on disk. None of them constructs a <see cref="PlanSourceRecord"/> or writes the artifact
/// itself: a test that injects the seam it claims to verify stays green against a
/// <c>PrepareInvocation</c> that was never wired, which is the unwired-factory failure (#120) with extra
/// steps. Writing the <c>plan.md</c> FIXTURE is the input to the production call, never the output under
/// test.
///
/// <para><b>What is NOT under test here, deliberately — do not read a green run as covering it.</b> Half B
/// of the design, <c>BreakdownCommand</c> enforcing the declared-count gate after the breakdown agent
/// returns, cannot be exercised from this project: <c>Guardrails.Core.Tests</c> references
/// <c>Guardrails.Core</c> ONLY and cannot see <c>Guardrails.Cli</c>. What is proven below is that the REAL
/// gate rejects an under-recording folder when fed the count read back out of the artifact the REAL
/// <c>PrepareInvocation</c> wrote. That the CLI is the thing feeding it is covered by a separate
/// structural guardrail over <c>src/Guardrails.Cli/Commands/BreakdownCommand.cs</c>, which proves the text
/// is there and not that the call is reached.</para>
///
/// <para>Tagged Category=PlanSourceProvenance (class-level, inherited by every case) so the plan's
/// baseline preflight can exclude this suite via <c>--filter "Category!=PlanSourceProvenance"</c>,
/// matching its siblings.</para>
/// </summary>
[Trait("Category", "PlanSourceProvenance")]
public sealed class PlanSourceWiringTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "gr-plan-source-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Drive the production entry point the way <c>BreakdownCommand</c> drives it — the output folder
    /// created first (it does), its <c>state/</c> subdirectory NOT (it does not) — and hand back the
    /// folder that call authored into. Nothing here touches the repository tree, and the output folder is
    /// always a fresh temp directory, never a real plan folder.
    /// </summary>
    private string PrepareAgainst(string planMarkdown)
    {
        string planPath = Path.Combine(NewTempDir(), "plan.md");
        File.WriteAllText(planPath, planMarkdown);

        string outputFolder = NewTempDir();
        InitialBreakdownInvoker.PrepareInvocation(
            planPath, outputFolder, Path.Combine(outputFolder, "logs", "breakdown"));

        return outputFolder;
    }

    private static string ArtifactIn(string outputFolder) =>
        Path.Combine(outputFolder, "state", "plan-source.json");

    /// <summary>
    /// N, read back OUT OF THE FILE the production call wrote — never out of an in-memory record this
    /// test built. Matched case-insensitively against the property itself so the lookup is tied to
    /// <see cref="PlanSourceRecord"/> rather than to a hand-copied JSON key.
    /// </summary>
    private static int DeclaredDecisionsIn(string outputFolder)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ArtifactIn(outputFolder)));

        return document.RootElement
            .EnumerateObject()
            .Single(property => string.Equals(
                property.Name,
                nameof(PlanSourceRecord.DeclaredDelegatedDecisions),
                StringComparison.OrdinalIgnoreCase))
            .Value
            .GetInt32();
    }

    [Fact]
    public void PrepareInvocation_WritesPlanSourceJson_IntoTheOutputFoldersStateDirectory()
    {
        string outputFolder = PrepareAgainst("# Plan\n\n- One item.\n");

        string artifact = ArtifactIn(outputFolder);
        Assert.True(
            File.Exists(artifact),
            $"the real PrepareInvocation did not write {artifact} — the recorder is not wired into the " +
            "breakdown path, so `guardrails breakdown` records no provenance at all");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(artifact));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void PrepareInvocation_RecordsTheDeclaredDelegatedDecisionCount_FromTheRealPlanBytes()
    {
        string outputFolder = PrepareAgainst(
            "# Plan\n\n**DECISIONS DELEGATED TO YOU: 2**\n\n- One item.\n");

        Assert.Equal(2, DeclaredDecisionsIn(outputFolder));
    }

    [Fact]
    public void DeclaredCountGate_RejectsAnUnderRecordingFolder_UsingTheRecordPrepareInvocationWrote()
    {
        string outputFolder = PrepareAgainst(
            "# Plan\n\n**DECISIONS DELEGATED TO YOU: 2**\n\n- One item.\n");

        // The breakdown authored no decisions.md, so M = 0. This is the NEVER-SCANNED breakdown: the
        // plan-root preflight cannot see it, because that preflight is authored by the very agent it
        // polices — no ids found means no preflight, and a green run on an invented decision.
        Assert.False(File.Exists(Path.Combine(outputFolder, "decisions.md")));

        DeclaredCountGateResult result =
            DeclaredCountGate.Evaluate(DeclaredDecisionsIn(outputFolder), outputFolder);

        Assert.False(
            result.Passed,
            "the plan declared 2 delegated decisions and the folder records none — the gate must reject it");
        Assert.Equal(2, result.DeclaredCount);
        Assert.Equal(0, result.RecordedCount);
    }

    [Fact]
    public void PlanSourceJson_SurvivesAFreshReset()
    {
        string planFolder = PrepareAgainst("# Plan\n\n- One item.\n");
        string artifact = ArtifactIn(planFolder);
        Assert.True(File.Exists(artifact), "precondition: the artifact must exist before the reset");

        RunReset.Fresh(planFolder);

        Assert.True(
            File.Exists(artifact),
            "RunReset.Fresh deletes NAMED files under state/ — run.json, state.json, merge-conflicts.log " +
            "and the rewind-intent marker — not the folder, which is exactly why provenance outlives a " +
            "--fresh run. A refactor that starts clearing state/ wholesale must turn red HERE rather than " +
            "silently losing the record of which plan.md the folder came from.");
    }
}
