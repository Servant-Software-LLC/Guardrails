using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #574 — the pre-DAG phase's resume skip must key on the thing it CHECKS.
///
/// <para>
/// It keyed on <c>PlanHash</c>, which covers <c>guardrails.json</c> plus every <c>task.json</c> and
/// covers none of what the phase evaluates. One mismatch, two opposite defects:
/// </para>
/// <list type="bullet">
///   <item><b>#623's second claim</b> — the hash HOLDS while the checked thing CHANGED: edit the
///   preflight that failed the gate and the marker still matches, so the phase skips when it should run.</item>
///   <item><b>#574</b> — the hash MOVES while the checked thing did NOT: any <c>task.json</c> edit mid-run
///   re-runs the phase, and by then the plan's own work has landed, so a baseline asserting "the area was
///   green before we started" is re-evaluated against POST-WORK bytes and halts a healthy resume with a
///   message blaming pre-existing breakage.</item>
/// </list>
///
/// <para>
/// Both directions are pinned here, because a fix for either one alone is satisfiable by breaking the
/// other — always skip, or never skip. The maintainer chose a preflight-SCOPED hash over
/// <see cref="PlanDefinitionHash"/> precisely so an unrelated action-body edit cannot force a re-run.
/// </para>
/// </summary>
[Trait("Category", "ScanSoundness")]
public sealed class PreflightSkipScopeTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();
    private static readonly string Ext = Ps ? ".ps1" : ".sh";

    /// <summary>#574: an unrelated <c>task.json</c> edit must NOT re-run a passed phase.</summary>
    [Fact]
    public async Task EditingAnUnrelatedTaskJson_DoesNotReRunAPassedPhase()
    {
        using var plan = new PreflightPlan();

        Assert.True(await EvaluateAsync(plan));
        DateTimeOffset firstRun = ReadMarker(plan)!.EvaluatedAt;

        // Moves PlanHash — and nothing the phase checks.
        plan.RewriteTaskDescription("a different description entirely");

        Assert.True(await EvaluateAsync(plan));

        Assert.Equal(
            firstRun,
            ReadMarker(plan)!.EvaluatedAt);
    }

    /// <summary>
    /// #623's second claim, and the reason the fix is not simply "always skip": editing the PREFLIGHT
    /// must re-run it. An unchanged <c>EvaluatedAt</c> proves a skip without reasoning about the guard —
    /// the sharpest diagnostic either issue produced, and it stays true here.
    /// </summary>
    [Fact]
    public async Task EditingThePreflightItself_DoesReRunThePhase()
    {
        using var plan = new PreflightPlan();

        Assert.True(await EvaluateAsync(plan));
        DateTimeOffset firstRun = ReadMarker(plan)!.EvaluatedAt;

        await Task.Delay(10, TestContext.Current.CancellationToken); // so a re-evaluation cannot share the first one's timestamp
        plan.RewritePreflight(passes: true, marker: "SECOND-EDITION");

        Assert.True(await EvaluateAsync(plan));

        Assert.NotEqual(firstRun, ReadMarker(plan)!.EvaluatedAt);
    }

    /// <summary>
    /// A marker written before #574 carries no <c>PreflightsHash</c>. Unknown must mean DO NOT SKIP:
    /// re-running costs a little time, while skipping on an unverifiable marker is the "recorded but
    /// never executed" failure the phase exists to prevent.
    /// </summary>
    [Fact]
    public async Task APreFixMarkerWithNoPreflightsHash_IsNotTrusted()
    {
        using var plan = new PreflightPlan();

        Assert.True(await EvaluateAsync(plan));
        DateTimeOffset firstRun = ReadMarker(plan)!.EvaluatedAt;

        await Task.Delay(10, TestContext.Current.CancellationToken);
        plan.StripPreflightsHashFromMarker();

        Assert.True(await EvaluateAsync(plan));

        Assert.NotEqual(firstRun, ReadMarker(plan)!.EvaluatedAt);
    }

    private static async Task<bool> EvaluateAsync(PreflightPlan plan)
    {
        PlanDefinition definition = LoadPlan(plan);
        RunJournal journal = RunJournal.LoadOrCreate(definition);
        return await PlanPreflightPhase.EvaluateAsync(
            definition, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static PlanDefinition LoadPlan(PreflightPlan plan)
    {
        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    private static PlanPreflightsSection? ReadMarker(PreflightPlan plan) =>
        RunJournal.LoadOrCreate(LoadPlan(plan)).Document.PlanPreflights;

    /// <summary>A minimal plan with one task and one passing plan-root Full Flight Check.</summary>
    private sealed class PreflightPlan : IDisposable
    {
        public string PlanDir { get; }
        private readonly string _taskJson;
        private readonly string _preflight;

        public PreflightPlan()
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr574-" + Guid.NewGuid().ToString("N"));
            string taskDir = Path.Combine(PlanDir, "tasks", "01-only");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "preflights"));

            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "workspace": "."
                }
                """);

            _taskJson = Path.Combine(taskDir, "task.json");
            RewriteTaskDescription("the only task");

            WriteScript(Path.Combine(taskDir, "action" + Ext), Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            WriteScript(Path.Combine(taskDir, "guardrails", "01-ok" + Ext),
                Ps ? "# catches: nothing\nexit 0\n" : "#!/usr/bin/env bash\n# catches: nothing\nexit 0\n");

            _preflight = Path.Combine(PlanDir, "preflights", "01-baseline" + Ext);
            RewritePreflight(passes: true, marker: "FIRST-EDITION");
        }

        /// <summary>Moves <c>PlanHash</c> without touching anything the pre-DAG phase evaluates.</summary>
        public void RewriteTaskDescription(string description) =>
            File.WriteAllText(_taskJson,
                $$"""
                {
                  "description": "{{description}}",
                  "dependsOn": [],
                  "writeScope": []
                }
                """);

        /// <summary>Moves the preflight-scoped hash — the subject of the skip.</summary>
        public void RewritePreflight(bool passes, string marker)
        {
            string exit = passes ? "0" : "1";
            WriteScript(_preflight,
                Ps ? $"# catches: nothing - {marker}\nexit {exit}\n"
                   : $"#!/usr/bin/env bash\n# catches: nothing - {marker}\nexit {exit}\n");
        }

        /// <summary>Rewrites the journal as a pre-#574 marker would have looked: no <c>preflightsHash</c>.</summary>
        public void StripPreflightsHashFromMarker()
        {
            string journalPath = Path.Combine(PlanDir, "state", "run.json");
            string text = File.ReadAllText(journalPath);
            Assert.Contains("preflightsHash", text, StringComparison.OrdinalIgnoreCase);

            System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            root["planPreflights"]!.AsObject().Remove("preflightsHash");
            File.WriteAllText(journalPath, root.ToJsonString());
        }

        private static void WriteScript(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(PlanDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
