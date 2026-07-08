using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// THE highest-risk M2 delta (SSOT §14.2, design C2): wave-qualified task identity generalizes the §6.2
/// single-writer-per-key anti-poisoning contract from "key == folder name" to "key == <c>&lt;waveDir&gt;/&lt;folder&gt;</c>".
/// These tests pin the no-collision property two ways: (1) the LOADER gives two waves' identically-named
/// tasks DISTINCT wave-qualified ids, so no two writers ever present the same key to the harness; and
/// (2) the <see cref="StateManager"/> single-writer rule, fed those ids, rejects any fragment keyed with a
/// bare or foreign-wave id and accepts only the writer's own wave-qualified id.
/// </summary>
public sealed class WaveQualifiedIdentityTests
{
    // --- (1) The loader mints distinct wave-qualified ids for identically-numbered tasks ----------

    [Fact]
    public void TwoWaves_EachWithA01Task_GetDistinctWaveQualifiedIds()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-provision")
            .Task("wave-02-build", "01-provision");

        PlanLoadResult result = plan.Load();

        Assert.False(result.HasErrors, Dump(result));
        var ids = result.Plan!.Tasks.Select(t => t.Id).ToList();

        Assert.Equal(["wave-01-scaffold/01-provision", "wave-02-build/01-provision"], ids);
        // Two writers, two DISTINCT keys — the collision the flat "01-provision" key would have caused
        // is structurally impossible.
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void WavedTask_CarriesItsWaveDir_FlatTaskDoesNot()
    {
        using var waved = new WavePlanBuilder().Task("wave-03-ship", "07-release");
        TaskNode wavedTask = Assert.Single(waved.Load().Plan!.Tasks);
        Assert.Equal("wave-03-ship", wavedTask.WaveDir);
        Assert.Equal("wave-03-ship/07-release", wavedTask.Id);

        // A flat plan is unchanged: id is the bare folder name and WaveDir is null.
        PlanLoadResult flat = new PlanLoader().Load(TestPaths.Fixture("valid-minimal"));
        TaskNode flatTask = Assert.Single(flat.Plan!.Tasks);
        Assert.Null(flatTask.WaveDir);
        Assert.Equal("01-do-thing", flatTask.Id);
    }

    // --- (2) The single-writer rule is wave-qualified: no cross-wave fragment can be accepted -----

    [Fact]
    public void SingleWriter_AcceptsOwnWaveQualifiedKey()
    {
        using var state = new StateHarness();
        MergeFragmentResult result = state.Merge(
            taskId: "wave-02-build/01-provision",
            fragmentJson: """{ "wave-02-build/01-provision": { "artifact": "x" } }""");

        Assert.True(result.Merged);
    }

    [Fact]
    public void SingleWriter_RejectsBareUnqualifiedKey_AsForeign()
    {
        using var state = new StateHarness();
        // A task in wave-02 writing under the BARE "01-provision" key — the pre-wave flat key — must be
        // rejected: a bare key is not this writer's wave-qualified id (SSOT §14.2, mirrors the #164
        // stableId-keyed rejection).
        MergeFragmentResult result = state.Merge(
            taskId: "wave-02-build/01-provision",
            fragmentJson: """{ "01-provision": { "artifact": "x" } }""");

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.ForeignKey, result.Rejection);
        Assert.Equal("01-provision", Assert.Single(result.ForeignKeys));
    }

    [Fact]
    public void SingleWriter_RejectsOtherWaveKey_AsForeign()
    {
        using var state = new StateHarness();
        // wave-02's "01-provision" task cannot write into wave-01's "01-provision" namespace — the exact
        // cross-wave poisoning the wave-qualified key closes.
        MergeFragmentResult result = state.Merge(
            taskId: "wave-02-build/01-provision",
            fragmentJson: """{ "wave-01-scaffold/01-provision": { "artifact": "x" } }""");

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.ForeignKey, result.Rejection);
        Assert.Equal("wave-01-scaffold/01-provision", Assert.Single(result.ForeignKeys));
    }

    [Theory]
    // Every (writer, foreign-key) pair across three waves that each reuse 01-/02- numbering: the writer's
    // OWN wave-qualified key is the ONLY one the harness accepts; every sibling-numbered or bare key from
    // any wave is rejected. This is the property the design flags for fuzz coverage (design C2).
    [InlineData("wave-01-a/01-t", "01-t")]
    [InlineData("wave-01-a/01-t", "wave-02-b/01-t")]
    [InlineData("wave-01-a/01-t", "wave-03-c/01-t")]
    [InlineData("wave-02-b/01-t", "01-t")]
    [InlineData("wave-02-b/01-t", "wave-01-a/01-t")]
    [InlineData("wave-02-b/02-t", "wave-01-a/02-t")]
    [InlineData("wave-03-c/01-t", "wave-02-b/01-t")]
    public void SingleWriter_NoTwoWavesFragmentsCanCollide(string writer, string foreignKey)
    {
        using var state = new StateHarness();
        MergeFragmentResult result = state.Merge(writer, $$"""{ "{{foreignKey}}": { "v": 1 } }""");

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.ForeignKey, result.Rejection);
    }

    private static string Dump(PlanLoadResult result) =>
        string.Join("\n", result.Diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}"));

    /// <summary>A minimal on-disk <see cref="StateManager"/> harness that merges a fragment string.</summary>
    private sealed class StateHarness : IDisposable
    {
        private readonly string _planDir;
        private readonly StateManager _manager;

        public StateHarness()
        {
            _planDir = Path.Combine(Path.GetTempPath(), "gr-waveid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_planDir, "state"));
            _manager = new StateManager(_planDir);
            _manager.Initialize();
        }

        public MergeFragmentResult Merge(string taskId, string fragmentJson)
        {
            string attemptDir = Path.Combine(_planDir, "attempt");
            Directory.CreateDirectory(attemptDir);
            string fragmentPath = Path.Combine(attemptDir, "fragment.json");
            File.WriteAllText(fragmentPath, fragmentJson);
            return _manager.MergeFragment(taskId, fragmentPath, mergeSequence: 1, attemptDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_planDir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
