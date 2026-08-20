using System.CommandLine;
using System.Text.RegularExpressions;
using Guardrails.Core.Graph;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using Spectre.Console;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails graph [folder] [--check] [--stdout] [--format mermaid]</c> — render the
/// plan's task/guardrail DAG as a Mermaid <c>flowchart TD</c> (SSOT §10). Default: write
/// <c>&lt;folder&gt;/diagram.md</c> (a provenance comment + a fenced <c>mermaid</c> block + a
/// one-line structure-only caption after the fence). <c>--check</c> writes nothing and reports
/// staleness via exit code (0 fresh, 2 stale/missing, 1 on a load/validate error). <c>--stdout</c>
/// prints the diagram instead of writing a file. Defaults to the current directory when the
/// folder is omitted.
/// <para>
/// When the supplied folder matches the wave-dir pattern (<c>^wave-([0-9]+)-[a-z0-9-]+$</c>)
/// AND its parent contains a <c>guardrails.json</c>, the command renders a wave-scoped sub-diagram:
/// only that wave's task DAG plus its entry/exit gates. The output files (<c>diagram.md</c> and
/// <c>diagram.html</c>) are written to the wave folder so the per-wave review pause can surface
/// just that wave (SSOT §14, issue #355).
/// </para>
/// <para>
/// When the supplied folder is a WAVED PLAN ROOT the command operates on EVERY diagram that plan
/// owns — the plan-level one PLUS one per <c>wave-NN-slug/</c> folder (see
/// <see cref="DiagramTargets"/>, issue #447). Before that fix a regenerate wrote only the
/// plan-level file while <c>--check</c> validated only the plan-level file, so a waved plan could
/// not be brought fresh by the documented command AND <c>--check</c> then reported a false "fresh"
/// over demonstrably stale per-wave diagrams — the very signal <c>/guardrails-review</c> branches
/// on, and a mid-run regeneration source that dirties tracked files.
/// </para>
/// </summary>
public static partial class GraphCommand
{
    private const string DiagramFileName = "diagram.md";
    private const string DiagramHtmlFileName = "diagram.html";

    /// <summary>
    /// Exit code returned by <c>--check</c> when <c>diagram.md</c> is stale OR missing — the
    /// "regenerate" signal (SSOT §7: exit 2 = "the operation completed but an actionable
    /// condition was found"). Distinct from <see cref="ExitCodes.HarnessError"/> (1), which a
    /// genuine load/validate failure returns, so CI can tell "regenerate the diagram" apart
    /// from "the plan is broken". Deliberately NOT added to the shared <see cref="ExitCodes"/>
    /// class: it shares the numeric value of <see cref="ExitCodes.TaskFailed"/> (2) by design
    /// (both are the §7 "actionable condition found" code) but is a graph-specific meaning, so
    /// it lives here next to its only caller rather than aliasing the run-time constant.
    /// </summary>
    private const int StaleExitCode = 2;

    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var checkOption = new Option<bool>("--check")
        {
            Description = "Report whether diagram.md (and diagram.html if present) are up to date (exit 0 fresh, 2 stale/missing, 1 on a load/validate error); writes nothing. On a waved plan EVERY wave's diagram is checked too, and one line is printed per stale/missing file. A missing diagram.html is not stale — only a present-but-hash-mismatched one is."
        };

        var stdoutOption = new Option<bool>("--stdout")
        {
            Description = "Print the diagram to stdout instead of writing diagram.md (writes nothing to disk). On a waved plan this prints the plan-level diagram only."
        };

        var noHtmlOption = new Option<bool>("--no-html")
        {
            Description = "Write only diagram.md; skip the interactive diagram.html navigation companion (at every scope — plan-level and per-wave). Has no effect when combined with --stdout (which writes nothing to disk)."
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Diagram format. Only 'mermaid' is supported for now.",
            DefaultValueFactory = _ => "mermaid"
        };
        // Reserved per SSOT §10: the parsed value is intentionally unconsumed until a second
        // format exists. Only the AcceptOnlyFromAmong("mermaid") rejection below is active today.
        formatOption.AcceptOnlyFromAmong("mermaid");

