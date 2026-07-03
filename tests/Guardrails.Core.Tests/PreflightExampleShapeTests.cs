using System.Text.Json;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Regression guard (audit finding D10) over the re-authored worked example
/// <c>docs/plans/09-preflight-first-class/example/example-plan</c>. The example is the human-facing
/// illustration of the two-scope, four-folder preflights model, and it is correct today but was
/// UNGUARDED — a future edit could silently reintroduce a retired shape (the <c>integrationGate</c>
/// task kind, a no-op ROOT/END sink task, a <c>scope:"precondition"</c> marker) and no test would notice.
///
/// <para>
/// These assertions pin the SHAPE the design retired (09-preflight-first-class, preflights-impl):
/// <list type="bullet">
///   <item>(a) NO <c>integrationGate</c> key in any <c>task.json</c> (the retired terminal-sink marker,
///     a hard GR2029 error under the four-folder loader).</item>
///   <item>(b) NO no-op ROOT/END sink task (the terminal gate now lives in the plan-level
///     <c>guardrails/</c> folder, not a dedicated no-op task).</item>
///   <item>(c) NO <c>scope:"precondition"</c> marker anywhere (the guardrail <c>scope</c> field
///     recognises exactly two values, <c>local</c> and <c>integration</c>).</item>
///   <item>(d) the plan-level <c>&lt;plan&gt;/guardrails/</c> folder carries ≥1 file with REAL teeth
///     (a <c>dotnet build</c>/<c>dotnet test</c>/suite invocation OR a conflict-marker union invariant),
///     never only a bare <c>exit 0</c>.</item>
///   <item>(e) the whole example loads + validates CLEAN under the real loader/validator.</item>
/// </list>
/// </para>
///
/// Reuses the example-loading conventions of <see cref="GoldenRoundTripTests"/> (repo-root-relative
/// path via <see cref="TestPaths.ProjectDir"/>, real <see cref="PlanLoader"/> + <see cref="PlanValidator"/>
/// with <see cref="FakeExecutableProbe.All"/> so no machine PATH or token is touched).
/// </summary>
public sealed class PreflightExampleShapeTests
{
    private static string ExamplePlanDir
    {
        get
        {
            // tests/Guardrails.Core.Tests -> repo root -> docs/plans/09-.../example/example-plan
            string repoRoot = Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
            return Path.Combine(
                repoRoot, "docs", "plans", "09-preflight-first-class", "example", "example-plan");
        }
    }

    [Fact]
    public void Example_ValidatesCleanUnderTheLoader()
    {
        // (e) The whole four-folder example loads and validates with no ERRORS (a GR2025 review-marker
        // WARNING is expected and allowed — the example is a doc artifact, not a reviewed run target).
        PlanLoadResult load = new PlanLoader().Load(ExamplePlanDir);
        Assert.False(load.HasErrors,
            "example must load cleanly:\n" + string.Join("\n", load.Diagnostics));
        Assert.NotNull(load.Plan);

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(load.Plan!);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Example_HasNoIntegrationGateKey_InAnyTaskJson()
    {
        // (a) The retired integrationGate:true terminal-sink marker must not reappear in any task.json.
        foreach (string taskJson in TaskJsonFiles())
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(taskJson),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            Assert.False(
                doc.RootElement.TryGetProperty("integrationGate", out _),
                $"{taskJson} declares the RETIRED integrationGate key — the terminal gate now lives in " +
                "the plan-level guardrails/ folder (a task-level integrationGate is a hard GR2029 error).");
        }
    }

    [Fact]
    public void Example_HasNoNoOpRootOrEndSinkTask()
    {
        // (b) No dedicated no-op ROOT/END/integration-gate sink task. The four-folder model retired the
        // no-op sink whose only job was to host the terminal integration gate — that gate is now the
        // plan-level guardrails/ folder. Detect a sink by BOTH a sink-shaped folder name AND a
        // trivially-empty (bare `exit 0`) action, so a real task that merely happens to be named
        // "root" is not falsely flagged.
        string tasksRoot = Path.Combine(ExamplePlanDir, "tasks");
        foreach (string taskDir in Directory.EnumerateDirectories(tasksRoot))
        {
            string name = Path.GetFileName(taskDir);
            bool sinkShapedName =
                name.Contains("root", StringComparison.OrdinalIgnoreCase)
                || name.Contains("end", StringComparison.OrdinalIgnoreCase)
                || name.Contains("integration-gate", StringComparison.OrdinalIgnoreCase)
                || name.Contains("terminal-gate", StringComparison.OrdinalIgnoreCase);

            if (sinkShapedName)
            {
                Assert.False(HasBareExitZeroAction(taskDir),
                    $"task '{name}' looks like a retired no-op ROOT/END sink (sink-shaped name + a bare " +
                    "`exit 0` action). The terminal gate belongs in the plan-level guardrails/ folder.");
            }
        }
    }

