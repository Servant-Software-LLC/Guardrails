using System.Text;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Drives <c>plan-breakdown</c> for the FIRST breakdown of a plan — markdown in, plan folder out —
/// behind <c>guardrails breakdown</c> (#498).
///
/// <para><b>Why this exists as a sibling of <see cref="WaveBreakdownInvoker"/> rather than a method on
/// it.</b> The wave invoker is wave-shaped by construction: its entry point takes a
/// <c>WaveNode</c> and a loaded <c>PlanDefinition</c>, and an initial breakdown has neither — the plan
/// folder it would load is the thing being authored. What the two genuinely share is the INVOCATION,
/// and that is shared by calling <see cref="WaveBreakdownInvoker.InvokeCoreAsync"/> rather than by
/// re-implementing it: the 30-minute timeout, the authoring-tool grant, the stream tee and the
/// preserved <c>FailureKind</c> are all scar tissue from #385/#402/#489, and a second copy of them
/// would drift from the path those were fixed on.</para>
///
/// <para><b>What it deliberately does NOT do: mark anything reviewed.</b> Its output is a DRAFT,
/// exactly as the interactive skill's is. <c>GR2025</c> keeps firing until <c>/guardrails-review</c>
/// has run and <c>mark-reviewed</c> has stamped it. A CLI entry point must not hand back with one
/// command what the review gate exists to withhold — <c>AnswerableGates</c> lists the review gate as
/// NON-answerable, and that is a property of the gate, not of which door authored the folder.</para>
/// </summary>
public sealed class InitialBreakdownInvoker
{
    private readonly WaveBreakdownInvoker _core;

    public InitialBreakdownInvoker(IPromptRunner runner) => _core = new WaveBreakdownInvoker(runner);

    /// <summary>
    /// Compose the prompt, tee it beside the other breakdown logs, and resolve the paths and turn budget
    /// this invocation will use — WITHOUT running anything, mirroring
    /// <see cref="WaveBreakdownInvoker.PrepareInvocation"/> so a caller can report what it is about to
    /// start before a session that may run for half an hour.
    /// </summary>
    /// <param name="planMarkdownPath">The plain-markdown plan to break down.</param>
    /// <param name="outputFolder">The plan folder to author.</param>
    /// <param name="logDir">Where to tee the composed prompt, the stream and the transcript.</param>
    public static BreakdownInvocationPlan PrepareInvocation(
        string planMarkdownPath,
        string outputFolder,
        string logDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planMarkdownPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);

        try { Directory.CreateDirectory(logDir); } catch { /* the invoker retries */ }

        string planText = TryReadPlan(planMarkdownPath);

        // The SAME budget rule the wave path uses (#94/#385): a base plus headroom scaled by how many
        // work-item signals the source declares. A bigger plan gets more turns; the ceiling still binds.
        int maxTurns = WaveBreakdownInvoker.ComputeMaxTurns(
            WaveBreakdownInvoker.EstimateBriefSignalCount(planText));

        string prompt = ComposePrompt(planMarkdownPath, outputFolder, planText);
        string composedPromptPath = Path.Combine(logDir, "composed-prompt.md");
        try { File.WriteAllText(composedPromptPath, prompt); } catch { /* best-effort log tee */ }

        return new BreakdownInvocationPlan
        {
            Prompt = prompt,
            ComposedPromptPath = composedPromptPath,
            ComposedPromptBytes = Encoding.UTF8.GetByteCount(prompt),
            StreamLogPath = Path.Combine(logDir, "claude-stream.jsonl"),
            TranscriptLogPath = Path.Combine(logDir, "transcript.md"),
            MaxTurns = maxTurns
        };
    }

    /// <summary>
    /// Run the prepared invocation. <paramref name="workspaceDirectory"/> is both the sub-process working
    /// directory and its write grant: the output folder lives inside the workspace, so no second
    /// <c>--add-dir</c> is needed — and an initial breakdown has no materialized upstream to grant one for.
    /// </summary>
    public Task<WaveBreakdownOutcome> InvokeAsync(
        BreakdownInvocationPlan prepared,
        string workspaceDirectory,
        CancellationToken ct) =>
        _core.InvokeCoreAsync(
            prepared,
            workingDirectory: workspaceDirectory,
            planDirectory: workspaceDirectory,
            additionalReadDirectory: null,
            // No journal exists yet — there is no plan folder to hold one. The spend comes back on the
            // outcome instead, and the command reports it.
            chargeCost: null,
            ct);

    private static string TryReadPlan(string planMarkdownPath)
    {
        try { return File.ReadAllText(planMarkdownPath); }
        catch { return string.Empty; }
    }

    private static string ComposePrompt(string planMarkdownPath, string outputFolder, string planText)
    {
        var sb = new StringBuilder();

        sb.Append("# Initial plan breakdown (Guardrails harness, #498)\n\n");
        sb.Append("You are being invoked by the `guardrails breakdown` CLI verb, NOT by an interactive ")
          .Append("session. There is no human watching this run.\n\n");

        sb.Append($"Break down the plan at `{planMarkdownPath}` into an executable task DAG with ")
          .Append($"guardrails, authored into `{outputFolder}` — using the `plan-breakdown` skill.\n\n");

        sb.Append("## Target\n\n");
        sb.Append($"- Source plan: `{planMarkdownPath}`\n");
        sb.Append($"- Author the plan folder at: `{outputFolder}`\n\n");

        sb.Append("## Contract\n\n");
        sb.Append("- Apply the skill's FULL procedure, including its self-review steps. Nobody will ")
          .Append("catch a step you skip: there is no human in this loop.\n");
        sb.Append("- Lean deterministic (tests / regex / exit codes) over prompt-judges.\n");
        sb.Append("- Every task needs >= 1 guardrail; insert the guardrail-enabling tasks the plan omits.\n");
        sb.Append("- Every task declares a `writeScope` — real paths, or `[]` when it writes nothing.\n");
        sb.Append("- Run `guardrails validate` on the folder before you finish and fix what it reports. ")
          .Append("The caller runs it again as a deterministic gate, so a folder that does not validate ")
          .Append("is a failed breakdown.\n");
        sb.Append("- **The output is a DRAFT.** Do NOT run `guardrails mark-reviewed`, and do not describe ")
          .Append("the folder as reviewed or ready to run. It still owes `/guardrails-review`.\n\n");

        // The plan text is INLINED as well as pathed. The path alone is enough for a session that can read
        // the file, but inlining makes the prompt self-contained in the tee'd composed-prompt.md, which is
        // the only durable record of what this session was actually asked to do.
        if (!string.IsNullOrWhiteSpace(planText))
        {
            sb.Append("## The plan (inlined verbatim)\n\n");
            sb.Append(planText);
            if (!planText.EndsWith('\n')) { sb.Append('\n'); }
            sb.Append('\n');
        }

        string? skill = WaveBreakdownInvoker.TryLoadPlanBreakdownSkill();
        if (skill is not null)
        {
            sb.Append("## plan-breakdown skill (inlined)\n\n");
            sb.Append(skill);
        }
        else
        {
            // Degrade honestly rather than silently authoring to no doctrine at all.
            sb.Append("## plan-breakdown skill\n\n");
            sb.Append("The bundled skill could not be read. Apply the installed `plan-breakdown` skill's ")
              .Append("full procedure.\n");
        }

        return sb.ToString();
    }
}
