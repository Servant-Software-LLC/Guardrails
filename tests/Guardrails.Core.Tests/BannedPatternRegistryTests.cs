using System.Text.RegularExpressions;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// The banned-guardrail-pattern registry (SSOT §4.6, issue #346, GR2037): the data-driven lint that
/// mechanically rejects a generated guardrail SCRIPT containing a known-bad regex construction so a
/// fixed-spelling catalogue lesson (a fresh LLM generation) cannot silently regress. Two halves:
/// <list type="bullet">
///   <item>the <b>meta-test</b> — the maintainer's quality bar: every seed entry's <c>badPattern</c> is a
///     valid regex, matches ALL its inline <c>mustMatch</c> fixtures, and matches NONE of its
///     <c>mustNotMatch</c> fixtures, so a malformed entry cannot ship; and</item>
///   <item>the <b>scan</b> — <c>PlanValidator</c> emits one GR2037 per (four-folder script guardrail,
///     matching entry), after comment-stripping (the #97 lesson), citing the entry id/reason/hint.</item>
/// </list>
/// </summary>
public sealed class BannedPatternRegistryTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("gr2037-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // ============================================================================================
    // Meta-test — the registry's own quality bar (a malformed entry cannot land).
    // ============================================================================================

    [Fact]
    public void EmbeddedRegistry_LoadsAndPreCompiles()
    {
        // Load() deserializes the embedded default AND pre-compiles every badPattern, so an invalid
        // regex or a missing required field is a loud fault here — not a silent mid-scan surprise.
        BannedPatternRegistry registry = BannedPatternRegistry.Load();
        Assert.NotEmpty(registry.Patterns);
    }

    [Fact]
    public void EverySeedEntry_BadPatternMatchesAllMustMatch_AndNoMustNotMatch()
    {
        BannedPatternRegistry registry = BannedPatternRegistry.Load();

        foreach (BannedPattern pattern in registry.Patterns)
        {
            Regex matcher = pattern.Matcher; // a valid regex (throws here if not — Load pre-compiles)

            Assert.NotEmpty(pattern.MustMatch);    // fixtures are the quality bar — they must exist
            Assert.NotEmpty(pattern.MustNotMatch);

            foreach (string fixture in pattern.MustMatch)
            {
                Assert.True(matcher.IsMatch(fixture),
                    $"entry '{pattern.Id}' badPattern must MATCH its mustMatch fixture but did not:\n{fixture}");
            }

            foreach (string fixture in pattern.MustNotMatch)
            {
                Assert.False(matcher.IsMatch(fixture),
                    $"entry '{pattern.Id}' badPattern must NOT match its mustNotMatch fixture but did:\n{fixture}");
            }
        }
    }

    [Fact]
    public void SeedSet_IsExactlyTheHonestCut_73_And_187a()
    {
        // The user-approved honest cut: exactly two entries, #73 (hollow assertion) and #187a
        // (unanchored/bare-======= conflict marker). #175/#97/#98/#112 are deliberately EXCLUDED
        // (wrong polarity / structural / FP-prone — see docs/plans/15-guardrail-script-lint.md §B.6).
        BannedPatternRegistry registry = BannedPatternRegistry.Load();

        string[] ids = registry.Patterns.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "#187a", "#73" }, ids);
    }

    // ============================================================================================
    // Scan — GR2037 fires on the shipped seed patterns, in each of the four folders.
    // ============================================================================================

    [Fact]
    public void UnanchoredConflictMarker_FiresGr2037_Citing187a()
    {
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            """
            $content = Get-Content -Raw $file
            if ($content -match '<<<<<<<' -or $content -match '>>>>>>>') { Write-Output 'conflict'; exit 1 }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#187a", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongParenAnchorForm_FiresGr2037_Citing187a()
    {
        // The exact #346-incident spelling: (^|[[:space:]]) accepts a whitespace-preceded (non-start)
        // match, so a mid-line illustrative marker false-fails. Banned.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            """
            grep -Eq '(^|[[:space:]])(<<<<<<<|>>>>>>>)' "$rel" && exit 1
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#187a", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetextUnderlineOrBannerCheck_IsClean_NoGr2037()
    {
        // The deferred '={7}' term is correctly ABSENT from #187a (review BLOCKER): a legitimate
        // markdown-setext-underline / banner check that greps for a bare '=======' must NOT be
        // rejected — banning it added no coverage of the actual #346 incident and was pure FP surface.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-setext",
            """
            $doc = Get-Content -Raw $file
            if ($doc -notmatch '(?m)^=======') { Write-Output 'missing setext underline'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void AnchoredConflictMarker_IsClean_NoGr2037()
    {
        // The GOOD line-anchored form (the #187 doctrine, matching examples/parallel-hello) — the
        // ours/theirs tokens are immediately preceded by '^', so the unanchored ban does not fire.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            """
            $content = Get-Content -Raw $file
            if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') { exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void HollowAssertion_FiresGr2037_Citing73()
    {
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-moved-count",
            """
            $src = Get-Content -Raw $test
            if ($src -notmatch 'Assert.*\([^)]*(Moved|Written|Count|Entities)') { Write-Output 'no assertion'; exit 1 }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#73", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveValueAssertion_IsClean_NoGr2037()
    {
        // The GOOD form: require a STRICTLY-POSITIVE value — a legitimate Count>0 / NotEmpty check must
        // not be mistaken for the hollow keyword-presence construction.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-moved-count",
            """
            $src = Get-Content -Raw $test
            if ($src -notmatch '(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)') { exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void HollowShapeButRequiresPositivity_IsClean_NoGr2037()
    {
        // The review's #73 WEAK FP: an Assert-on-quantity construct that ALSO requires positivity
        // ('.*>\s*0' inside the SAME quoted regex) IS sufficient, not hollow — the trailing negative
        // lookahead keeps it clean.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-moved-positive",
            """
            $src = Get-Content -Raw $test
            if ($src -notmatch 'Assert.*(Moved|Written|Count|Entities).*>\s*0') { exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void BannedConstructionOnlyInComment_IsClean_NoGr2037()
    {
        // Comment-strip discipline (the #97 lesson, itself the reason to strip first): a `catches:`
        // header that merely DESCRIBES the banned constructions must not false-fire.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            """
            # catches: a hollow assertion Assert.*\([^)]*(Moved|Written|Count|Entities) that passes on
            #          zero, and an unanchored <<<<<<< / ======= conflict-marker scan (#73 / #187a).
            $count = 5
            if ($count -le 0) { Write-Output 'nothing produced'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void OneGuardrailMatchingBothSeeds_EmitsTwoGr2037_OnePerEntry()
    {
        // "One GR2037 per match" — a body carrying BOTH banned constructions yields one diagnostic per
        // matching entry (#73 and #187a).
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-both",
            """
            $src = Get-Content -Raw $test
            if ($src -match 'Assert.*\([^)]*(Moved|Written|Count|Entities)') { exit 0 }
            if ($src -match '<<<<<<<') { exit 1 }
            exit 0
            """);

        IReadOnlyList<Diagnostic> diagnostics = ValidateEmbedded(PlanWithTaskGuardrail(guardrail));

        List<Diagnostic> gr2037 = diagnostics.Where(d => d.Code == DiagnosticCodes.BannedGuardrailPattern).ToList();
        Assert.Equal(2, gr2037.Count);
        Assert.Contains(gr2037, d => d.Message.Contains("#73", StringComparison.Ordinal));
        Assert.Contains(gr2037, d => d.Message.Contains("#187a", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptGuardrail_IsNotScanned_NoGr2037()
    {
        // Prompt guardrails are prose, not a regex construction — out of scope for the scan even if the
        // prose happens to contain a banned token.
        GuardrailDefinition prompt = WriteScript("tasks/01-a/guardrails", "01-judge",
            "The output must contain no <<<<<<< conflict markers.");
        prompt = prompt with { Kind = ActionKind.Prompt };

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(prompt)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ---- per-folder coverage: the scan reaches every four-folder script slot -------------------

    [Fact]
    public void TaskPreflight_IsScanned_FiresGr2037()
    {
        GuardrailDefinition preflight = WriteScript("tasks/01-a/preflights", "01-dep",
            "if ($content -match '<<<<<<<') { exit 1 }");
        PlanDefinition plan = BasePlan() with
        {
            Tasks = [SimpleTask("01-a", preflights: [preflight])],
        };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    [Fact]
    public void PlanLevelPreflight_IsScanned_FiresGr2037()
    {
        GuardrailDefinition preflight = WriteScript("preflights", "01-baseline",
            "if ($content -match '<<<<<<<') { exit 1 }");
        PlanDefinition plan = BasePlan() with
        {
            Tasks = [SimpleTask("01-a")],
            PlanPreflights = [preflight],
        };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    [Fact]
    public void PlanLevelGuardrail_IsScanned_FiresGr2037()
    {
        GuardrailDefinition planGuardrail = WriteScript("guardrails", "01-terminal",
            "if ($content -match '<<<<<<<') { exit 1 }");
        PlanDefinition plan = BasePlan() with
        {
            Tasks = [SimpleTask("01-a")],
            PlanGuardrails = [planGuardrail],
        };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    [Fact]
    public void WaveLevelGuardrail_IsScanned_FiresGr2037()
    {
        GuardrailDefinition waveGuardrail = WriteScript("wave-01-x/guardrails", "01-exit",
            "if ($content -match '<<<<<<<') { exit 1 }");
        TaskNode waveTask = SimpleTask("wave-01-x/01-a") with { WaveDir = "wave-01-x" };
        var wave = new WaveNode
        {
            Dir = "wave-01-x",
            Number = 1,
            Slug = "x",
            Directory = Path.Combine(_tempRoot, "wave-01-x"),
            Tasks = [waveTask],
            Guardrails = [waveGuardrail],
        };
        PlanDefinition plan = BasePlan() with { Tasks = [waveTask], Waves = [wave] };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    // ============================================================================================
    // Injection seam (DIP) — a synthetic registry drives the scan; an empty one disables it.
    // ============================================================================================

    [Fact]
    public void InjectedSyntheticRegistry_DrivesTheScan()
    {
        var synthetic = new BannedPatternRegistry(
        [
            new BannedPattern
            {
                Id = "#synthetic",
                BadPattern = "FORBIDDEN_TOKEN",
                Reason = "a synthetic banned construction for the injection test.",
                GoodPatternHint = "use ALLOWED_TOKEN.",
            },
        ]);

        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            "if ($x -match 'FORBIDDEN_TOKEN') { exit 1 }");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All, synthetic).Validate(PlanWithTaskGuardrail(guardrail));

        Diagnostic diagnostic = AssertSingleGr2037(diagnostics);
        Assert.Contains("#synthetic", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRegistry_EmitsNoGr2037()
    {
        var empty = new BannedPatternRegistry([]);

        // A body that WOULD trip the seed patterns is clean under an empty registry.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            "if ($content -match '<<<<<<<') { exit 1 }");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All, empty).Validate(PlanWithTaskGuardrail(guardrail));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    private GuardrailDefinition WriteScript(string relFolder, string name, string body)
    {
        string dir = Path.Combine(_tempRoot, relFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".ps1");
        File.WriteAllText(path, body);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    /// <summary>A task with one clean, always-passing script guardrail (so GR2003 never fires on it).</summary>
    private GuardrailDefinition CleanGuardrail(string taskId) =>
        WriteScript($"tasks/{taskId}/guardrails", "00-ok", "exit 0");

    private TaskNode SimpleTask(string id, IReadOnlyList<GuardrailDefinition>? preflights = null) => new()
    {
        Id = id,
        Directory = Path.Combine(_tempRoot, "tasks", id),
        Description = $"task {id}",
        Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", id, "action.ps1"), Kind = ActionKind.Script },
        Guardrails = [CleanGuardrail(id)],
        Preflights = preflights ?? [],
    };

    private PlanDefinition BasePlan() => new()
    {
        PlanDirectory = _tempRoot,
        Workspace = _tempRoot,
        // Serial (maxParallelism 1) so the git-root (GR2015) / terminal-gate (GR2028) worktree-mode
        // checks stay silent — the GR2037 scan is the only rule under test.
        Config = new RunConfig { Version = 1, MaxParallelism = 1 },
        Tasks = [],
        PlanPreflights = [],
        PlanGuardrails = [],
    };

    /// <summary>A single-task plan whose one task carries <paramref name="guardrail"/> as its only guardrail.</summary>
    private PlanDefinition PlanWithTaskGuardrail(GuardrailDefinition guardrail)
    {
        TaskNode task = new()
        {
            Id = "01-a",
            Directory = Path.Combine(_tempRoot, "tasks", "01-a"),
            Description = "task 01-a",
            Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", "01-a", "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [guardrail],
            Preflights = [],
        };
        return BasePlan() with { Tasks = [task] };
    }

    private static IReadOnlyList<Diagnostic> ValidateEmbedded(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    private static Diagnostic AssertSingleGr2037(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        return diagnostic;
    }
}