    [Fact]
    public void Example_HasNoPreconditionScopeMarker_Anywhere()
    {
        // (c) The guardrail scope field recognises exactly two values (local | integration). A
        // scope:"precondition" MARKER (an earlier modelling that never shipped) must appear NOWHERE
        // across any sidecar .json in the four folders. Assert on the parsed `scope` VALUE, not a bare
        // substring — the word "precondition" legitimately appears in guardrails.json PROSE ("JIT
        // dependency-delivery precondition"), which is not a scope marker.
        foreach (string jsonFile in Directory.EnumerateFiles(ExamplePlanDir, "*.json", SearchOption.AllDirectories))
        {
            JsonElement root;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    File.ReadAllText(jsonFile),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not a JSON object we can inspect — no scope marker to find.
                continue;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("scope", out JsonElement scope)
                && scope.ValueKind == JsonValueKind.String)
            {
                Assert.False(
                    string.Equals(scope.GetString(), "precondition", StringComparison.OrdinalIgnoreCase),
                    $"{jsonFile} declares the retired scope:\"precondition\" marker — the scope field " +
                    "recognises exactly local | integration.");
            }
        }
    }

    [Fact]
    public void Example_PlanLevelGuardrailsFolder_HasAtLeastOneCheckWithRealTeeth()
    {
        // (d) The plan-level guardrails/ folder (the Terminal Gate) must carry ≥1 file with REAL teeth:
        // a whole-repo build / full-suite invocation OR a genuine conflict-marker union invariant —
        // never only a bare `exit 0`. The example's 03-union-invariant check is genuinely executable
        // (scans for conflict markers and exits 1); 01/02 name dotnet build/test invocations.
        string planGuardrailsDir = Path.Combine(ExamplePlanDir, "guardrails");
        Assert.True(Directory.Exists(planGuardrailsDir),
            "the example must carry a plan-level guardrails/ terminal-gate folder");

        List<string> scriptFiles = Directory
            .EnumerateFiles(planGuardrailsDir)
            .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(scriptFiles);

        bool anyWithTeeth = scriptFiles.Any(HasRealTeeth);
        Assert.True(anyWithTeeth,
            "the plan-level guardrails/ terminal gate must carry ≥1 check with real teeth (a " +
            "dotnet build/test / suite invocation OR a conflict-marker union invariant), not only " +
            "bare `exit 0` files.");
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static IEnumerable<string> TaskJsonFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(ExamplePlanDir, "tasks"), "task.json", SearchOption.AllDirectories);

    /// <summary>
    /// True when a task folder's action file is a bare, effectively-empty <c>exit 0</c> — the shape of
    /// a retired no-op sink. Reads the single action.* file (script actions only; the example's sink,
    /// if present, would be a script). Comment lines and blank lines are ignored; the only meaningful
    /// statement being <c>exit 0</c> marks it a no-op.
    /// </summary>
    private static bool HasBareExitZeroAction(string taskDir)
    {
        string? action = Directory
            .EnumerateFiles(taskDir)
            .FirstOrDefault(f =>
            {
                string n = Path.GetFileName(f);
                return n.StartsWith("action.", StringComparison.OrdinalIgnoreCase);
            });

        if (action is null)
        {
            return false;
        }

        List<string> meaningful = File.ReadAllLines(action)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("//"))
            .ToList();

        return meaningful.Count > 0
               && meaningful.All(l => l.Equals("exit 0", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when a guardrail script has REAL teeth: it invokes a build/test toolchain
    /// (<c>dotnet build</c> / <c>dotnet test</c>) OR performs a genuine git-conflict-marker union
    /// invariant scan (matches on the conflict-marker sigils in an actual check, not merely a comment).
    /// A file whose only executable statement is <c>exit 0</c> has no teeth.
    /// </summary>
    private static bool HasRealTeeth(string scriptPath)
    {
        string[] lines = File.ReadAllLines(scriptPath);

        // Strip comment lines so a build/test/conflict-marker token that only appears in a comment does
        // not count (comment-stripping discipline — mirrors GR2028's content-teeth rule).
        IEnumerable<string> code = lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));

        string codeText = string.Join("\n", code);

        bool buildOrTest =
            codeText.Contains("dotnet build", StringComparison.OrdinalIgnoreCase)
            || codeText.Contains("dotnet test", StringComparison.OrdinalIgnoreCase);

        bool conflictMarkerScan =
            codeText.Contains("<<<<<<<", StringComparison.Ordinal)
            || codeText.Contains(">>>>>>>", StringComparison.Ordinal);

        return buildOrTest || conflictMarkerScan;
    }
}