        var command = new Command("graph", "Render a Mermaid diagram of a plan folder's task/guardrail DAG.");
        command.Add(folderArgument);
        command.Add(checkOption);
        command.Add(stdoutOption);
        command.Add(formatOption);
        command.Add(noHtmlOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            bool check = parseResult.GetValue(checkOption);
            bool toStdout = parseResult.GetValue(stdoutOption);
            bool noHtml = parseResult.GetValue(noHtmlOption);
            return Execute(folder, check, toStdout, noHtml, io);
        });

        return command;
    }

    private static int Execute(string folder, bool check, bool toStdout, bool noHtml, IConsoleIo io)
    {
        TextWriter output = io.Out;

        // Wave-scoped sub-diagram: when the caller targets a wave folder (name matches the
        // wave-dir pattern and the parent has guardrails.json) load only that wave's slice of
        // the plan and write diagram.md / diagram.html into the wave folder itself — so the
        // per-wave review pause surfaces just that wave (SSOT §14, issue #355).
        if (IsWaveFolder(folder))
        {
            PlanDefinition? wavePlan = LoadWaveScoped(folder, output);
            if (wavePlan is null) return ExitCodes.HarnessError;
            return Render([wavePlan], check, toStdout, noHtml, io);
        }

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return ExitCodes.HarnessError;
        }

        return Render(DiagramTargets(probe.Plan), check, toStdout, noHtml, io);
    }

    /// <summary>
    /// Every diagram a plan folder OWNS, in write/report order: the plan-level diagram FIRST, then
    /// — for a WAVED plan (SSOT §14) — one wave-scoped projection per wave, each carrying its own
    /// wave folder as <see cref="PlanDefinition.PlanDirectory"/> so it renders/checks
    /// <c>&lt;wave&gt;/diagram.{md,html}</c>.
    /// <para>
    /// This list IS the fix for issue #447. A waved plan's per-wave diagrams are first-class
    /// artifacts of the plan folder — the run writes them at every wave boundary
    /// (<see cref="RenderWaveScoped"/>) and the per-wave review pause reads them — but the
    /// <c>graph</c> command used to treat the plan-level file as the plan's ONLY diagram. That made
    /// the two halves of the contract lie in opposite directions: a regenerate could not bring the
    /// wave files fresh, and <c>--check</c> then reported exit 0 over stale ones (a
    /// <c>/guardrails-review</c> pass recorded "diagram fresh" while two waves were stale, and the
    /// next run silently rewrote them mid-flight). Deriving BOTH the write set and the check set
    /// from this one list is what keeps them from drifting apart again.
    /// </para>
    /// <para>
    /// The plan-level target stays FIRST because it is the primary artifact: it is what
    /// <c>--stdout</c> prints and the one <c>Diagram (interactive):</c> link points at (the line
    /// <c>plan-breakdown</c>'s SKILL.md Step 7 relays verbatim, issues #249/#256 — which is why the
    /// per-wave fan-out must never emit a second link line).
    /// </para>
    /// </summary>
    private static IReadOnlyList<PlanDefinition> DiagramTargets(PlanDefinition plan) =>
        plan.IsWaved
            ? [plan, .. plan.Waves.Select(wave => ProjectWave(plan, wave))]
            : [plan];

    /// <summary>
    /// Render a wave-scoped diagram (<c>diagram.md</c> + <c>diagram.html</c>) into
    /// <paramref name="waveDir"/> — both generated, non-authored files excluded from
    /// <c>guardrails.baseline</c> (SSOT §10). Returns <c>true</c> on success; on any load or
    /// render failure writes a one-line diagnostic to <paramref name="errorOutput"/> and returns
    /// <c>false</c>. Best-effort: a failure never crashes the caller.
    /// <para>
    /// Called by <see cref="RunCommand"/> at the JIT wave checkpoint (issue #359, §14.4) to surface
    /// a focused wave diagram alongside the checkpoint message, and at wave-start on re-run so the
    /// diagram reflects the now-authored tasks before execution begins.
    /// </para>
    /// </summary>
    public static bool RenderWaveScoped(string waveDir, TextWriter errorOutput)
    {
        if (!IsWaveFolder(waveDir))
        {
            return false;
        }

        PlanDefinition? wavePlan = LoadWaveScoped(waveDir, errorOutput);
        if (wavePlan is null) return false;

        try
        {
            // The SAME writer the `graph` command uses, so the run's wave-boundary regeneration and
            // an explicit `guardrails graph` produce byte-identical files (issue #447: they must
            // never drift, or a run would keep re-dirtying files `graph --check` calls fresh).
            WriteDiagramFiles(wavePlan, noHtml: false);
            return true;
        }
        catch (Exception ex)
        {
            errorOutput.WriteLine($"  [graph] wave diagram render failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="folder"/> names a wave folder of a waved plan (rather than
    /// a coincidentally-named standalone folder). Delegates to <see cref="WaveFolder"/>, which owns the ONE
    /// spelling of the wave-dir pattern and of wave-target resolution — this used to carry a third private
    /// copy of the regex, kept in step with the loader's by convention alone.
    /// </summary>
    private static bool IsWaveFolder(string folder) =>
        WaveFolder.TryResolveWaveTarget(folder, out _, out _);

    /// <summary>
    /// Load the parent plan and project it down to a wave-scoped <see cref="PlanDefinition"/>
    /// whose <see cref="PlanDefinition.PlanDirectory"/> is the wave folder, so the renderer
    /// writes <c>diagram.md</c> / <c>diagram.html</c> into the wave folder. Returns
    /// <c>null</c> (and prints a diagnostic) on any load/validate error or if the wave folder
    /// does not appear in the parent plan's wave list.
    /// </summary>
    private static PlanDefinition? LoadWaveScoped(string folder, TextWriter output)
    {
        // The same resolution `plan-hash` / `mark-reviewed` use (issue #472): up to the parent plan, then
        // select the wave. A wave that resolves by path but is absent from the loaded plan comes back as an
        // error diagnostic from the probe, so there is one message for that case, not two.
        PlanProbe.Result probe = PlanProbe.LoadAndValidateTarget(folder);
        if (probe.HasErrors || probe.Plan is null || probe.Wave is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return null;
        }

        return ProjectWave(probe.Plan, probe.Wave);
    }

    /// <summary>
    /// Project a loaded plan down to a wave-scoped slice: only this wave's tasks, entry gate, and
    /// exit gate; <see cref="PlanDefinition.PlanDirectory"/> set to the wave folder so
    /// <c>diagram.md</c> / <c>diagram.html</c> land THERE, and <see cref="PlanDefinition.Waves"/>
    /// cleared so <see cref="MermaidRenderer"/> sees a flat-plan shape (no wave headers) and
    /// <see cref="GraphSourceHash"/> keys the file on that wave's own content.
    /// <para>
    /// Shared by the wave-folder invocation (<c>guardrails graph &lt;plan&gt;/&lt;wave&gt;</c>, via
    /// <see cref="LoadWaveScoped"/>) and the waved-plan-root fan-out
    /// (<see cref="DiagramTargets"/>) — one projection, so a wave's diagram is byte-identical no
    /// matter which of the two routes wrote it.
    /// </para>
    /// </summary>
    private static PlanDefinition ProjectWave(PlanDefinition plan, WaveNode wave) =>
        plan with
        {
            PlanDirectory = wave.Directory,
            Tasks = wave.Tasks,
            Waves = [],
            PlanPreflights = wave.Preflights,
            PlanGuardrails = wave.Guardrails,
        };

    /// <summary>
    /// Shared render path for the flat-plan, waved-plan, and wave-scoped code paths. Drives the
    /// full write / <c>--check</c> / <c>--stdout</c> pipeline against every diagram the invoked
    /// folder owns (<see cref="DiagramTargets"/>). Each target's <c>PlanDirectory</c> is its own
    /// output directory — the plan root for the plan-level diagram, the wave folder for a
    /// wave-scoped one — and <paramref name="targets"/>[0] is always the PRIMARY (plan-level, or
    /// the wave itself for a wave-folder invocation).
    /// </summary>
    private static int Render(
        IReadOnlyList<PlanDefinition> targets, bool check, bool toStdout, bool noHtml, IConsoleIo io)
    {
        TextWriter output = io.Out;
        PlanDefinition primary = targets[0];

        if (check)
        {
            return Check(targets, output);
        }

        if (toStdout)
        {
            // --stdout is a "show me the diagram" affordance, not a regeneration: it prints the
            // PRIMARY diagram only. Concatenating a waved plan's per-wave diagrams into one stream
            // would produce several `flowchart TD` documents with nothing to delimit them; a caller
            // who wants one wave's source asks for it by folder (`graph <plan>/<wave> --stdout`).
            output.WriteLine(MermaidRenderer.Render(primary));
            return ExitCodes.Success;
        }

        // One line per file written — on a waved plan this is the ONLY place a reader can see that
        // the per-wave diagrams were regenerated too. The single-line "Wrote <plan>/diagram.md" this
        // replaces is precisely why nobody noticed only one file was ever being produced (#447).
        foreach (PlanDefinition target in targets)
        {
            output.WriteLine($"Wrote {WriteDiagramFiles(target, noHtml)}");
        }

        // Exactly ONE "Diagram (interactive):" line, for the PRIMARY diagram — plan-breakdown's
        // SKILL.md Step 7 relays this line verbatim as its report's last line (#249/#256), and a
        // per-wave link line each would leave "the" link ambiguous. A wave's own interactive link is
        // still reachable: `guardrails graph <plan>/<wave>` prints it, and the run prints
        // "Wave diagram (focused):" at every wave boundary.
        if (!noHtml)
        {
            PrintDiagramLink(Path.Combine(primary.PlanDirectory, DiagramHtmlFileName), output);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Write ONE target's <c>diagram.md</c> — and, unless <paramref name="noHtml"/>, its
    /// <c>diagram.html</c> companion (issue #33) — into <c>plan.PlanDirectory</c>, returning the
    /// written <c>diagram.md</c> path. <c>diagram.md</c> stays the GitHub render (click-free, since
    /// GitHub sandboxes Mermaid and the targets are <c>file://</c>-local); <c>diagram.html</c> is
    /// the pan/zoom/fullscreen viewer whose nodes click through to their source. Both carry the
    /// SAME <c>source-sha256</c>, so <c>--check</c> governs the pair.
    /// <para>
    /// Both are generated, non-authored artifacts, excluded from the <c>guardrails.baseline</c>
    /// snapshot (SSOT §10/§11) — <see cref="Guardrails.Core.Breakdown.BreakdownManifest"/> drops
    /// them by their manifest-root-relative name, which covers the plan-level pair and a PER-WAVE
    /// baseline (the §11 model: <c>lock</c>/<c>merge</c> take a folder argument and operate on
    /// <c>&lt;plan&gt;/&lt;wave&gt;/</c>). A baseline captured at a WAVED PLAN ROOT sees a wave's
    /// pair as <c>&lt;wave&gt;/diagram.md</c> — two segments — and does NOT drop it. That predates
    /// this command and is not changed by it (the run has always written those files at every wave
    /// boundary), but it is why a plan-root <c>lock --diff</c> on a waved plan lists
    /// <c>ADDED &lt;wave&gt;/diagram.html</c>; fixing it belongs to the manifest, not here.
    /// </para>
    /// <para>
    /// THE single writer: the plan-level path, the per-wave fan-out (<see cref="DiagramTargets"/>),
    /// and <see cref="RenderWaveScoped"/> (the run's wave boundaries) all go through it, so no
    /// scope can ever be written by a divergent second renderer.
    /// </para>
    /// </summary>
    private static string WriteDiagramFiles(PlanDefinition plan, bool noHtml)
    {
        string sourceHash = GraphSourceHash.Compute(plan);
        string diagramPath = Path.Combine(plan.PlanDirectory, DiagramFileName);

        AtomicFile.WriteAllText(diagramPath, ComposeDocument(MermaidRenderer.Render(plan), sourceHash));

        if (!noHtml)
        {
            string interactive = MermaidRenderer.RenderInteractive(plan);
            IReadOnlyDictionary<string, string> taskFolderTargets = MermaidRenderer.TaskFolderTargets(plan);
            AtomicFile.WriteAllText(
                Path.Combine(plan.PlanDirectory, DiagramHtmlFileName),
                HtmlDiagramRenderer.Render(interactive, sourceHash, taskFolderTargets));
        }

        return diagramPath;
    }

    /// <summary>
    /// Print <c>Diagram (interactive): &lt;link&gt;</c> — the line whose <c>file://</c> URI
    /// <c>.claude/skills/plan-breakdown/SKILL.md</c> Step 7 wraps in a Markdown link as the last
    /// line of its breakdown report (issues #249 + #256). In a raw, link-capable terminal this is an OSC 8 hyperlink
    /// via <see cref="RunCommand.Hyperlink"/> (the same escape shape <c>guardrails run</c>'s "Logs"
    /// link and <c>guardrails logs</c>'s static-site link already use). When output is redirected or
    /// the terminal cannot render OSC 8 links — the plan-breakdown skill's case, since it captures
    /// this stdout — it falls back to the absolute <c>file://</c> URI
    /// (<c>new Uri(path).AbsoluteUri</c>) rather than the bare path, so the skill can wrap that URI
    /// in a Markdown link for markdown-rendering hosts (issue #256) without hand-assembling a
    /// <c>file://</c> URL itself. Building the URI in the CLI from .NET's own <see cref="Uri"/> off
    /// the absolute path is what keeps it correct and percent-encoded on every OS (the space in a
    /// path like <c>C:\Dev AI\...</c> becomes <c>%20</c>): before this fix the skill built the URL
    /// from a shell <c>pwd</c>, which under Git Bash/MSYS on Windows returns the non-resolvable mount
    /// form (<c>/f/...</c>) instead of the native drive form (<c>F:/...</c>) a <c>file://</c> URI
    /// needs.
    /// </summary>
    private static void PrintDiagramLink(string diagramHtmlPath, TextWriter output)
    {
        bool linkable = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Links;
        string link = linkable
            ? RunCommand.Hyperlink(diagramHtmlPath, true)
            : new Uri(diagramHtmlPath).AbsoluteUri;
        output.WriteLine($"Diagram (interactive): {link}");
    }

    /// <summary>
    /// <c>--check</c>: report freshness across EVERY diagram the invoked folder owns
    /// (<see cref="DiagramTargets"/>) — exit 0 only when they are ALL fresh; exit
    /// <see cref="StaleExitCode"/> (2), the "regenerate" signal (SSOT §7/§10), when ANY is stale or
    /// missing. A genuine load/validate failure never reaches here — <see cref="Execute"/> returns
    /// <see cref="ExitCodes.HarnessError"/> (1) before <c>--check</c> is dispatched — so CI can
    /// distinguish "regenerate the diagram" (2) from "the plan is broken" (1).
    /// <para>
    /// Every target is evaluated (no short-circuit ACROSS targets) so the output names each stale or
    /// missing file rather than only the first: on a waved plan the caller needs to know WHICH waves
    /// drifted, and a single line was how the #447 under-report hid for so long. The regenerate hint
    /// on every line names the PRIMARY folder, because one <c>guardrails graph &lt;plan&gt;</c> now
    /// fixes them all.
    /// </para>
    /// </summary>
    private static int Check(IReadOnlyList<PlanDefinition> targets, TextWriter output)
    {
        // Trim a trailing separator: `Path.GetFullPath("plan/")` keeps one, and a quoted Windows
        // path ending in a backslash would escape its own closing quote in the copy-pasteable hint
        // (`"C:\Dev AI\plan\"`). This also reproduces, byte for byte, the hint the pre-#447 code
        // built from Path.GetDirectoryName(diagramPath), which never carried one.
        string primaryDir = Path.TrimEndingDirectorySeparator(targets[0].PlanDirectory);
        string regenHint = $"run: guardrails graph {QuoteIfNeeded(primaryDir)}";

        bool allFresh = true;
        foreach (PlanDefinition target in targets)
        {
            allFresh &= CheckOne(target, primaryDir, regenHint, output);
        }

        return allFresh ? ExitCodes.Success : StaleExitCode;
    }

    /// <summary>
    /// Freshness of ONE target's diagram pair: recompute that target's source hash and compare it
    /// to the one embedded in its <c>diagram.md</c> provenance comment. Returns <c>true</c> when
    /// fresh; otherwise writes ONE actionable line naming the offending file and returns
    /// <c>false</c>. Within a target the checks DO short-circuit — a stale <c>diagram.md</c> is
    /// reported alone, because the same regenerate rewrites its <c>diagram.html</c> companion too,
    /// so a second line would add noise, not information.
    /// <para>
    /// The <c>diagram.html</c> rule is applied identically at EVERY scope, plan-level and per-wave:
    /// a MISSING <c>diagram.html</c> is not staleness (the caller may legitimately have used
    /// <c>--no-html</c>, which suppresses the companion at every scope, so counting a missing one
    /// would make <c>--check</c> permanently exit 2 for those callers), but a PRESENT one carrying a
    /// different <c>source-sha256</c> has drifted from its own <c>diagram.md</c> and must regenerate
    /// (issue #33). A missing per-wave <c>diagram.md</c>, by contrast, IS reported — that is the
    /// real-incident case where a wave folder had no diagram at all until a run created one (#447).
    /// </para>
    /// </summary>
    private static bool CheckOne(PlanDefinition target, string primaryDir, string regenHint, TextWriter output)
    {
        string sourceHash = GraphSourceHash.Compute(target);
        string diagramPath = Path.Combine(target.PlanDirectory, DiagramFileName);
        string diagramHtmlPath = Path.Combine(target.PlanDirectory, DiagramHtmlFileName);

        if (!File.Exists(diagramPath))
        {
            output.WriteLine($"{Describe(diagramPath, primaryDir)} missing — {regenHint}");
            return false;
        }

        if (!string.Equals(ReadEmbeddedHash(File.ReadAllText(diagramPath)), sourceHash, StringComparison.Ordinal))
        {
            output.WriteLine($"{Describe(diagramPath, primaryDir)} is stale — {regenHint}");
            return false;
        }

        if (File.Exists(diagramHtmlPath) &&
            !string.Equals(ReadEmbeddedHash(File.ReadAllText(diagramHtmlPath)), sourceHash, StringComparison.Ordinal))
        {
            output.WriteLine($"{Describe(diagramHtmlPath, primaryDir)} is stale — {regenHint}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// How a diagram file is NAMED in <c>--check</c> output: its path relative to the invoked
    /// folder, with forward slashes so the line reads the same on every OS. The plan-level file
    /// renders as bare <c>diagram.md</c> (byte-identical to the pre-#447 message, so a flat plan's
    /// output is unchanged); a wave's renders as <c>wave-03-provision/diagram.md</c>, which is what
    /// tells a reader WHICH wave drifted.
    /// </summary>
    private static string Describe(string diagramPath, string primaryDir) =>
        Path.GetRelativePath(primaryDir, diagramPath).Replace('\\', '/');

    /// <summary>
    /// One-line italic caption written AFTER the closing mermaid fence (SSOT §10). It lives in
    /// the markdown wrapper ONLY — never inside the fenced block, the <see cref="MermaidRenderer"/>
    /// output, or the hashed semantic content — so it does NOT affect <c>source-sha256</c>, the
    /// golden render test, or <c>--stdout</c>, and two regens stay byte-identical. Its job is to
    /// tell a reader the diagram is structure-only: retry, feedback, and needs-human edges are
    /// out of scope for v1 (SSOT §10) and the static flowchart would otherwise read like a
    /// one-pass pipeline.
    /// </summary>
    private const string DiagramCaption =
        "_Structure only — retry, feedback, and needs-human edges are omitted._";

    /// <summary>
    /// Compose the persisted artifact: a single-line provenance comment (SSOT §10), a fenced
    /// <c>mermaid</c> block holding the rendered diagram, a one-line italic caption after the
    /// fence (<see cref="DiagramCaption"/>), and the shared <see cref="MermaidRenderer.LegendMarkdown"/>
    /// block. GitHub's Mermaid sandbox has no overlay-content option, so the legend cannot live
    /// inside the fenced block (a Mermaid-native legend subgraph was prototyped and rendered
    /// broken — see <see cref="MermaidRenderer"/> remarks); a plain Markdown block after the fence
    /// is the only placement that reads correctly on GitHub. The comment carries only the
    /// <c>source-sha256</c> identity — no timestamp — and both the caption and the legend are
    /// outside the hashed content, so re-running <c>graph</c> on an unchanged plan yields a
    /// byte-identical file (a deterministic projection, no git churn) and legend wording changes
    /// never move <c>source-sha256</c>.
    /// </summary>
    private static string ComposeDocument(string diagram, string sourceHash)
    {
        string provenance = $"<!-- guardrails:graph v1 source-sha256={sourceHash} -->";

        return provenance + "\n\n```mermaid\n" + diagram.TrimEnd('\n') + "\n```\n\n" + DiagramCaption + "\n\n"
            + MermaidRenderer.LegendMarkdown;
    }

    /// <summary>
    /// Parse the <c>source-sha256</c> token from the provenance comment, or null if no
    /// recognizable provenance line is present. The regex is anchored to the START of the
    /// document (SSOT §10: the provenance is the first line), so body text echoed into the
    /// mermaid block (e.g. a description) can never be matched as the embedded hash.
    /// </summary>
    private static string? ReadEmbeddedHash(string document)
    {
        Match match = ProvenanceHashRegex().Match(document);
        return match.Success ? match.Groups["hash"].Value : null;
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;

    [GeneratedRegex(@"\A\s*<!--\s*guardrails:graph\s+v1\s+source-sha256=(?<hash>[0-9a-f]+)\b")]
    private static partial Regex ProvenanceHashRegex();

}
