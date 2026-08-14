using System.Text.Json;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// DoR Invariant 7, mechanism TWO of two: the NEGATIVE ASSERTIONS.
///
/// <para>Where <see cref="NoRoutingGoldenTests"/> seals the bytes and so catches drift nobody
/// enumerated, these say plainly — in the words of SKILL.md Step 4c.1 and the charter's gate warning —
/// what must never appear when tiering is not configured:</para>
/// <list type="number">
///   <item><b>no <c>action.tier</c></b> in any <c>task.json</c> — not <c>"tier": "medium"</c>, and not
///     <c>"tier": null</c> either, because a null key is still a byte that was not there before;</item>
///   <item><b>no <c>tiering</c> block</b> in <c>guardrails.json</c>;</item>
///   <item><b>no classification report line</b> — <i>including</i> a well-meant "tiering: not
///     configured" note, which is itself one. Step 4c.1 calls that out as the trap: the reflex this
///     skill trains everywhere else — surface every decision, never a silent default — is inverted
///     here deliberately, because a plan with no routing config never opted into tiering and there is
///     no decision to surface. Silence is the specification.</item>
/// </list>
///
/// <para><b>What they run over.</b> The committed golden's <c>expected/</c> — a real breakdown output
/// against a real no-<c>routing</c> config — plus, as independent and non-circular evidence, every
/// committed plan folder under <c>examples/</c> whose own config is likewise unconfigured. The
/// examples were emitted by the skill, live in the repo, and are regenerated when it changes: a gate
/// that leaks would show up there without anyone having to remember this fixture. When
/// <c>GUARDRAILS_FRESH_BREAKDOWN_DIR</c> is set, the same scan runs over a live breakdown too.</para>
///
/// <para>Each assertion is made twice on purpose: STRUCTURALLY, by walking the parsed JSON for a key
/// of that name anywhere in the document, and TEXTUALLY, by sweeping every byte of the folder for the
/// substring. The structural form is precise about the contract; the textual form catches the same
/// leak spelled somewhere the structural form does not look — a frontmatter key, a comment, a report
/// column, the diagram.</para>
/// </summary>
[Trait("Category", "ModelTieringStage1")]
public sealed class NoRoutingNegativeAssertionTests
{
    /// <summary>
    /// (1) No <c>action.tier</c>. Structural, and deliberately recursive: the contract is "no
    /// <c>tier</c> key", not "no <c>tier</c> key directly under <c>action</c>" — a tier smuggled in at
    /// the task root or inside a nested block is the same leak.
    /// </summary>
    [Fact]
    public void NoRoutingBreakdown_EmitsNoActionTierInAnyTaskManifest()
    {
        IReadOnlyList<string> manifests = NoRoutingGolden.TaskManifests(NoRoutingGolden.GoldenPlanDir);
        Assert.NotEmpty(manifests);

        foreach (string manifest in manifests)
        {
            using JsonDocument task = NoRoutingGolden.ParseJson(manifest);
            IReadOnlyList<string> tiers = NoRoutingGolden.FindPropertyPaths(task.RootElement, "tier");

            Assert.True(tiers.Count == 0,
                $"{Rel(manifest)} declares a tier at {string.Join(", ", tiers)}. A breakdown against a " +
                "no-routing config must emit no action.tier at all — not even \"tier\": null.");

            // The textual half: a tier commented out, or spelled in a key the walk above does not treat
            // as a property, is still a byte that was not in the pre-#225 manifest.
            Assert.DoesNotContain("tier", NoRoutingGolden.ReadNormalized(manifest), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// (2) No <c>tiering</c> block. Checked on every <c>guardrails.json</c> in the emitted folder, and
    /// alongside it that no <c>routing</c> block appeared either — a breakdown that invented the config
    /// which would open its own gate is the same failure arriving one step earlier.
    /// </summary>
    [Fact]
    public void NoRoutingBreakdown_EmitsNoTieringBlockInAnyRunConfig()
    {
        IReadOnlyList<string> configs = NoRoutingGolden.RunConfigs(NoRoutingGolden.GoldenPlanDir);
        Assert.NotEmpty(configs);

        foreach (string configPath in configs)
        {
            using JsonDocument config = NoRoutingGolden.ParseJson(configPath);

            IReadOnlyList<string> tiering = NoRoutingGolden.FindPropertyPaths(config.RootElement, "tiering");
            Assert.True(tiering.Count == 0,
                $"{Rel(configPath)} declares a tiering block at {string.Join(", ", tiering)}. The " +
                "plan-wide default is emitted ONLY when tiering is configured.");

            IReadOnlyList<string> routing = NoRoutingGolden.FindPropertyPaths(config.RootElement, "routing");
            Assert.True(routing.Count == 0,
                $"{Rel(configPath)} declares a routing block at {string.Join(", ", routing)}. The " +
                "breakdown authored the very metadata that would open its own gate.");

            // The textual half — catches a tiering block left in a jsonc comment, which the parse skips.
            Assert.DoesNotContain("tiering", NoRoutingGolden.ReadNormalized(configPath), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// (3) No classification report line. The captured Step 7 report must not classify, tag, mention
    /// tiering, carry an <c>(n/a)</c> tier column, or offer tiering as a suggestion — and must not
    /// carry the "tiering: not configured" note either, which the <c>tier</c> marker catches for the
    /// reason Step 4c.1 gives: that note IS a classification report line.
    /// </summary>
    [Fact]
    public void NoRoutingBreakdown_EmitsNoClassificationReportLine()
    {
        Assert.True(File.Exists(NoRoutingGolden.ReportPath),
            $"{NoRoutingGolden.ReportPath} is missing — the report is where a classification LINE lands, " +
            "so without it this assertion has nothing to be negative about.");

        IReadOnlyList<NoRoutingGolden.TierLeak> leaks =
            NoRoutingGolden.ScanForTierArtefacts(Path.GetDirectoryName(NoRoutingGolden.ReportPath)!)
                .Where(l => l.RelativePath.Equals("breakdown-report.md", StringComparison.Ordinal))
                .ToList();

        Assert.True(leaks.Count == 0,
            "The breakdown report carries classification lines it must not:" + Environment.NewLine +
            NoRoutingGolden.Describe(leaks));

        // Said the blunt way as well, because this is the one the skill calls a trap: the report must
        // not mention tiering AT ALL, and "tiering: not configured" is a mention.
        string report = NoRoutingGolden.ReadNormalized(NoRoutingGolden.ReportPath);
        Assert.DoesNotContain("tier", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("classif", report, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The blunt sweep: ZERO tier bytes anywhere under <c>expected/</c> — Step 7.0e's self-review
    /// obligation, executable. This is what catches a leak in a shape the two structural assertions
    /// above never look at: a <c>tier:</c> prompt-frontmatter key (which 4c.2 explicitly forbids
    /// inventing), a comment, a diagram label, a guardrail script's message.
    /// </summary>
    [Fact]
    public void NoRoutingBreakdown_CarriesZeroTierBytesAnywhere()
    {
        IReadOnlyList<NoRoutingGolden.TierLeak> leaks =
            NoRoutingGolden.ScanForTierArtefacts(NoRoutingGolden.ExpectedDir);

        Assert.True(leaks.Count == 0,
            "A breakdown against a no-routing config must contain ZERO tier bytes; found:" +
            Environment.NewLine + NoRoutingGolden.Describe(leaks));
    }

    /// <summary>
    /// Independent, non-circular evidence: the plan folders committed under <c>examples/</c> are real
    /// <c>/plan-breakdown</c> output, they ship with the tool, and they are regenerated when the skill
    /// changes — so a leaking gate surfaces there whether or not anyone remembers this fixture.
    ///
    /// <para>Self-adjusting on purpose: a folder is swept only if ITS OWN config is unconfigured (no
    /// <c>routing</c>, no <c>tiering</c>). An example that legitimately opts into tiering later is
    /// excluded from the sweep rather than failing it.</para>
    /// </summary>
    [Fact]
    public void CommittedNoRoutingPlanFolders_CarryNoTierArtefacts()
    {
        string examples = Path.Combine(NoRoutingGolden.RepoRoot, "examples");
        IReadOnlyList<string> planFolders = NoRoutingGolden.PlanFoldersUnder(examples);

        Assert.True(planFolders.Count >= 2,
            $"expected at least two committed plan folders under {examples}; found {planFolders.Count}. " +
            "A sweep over nothing passes without proving anything.");

        int swept = 0;
        List<NoRoutingGolden.TierLeak> leaks = [];
        foreach (string planFolder in planFolders)
        {
            if (!NoRoutingGolden.IsUnconfiguredForTiering(Path.Combine(planFolder, "guardrails.json")))
            {
                continue; // legitimately opted in — tiers are allowed there.
            }

            swept++;
            leaks.AddRange(NoRoutingGolden.ScanForTierArtefacts(planFolder)
                .Select(l => l with { RelativePath = $"{Path.GetFileName(planFolder)}/{l.RelativePath}" }));
        }

        Assert.True(swept >= 2,
            $"only {swept} of {planFolders.Count} committed example plan folders are unconfigured for " +
            "tiering — if they all opted in, this sweep no longer witnesses the single-model default.");

        Assert.True(leaks.Count == 0,
            "Committed example plan folders built against no-routing configs carry tier artefacts:" +
            Environment.NewLine + NoRoutingGolden.Describe(leaks));
    }

    /// <summary>
    /// The live half — skipped unless <c>GUARDRAILS_FRESH_BREAKDOWN_DIR</c> names the output of a real
    /// <c>/plan-breakdown</c> over the fixture's <c>input/</c>. Unlike its sibling in
    /// <see cref="NoRoutingGoldenTests"/>, this one IS a hard statement about a live run: a model may
    /// legitimately word a report differently from the golden, but it may not emit a tier at all.
    /// </summary>
    [Fact]
    public void FreshBreakdown_CarriesNoTierArtefacts()
    {
        string? fresh = NoRoutingGolden.FreshBreakdownDir;
        Assert.SkipUnless(fresh is not null,
            $"Set {NoRoutingGolden.FreshBreakdownDirVariable} to a folder produced by a real " +
            "/plan-breakdown over the fixture's input/ to assert the gate held on a live run.");
        Assert.True(Directory.Exists(fresh),
            $"{NoRoutingGolden.FreshBreakdownDirVariable} points at '{fresh}', which does not exist.");

        IReadOnlyList<NoRoutingGolden.TierLeak> leaks = NoRoutingGolden.ScanForTierArtefacts(fresh!);

        Assert.True(leaks.Count == 0,
            "A LIVE breakdown against the no-routing config emitted tier artefacts — the gate leaked:" +
            Environment.NewLine + NoRoutingGolden.Describe(leaks));
    }

    private static string Rel(string absolute) =>
        Path.GetRelativePath(NoRoutingGolden.FixtureRoot, absolute).Replace('\\', '/');
}
