using System.Runtime.CompilerServices;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #547 — the test suites were writing into the OPERATOR'S REAL telemetry corpus.
///
/// <para><b>What was measured.</b> The corpus was purged and backfilled to 419 clean rows on the morning of
/// 2026-08-30. By that evening it held 1138 rows, of which <b>719 had been written that day by test
/// fixtures</b> — repos named <c>gr-m3-*</c>, <c>guardrails-it-*</c>, <c>gr-reval-*</c>, <c>gr-escwire-*</c>,
/// <c>gr-fakecli-*</c>, plus 394 rows whose repo dimension was the literal placeholder string <c>"repo"</c>
/// or <c>"plan"</c>. 461 of them landed in a single hour: the hour plan 28's brownfield preflight (#181)
/// spent running these suites inside its worktree.</para>
///
/// <para><b>Why it is worse than noise.</b> In that same two-hour window the real $39.91 run contributed
/// ZERO rows, because it was launched with an installed tool predating the telemetry verb. So the corpus
/// captured only the fixtures and missed the run — and Phase 0's first stratified report was rendered over
/// exactly that. A corpus that silently mixes synthetic rows into the evidence for a model-graduation
/// decision is not a tidiness problem; it is a wrong answer with a confident table around it.</para>
///
/// <para><b>Why HERE and not in each test.</b> The rows are not written by the tests directly — they come
/// from tests that drive the real <c>run</c> path, which resolves its corpus root from
/// <c>GUARDRAILS_TELEMETRY_CORPUS_ROOT</c> and otherwise falls back to <c>~/.guardrails/telemetry</c>. Any
/// test that exercises a run therefore pollutes by DEFAULT, including every test written in the future by
/// someone who has never heard of this issue. A module initializer is the only place that holds for tests
/// that do not yet exist; a per-test opt-in would be a rule to remember, and this is a rule that has
/// already been forgotten once.</para>
///
/// <para><b>Redirect, never disable.</b> <c>GUARDRAILS_TELEMETRY=off</c> would look like the simpler switch
/// and is the wrong one: it suppresses the writes the telemetry tests themselves assert on, turning 12 of
/// them red. Isolation has to move WHERE the corpus lives, not whether collection happens — the suites must
/// keep exercising the real write path, just not into the operator's data.</para>
///
/// <para>An explicitly-set root is honoured rather than overwritten, so CI or a developer can still aim the
/// suites somewhere specific.</para>
/// </summary>
internal static class TelemetryCorpusIsolation
{
    private const string CorpusRootEnvVar = "GUARDRAILS_TELEMETRY_CORPUS_ROOT";

    [ModuleInitializer]
    internal static void RedirectCorpusToScratch()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CorpusRootEnvVar)))
        {
            return;
        }

        // Per-process, so parallel suite runs never share a corpus and cannot make each other flaky by
        // counting each other's rows. Left on disk in the OS temp area rather than deleted at exit: a
        // failing telemetry test is diagnosed FROM the rows it wrote, and the whole point of this file is
        // that evidence should survive the thing that produced it.
        string root = Path.Combine(
            Path.GetTempPath(), "guardrails-test-telemetry", Environment.ProcessId.ToString());

        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(CorpusRootEnvVar, root);
    }
}
