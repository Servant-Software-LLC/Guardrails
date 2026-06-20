using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Golden round-trip safety net (risk-register row 5 / M6 exit criterion:
/// "golden round-trip test in CI"). This is the DETERMINISTIC, CI-feasible HALF of that
/// promise:
/// <list type="bullet">
///   <item>the committed golden example <c>examples/hello-guardrails/hello-guardrails</c>
///     loads via the real <see cref="PlanLoader"/> and <see cref="PlanValidator"/> reports
///     it CLEAN (no errors); and</item>
///   <item>the loaded plan/task model survives a serialize → re-load round-trip with NO
///     structural loss (lossless + stable), guarding the loader/serializer schema contract
///     and the golden example together.</item>
/// </list>
///
/// COMPLEMENTARY MANUAL HALF (NOT covered here, by design — it needs real Claude and cannot
/// run in CI): the LIVE <c>/plan-breakdown</c> regeneration of the example into a
/// validate-clean, structurally equivalent folder. That remains the manual/dogfood
/// demonstration. Do NOT mistake this deterministic test for the full M6 exit criterion —
/// it is the safety net that the LOADER/SERIALIZER side of the contract never silently
/// regresses, runnable on the 3-OS matrix without a token spend.
/// </summary>
public sealed class GoldenRoundTripTests
{
    [Fact]
    public void GoldenExample_LoadsValidatesCleanAndRoundTrips()
    {
        string planDir = GoldenExamplePath;

        // 1. Load via the real loader.
        PlanLoadResult first = new PlanLoader().Load(planDir);
        Assert.False(first.HasErrors, DiagnosticDump(first));
        Assert.NotNull(first.Plan);

        // 2. Validator reports CLEAN (no errors). FakeExecutableProbe.All resolves every
        //    interpreter and prompt-runner command so this never touches the machine PATH and
        //    never spends a token — the golden uses .ps1 scripts + a "claude" prompt runner.
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(first.Plan!);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // 3. Structural round-trip. Project the loaded model to a canonical, location-independent
        //    JSON (paths made relative to the plan dir), serialize, deserialize, re-serialize, and
        //    assert byte-equality: the serialization is lossless and stable.
        string canonical = SerializeCanonical(Project(first.Plan!, planDir));
        PlanSnapshot reloaded = JsonSerializer.Deserialize<PlanSnapshot>(canonical, SnapshotOptions)!;
        string roundTripped = SerializeCanonical(reloaded);
        Assert.Equal(canonical, roundTripped);

        // 4. Independent re-load of the same folder is structurally identical. PlanDefinition is a
        //    record graph, so this is value-equality over the whole loaded model — a second load
        //    must produce the same plan (deterministic loader contract).
        PlanLoadResult second = new PlanLoader().Load(planDir);
        Assert.NotNull(second.Plan);
        Assert.Equal(SerializeCanonical(Project(second.Plan!, planDir)), canonical);
    }

    // ---- Canonical projection ---------------------------------------------------------------
    // A normalized snapshot of the loaded model with absolute paths rewritten relative to the
    // plan dir (so the serialized form is identical regardless of where the worktree lives on a
    // CI runner), with deterministic ordering. Equivalence of two snapshots == structural
    // equivalence of two loaded plans.

    private static PlanSnapshot Project(PlanDefinition plan, string planDir) => new()
    {
        Version = plan.Config.Version,
        MaxParallelism = plan.Config.MaxParallelism,
        DefaultRetries = plan.Config.DefaultRetries,
        DefaultTimeoutSeconds = plan.Config.DefaultTimeoutSeconds,
        GuardrailMode = plan.Config.GuardrailMode.ToString(),
        Workspace = plan.Config.Workspace,
        DefaultPromptRunner = plan.Config.DefaultPromptRunner,
        PromptRunnerNames = plan.Config.PromptRunnerNames.OrderBy(n => n, StringComparer.Ordinal).ToList(),
        Tasks = plan.Tasks.Select(t => new TaskSnapshot
        {
            Id = t.Id,
            StableId = t.StableId,
            Description = t.Description,
            DependsOn = t.DependsOn.ToList(),
            Retries = t.Retries,
            TimeoutSeconds = t.TimeoutSeconds,
            Action = new ActionSnapshot
            {
                Path = Rel(planDir, t.Action.Path),
                Kind = t.Action.Kind.ToString(),
                Args = t.Action.Args.ToList(),
                Runner = t.Action.Runner,
                MaxTurns = t.Action.MaxTurns,
                TimeoutSeconds = t.Action.TimeoutSeconds
            },
            Guardrails = t.Guardrails.Select(g => new GuardrailSnapshot
            {
                Name = g.Name,
                Path = Rel(planDir, g.Path),
                Kind = g.Kind.ToString(),
                Description = g.Description,
                Args = g.Args.ToList(),
                TimeoutSeconds = g.TimeoutSeconds
            }).ToList()
        }).ToList()
    };

    /// <summary>Plan-relative, forward-slashed path so the snapshot is OS- and location-independent.</summary>
    private static string Rel(string planDir, string absolute) =>
        Path.GetRelativePath(planDir, absolute).Replace('\\', '/');

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static string SerializeCanonical(PlanSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, SnapshotOptions);

    private static string GoldenExamplePath
    {
        get
        {
            // tests/Guardrails.Core.Tests -> repo root -> examples/...
            string repoRoot = Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
            return Path.Combine(repoRoot, "examples", "hello-guardrails", "hello-guardrails");
        }
    }

    private static string DiagnosticDump(PlanLoadResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));

    // ---- Snapshot DTOs (plain, serializable mirror of the loaded model) ---------------------

    private sealed record PlanSnapshot
    {
        public int Version { get; init; }
        public int MaxParallelism { get; init; }
        public int DefaultRetries { get; init; }
        public int DefaultTimeoutSeconds { get; init; }
        public string GuardrailMode { get; init; } = "";
        public string Workspace { get; init; } = "";
        public string? DefaultPromptRunner { get; init; }
        public List<string> PromptRunnerNames { get; init; } = [];
        public List<TaskSnapshot> Tasks { get; init; } = [];
    }

    private sealed record TaskSnapshot
    {
        public string Id { get; init; } = "";
        public string? StableId { get; init; }
        public string Description { get; init; } = "";
        public List<string> DependsOn { get; init; } = [];
        public int? Retries { get; init; }
        public int? TimeoutSeconds { get; init; }
        public ActionSnapshot Action { get; init; } = new();
        public List<GuardrailSnapshot> Guardrails { get; init; } = [];
    }

    private sealed record ActionSnapshot
    {
        public string Path { get; init; } = "";
        public string Kind { get; init; } = "";
        public List<string> Args { get; init; } = [];
        public string? Runner { get; init; }
        public int? MaxTurns { get; init; }
        public int? TimeoutSeconds { get; init; }
    }

    private sealed record GuardrailSnapshot
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public string Kind { get; init; } = "";
        public string? Description { get; init; }
        public List<string> Args { get; init; } = [];
        public int? TimeoutSeconds { get; init; }
    }
}
