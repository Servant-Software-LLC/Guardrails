using System.CommandLine;
using System.Text.Json;
using Guardrails.Core.Breakdown;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails breakdown &lt;plan.md&gt;</c> — author a plan folder from a plain-markdown plan by driving
/// the bundled <c>plan-breakdown</c> skill through the shipped <see cref="IPromptRunner"/> seam (#498).
///
/// <para><b>Why a verb and not just the skill.</b> Until this existed, the ONLY way to turn a plan into a
/// folder was to be a Claude Code session with <c>plan-breakdown</c> loaded and type <c>/plan-breakdown</c>
/// — a hard dependency on one host. The harness could already invoke a breakdown (JIT waves do it through
/// <c>WaveBreakdownInvoker</c>); it simply could not be ASKED to. That gap is what blocks an unattended
/// pipeline (#496) and it is equally in the way of any other agent.</para>
///
/// <para><b>It never marks the plan reviewed.</b> The output is a DRAFT, exactly as the interactive skill's
/// is, and GR2025 keeps firing until <c>/guardrails-review</c> has run and <c>mark-reviewed</c> has stamped
/// it. A CLI door must not hand back what the review gate exists to withhold.</para>
/// </summary>
public static class BreakdownCommand
{
    /// <summary>
    /// Exit code for "the folder was authored, but it is not clean" — validate reported errors, or the
    /// authoring session was cut off. Distinct from <see cref="ExitCodes.HarnessError"/> (1, the tool could
    /// not do the job) because the two need opposite responses: a 2 means READ THE FOLDER, a 1 means fix the
    /// invocation. Same §7 "completed but an actionable condition was found" meaning `lock --check` and
    /// `graph --check` use.
    /// </summary>
    private const int NotCleanExitCode = 2;

