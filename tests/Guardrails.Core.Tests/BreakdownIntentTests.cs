using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// The breakdown INTENT manifest (<c>&lt;wave&gt;/state/breakdown-intent.json</c>, SSOT §14.11) and the GR2063
/// shortfall warning it makes decidable (issue #402). The manifest exists because a truncated breakdown's
/// DEBT is not computable from its prefix — the measured manual recovery read the same artifacts and
/// concluded 13 tasks when the real number was 14 — so the debt is a set-compare against a DECLARED list,
/// never an inference from prose or from forward references in the authored gates.
/// </summary>
public sealed class BreakdownIntentTests : IDisposable
{
    private const string Wave = "wave-02-build";

    private readonly WavePlanBuilder _b = new();

    public void Dispose() => _b.Dispose();

    private string WaveDir => Path.Combine(_b.PlanDir, Wave);

    private void WriteManifest(string json)
    {
        Directory.CreateDirectory(Path.Combine(WaveDir, "state"));
        File.WriteAllText(BreakdownIntent.PathFor(WaveDir), json);
    }

    private void Declare(params string[] folders) =>
        WriteManifest($$"""
            {
              "version": 1,
              "declaredAt": "2026-08-20T05:00:00Z",
              "tasks": [
                {{string.Join(",\n    ", folders.Select(f => $$"""{ "folder": "{{f}}" }"""))}}
              ]
            }
            """);

    private void AuthorComplete(string folder)
    {
        string dir = Path.Combine(WaveDir, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(dir, "guardrails"));
        File.WriteAllText(Path.Combine(dir, "task.json"), """{ "description": "x", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(dir, "action.sh"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(dir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    // --- reading ------------------------------------------------------------------------------

    [Fact]
    public void AbsentManifest_ReadsAsNull_SoTheCheckIsSkippedEntirely()
    {
        _b.WaveStub(Wave);
        Assert.Null(BreakdownIntent.TryRead(WaveDir));
    }

    [Fact]
    public void UnparseableManifest_ReadsAsNull_NeverThrows_NeverAnInferredZero()
    {
        _b.WaveStub(Wave);
        WriteManifest("{ this is not json");
        Assert.Null(BreakdownIntent.TryRead(WaveDir));
    }

    [Fact]
    public void DeclaredFolders_AreTrimmed_Deduplicated_AndPathBearingEntriesDropped()
    {
        _b.WaveStub(Wave);
        WriteManifest("""
            {
              "version": 1,
              "tasks": [
                { "folder": "  01-compile  " },
                { "folder": "01-compile" },
                { "folder": "" },
                { "folder": "nested/02-package" },
                { "folder": "03-publish" }
              ]
            }
            """);

        BreakdownIntent intent = Assert.IsType<BreakdownIntent>(BreakdownIntent.TryRead(WaveDir));
        Assert.Equal(new[] { "01-compile", "03-publish" }, intent.DeclaredFolders());
    }

    // --- the completeness predicate the sweep and the shortfall both key on --------------------

    [Fact]
    public void ATaskFolderWithoutAnActionFile_IsIncomplete_TheExact385Artifact()
    {
        _b.WaveStub(Wave);
        string dir = Path.Combine(WaveDir, "tasks", "12-half-written");
        Directory.CreateDirectory(Path.Combine(dir, "guardrails"));
        File.WriteAllText(Path.Combine(dir, "task.json"), """{ "description": "x", "writeScope": [] }""");

        Assert.False(BreakdownIntent.IsCompleteTaskFolder(dir));
    }

    [Fact]
    public void ATaskFolderWithAnExplicitActionPath_IsComplete_EvenWithNoConventionActionFile()
    {
        _b.WaveStub(Wave);
        string dir = Path.Combine(WaveDir, "tasks", "05-explicit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            """{ "description": "x", "writeScope": [], "action": { "path": "scripts/build.sh" } }""");

        Assert.True(BreakdownIntent.IsCompleteTaskFolder(dir));
    }

    // --- GR2063 -------------------------------------------------------------------------------

    [Fact]
    public void Gr2063_NamesTheDeclaredFoldersWithNoCompleteTaskFolder()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");
        Declare("01-compile", "02-package", "03-publish");

        Diagnostic d = Assert.Single(Validate(), x => x.Code == DiagnosticCodes.WaveBreakdownIncomplete);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("declared 3 task(s)", d.Message);
        Assert.Contains("02-package", d.Message);
        Assert.Contains("03-publish", d.Message);
        Assert.DoesNotContain("01-compile,", d.Message);
        // The remedy is named, and it is "record the intent that actually holds" — not "delete the lint".
        Assert.Contains("correct or delete", d.Message);
    }

    [Fact]
    public void Gr2063_IsSilentWhenTheManifestIsSatisfied()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");
        AuthorComplete("02-package");
        Declare("01-compile", "02-package");

        Assert.DoesNotContain(Validate(), d => d.Code == DiagnosticCodes.WaveBreakdownIncomplete);
    }

    [Fact]
    public void Gr2063_IsSilentWhenThereIsNoManifest_SilenceIsNotProofOfValidity()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");

        Assert.DoesNotContain(Validate(), d => d.Code == DiagnosticCodes.WaveBreakdownIncomplete);
    }

    [Fact]
    public void Gr2063_CountsAHalfWrittenTaskFolderAsMissing_NotAsPresent()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");

        // A folder that EXISTS but has no action file is the #385 artifact; it must read as owed.
        string half = Path.Combine(WaveDir, "tasks", "02-package");
        Directory.CreateDirectory(half);
        File.WriteAllText(Path.Combine(half, "task.json"), """{ "description": "x", "writeScope": [] }""");
        Declare("01-compile", "02-package");

        Diagnostic d = Assert.Single(Validate(), x => x.Code == DiagnosticCodes.WaveBreakdownIncomplete);
        Assert.Contains("02-package", d.Message);
    }

    [Fact]
    public void Fresh_ClearsTheManifest_ButNotTheWavesReviewMarker()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");
        Declare("01-compile", "02-package");

        // The review marker beside it is a COMMITTED plan artifact and must survive a fresh slate; the
        // manifest is per-attempt runtime state and must not, or --fresh would resume a half-authored wave.
        string marker = Path.Combine(WaveDir, "state", "guardrails-review.json");
        File.WriteAllText(marker, """{ "version": 2 }""");

        State.RunReset.Fresh(_b.PlanDir);

        Assert.False(File.Exists(BreakdownIntent.PathFor(WaveDir)));
        Assert.True(File.Exists(marker));
    }

    private IReadOnlyList<Diagnostic> Validate()
    {
        PlanLoadResult result = _b.Load();
        Assert.NotNull(result.Plan);
        return new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);
    }
}
