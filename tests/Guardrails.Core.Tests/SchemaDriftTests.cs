using System.Text.RegularExpressions;

namespace Guardrails.Core.Tests;

/// <summary>
/// Drift guard (safety net: schemas-md-duplicate-no-drift-test). The <c>plan-breakdown</c>
/// skill's <c>references/schemas.md</c> hand-maintains a copy of the SSOT
/// (<c>docs/plans/02-schemas-and-contracts.md</c>) <c>promptRunners</c> schema block. SSOT
/// invariant #4: the schema doc is the single source of truth — skills, serializers, and
/// examples implement it and must never fork it.
///
/// This test extracts the canonical <c>promptRunners</c> region from BOTH files and asserts
/// they are content-identical. It FAILS the moment the two drift, with an actionable diff hint
/// naming which file to reconcile. Line endings are normalized before comparison (a checkout
/// artifact governed by the repo .gitattributes, not a content difference).
/// </summary>
public sealed class SchemaDriftTests
{
    /// <summary>
    /// The skill copy is wrapped in HTML sentinels so the canonical region is explicitly marked
    /// (and the test fails loudly if a future edit removes/breaks them). Within that region we
    /// extract the same <c>"promptRunners": { … }</c> JSON block as in the SSOT, making the
    /// comparison fence-agnostic.
    /// </summary>
    private const string SkillSentinelPattern =
        @"<!-- canonical-schema:promptRunners.*?-->.*?(?<block>^  ""promptRunners"": \{.*?^  \}).*?<!-- /canonical-schema:promptRunners -->";

    /// <summary>The SSOT block: from <c>^  "promptRunners": {</c> to its matching <c>^  }</c>.</summary>
    private const string SsotBlockPattern = @"(?<block>^  ""promptRunners"": \{.*?^  \})";

    [Fact]
    public void PromptRunnersSchema_SkillCopyMatchesSsot()
    {
        string skillPath = SkillSchemasPath;
        string ssotPath = SsotSchemasPath;

        Assert.True(File.Exists(skillPath), $"Skill schema file not found at {skillPath}");
        Assert.True(File.Exists(ssotPath), $"SSOT schema file not found at {ssotPath}");

        string skillText = File.ReadAllText(skillPath);
        string ssotText = File.ReadAllText(ssotPath);

        string skillBlock = ExtractBlock(skillText, SkillSentinelPattern, skillPath, "between the canonical-schema:promptRunners sentinels");
        string ssotBlock = ExtractBlock(ssotText, SsotBlockPattern, ssotPath, "the \"promptRunners\": { … } block of the §2 example");

        string skillNorm = Normalize(skillBlock);
        string ssotNorm = Normalize(ssotBlock);

        Assert.True(
            skillNorm == ssotNorm,
            "The plan-breakdown skill's promptRunners schema has DRIFTED from the SSOT.\n" +
            "Reconcile (edit the SSOT first, then copy verbatim into the skill):\n" +
            $"  SSOT : {ssotPath}\n" +
            $"  Skill: {skillPath}\n" +
            "----- SSOT (canonical) -----\n" + ssotNorm + "\n" +
            "----- Skill (copy) -----\n" + skillNorm + "\n" +
            "----- first divergence -----\n" + FirstDivergence(ssotNorm, skillNorm));
    }

