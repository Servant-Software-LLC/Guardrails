using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #509 — a plan-root sample pair had nowhere legal to live on a WAVED plan.
///
/// <para>
/// <c>plan-breakdown</c> Step 7.4 requires it unconditionally: <i>"every surviving source-shape check over
/// CODE ships its committed <c>.valid</c>/<c>.invalid</c> sample pair"</i>. The Stage 3 wave-2 breakdown did
/// exactly that for its two wave-level checks, executed the pair, and <b>caught a real defect with it</b> —
/// a PowerShell hashtable that unwrapped a single-element array literal, so for every file with one clause
/// the iteration variable became a STRING and <c>$clause[0]</c> became its first CHARACTER. Four clauses of
/// a wave-entry gate could never fail, including the one its header calls load-bearing. <b>The invalid
/// sample caught it; the valid sample did not</b>, because under an all-present tree everything passes
/// either way.
/// </para>
///
/// <para>
/// Then the fixtures went to <c>%TEMP%</c> and evaporated, because there was no legal place to commit them:
/// <c>KnownPlanRootFolders</c> was <c>{state, logs, guardrails, preflights, captured, tasks}</c>, so
/// <c>&lt;plan&gt;/samples/</c> on a waved plan was a hard <b>GR2033 error</b>.
/// </para>
///
/// <para>
/// <b>The altitude is the point.</b> A task guardrail that cannot fail passes one task; a wave-entry or
/// plan-terminal gate that cannot fail waves the whole wave — or the whole plan — through. The rule was
/// missing exactly where it matters most.
/// </para>
///
/// <para>
/// It was also an inconsistency inside the harness: <c>SampleVerifier</c> walks
/// <c>&lt;plan&gt;/samples/&lt;check&gt;/{valid,invalid}/</c> (#624) and the pre-DAG gate executes what it
/// finds, so the verifier read a folder the loader refused to load. Unnoticed because the #624 tests use a
/// FLAT plan, where the GR2033 check never runs — which is why the first test below is WAVED.
/// </para>
/// </summary>
public sealed class PlanRootSamplesFolderTests
{
    /// <summary>
    /// The regression pin: a waved plan carrying <c>&lt;plan&gt;/samples/</c> loads clean. Waved
    /// deliberately — the GR2033 subdirectory check runs only alongside wave directories, so a flat fixture
    /// would pass with or without the fix and prove nothing.
    /// </summary>
    [Fact]
    public void AWavedPlanCarryingPlanRootSamples_LoadsClean()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config");
        Directory.CreateDirectory(Path.Combine(b.PlanDir, "samples", "01-union-verified", "valid"));
        Directory.CreateDirectory(Path.Combine(b.PlanDir, "samples", "01-union-verified", "invalid"));

        PlanLoadResult result = b.Load();

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.WaveNumbering);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
    }

    /// <summary>
    /// The control, and the reason this is an addition to the allow-list rather than a relaxation of the
    /// check: a genuinely stray sibling still errors. GR2033 exists to catch a typo'd wave dir
    /// (<c>wave-scaffold</c>, no number) or a leftover <c>tasks-old/</c>, and widening it to "anything
    /// goes" would lose that for the sake of one folder.
    /// </summary>
    [Fact]
    public void AStraySiblingStillErrors()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config");
        b.RootDir("tasks-old");

        Assert.Contains(b.Load().Diagnostics, d => d.Code == DiagnosticCodes.WaveNumbering);
    }

    /// <summary>
    /// <c>samples/</c> contributes no guardrail at any altitude. This is the invariant that makes the
    /// folder safe to allow at all — the loader enumerates every non-<c>.json</c> file in a guardrail
    /// folder with no extension allow-list, so a fixture in the wrong place would load as a script
    /// guardrail, satisfy GR2003 on its own, and be EXECUTED at run time (SSOT §1.1).
    /// </summary>
    [Fact]
    public void PlanRootSamples_ContributeNoGuardrail()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config");

        string half = Path.Combine(b.PlanDir, "samples", "01-union-verified", "valid");
        Directory.CreateDirectory(half);
        File.WriteAllText(Path.Combine(half, "greeting.txt"), "OK-MARKER\n");

        Assert.Empty(b.Load().Plan!.PlanGuardrails);
    }
}
