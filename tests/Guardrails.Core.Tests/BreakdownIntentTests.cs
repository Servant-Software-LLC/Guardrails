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
    public void Read_TellsAbsentFromUnreadableFromDeclaresNothingFromUsable_TheFourStates()
    {
        _b.WaveStub(Wave);
        Assert.Equal(BreakdownIntentPresence.Absent, BreakdownIntent.Read(WaveDir).Presence);

        WriteManifest("{ this is not json");
        Assert.Equal(BreakdownIntentPresence.Unreadable, BreakdownIntent.Read(WaveDir).Presence);

        WriteManifest("""{ "version": 1, "tasks": [ { "folder": "nested/01-compile" } ] }""");
        Assert.Equal(BreakdownIntentPresence.NoUsableEntries, BreakdownIntent.Read(WaveDir).Presence);

        Declare("01-compile");
        Assert.Equal(BreakdownIntentPresence.Usable, BreakdownIntent.Read(WaveDir).Presence);
    }

    [Fact]
    public void APresentButUnusableManifest_IsStillNullFromTryRead_ButNoLongerIndistinguishableFromAbsent()
    {
        // The bug: BOTH read as null, so a typo cost the salvage with no way to say so. TryRead stays lossy
        // ON PURPOSE (its callers want a declaration or nothing); Read is what makes the two tellable apart.
        _b.WaveStub(Wave);
        WriteManifest("""{ "version": 1, "tasks": [ { "folder": "  " } ] }""");

        Assert.Null(BreakdownIntent.TryRead(WaveDir));
        BreakdownIntentRead read = BreakdownIntent.Read(WaveDir);
        Assert.Equal(BreakdownIntentPresence.NoUsableEntries, read.Presence);
        Assert.True(read.IsPresent);
        Assert.Null(read.Usable);
        Assert.Equal(BreakdownIntent.PathFor(WaveDir), read.Path);
    }

    [Fact]
    public void AManifestWithNoTasksEntries_ReadsAsDeclaresNothing_WithNoRejectedEntriesToList()
    {
        _b.WaveStub(Wave);
        WriteManifest("""{ "version": 1, "tasks": [] }""");

        BreakdownIntentRead read = BreakdownIntent.Read(WaveDir);
        Assert.Equal(BreakdownIntentPresence.NoUsableEntries, read.Presence);
        Assert.Empty(read.RejectedEntries);
        Assert.Contains("no 'tasks' entries", read.Explanation);
    }

    [Fact]
    public void AManifestWhoseContentIsJsonNull_ReadsAsDeclaresNothing_NotAsAbsent()
    {
        _b.WaveStub(Wave);
        WriteManifest("null");

        BreakdownIntentRead read = BreakdownIntent.Read(WaveDir);
        Assert.Equal(BreakdownIntentPresence.NoUsableEntries, read.Presence);
        Assert.Contains("'null'", read.Explanation);
    }

    [Fact]
    public void RejectedEntries_NameThePositionTheValueAndTheReason_ForEachOfTheThreeDropRules()
    {
        _b.WaveStub(Wave);
        WriteManifest("""
            {
              "tasks": [
                { "folder": "" },
                { "folder": "nested/02-package" },
                { "folder": "03-publish" },
                { "folder": "03-publish" }
              ]
            }
            """);

        BreakdownIntent intent = Assert.IsType<BreakdownIntent>(BreakdownIntent.TryRead(WaveDir));
        Assert.Equal(new[] { "03-publish" }, intent.DeclaredFolders());

        IReadOnlyList<string> rejected = intent.RejectedEntries();
        Assert.Equal(3, rejected.Count);
        Assert.Contains("entry 1", rejected[0]);
        Assert.Contains("missing or blank", rejected[0]);
        Assert.Contains("nested/02-package", rejected[1]);
        Assert.Contains("path separator", rejected[1]);
        Assert.Contains("entry 4", rejected[2]);
        Assert.Contains("repeats an earlier entry", rejected[2]);
    }

    [Fact]
    public void VersionAndDeclaredAt_AreOptional_AndCommentsAndTrailingCommasAreAccepted()
    {
        // The documented shape is `{ version, declaredAt, tasks }`, but only tasks[].folder is load-bearing:
        // refusing a manifest over a missing timestamp would cost the wave the salvage the manifest exists for.
        _b.WaveStub(Wave);
        WriteManifest("""
            {
              // no version, no declaredAt, and a trailing comma
              "tasks": [
                { "folder": "01-compile" },
              ],
            }
            """);

        BreakdownIntent intent = Assert.IsType<BreakdownIntent>(BreakdownIntent.TryRead(WaveDir));
        Assert.Equal(BreakdownIntent.CurrentVersion, intent.Version);
        Assert.Null(intent.DeclaredAt);
        Assert.Equal(new[] { "01-compile" }, intent.DeclaredFolders());
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

    // --- GR2064: present-but-unusable is DISTINGUISHABLE from absent, and says so -----------------

    [Fact]
    public void Gr2064_FiresOnAManifestThatParsesButDeclaresNothingUsable_NamingThePathAndEveryRejection()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");
        WriteManifest("""
            {
              "version": 1,
              "tasks": [
                { "folder": "tasks/01-compile" },
                { "folder": "" }
              ]
            }
            """);

        Diagnostic d = Assert.Single(Validate(), x => x.Code == DiagnosticCodes.BreakdownIntentDeclaresNothing);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(BreakdownIntent.PathFor(WaveDir), d.Path);
        Assert.Contains("tasks/01-compile", d.Message);
        Assert.Contains("path separator", d.Message);
        Assert.Contains("missing or blank", d.Message);
        // The whole point: it names WHAT was silently lost, and the remedy is both-ways.
        Assert.Contains("salvage", d.Message);
        Assert.Contains("delete", d.Message);
    }

    [Fact]
    public void Gr2064_AndGr2063_AreMutuallyExclusive_TheFourthCaseIsNotASecondShortfall()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        WriteManifest("""{ "tasks": [ { "folder": "nested/01-compile" } ] }""");

        IReadOnlyList<Diagnostic> diagnostics = Validate();
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.BreakdownIntentDeclaresNothing);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.WaveBreakdownIncomplete);
    }

    [Theory]
    [InlineData(null)]                                    // absent
    [InlineData("{ this is not json")]                    // unparseable — a DELIBERATE, documented silence
    public void Gr2064_IsSilentForTheTwoStatesTheSsotKeepsSilent(string? manifest)
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");
        if (manifest is not null)
        {
            WriteManifest(manifest);
        }

        Assert.DoesNotContain(Validate(), d => d.Code == DiagnosticCodes.BreakdownIntentDeclaresNothing);
    }

    [Fact]
    public void Gr2064_IsSilentOnAUsableManifest_EvenAnUnsatisfiedOne()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub(Wave);
        AuthorComplete("01-compile");
        Declare("01-compile", "02-package");

        IReadOnlyList<Diagnostic> diagnostics = Validate();
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.BreakdownIntentDeclaresNothing);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.WaveBreakdownIncomplete);
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
