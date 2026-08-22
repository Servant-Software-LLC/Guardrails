using System.Text;
using System.Text.RegularExpressions;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The between-wave breakdown actor (#360 Phase 1, design of record <c>docs/plans/11-overwatcher.md</c> §9).
/// At the JIT wave checkpoint (an unauthored/empty next wave carrying a human-authored <c>brief.md</c>) the
/// wave loop (<see cref="Scheduler.RunWavedAsync"/>) invokes this actor to AUTHOR the wave's <c>tasks/**</c>
/// by driving the <c>plan-breakdown</c> skill through the shipped <see cref="IPromptRunner"/> seam under the
/// reserved <c>breakdown</c> profile.
///
/// <para>It is a DISTINCT component from the per-task <see cref="Overwatch"/> (they are "one supervisor"
/// conceptually, two components mechanically — doc 11 §9.1): the overwatcher's <c>overwatch</c> profile is
/// READ-ONLY (it only reasons); the <c>breakdown</c> profile has the FULL authoring tool set
/// (Read/Write/Edit/Bash/Grep/Glob) because it writes task files into a <c>pending</c> wave folder
/// (invariant 2 — never merged state). The materialized upstream (the completed prior waves' real outputs)
/// lives on the plan branch in the integration worktree, injected via a second <c>--add-dir</c> so the
/// sub-process can READ it while WRITING the wave into the plan folder.</para>
///
/// <para>This actor only INVOKES; the deterministic gate on its output is the harness re-running
/// <c>guardrails validate</c> in-process (invariant 1) — owned by the caller, never the judge that produced
/// the wave. Its own prompt spend is charged to the shared overhead sink (<c>overheadCostUsd</c>, SSOT
/// §7/§9), folded into <c>maxCostUsd</c> and the reported total, exactly like the diagnose / AI-merge /
/// triage spend (#314).</para>
/// </summary>
public sealed class WaveBreakdownInvoker
{
    /// <summary>The full authoring tool set (doc 11 §9.2) — distinct from the read-only <c>overwatch</c> diagnose profile.</summary>
    private static readonly IReadOnlyList<string> AuthoringTools = ["Read", "Write", "Edit", "Bash", "Grep", "Glob"];

    /// <summary>
    /// The turn-budget BASE (issue #385): a generous fixed ceiling every wave breakdown gets, comfortably
    /// above the old fixed 120 that TRUNCATED a large (~11-task) wave — and enough, on its own (zero brief
    /// signal), to author a large wave. <c>--max-turns</c> is a CEILING, not a target: the agent stops when
    /// the wave is authored + self-validated, so headroom is FREE for a wave that finishes early and only
    /// ever bites the large wave it exists to protect. (Wall-clock is separately bounded by
    /// <see cref="BreakdownTimeout"/>; prompt spend by <c>maxCostUsd</c> — a higher ceiling adds neither cost
    /// nor time to a wave that completes.)
    /// </summary>
    private const int BreakdownBaseTurns = 400;

    /// <summary>
    /// Extra turn headroom added per work-item signal counted in the wave's <c>brief.md</c> (a list item /
    /// numbered step / sub-heading — <see cref="EstimateBriefSignalCount"/>). The brief describes INTENT and
    /// UNDER-declares the eventual task count (#385: a 3-bullet brief broke down to ~11 tasks), so this only
    /// ADDS headroom on top of the generous base — it never lowers the budget below it.
    /// </summary>
    private const int BreakdownTurnsPerBriefSignal = 25;

    /// <summary>A hard turn ceiling so a pathological brief can never request an unbounded session (the timeout also bounds it).</summary>
    private const int BreakdownMaxTurnsCeiling = 1000;

    /// <summary>
    /// A generous timeout — a whole-wave breakdown + self-validate is a long session. PUBLIC because the
    /// ceiling is now OPERATOR-FACING (design 23 §4): it is the only honest denominator the phase has, and
    /// every surface renders elapsed against it. It denominates the BUDGET, never the work.
    /// </summary>
    public static readonly TimeSpan BreakdownTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A markdown "work-item" line in a brief: a list item (<c>- </c>/<c>* </c>), a numbered step
    /// (<c>N.</c>/<c>N)</c>), or a sub-heading (<c>##</c>+). A coarse size proxy only (see
    /// <see cref="EstimateBriefSignalCount"/>); the level-1 title (a single <c>#</c>) is deliberately excluded.
    /// </summary>
    private static readonly Regex BriefWorkItemLine = new(
        @"^[ \t]*(?:[-*][ \t]+|\d+[.)][ \t]+|#{2,}[ \t]+)\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly IPromptRunner _runner;

    /// <param name="runner">The runner for the reserved <c>breakdown</c> profile (resolved with fallback to the default/sole runner).</param>
    public WaveBreakdownInvoker(IPromptRunner runner) => _runner = runner;

    /// <summary>
    /// Invoke <c>plan-breakdown</c> for <paramref name="wave"/> against its <c>brief.md</c> and the
    /// materialized upstream in <paramref name="integrationWorktreePath"/>, teeing the transcript under
    /// <paramref name="breakdownLogDir"/> (SSOT §8 <c>logs/&lt;runId&gt;/&lt;wave-dir&gt;/breakdown/</c>).
    /// Charges the invocation's prompt spend to the shared overhead sink BEFORE returning (regardless of
    /// outcome — the spend is real either way). Never throws: a runner fault degrades to a faulted outcome
    /// so the caller's deterministic validate gate still decides the halt kind.
    /// </summary>
    internal async Task<WaveBreakdownOutcome> InvokeAsync(
        WaveNode wave,
        PlanDefinition plan,
        string integrationWorktreePath,
        string breakdownLogDir,
        ISchedulerJournal journal,
        CancellationToken ct,
        BreakdownResumeContext? resume = null,
        BreakdownInvocationPlan? prepared = null)
    {
        try
        {
            // Composing + teeing the prompt is SEPARATE from running it (design 23 §10.1) so the caller can
            // raise WaveBreakdownStarting with the real stream path and composed-prompt size BEFORE the
            // 30-minute session begins. A caller that does not care passes null and nothing changes.
            BreakdownInvocationPlan p =
                prepared ?? PrepareInvocation(wave, plan, integrationWorktreePath, breakdownLogDir, resume);

            return await InvokeCoreAsync(
                p,
                // Author into the plan folder (the wave's tasks/ live here); the integration worktree is
                // granted read access via a second --add-dir (materialized upstream).
                workingDirectory: plan.PlanDirectory,
                planDirectory: plan.PlanDirectory,
                additionalReadDirectory: integrationWorktreePath,
                chargeCost: journal.AddOverheadCost,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A runner fault (e.g. the runner binary off PATH) must never crash the run — the caller's
            // deterministic validate gate then reports BreakdownFailed with this error carried in the detail.
            return new WaveBreakdownOutcome
            {
                ProcessCompleted = false,
                FailureKind = PromptFailureKind.Error,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Run one already-composed breakdown invocation and classify how it stopped — the part that is
    /// IDENTICAL for a between-wave JIT breakdown and an initial <c>guardrails breakdown</c> (#498).
    /// <para><b>Extracted rather than copied, deliberately.</b> Everything it carries is scar tissue:
    /// the 30-minute <see cref="BreakdownTimeout"/>, the <c>acceptEdits</c> + authoring-tools grant, the
    /// stream/transcript tee, and — load-bearing — preserving <see cref="PromptResult.FailureKind"/>
    /// instead of collapsing it to "did not complete cleanly" (#385 milestone 1, the fix that made two
    /// measured truncations diagnosable). A second entry point re-implementing any of that would
    /// silently drift from the path those issues were fixed on.</para>
    /// </summary>
    /// <param name="workingDirectory">Where the sub-process runs. The plan folder for a wave; the
    /// workspace for an initial breakdown, which has no plan folder to run in yet.</param>
    /// <param name="additionalReadDirectory">An extra <c>--add-dir</c> read grant, or null. The
    /// materialized integration worktree for a wave; null for an initial breakdown, where there is no
    /// upstream to read.</param>
    /// <param name="chargeCost">Sink for the attempt's spend, or null when no journal exists. The spend is
    /// charged BEFORE any gate — it is real whether or not the output validates, and it must count toward
    /// <c>maxCostUsd</c> and appear in the reported total (SSOT §9/#314).</param>
    internal async Task<WaveBreakdownOutcome> InvokeCoreAsync(
        BreakdownInvocationPlan p,
        string workingDirectory,
        string planDirectory,
        string? additionalReadDirectory,
        Action<decimal?>? chargeCost,
        CancellationToken ct)
    {
        var invocation = new PromptInvocation
        {
            ComposedPrompt = p.Prompt,
            WorkingDirectory = workingDirectory,
            PlanDirectory = planDirectory,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Settings = new PromptRunnerSettings
            {
                PermissionMode = "acceptEdits",
                AllowedTools = AuthoringTools,
                MaxTurns = p.MaxTurns,
                // Doc 11 §9.3 step 4: grant the sub-process access to the materialized upstream on the plan
                // branch (a SECOND --add-dir on top of the working dir) so the skill reads real prior-wave
                // outputs from the integration worktree, NOT the read-only user checkout. An initial
                // breakdown has no upstream, so it grants nothing extra.
                ExtraArgs = additionalReadDirectory is null
                    ? []
                    : ["--add-dir", additionalReadDirectory]
            },
            Timeout = BreakdownTimeout,
            StreamLogPath = p.StreamLogPath,
            TranscriptLogPath = p.TranscriptLogPath
        };

        PromptResult result = await _runner.RunAsync(invocation, ct).ConfigureAwait(false);

        chargeCost?.Invoke(result.CostUsd);

        return new WaveBreakdownOutcome
        {
            ProcessCompleted = result.Completed && !result.IsError,
            // Issue #385 milestone 1: the runner ALREADY classifies why it stopped; discarding it is why
            // the two measured truncations were reconstructible only from file mtimes and why the halt
            // said "did not complete cleanly", leaving the operator to guess between skill bug and budget.
            FailureKind = result.FailureKind,
            NumTurns = result.NumTurns,
            MaxTurns = p.MaxTurns,
            Summary = result.Summary,
            CostUsd = result.CostUsd
        };
    }

    /// <summary>
    /// Compose the prompt, tee it to <c>composed-prompt[-segment-N].md</c>, and resolve every path + budget
    /// this segment will use — WITHOUT running anything (design 23 §10.1). Split out of
    /// <see cref="InvokeAsync"/> so the caller can raise <see cref="IRunObserver.WaveBreakdownStarting"/>
    /// with the real <c>StreamLogPath</c> and composed-prompt size before a session that may run for half an
    /// hour, instead of guessing them or reporting zero.
    /// <para>Never throws: every IO step here is already best-effort (the tee was, and the log directory is
    /// re-created by the invoker), because a caller raising a UI event must not be able to abort a run.</para>
    /// </summary>
    internal static BreakdownInvocationPlan PrepareInvocation(
        WaveNode wave,
        PlanDefinition plan,
        string integrationWorktreePath,
        string breakdownLogDir,
        BreakdownResumeContext? resume)
    {
        try { Directory.CreateDirectory(breakdownLogDir); } catch { /* the invoker retries */ }

        string suffix = resume is null ? "" : $"-segment-{resume.Segment}";
        string composedPromptPath = Path.Combine(breakdownLogDir, $"composed-prompt{suffix}.md");
        string prompt = ComposePrompt(wave, plan, integrationWorktreePath, resume);
        try { File.WriteAllText(composedPromptPath, prompt); } catch { /* best-effort log tee */ }

        // Scale the turn budget to the wave's size (issue #385): a generous base that covers a large wave
        // on its own, plus per-brief-signal headroom. --max-turns is a CEILING, so this only ever helps a
        // wave large enough to truncate; a small wave finishes well below the cap at no extra cost/time.
        return new BreakdownInvocationPlan
        {
            Prompt = prompt,
            ComposedPromptPath = composedPromptPath,
            ComposedPromptBytes = Encoding.UTF8.GetByteCount(prompt),
            StreamLogPath = Path.Combine(breakdownLogDir, $"claude-stream{suffix}.jsonl"),
            TranscriptLogPath = Path.Combine(breakdownLogDir, $"transcript{suffix}.md"),
            MaxTurns = ComputeMaxTurns(ReadBriefSignalCount(wave))
        };
    }

    /// <summary>
    /// Compute the breakdown invocation's turn ceiling (issue #385) from the coarse brief-size signal: a
    /// generous fixed <see cref="BreakdownBaseTurns"/> base (covers a large wave on its own) PLUS
    /// <see cref="BreakdownTurnsPerBriefSignal"/> per counted work-item, floored at the base (a small wave is
    /// never starved) and clamped to <see cref="BreakdownMaxTurnsCeiling"/>. Pure/deterministic — unit-pinned.
    /// </summary>
    internal static int ComputeMaxTurns(int briefSignalCount)
    {
        int scaled = BreakdownBaseTurns + BreakdownTurnsPerBriefSignal * Math.Max(briefSignalCount, 0);
        return Math.Clamp(scaled, BreakdownBaseTurns, BreakdownMaxTurnsCeiling);
    }

    /// <summary>
    /// A coarse count of the work-item signals a wave's <c>brief.md</c> declares — markdown list items,
    /// numbered steps, and sub-headings (<see cref="BriefWorkItemLine"/>). Used ONLY to add turn headroom on
    /// top of <see cref="BreakdownBaseTurns"/>, so imprecision is safe (it never reduces the base). A null,
    /// whitespace, or unreadable brief yields 0 → the base budget.
    /// </summary>
    internal static int EstimateBriefSignalCount(string? briefText) =>
        string.IsNullOrWhiteSpace(briefText) ? 0 : BriefWorkItemLine.Matches(briefText).Count;

    /// <summary>Best-effort read of the wave's <c>brief.md</c> and count its work-item signals (a read fault → 0 → the base budget).</summary>
    private static int ReadBriefSignalCount(WaveNode wave)
    {
        try
        {
            string briefPath = Path.Combine(wave.Directory, WaveNode.BriefFileName);
            return File.Exists(briefPath) ? EstimateBriefSignalCount(File.ReadAllText(briefPath)) : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Compose the breakdown-invocation prompt (doc 11 §9.3): inline the <c>plan-breakdown</c> SKILL.md when
    /// it can be located beside the tool (best-effort), name the wave's <c>brief.md</c> as the target, and
    /// point at the materialized upstream in the integration worktree. The exact text is not a wire contract
    /// (a stub runner in tests ignores it); the load-bearing parts are the target, the integration path, and
    /// the "write into &lt;wave&gt;/tasks/, self-validate, present as a draft" instruction.
    /// </summary>
    private static string ComposePrompt(
        WaveNode wave, PlanDefinition plan, string integrationWorktreePath, BreakdownResumeContext? resume)
    {
        var sb = new StringBuilder();
        sb.Append("# Between-wave breakdown invocation (Guardrails harness, #360)\n\n");
        if (resume is not null)
        {
            AppendResumeSection(sb, wave, resume);
        }

        sb.Append($"Break down the JIT wave `{wave.Dir}` of the plan at `{plan.PlanDirectory}` into its ")
          .Append("`tasks/` folder — a dependency DAG of tasks, each with an action and deterministic-first ")
          .Append("guardrails — using the `plan-breakdown` skill.\n\n");
        sb.Append("## Target (the reviewed `.md` plan for THIS wave)\n\n");
        sb.Append($"- Wave brief: `{wave.Dir}/{WaveNode.BriefFileName}` (the input plan for this wave)\n");
        sb.Append($"- Write the authored tasks into `{wave.Dir}/tasks/` (currently an empty JIT stub)\n\n");
        sb.Append("## Materialized upstream (read-only)\n\n");
        sb.Append($"The completed prior wave(s)' real outputs are materialized on the plan branch at ")
          .Append($"`{integrationWorktreePath}` (granted via `--add-dir`). Read the actual file paths and ")
          .Append("signatures there — NOT the user's checkout — when authoring this wave's tasks/guardrails.\n\n");
        sb.Append("## Contract\n\n");
        sb.Append("- Lean deterministic (tests / regex / exit codes) over prompt-judges.\n");
        sb.Append("- Every task needs >= 1 guardrail; insert guardrail-enabling tasks the brief omits.\n");
        sb.Append("- `dependsOn` is intra-wave only; state fragments are keyed by the wave-qualified id ")
          .Append($"`{wave.Dir}/<taskFolder>`.\n");
        sb.Append("- Self-validate with `guardrails validate` before finishing; the output is a DRAFT a human reviews.\n\n");

        string? skill = TryLoadPlanBreakdownSkill();
        if (skill is not null)
        {
            sb.Append("## plan-breakdown skill (inlined)\n\n");
            sb.Append(skill);
            sb.Append('\n');
        }
        else
        {
            sb.Append("## plan-breakdown skill\n\n");
            sb.Append("Apply the installed `plan-breakdown` skill's full procedure (Step 9, waved breakdown).\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Prepend the RESUME instruction (SSOT §14.11, design 20 §4.5) for a segment after the first: the wave
    /// already carries a valid PREFIX from a cut-off segment, so the remaining work is the manifest's
    /// unsatisfied tail — not the whole wave. Naming the complete folders as DONE is what stops the 232 KB
    /// brief being re-paid for work already on disk, and naming the owed folders is what stops the segment
    /// re-deciding the decomposition (the harness, not the judge, owns completeness — invariant 1).
    /// </summary>
    private static void AppendResumeSection(StringBuilder sb, WaveNode wave, BreakdownResumeContext resume)
    {
        sb.Append($"## RESUME — segment {resume.Segment} of at most {resume.MaxSegments}\n\n");
        sb.Append($"A previous breakdown segment for `{wave.Dir}` was CUT OFF before it finished. Its output ")
          .Append("is VALID and has been KEPT: do NOT re-author it, do NOT delete it, and do NOT re-plan the ")
          .Append("decomposition. Author ONLY the folders still owed, exactly as declared.\n\n");
        sb.Append($"- Declared decomposition: `{wave.Dir}/state/{BreakdownIntent.FileName}` ")
          .Append($"({resume.DeclaredCount} task(s))\n");
        sb.Append($"- Already complete ({resume.CompleteFolders.Count}): ")
          .Append(resume.CompleteFolders.Count == 0 ? "(none)" : string.Join(", ", resume.CompleteFolders))
          .Append('\n');
        sb.Append($"- Still owed ({resume.OwedFolders.Count}): ")
          .Append(resume.OwedFolders.Count == 0 ? "(none)" : string.Join(", ", resume.OwedFolders))
          .Append("\n\n");
        sb.Append("Write each owed task folder COMPLETELY (its `task.json` AND its action file AND its ")
          .Append("guardrails) before starting the next one, so a further cut-off still leaves a valid ")
          .Append("prefix. Leave the manifest in place; the harness removes it when the wave is finished.\n\n");
    }

    /// <summary>
    /// Best-effort locate + read the bundled <c>plan-breakdown</c> SKILL.md so it can be inlined. The tool
    /// bundles skills beside the entry assembly under <c>skills/</c> (dev-knowledge: the packer sweeps
    /// copy-to-output content into the nupkg next to <see cref="AppContext.BaseDirectory"/>); a test host has
    /// no such folder, so this returns null and the composer falls back to naming the installed skill.
    /// </summary>
    /// <summary>
    /// Load the <c>plan-breakdown</c> skill bundled BESIDE THE INSTALLED TOOL, for inlining into a
    /// breakdown prompt. Internal rather than private since #498: the initial-breakdown path needs the
    /// same copy, and the copy is the point — three different <c>plan-breakdown/SKILL.md</c> files exist
    /// on a developer box (<c>~/.claude/skills</c>, a repo's tracked <c>.claude/skills</c>, and this one),
    /// and only this one is version-matched to the harness by construction. Both entry points must inline
    /// the SAME one or they can author to different doctrine from the same tool.
    /// </summary>
    internal static string? TryLoadPlanBreakdownSkill()
    {
        try
        {
            string candidate = Path.Combine(
                AppContext.BaseDirectory, "skills", "plan-breakdown", "SKILL.md");
            return File.Exists(candidate) ? File.ReadAllText(candidate) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// One breakdown segment's composed prompt plus every path and budget it will use — the product of
/// <see cref="WaveBreakdownInvoker.PrepareInvocation"/>, handed straight back to
/// <see cref="WaveBreakdownInvoker.InvokeAsync"/>. It exists so the caller can announce the phase (design 23
/// §5.1) with the REAL stream path before the session starts, rather than reconstructing it from a naming
/// convention the invoker owns.
/// <para>PUBLIC since #498, when <see cref="InitialBreakdownInvoker"/> gave the CLI a second producer of
/// one. <c>Guardrails.Cli</c> deliberately has NO <c>InternalsVisibleTo</c> into this assembly, so a public
/// DTO is the sanctioned way across that boundary — the same route <see cref="WaveBreakdownOutcome"/>
/// already takes. Widening this record is a smaller change than a wrapper type whose only purpose would be
/// to dodge the accessibility, and far smaller than opening the internals.</para>
/// </summary>
public sealed record BreakdownInvocationPlan
{
    /// <summary>The composed invocation prompt (already teed to <see cref="ComposedPromptPath"/>).</summary>
    public required string Prompt { get; init; }

    /// <summary>Where the composed prompt was teed (SSOT §8).</summary>
    public required string ComposedPromptPath { get; init; }

    /// <summary>The composed prompt's UTF-8 size. Log-site evidence only — never a live surface (design 23 §4).</summary>
    public required long ComposedPromptBytes { get; init; }

    /// <summary>Where the runner will tee its JSONL stream — the liveness stat target.</summary>
    public required string StreamLogPath { get; init; }

    /// <summary>Where the runner will tee the groomed transcript.</summary>
    public required string TranscriptLogPath { get; init; }

    /// <summary>This segment's <c>--max-turns</c> ceiling.</summary>
    public required int MaxTurns { get; init; }
}

/// <summary>The outcome of one <see cref="WaveBreakdownInvoker.InvokeAsync"/> — advisory only; the deterministic
/// <c>guardrails validate</c> gate (run by the caller) is the actual verdict on the authored wave.</summary>
public sealed record WaveBreakdownOutcome
{
    /// <summary>True when the runner produced a terminal result without error (the authoring session itself completed).</summary>
    public required bool ProcessCompleted { get; init; }

    /// <summary>
    /// WHY the session stopped, as the runner already classified it (SSOT §9, issue #385 milestone 1).
    /// The invoker used to discard this, which is why the halt could only say "the breakdown invocation did
    /// not complete cleanly" and the operator was left guessing between a skill bug and a budget — and why
    /// the two measured truncations had to be diagnosed from file mtimes. Two of these values point at two
    /// DIFFERENT remedies, and only one of them is a budget.
    /// </summary>
    public PromptFailureKind FailureKind { get; init; } = PromptFailureKind.None;

    /// <summary>Turns the runner reported for the session; null when unknown. The evidence for §3.2's verdict that the turn cap was never the binding constraint.</summary>
    public int? NumTurns { get; init; }

    /// <summary>The turn CEILING this invocation was given, so <see cref="NumTurns"/> can be read against it.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>A short human-readable summary of the runner outcome, for the breakdown log / halt detail.</summary>
    public string? Summary { get; init; }

    /// <summary>Set only when the invocation FAULTED (the runner threw) — carried into a <c>BreakdownFailed</c> halt's detail.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// What the session cost, or null when the runner reported nothing. On the WAVE path this is already
    /// charged to the journal's overhead sink and the field is informational; on the initial-breakdown path
    /// (#498) there IS no journal, so this is the only place the spend is reported at all — a
    /// <c>guardrails breakdown</c> that silently spent money would be the worst kind of quiet.
    /// </summary>
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// True only when the authoring session reached a clean terminal result. A session that was CUT OFF —
    /// timeout, turn cap, output cap, a fault, or simply no terminal result — is never clean, and per
    /// SSOT §14.4 such a session can NEVER be reported <c>BreakdownComplete</c> whatever <c>validate</c> says:
    /// a valid prefix that reads as a finished wave is strictly worse than today's loud quarantine.
    /// </summary>
    public bool TerminatedCleanly =>
        ProcessCompleted && Error is null && FailureKind == PromptFailureKind.None;

    /// <summary>
    /// The runner's own stop classification as the SSOT §9 kebab token, or null when the session ended
    /// cleanly. Carried to <see cref="IRunObserver.WaveBreakdownFinished"/> so a UI can name the bound
    /// without re-deriving it from prose (design 23 §10.1).
    /// </summary>
    public string? FailureKindToken => FailureKind switch
    {
        PromptFailureKind.None => Error is { Length: > 0 } ? BreakdownFailureTokens.Error : null,
        PromptFailureKind.Timeout => BreakdownFailureTokens.Timeout,
        PromptFailureKind.MaxTurns => BreakdownFailureTokens.MaxTurns,
        PromptFailureKind.OutputCap => BreakdownFailureTokens.OutputCap,
        PromptFailureKind.Transient => BreakdownFailureTokens.Transient,
        _ => BreakdownFailureTokens.Error
    };

    /// <summary>The bound the session hit, in the operator's words — the halt-detail sentence for milestone 1.</summary>
    public string CutOffCause => FailureKind switch
    {
        PromptFailureKind.Timeout => "was CUT OFF by the breakdown timeout",
        PromptFailureKind.MaxTurns => MaxTurns is { } cap
            ? $"ran out of TURNS (cap {cap})"
            : "ran out of TURNS",
        PromptFailureKind.OutputCap => "hit the runner's OUTPUT-TOKEN cap",
        PromptFailureKind.Transient => "stopped on a transient runner condition (rate limit / overload)",
        PromptFailureKind.Error => Error is { Length: > 0 } err
            ? $"FAULTED: {err}"
            : "reported an error",
        _ => "did not reach a terminal result"
    };
}

/// <summary>
/// The bounded RESUME context for a breakdown segment after the first (SSOT §14.11, design 20 §4.5): what the
/// manifest declared, what is already complete on disk, and what is still owed. Computed by the harness from
/// the declared list plus the loader's completeness predicate — never from the breakdown's own opinion of
/// whether it finished (invariant 1).
/// </summary>
public sealed record BreakdownResumeContext
{
    /// <summary>This segment's 1-based number (the first invocation is segment 1 and carries no resume context).</summary>
    public required int Segment { get; init; }

    /// <summary>The hard segment cap for one wave in one run.</summary>
    public required int MaxSegments { get; init; }

    /// <summary>How many task folders the manifest declared.</summary>
    public required int DeclaredCount { get; init; }

    /// <summary>Declared folders that already exist COMPLETE on disk.</summary>
    public required IReadOnlyList<string> CompleteFolders { get; init; }

    /// <summary>Declared folders with no complete task folder yet — this segment's whole job.</summary>
    public required IReadOnlyList<string> OwedFolders { get; init; }
}