    /// <summary>
    /// The canonical block is not just documentation — it is what a hand-editor or a generator COPIES, so
    /// it has to be a config that actually works. This lifts the SSOT's `promptRunners` block into a real
    /// `guardrails.json`, runs it through the loader and validator, and asserts two things:
    ///
    /// <list type="number">
    ///   <item><b>Zero diagnostics.</b> A canonical example that trips its own validator would teach every
    ///     reader a broken shape, and the model-tiering keys (#201) are exactly the kind of addition where
    ///     that happens — <c>routing</c> requires a non-empty <c>tiers</c> (GR2047), so an example carrying
    ///     a decorative empty one would be self-refuting.</item>
    ///   <item><b>Zero tiering CONFIGURED.</b> Tiering turns on for a whole plan the moment ONE block
    ///     declares <c>routing</c> (SSOT §9.6). The canonical block is therefore obliged to show the
    ///     DEFAULT — every tiering key absent/null — because a live <c>routing</c> block here would opt in
    ///     every reader who copied it, and would push <c>/plan-breakdown</c> (which mirrors this block) into
    ///     emitting tier tags for single-model users. That is DoR Invariant 7, and this is the assertion
    ///     that keeps a well-meaning "make the example richer" edit from quietly breaking it.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void CanonicalPromptRunnersBlock_ValidatesClean_AndConfiguresNoTiering()
    {
        string ssotBlock = ExtractBlock(
            File.ReadAllText(SsotSchemasPath), SsotBlockPattern, SsotSchemasPath,
            "the \"promptRunners\": { … } block of the §2 example");

        string root = Path.Combine(Path.GetTempPath(), "gr-canon-" + Guid.NewGuid().ToString("N"));
        try
        {
            string taskDir = Path.Combine(root, "tasks", "01-task");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(root, "guardrails.json"),
                "{\n  \"version\": 1,\n  \"maxParallelism\": 1,\n" + ssotBlock + "\n}\n");
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """{ "description": "t", "dependsOn": [], "writeScope": [] }""");
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "# catches: nothing\nexit 0\n");

            Loading.PlanLoadResult result = new Loading.PlanLoader().Load(root);
            Assert.NotNull(result.Plan);

            List<Loading.Diagnostic> diagnostics = [.. result.Diagnostics];
            diagnostics.AddRange(new Loading.PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!));

            // GR2015 is excluded: the fixture lives in a temp folder, which is never a git root.
            Loading.Diagnostic[] real =
            [
                .. diagnostics.Where(d => d.Code != Loading.DiagnosticCodes.WorkspaceNotGitRoot)
            ];

            Assert.True(real.Length == 0,
                "The SSOT §2 canonical promptRunners block does not validate cleanly. A canonical example " +
                "is what readers and generators copy, so it must be a working config.\nDiagnostics:\n" +
                string.Join("\n", real.Select(d => $"  {d.Severity} {d.Code}: {d.Message}")));

            Assert.DoesNotContain(result.Plan!.Config.PromptRunners.Values, r => r.Routing is not null);
            Assert.Null(result.Plan!.Config.Tiering);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static string ExtractBlock(string text, string pattern, string path, string where)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(
            match.Success,
            $"Could not locate the canonical promptRunners region in {path} ({where}). " +
            "If the file layout or the canonical-schema sentinels changed, update this test's extraction.");
        return match.Groups["block"].Value;
    }

    /// <summary>Normalize line endings so a CRLF working-tree checkout compares equal to LF.</summary>
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>A compact "first character that differs" hint for the failure message.</summary>
    private static string FirstDivergence(string a, string b)
    {
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            if (a[i] != b[i])
            {
                string context = a[..i];
                int line = context.Count(c => c == '\n') + 1;
                return $"at index {i} (~line {line}): SSOT '{Describe(a, i)}' vs skill '{Describe(b, i)}'";
            }
        }

        return a.Length == b.Length
            ? "(no character divergence — difference is whitespace/length only)"
            : $"lengths differ: SSOT {a.Length} vs skill {b.Length} chars";
    }

    private static string Describe(string s, int i) =>
        i < s.Length ? s.Substring(i, Math.Min(20, s.Length - i)).Replace("\n", "\\n") : "(end)";

    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));

    private static string SkillSchemasPath =>
        Path.Combine(RepoRoot, ".claude", "skills", "plan-breakdown", "references", "schemas.md");

    private static string SsotSchemasPath =>
        Path.Combine(RepoRoot, "docs", "plans", "02-schemas-and-contracts.md");
}