    public static Command Create(IConsoleIo io)
    {
        var planArgument = new Argument<string>("plan")
        {
            Description = "Plain-markdown plan to break down (a brief, or `charter handoff` output)."
        };

        var outOption = new Option<string?>("--out")
        {
            Description = "Plan folder to author. Default: the plan's filename minus .md, beside it."
        };

        var runnerConfigOption = new Option<string?>("--runner-config")
        {
            Description = "Borrow promptRunners from an existing guardrails.json. Default: a bundled `claude` runner."
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Author into a non-empty target folder. Without it a non-empty target is refused."
        };

        var noValidateOption = new Option<bool>("--no-validate")
        {
            Description = "Skip the post-authoring `validate` gate (diagnostic use only; the folder is then unverified)."
        };

        var command = new Command(
            "breakdown",
            "Author a plan folder from a plain-markdown plan (drives the bundled plan-breakdown skill).");
        command.Add(planArgument);
        command.Add(outOption);
        command.Add(runnerConfigOption);
        command.Add(forceOption);
        command.Add(noValidateOption);

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(planArgument)!,
            parseResult.GetValue(outOption),
            parseResult.GetValue(runnerConfigOption),
            parseResult.GetValue(forceOption),
            parseResult.GetValue(noValidateOption),
            io,
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string planPath,
        string? outFolder,
        string? runnerConfigPath,
        bool force,
        bool noValidate,
        IConsoleIo io,
        CancellationToken ct)
    {
        string plan = Path.GetFullPath(planPath);

        if (!File.Exists(plan))
        {
            io.Out.WriteLine($"ERROR: plan not found: {plan}");
            return ExitCodes.HarnessError;
        }

        // A .charter.md is REFUSED, not interpreted. Guardrails takes no Charter dependency
        // (plan-breakdown Step 0c) and the flattening rules live in Charter's `charter-format` skill;
        // guessing at `:::` blocks here would fork a contract this repo deliberately does not own.
        if (plan.EndsWith(".charter.md", StringComparison.OrdinalIgnoreCase))
        {
            io.Out.WriteLine(
                $"ERROR: {Path.GetFileName(plan)} is a Charter plan. This verb consumes PLAIN markdown.\n" +
                "  Flatten it first:  charter handoff <plan.charter.md> -o <plan.md>\n" +
                "  (Guardrails deliberately takes no Charter dependency, so it will not interpret ::: blocks.)");
            return ExitCodes.HarnessError;
        }

        string outputFolder = Path.GetFullPath(
            outFolder ?? Path.Combine(
                Path.GetDirectoryName(plan) ?? ".",
                Path.GetFileNameWithoutExtension(plan)));

        if (Directory.Exists(outputFolder)
            && Directory.EnumerateFileSystemEntries(outputFolder).Any()
            && !force)
        {
            io.Out.WriteLine(
                $"ERROR: {outputFolder} already exists and is not empty.\n" +
                "  Re-run with --force to author into it anyway, or pass --out <dir> for a fresh target.\n" +
                "  (To REGENERATE a plan while preserving human guardrail edits, use the merge flow:\n" +
                "   author into a staging dir, then `guardrails merge <folder> --remote <staging>`.)");
            return ExitCodes.HarnessError;
        }

        if (!TryResolveRunner(runnerConfigPath, io, out IPromptRunner? runner, out string runnerDescription))
        {
            return ExitCodes.HarnessError;
        }

        string workspace = Path.GetDirectoryName(plan) ?? Directory.GetCurrentDirectory();
        string logDir = Path.Combine(outputFolder, "logs", "breakdown");

        BreakdownInvocationPlan prepared;
        try
        {
            Directory.CreateDirectory(outputFolder);
            prepared = InitialBreakdownInvoker.PrepareInvocation(plan, outputFolder, logDir);
        }
        catch (Exception ex)
        {
            io.Out.WriteLine($"ERROR: could not prepare the breakdown: {ex.Message}");
            return ExitCodes.HarnessError;
        }

        // Announce BEFORE the session, not after: this can run for up to 30 minutes, and an operator
        // staring at a silent terminal cannot tell healthy-slow from hung. Same reason design 23 split
        // PrepareInvocation out of the wave path.
        io.Out.WriteLine($"Breaking down {plan}");
        io.Out.WriteLine($"  into:    {outputFolder}");
        io.Out.WriteLine($"  runner:  {runnerDescription}");
        io.Out.WriteLine($"  budget:  {prepared.MaxTurns} turns, {WaveBreakdownInvoker.BreakdownTimeout.TotalMinutes:0} min");
        io.Out.WriteLine($"  stream:  {prepared.StreamLogPath}");
        io.Out.WriteLine("");

        var invoker = new InitialBreakdownInvoker(runner!);
        WaveBreakdownOutcome outcome = await invoker.InvokeAsync(prepared, workspace, ct).ConfigureAwait(false);

        if (outcome.CostUsd is { } cost)
        {
            io.Out.WriteLine($"Breakdown spend: ${cost:0.00}");
        }

        if (!outcome.TerminatedCleanly)
        {
            io.Out.WriteLine(
                $"BREAKDOWN DID NOT COMPLETE CLEANLY ({outcome.FailureKindToken ?? "unknown"}).\n" +
                $"  {outcome.Error ?? outcome.Summary ?? "(no detail reported)"}\n" +
                $"  Turns: {outcome.NumTurns?.ToString() ?? "?"} of {outcome.MaxTurns?.ToString() ?? "?"}\n" +
                $"  Anything authored is on disk at {outputFolder} — inspect it before re-running; a cut-off\n" +
                "  session can leave a VALID PREFIX, which is worth keeping rather than re-authoring blind.");
            return NotCleanExitCode;
        }

        if (noValidate)
        {
            io.Out.WriteLine(
                $"Authored {outputFolder} (validate SKIPPED — the folder is unverified).\n" +
                "This is a DRAFT: review it, then run /guardrails-review before `guardrails run`.");
            return ExitCodes.Success;
        }

        // The deterministic gate, exactly as the JIT path applies it: a breakdown that does not validate
        // is a failed breakdown, whatever the session reported about itself.
        PlanProbe.Result result = PlanProbe.LoadAndValidate(outputFolder);
        PlanProbe.PrintDiagnostics(result.Diagnostics, io.Out);

        if (result.HasErrors)
        {
            io.Out.WriteLine(
                $"\nAuthored {outputFolder}, but it does NOT validate — see the errors above.\n" +
                "  The folder is on disk. Fix it by hand, or re-run with --force to re-author.");
            return NotCleanExitCode;
        }

        // The declared-count gate (#500, design 24 §4), on the same post-return path as validate: compare
        // what the HARNESS read from the plan source against what the AGENT produced. A breakdown that
        // never ran the delegated-decision scan authors no decisions.md at all, so M = 0 — the one case
        // the plan-root preflight structurally cannot catch, because that preflight is authored by the
        // very agent it polices. This ADDS a reason to fail; it removes none.
        if (TryReadDeclaredDelegatedDecisions(outputFolder, io, out int declaredDelegatedDecisions))
        {
            DeclaredCountGateResult gate = DeclaredCountGate.Evaluate(declaredDelegatedDecisions, outputFolder);
            if (!gate.Passed)
            {
                // The gate's own message already names N, M and both limits of the check — print it
                // rather than paraphrasing it, so the operator reads the same words the tests pin.
                io.Out.WriteLine(
                    $"\nAuthored {outputFolder}, but the DELEGATED-DECISION COUNT GATE failed:\n" +
                    $"  {gate.FailureMessage}\n" +
                    "  The folder is on disk. Fix it by hand, or re-run with --force to re-author.");
                return NotCleanExitCode;
            }
        }

        io.Out.WriteLine(
            $"\nOK: authored and validated {outputFolder}\n" +
            "This is a DRAFT. Review the folder — especially the guardrails — then run\n" +
            $"  /guardrails-review {outputFolder}\n" +
            "before executing it with `guardrails run`. This command does NOT mark the plan reviewed.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Read N — the declared delegated-decision count — back out of the <c>state/plan-source.json</c> that
    /// <see cref="InitialBreakdownInvoker.PrepareInvocation"/> wrote before the session started. That
    /// artifact is the HARNESS's own reading of the plan; re-parsing the markdown here would be a second
    /// reading that could disagree with it, and then the gate would be arguing with the record instead of
    /// with the folder.
    /// <para>Returns <see langword="false"/> — and SAYS SO — when the artifact is absent, unreadable, or
    /// carries no count. The provenance write is best-effort by design, and a missing record is no
    /// evidence that the folder under-records; but a gate that silently stops running is exactly the
    /// shape of failure this gate exists to catch, so its absence is reported rather than swallowed.</para>
    /// </summary>
    private static bool TryReadDeclaredDelegatedDecisions(string outputFolder, IConsoleIo io, out int declared)
    {
        declared = 0;
        string recordPath = Path.Combine(outputFolder, "state", "plan-source.json");

        if (!File.Exists(recordPath))
        {
            io.Out.WriteLine(
                $"\nNOTE: {recordPath} is missing, so the delegated-decision count gate did NOT run.");
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(recordPath));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                // Matched case-insensitively against the property itself: PlanSourceRecord owns the
                // camelCase naming policy that produced this file, and `nameof` plus an insensitive
                // compare means a rename there fails the BUILD rather than turning this gate inert.
                if (string.Equals(
                        property.Name,
                        nameof(PlanSourceRecord.DeclaredDelegatedDecisions),
                        StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out declared))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            io.Out.WriteLine(
                $"\nNOTE: {recordPath} could not be read ({ex.Message}), so the delegated-decision count " +
                "gate did NOT run.");
            return false;
        }

        io.Out.WriteLine(
            $"\nNOTE: {recordPath} records no {nameof(PlanSourceRecord.DeclaredDelegatedDecisions)}, so " +
            "the delegated-decision count gate did NOT run.");
        return false;
    }

    /// <summary>
    /// Resolve the prompt runner. The chicken-and-egg this verb has to solve: <c>promptRunners</c> normally
    /// lives in the plan folder's <c>guardrails.json</c>, which does not exist yet at initial-breakdown time.
    /// So it is <c>--runner-config</c> (borrow one) first, else a bundled default <c>claude</c> runner —
    /// which is what an interactive session would have used anyway.
    /// </summary>
    private static bool TryResolveRunner(
        string? runnerConfigPath,
        IConsoleIo io,
        out IPromptRunner? runner,
        out string description)
    {
        runner = null;
        description = "";

        if (string.IsNullOrWhiteSpace(runnerConfigPath))
        {
            runner = new ClaudePromptRunner("claude", "claude", new ProcessRunner());
            description = "claude (built-in default; pass --runner-config to borrow a configured one)";
            return true;
        }

        string configPath = Path.GetFullPath(runnerConfigPath);
        if (!File.Exists(configPath))
        {
            io.Out.WriteLine($"ERROR: --runner-config not found: {configPath}");
            return false;
        }

        // Borrow the promptRunners of an existing plan by loading it and asking its registry for the
        // default. Reusing the real loader means a borrowed config gets the SAME validation a run would
        // give it, instead of a second bespoke parse that could accept something the harness would reject.
        string configFolder = Path.GetDirectoryName(configPath) ?? ".";
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(configFolder);
        if (probe.Plan is null)
        {
            io.Out.WriteLine(
                $"ERROR: could not load a plan from {configFolder} to borrow promptRunners from.\n" +
                "  --runner-config expects a guardrails.json inside a loadable plan folder.");
            return false;
        }

        try
        {
            PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(probe.Plan.Config, new ProcessRunner());
            runner = registry.Resolve(null);
            description = $"{registry.DefaultRunnerName ?? "(default)"} (borrowed from {configPath})";
            return true;
        }
        catch (Exception ex)
        {
            // The registry refuses a config it cannot serve — an unimplemented `kind`, or no resolvable
            // default. That is exactly the pre-run signal worth having, so report it rather than falling
            // back to the built-in runner and silently ignoring what the operator asked for.
            io.Out.WriteLine($"ERROR: {configPath} does not yield a usable prompt runner: {ex.Message}");
            return false;
        }
    }
}
