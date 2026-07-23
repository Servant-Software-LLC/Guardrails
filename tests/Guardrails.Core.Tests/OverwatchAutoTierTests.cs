using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD (red) for the overwatcher <c>auto</c>-tier gate (issue #361 Phase 4, doc 12 §9 Phase 4; doc 11
/// §6/§9.6). Today <c>auto</c> DEGRADES to <c>prompt</c> (grep <see cref="Overwatch"/>'s
/// <c>"prompt (and, in v1, auto — which degrades to prompt)"</c> comment); the Phase-4 gate makes a
/// block-present <c>auto</c> tier SILENTLY auto-apply a sanctioned ALLOWLIST lever (guidance / budget),
/// while keeping two invariants intact:
/// <list type="bullet">
///   <item>The gate keys on the PRESENCE of an <c>autonomy</c> block (the ctor's
///   <c>autonomyBlockPresent</c>), NOT on <c>autonomyPolicy: auto</c> alone — the anti-Option-(c) guard.
///   A bare <c>auto</c> with no block still degrades to <c>prompt</c>, byte-identical to today.</item>
///   <item>A <see cref="OverwatchAuthorityClass.Denylist"/> op (the verdict surface) is propose-only at
///   EVERY tier, including <c>auto</c> — it is NEVER silently auto-applied.</item>
/// </list>
///
/// <para>These mirror the shipped <c>OverwatchClassifierTests</c> / integration <c>OverwatchTests</c>
/// shape: a fake <see cref="IPromptRunner"/> feeds a canned proposal; a RECORDING
/// <see cref="IOverwatchInteraction"/> distinguishes SILENT auto-apply (<c>ConfirmApply</c> NOT called)
/// from a prompt-then-apply (<c>ConfirmApply</c> called). Against the current stub (ctor accepts
/// <c>autonomyBlockPresent</c> but <see cref="Overwatch"/>.<c>Decide</c> is unchanged) the two silent-apply
/// tests FAIL (auto still degrades to prompt ⇒ it consults the interaction and honest-halts), the
/// back-compat test PASSES unchanged, and the denylist invariant holds at every tier.</para>
/// </summary>
public sealed class OverwatchAutoTierTests : IDisposable
{
    private readonly string _planDir;
    private readonly PlanDefinition _plan;
    private readonly RunJournal _journal;
    private readonly TaskNode _task;
    private readonly string _taskLogDir;

    public OverwatchAutoTierTests()
    {
        // A real on-disk plan + fresh journal (the direct-EvaluateAsync harness the integration
        // OverwatchTests use, reduced to what a Core.Tests unit needs): guardrails.json + one task.json,
        // so PlanHash.Compute + RunJournal.LoadOrCreate can persist state/run.json.
        _planDir = Path.Combine(Path.GetTempPath(), "gr-overwatch-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");

        string taskDir = Path.Combine(_planDir, "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        _task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "t",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.prompt.md"), Kind = ActionKind.Prompt },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ]
        };

        _plan = new PlanDefinition
        {
            PlanDirectory = _planDir,
            Workspace = _planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [_task]
        };

        _journal = RunJournal.LoadOrCreate(_plan);
        _taskLogDir = Path.Combine(_planDir, "logs", _journal.Document.RunId, _task.Id);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A minimal <see cref="IPromptRunner"/> that returns one canned diagnose body (no real claude).</summary>
    private sealed class StubRunner(string resultText) : IPromptRunner
    {
        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = resultText,
                Summary = "fake"
            });
    }

    /// <summary>
    /// A RECORDING interaction: it counts how many times <c>ConfirmApply</c> was called (the load-bearing
    /// distinction — a SILENT auto-apply must NOT call it) and returns a fixed response when it is.
    /// </summary>
    private sealed class RecordingInteraction(OverwatchInteractionResult result) : IOverwatchInteraction
    {
        public int Calls { get; private set; }

        public OverwatchInteractionResult ConfirmApply(
            OverwatchProposal proposal, TaskNode task, OverwatchTrigger trigger, string sanctionedChangeSummary)
        {
            Calls++;
            return result;
        }
    }

    // ── Canned diagnose bodies (mirror the integration OverwatchTests builders) ─────────────────────

    private static string GuidanceProposal() =>
        """{"classification":"retryable","diagnosis":"the action never emits the required token","fixes":[{"kind":"guidance","guidance":"emit the required token before exiting"}]}""";

    private static string BudgetProposal() =>
        """{"classification":"retryable","diagnosis":"needs one more attempt","fixes":[{"kind":"budget","field":"retries","value":1}]}""";

    private static string DenylistOnlyProposal(string guardrailBodyPath) =>
        $$"""{"classification":"retryable","diagnosis":"the guardrail expectation is wrong","fixes":[{"kind":"file-edit","path":{{JsonSerializer.Serialize(guardrailBodyPath)}}}]}""";

    private Task<OverwatchDecision> EvaluateAsync(Overwatch overwatch) =>
        overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, _task, _plan, attempt: 2, _taskLogDir, _journal, IRunObserver.Null,
            TestContext.Current.CancellationToken);

    // ── auto + block present + ALLOWLIST lever ⇒ SILENT auto-apply (the NEW behavior — FAILS on the stub) ─

    [Fact]
    public async Task Auto_BlockPresent_SanctionedGuidance_SilentlyAutoApplies_WithoutConsultingInteraction()
    {
        // Even a NonInteractive interaction must be BYPASSED: a block-present `auto` tier grants the retry
        // + applies the guidance lever WITH NO prompt. (On the stub, `auto` degrades to `prompt` ⇒ it DOES
        // consult the interaction ⇒ Calls == 1 and it honest-halts — so this test is red.)
        var interaction = new RecordingInteraction(OverwatchInteractionResult.NonInteractive);
        var overwatch = new Overwatch(new StubRunner(GuidanceProposal()), terminalTriage: null,
            AutonomyPolicy.Auto, interaction, autonomyBlockPresent: true);

        OverwatchDecision d = await EvaluateAsync(overwatch);

        Assert.Equal(0, interaction.Calls);                          // SILENT — ConfirmApply was never called
        Assert.Equal(OverwatchDecisionKind.Grant, d.Kind);           // the retry is granted with no prompt
        Assert.False(string.IsNullOrEmpty(d.GuidanceInjection));     // the sanctioned guidance lever was applied
    }

    [Fact]
    public async Task Auto_BlockPresent_SanctionedBudget_SilentlyAutoApplies_WithExtraRetries()
    {
        var interaction = new RecordingInteraction(OverwatchInteractionResult.NonInteractive);
        var overwatch = new Overwatch(new StubRunner(BudgetProposal()), terminalTriage: null,
            AutonomyPolicy.Auto, interaction, autonomyBlockPresent: true);

        OverwatchDecision d = await EvaluateAsync(overwatch);

        Assert.Equal(0, interaction.Calls);                          // SILENT — ConfirmApply was never called
        Assert.Equal(OverwatchDecisionKind.Grant, d.Kind);           // granted with no prompt
        Assert.Equal(1, d.ExtraRetries);                             // the retries:1 budget lever was applied
    }

    // ── auto + NO block ⇒ STILL degrades to prompt (byte-identical back-compat — PASSES on the stub) ──

    [Fact]
    public async Task Auto_BlockAbsent_StillDegradesToPrompt_ConsultsInteraction_NonInteractiveHalts()
    {
        // The REQUIRED anti-Option-(c) guard (doc 12 §9 Phase 4): `AutonomyPolicy.Auto` with NO `autonomy`
        // block (autonomyBlockPresent defaults to false) must behave EXACTLY as today — it PROPOSES
        // (consults the interaction) and a non-interactive context honest-halts. The gate keys on BLOCK
        // PRESENCE, never on `autonomyPolicy: auto` alone.
        var interaction = new RecordingInteraction(OverwatchInteractionResult.NonInteractive);
        var overwatch = new Overwatch(new StubRunner(GuidanceProposal()), terminalTriage: null,
            AutonomyPolicy.Auto, interaction); // autonomyBlockPresent omitted ⇒ false

        OverwatchDecision d = await EvaluateAsync(overwatch);

        Assert.Equal(1, interaction.Calls);                          // PROPOSED — it consulted the interaction
        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);            // non-interactive ⇒ honest halt (unchanged)
    }

    // ── A Denylist op NEVER auto-applies, even with a block present (the verdict surface is human-only) ─

    [Fact]
    public async Task Auto_BlockPresent_DenylistOnlyProposal_NeverSilentlyAutoApplies_Halts()
    {
        // A verdict-surface change (a guardrail body edit ⇒ OverwatchAuthorityClass.Denylist) is propose-only
        // at EVERY tier. Under `auto` WITH a block present it must still route to human/NonGrant — never a
        // silent auto-apply. Since a denylist op offers no sanctioned allowlist lever, it is NonGrant
        // (no-sanctioned-change) and the interaction is never even consulted.
        string guardrailBody = Path.Combine(_task.Directory, "guardrails", "01-check.ps1");
        var interaction = new RecordingInteraction(OverwatchInteractionResult.Apply); // would apply if ever asked
        var overwatch = new Overwatch(new StubRunner(DenylistOnlyProposal(guardrailBody)), terminalTriage: null,
            AutonomyPolicy.Auto, interaction, autonomyBlockPresent: true);

        OverwatchDecision d = await EvaluateAsync(overwatch);

        Assert.NotEqual(OverwatchDecisionKind.Grant, d.Kind);        // the verdict surface is NEVER auto-applied
        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);            // no sanctioned change ⇒ honest halt (the floor)
        Assert.Equal(0, interaction.Calls);                          // never offered — a denylist op is not a lever

        // The classifier's DENYLIST authority is recorded to the per-fire audit stream.
        string jsonl = await File.ReadAllTextAsync(
            Path.Combine(_taskLogDir, "overwatch.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("\"authority\":\"denylist\"", jsonl);
        Assert.Contains("\"kind\":\"file-edit\"", jsonl);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup — a locked file must never fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
