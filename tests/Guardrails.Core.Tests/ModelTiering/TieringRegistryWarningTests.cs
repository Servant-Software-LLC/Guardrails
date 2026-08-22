using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Stage 3, wave 1 — the two REGISTRY warnings that catch a <c>guardrails.json</c> which loads cleanly
/// and then routes differently from what its author wrote (DoR <c>docs/plans/17-model-tiering.md</c>
/// §4.2/§6.2/§12.6, SSOT §9.6).
///
/// <list type="bullet">
///   <item><b>GR2051 <see cref="DiagnosticCodes.NonRoutableBlockIsDefault"/></b> — the registry
///     <c>default</c> pointer names a NON-ROUTABLE block in a tiering-configured file. Non-routable
///     spans both reservation forms §4.2 declares: the DECLARED one (<c>costly: true</c>) and the
///     INCIDENTAL one (no <c>routing</c> at all). Either way untagged work falls to legacy resolution
///     and lands on the reserved model — the reservation evaporates through the back door.</item>
///   <item><b>GR2052 <see cref="DiagnosticCodes.CostlyBlockRoutingInert"/></b> — a <c>costly: true</c>
///     block ALSO declares <c>routing</c>. §6.2's one candidacy predicate excludes costly blocks at
///     every rung, so the declaration can never be read: an eligibility registered and never
///     considered.</item>
/// </list>
///
/// <para><b>Both are WARNINGS and §12.6 says why once for both: the plan still runs.</b> That is why
/// every positive test here asserts the SEVERITY alongside the code — an implementation that reported
/// either as an error would fail plans that route correctly today, and a test matching on the code
/// alone would not notice.</para>
///
/// <para><b>Each fixture isolates ONE cause.</b> The two GR2051 forms overlap in the obvious fixture (a
/// costly block usually has no routing either), so the costly-form test gives its default block a
/// <c>routing</c> key and the routing-less-form test states <c>"costly": false</c> outright. A check
/// wired to only one form still fails exactly one of them, which is the whole point of splitting them.</para>
///
/// <para><b>Everything goes through the REAL pipeline</b> — <see cref="PlanLoader"/> then
/// <see cref="PlanValidator"/>, diagnostics pooled from both phases — matching
/// <c>TierRoutingSchemaTests</c> beside this file, because "validate reports it" is a claim about that
/// pipeline and not about a record's property initializers.</para>
/// </summary>
[Trait("Category", "ModelTieringStage3")]
public sealed class TieringRegistryWarningTests : IDisposable
{
    private readonly string _root;

    public TieringRegistryWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-mt3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ── GR2051 NonRoutableBlockIsDefault ───────────────────────────────────────────────────────

    /// <summary>
    /// The DECLARED reservation form: <c>default</c> points at a <c>costly: true</c> block. The block
    /// carries a <c>routing</c> key of its own precisely so that the INCIDENTAL form cannot be what
    /// fires this — the only non-routable thing about <c>opus</c> here is the costly flag.
    ///
    /// <para>Naming a block <c>default</c> is a human assignment, so this does not violate §6.2's costly
    /// floor (which governs what the HARNESS chooses); what earns the flag is that untagged work would
    /// then spend the reserved model SILENTLY. A warning, therefore — the config is honest, just
    /// expensive in a way nobody said out loud.</para>
    /// </summary>
    [Fact]
    public void WarnsWhenCostlyBlockIsDefault()
    {
        Loaded loaded = Load(ConfigWith("""
            "sonnet": { "command": "claude", "strength": 3,
                        "routing": { "tiers": ["easy", "medium", "hard"] } },
            "opus":   { "command": "claude", "strength": 4, "costly": true,
                        "routing": { "tiers": ["hard"] } }
            """, defaultRunner: "opus"));

        Diagnostic warning = RequireCode(loaded, DiagnosticCodes.NonRoutableBlockIsDefault);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("opus", warning.Message, StringComparison.Ordinal);

        // §12.6 — the plan still runs, so nothing here may reach ERROR.
        AssertNoErrors(loaded);
    }

    /// <summary>
    /// The INCIDENTAL reservation form: <c>default</c> points at a block with no <c>routing</c> at all,
    /// in a file where another block DOES configure tiering. <c>"costly": false</c> is stated outright so
    /// the declared form is ruled out — the block is simply outside the tier system, which has the same
    /// back-door effect and needs the same flag.
    /// </summary>
    [Fact]
    public void WarnsWhenRoutinglessBlockIsDefault()
    {
        Loaded loaded = Load(ConfigWith("""
            "sonnet": { "command": "claude", "strength": 3,
                        "routing": { "tiers": ["easy", "medium", "hard"] } },
            "legacy": { "command": "claude", "strength": 2, "costly": false }
            """, defaultRunner: "legacy"));

        Diagnostic warning = RequireCode(loaded, DiagnosticCodes.NonRoutableBlockIsDefault);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("legacy", warning.Message, StringComparison.Ordinal);

        AssertNoErrors(loaded);
    }

    // ── GR2052 CostlyBlockRoutingInert ─────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>costly: true</c> block declaring <c>routing</c>: the two keys state opposite things about the
    /// same block and <c>costly</c> wins at every rung, so the routing is INERT. Reported at the block
    /// that contradicts itself, which here is NOT the default pointer — GR2052 is about the block, and
    /// asserting GR2051 stays absent is what keeps the two checks from being wired to one condition.
    /// </summary>
    [Fact]
    public void WarnsWhenCostlyBlockDeclaresRouting()
    {
        Loaded loaded = Load(ConfigWith("""
            "sonnet": { "command": "claude", "strength": 3,
                        "routing": { "tiers": ["easy", "medium", "hard"] } },
            "opus":   { "command": "claude", "strength": 4, "costly": true,
                        "routing": { "tiers": ["hard"] } }
            """, defaultRunner: "sonnet"));

        Diagnostic warning = RequireCode(loaded, DiagnosticCodes.CostlyBlockRoutingInert);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("opus", warning.Message, StringComparison.Ordinal);

        // The default pointer is a routable block, so the OTHER registry warning has nothing to say.
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.NonRoutableBlockIsDefault);
        AssertNoErrors(loaded);
    }

    /// <summary>
    /// <b>The composition GR2052 exists as a warning to permit.</b> One config carries both faults at
    /// once: <c>opus</c> is costly-with-routing (inert), and because it is the ONLY block declaring
    /// <c>hard</c>, the task tagged <c>hard</c> has no candidate at or above it — GR2048, an ERROR.
    ///
    /// <para>Both must be reported, and at their own severities. If GR2052 were an error the run would
    /// stop at the contradiction and never state the consequence; if emitting GR2052 suppressed GR2048
    /// the operator would be told about a redundant key while the thing that actually stops the run went
    /// unmentioned. Naming a block and naming an unservable rung are different claims about different
    /// things, and both are true here.</para>
    /// </summary>
    [Fact]
    public void ComposesWithUnservableTier()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "haiku": { "command": "claude", "strength": 2, "routing": { "tiers": ["easy"] } },
                "opus":  { "command": "claude", "strength": 4, "costly": true,
                           "routing": { "tiers": ["hard"] } }
                """, defaultRunner: "haiku"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        Loaded loaded = LoadFolder(planDir);

        Diagnostic inert = RequireCode(loaded, DiagnosticCodes.CostlyBlockRoutingInert);
        Assert.Equal(DiagnosticSeverity.Warning, inert.Severity);
        Assert.Contains("opus", inert.Message, StringComparison.Ordinal);

        Diagnostic unservable = RequireCode(loaded, DiagnosticCodes.UnservableTier);
        Assert.Equal(DiagnosticSeverity.Error, unservable.Severity);
        Assert.Contains("hard", unservable.Message, StringComparison.Ordinal);
    }

    // ── Invariant 7 — the file that never asked for tiering ────────────────────────────────────

    /// <summary>
    /// <b>DoR Invariant 7.</b> A file that configures NO tiering — no <c>routing</c> on any block, no
    /// <c>tiering</c> block — sees neither warning, and the fixture is deliberately the most provocative
    /// shape there is: the sole block is <c>costly: true</c> AND routing-less AND the <c>default</c>
    /// pointer, so it satisfies BOTH of GR2051's forms and would fire on any check that forgot the
    /// tiering-configured gate. Nothing here is a mistake — <c>costly</c> is a cost annotation a
    /// single-model user may reasonably write, and it does not become a defect just because a tier
    /// system exists elsewhere.
    ///
    /// <para>The codes are asserted ABSENT BY NAME rather than by "no diagnostics at all": an unrelated
    /// warning may legitimately appear on a fixture, and a blanket emptiness check would then fail for a
    /// reason that has nothing to do with Invariant 7. The positive assertions above them exist so this
    /// silence means something — if the fixture failed to load, "no GR2051" would be true for the wrong
    /// reason and the test would be measuring nothing.</para>
    /// </summary>
    [Fact]
    public void SilentWhenTieringNotConfigured()
    {
        Loaded loaded = Load(ConfigWith("""
            "opus": { "command": "claude", "strength": 4, "costly": true }
            """, defaultRunner: "opus"));

        // The pipeline actually ran and produced the provocative shape — otherwise the silence below is
        // the silence of a fixture that never loaded.
        Assert.NotNull(loaded.Plan);
        Assert.Equal("opus", loaded.Plan!.Config.DefaultPromptRunner);
        Assert.True(loaded.Runner("opus").Costly);
        Assert.Null(loaded.Runner("opus").Routing);
        Assert.Null(loaded.Plan!.Config.Tiering);

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.NonRoutableBlockIsDefault);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CostlyBlockRoutingInert);
        AssertNoErrors(loaded);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    private const string DefaultTaskJson =
        """{ "description": "t", "dependsOn": [], "writeScope": [] }""";

    /// <summary>
    /// A whole <c>guardrails.json</c> around <paramref name="runners"/> (raw block text, comma-separated).
    /// <c>maxParallelism: 1</c> keeps every fixture SERIAL — worktree mode drags in the unrelated GR2015
    /// (temp dirs are not git roots) and GR2028 (terminal-gate obligation) errors, and these assertions
    /// turn on there being no error at all.
    /// </summary>
    private static string ConfigWith(string runners, string defaultRunner) =>
        $$"""
        {
          "version": 1,
          "maxParallelism": 1,
          "promptRunners": {
            "default": "{{defaultRunner}}",
            {{runners}}
          }
        }
        """;

    /// <summary>A minimal one-prompt-task plan folder carrying the given config (and optional task.json).</summary>
    private string PlanFolder(string configJson, string? taskJson = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), configJson);
        WriteTask(_root, "01-task", taskJson ?? DefaultTaskJson);
        return _root;
    }

    private static void WriteTask(string planDir, string id, string taskJson)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        File.WriteAllText(
            Path.Combine(taskDir, "guardrails", "01-ok.sh"),
            "# catches: nothing — a fixture stand-in so the task is not guardrail-less.\nexit 0\n");
    }

    /// <summary>A loaded plan plus every diagnostic from BOTH phases — loading and validation.</summary>
    private sealed record Loaded(PlanDefinition? Plan, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public PromptRunnerConfig Runner(string name) => Plan!.Config.PromptRunners[name];
    }

    private Loaded Load(string configJson) => LoadFolder(PlanFolder(configJson));

    private static Loaded LoadFolder(string planDir)
    {
        PlanLoadResult result = new PlanLoader().Load(planDir);
        List<Diagnostic> diagnostics = [.. result.Diagnostics];

        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan));
        }

        return new Loaded(result.Plan, diagnostics);
    }

    /// <summary>The first diagnostic carrying <paramref name="code"/>, with a readable dump when it is missing.</summary>
    private static Diagnostic RequireCode(Loaded loaded, string code)
    {
        Diagnostic[] matches = [.. loaded.Diagnostics.Where(d => d.Code == code)];
        Assert.True(matches.Length > 0, $"expected a {code} diagnostic; diagnostics were:\n{Dump(loaded)}");
        return matches[0];
    }

    /// <summary>
    /// No ERROR diagnostics. <see cref="DiagnosticCodes.WorkspaceNotGitRoot"/> is excluded because every
    /// fixture lives in a temp folder, which is never a git root.
    /// </summary>
    private static void AssertNoErrors(Loaded loaded)
    {
        Diagnostic[] errors =
        [
            .. loaded.Diagnostics.Where(d =>
                d.Severity == DiagnosticSeverity.Error && d.Code != DiagnosticCodes.WorkspaceNotGitRoot)
        ];

        Assert.True(errors.Length == 0, $"unexpected validation errors:\n{string.Join("\n", errors)}");
    }

    private static string Dump(Loaded loaded) =>
        loaded.Diagnostics.Count == 0 ? "(none)" : string.Join("\n", loaded.Diagnostics);

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
