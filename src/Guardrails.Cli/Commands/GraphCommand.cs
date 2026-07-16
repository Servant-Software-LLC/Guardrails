using System.CommandLine;
using System.Text.RegularExpressions;
using Guardrails.Core.Graph;
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
            Description = "Report whether diagram.md (and diagram.html if present) are up to date (exit 0 fresh, 2 stale/missing, 1 on a load/validate error); writes nothing. A missing diagram.html is not stale — only a present-but-hash-mismatched one is."
        };

        var stdoutOption = new Option<bool>("--stdout")
        {
            Description = "Print the diagram to stdout instead of writing diagram.md (writes nothing to disk)."
        };

        var noHtmlOption = new Option<bool>("--no-html")
        {
            Description = "Write only diagram.md; skip the interactive diagram.html navigation companion. Has no effect when combined with --stdout (which writes nothing to disk)."
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
            return Render(wavePlan, check, toStdout, noHtml, io);
        }

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return ExitCodes.HarnessError;
        }

        return Render(probe.Plan, check, toStdout, noHtml, io);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="folder"/> names a wave folder: its leaf name
    /// matches the wave-dir pattern (<c>^wave-([0-9]+)-[a-z0-9-]+$</c>) AND its parent directory
    /// contains a <c>guardrails.json</c> (so we know it is genuinely a wave of a waved plan, not
    /// a coincidentally-named standalone folder).
    /// </summary>
    private static bool IsWaveFolder(string folder)
    {
        string absFolder = Path.GetFullPath(folder);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(absFolder));
        if (!WaveDirRegex().IsMatch(name)) return false;
        string? parent = Path.GetDirectoryName(absFolder);
        return parent is not null && File.Exists(Path.Combine(parent, "guardrails.json"));
    }

    /// <summary>
    /// Load the parent plan and project it down to a wave-scoped <see cref="PlanDefinition"/>
    /// whose <see cref="PlanDefinition.PlanDirectory"/> is the wave folder, so the renderer
    /// writes <c>diagram.md</c> / <c>diagram.html</c> into the wave folder. Returns
    /// <c>null</c> (and prints a diagnostic) on any load/validate error or if the wave folder
    /// does not appear in the parent plan's wave list.
    /// </summary>
    private static PlanDefinition? LoadWaveScoped(string folder, TextWriter output)
    {
        string absFolder = Path.GetFullPath(folder);
        string parentDir = Path.GetDirectoryName(absFolder)!;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(parentDir);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return null;
        }

        WaveNode? wave = probe.Plan.Waves.FirstOrDefault(w =>
            string.Equals(Path.GetFullPath(w.Directory), absFolder, StringComparison.OrdinalIgnoreCase));

        if (wave is null)
        {
            string waveName = Path.GetFileName(absFolder);
            output.WriteLine(
                $"error: '{waveName}' matches the wave-dir pattern but was not found in the parent plan's waves.");
            return null;
        }

        // Project the full plan down to a wave-scoped slice: only this wave's tasks, preflight
        // gate, and exit gate; PlanDirectory set to the wave folder so diagram.md / diagram.html
        // land there. Waves cleared so MermaidRenderer sees a flat-plan shape (no wave headers).
        return probe.Plan with
        {
            PlanDirectory = wave.Directory,
            Tasks = wave.Tasks,
            Waves = [],
            PlanPreflights = wave.Preflights,
            PlanGuardrails = wave.Guardrails,
        };
    }

    /// <summary>
    /// Shared render path for both the flat-plan and wave-scoped code paths. Drives the full
    /// write / <c>--check</c> / <c>--stdout</c> pipeline against the supplied
    /// <paramref name="plan"/>. <c>plan.PlanDirectory</c> is the output directory for
    /// <c>diagram.md</c> and <c>diagram.html</c> — for a wave-scoped call this is the wave
    /// folder, not the plan root.
    /// </summary>
    private static int Render(PlanDefinition plan, bool check, bool toStdout, bool noHtml, IConsoleIo io)
    {
        TextWriter output = io.Out;

        string sourceHash = GraphSourceHash.Compute(plan);
        string diagramPath = Path.Combine(plan.PlanDirectory, DiagramFileName);
        string diagramHtmlPath = Path.Combine(plan.PlanDirectory, DiagramHtmlFileName);

        if (check)
        {
            return Check(diagramPath, diagramHtmlPath, sourceHash, output);
        }

        string diagram = MermaidRenderer.Render(plan);

        if (toStdout)
        {
            output.WriteLine(diagram);
            return ExitCodes.Success;
        }

        string document = ComposeDocument(diagram, sourceHash);
        AtomicFile.WriteAllText(diagramPath, document);
        output.WriteLine($"Wrote {diagramPath}");

        // The interactive local-navigation companion (issue #33). diagram.md stays the GitHub
        // render; diagram.html is the pan/zoom/fullscreen viewer whose nodes click through to
        // their source under the plan folder. Both carry the same source-sha256 and are excluded
        // from guardrails.baseline, so neither causes drift. The HTML embeds the interactive
        // source (clean diagram + click directives); diagram.md stays click-free.
        if (!noHtml)
        {
            string interactive = MermaidRenderer.RenderInteractive(plan);
            IReadOnlyDictionary<string, string> taskFolderTargets = MermaidRenderer.TaskFolderTargets(plan);
            AtomicFile.WriteAllText(diagramHtmlPath, HtmlDiagramRenderer.Render(interactive, sourceHash, taskFolderTargets));
            PrintDiagramLink(diagramHtmlPath, output);
        }

        return ExitCodes.Success;
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
    /// <c>--check</c>: recompute the source hash and compare it to the one embedded in an
    /// existing <c>diagram.md</c> provenance comment. Fresh (present and equal) → exit 0;
    /// stale or missing → one actionable line on stdout and exit <see cref="StaleExitCode"/>
    /// (2) — the "regenerate" signal (SSOT §7/§10). A genuine load/validate failure never
    /// reaches here — <see cref="Execute"/> returns <see cref="ExitCodes.HarnessError"/> (1)
    /// before <c>--check</c> is dispatched — so CI can distinguish "regenerate the diagram"
    /// (2) from "the plan is broken" (1).
    /// </summary>
    private static int Check(string diagramPath, string diagramHtmlPath, string sourceHash, TextWriter output)
    {
        string regenHint = $"run: guardrails graph {QuoteIfNeeded(Path.GetDirectoryName(diagramPath)!)}";

        if (!File.Exists(diagramPath))
        {
            output.WriteLine($"{DiagramFileName} missing — {regenHint}");
            return StaleExitCode;
        }

        if (!string.Equals(ReadEmbeddedHash(File.ReadAllText(diagramPath)), sourceHash, StringComparison.Ordinal))
        {
            output.WriteLine($"{DiagramFileName} is stale — {regenHint}");
            return StaleExitCode;
        }

        // diagram.html is optional (it is skipped by --no-html), so a MISSING one is not staleness.
        // But a PRESENT one carrying a different source-sha256 has drifted from diagram.md/the plan
        // and must regenerate — it shares the same staleness key (issue #33).
        if (File.Exists(diagramHtmlPath) &&
            !string.Equals(ReadEmbeddedHash(File.ReadAllText(diagramHtmlPath)), sourceHash, StringComparison.Ordinal))
        {
            output.WriteLine($"{DiagramHtmlFileName} is stale — {regenHint}");
            return StaleExitCode;
        }

        return ExitCodes.Success;
    }

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

    /// <summary>
    /// Matches a wave folder name (e.g. <c>wave-01-foundation</c>, <c>wave-02-provision</c>).
    /// The numeric group drives the strict total order (SSOT §14.1). Mirrors the pattern used
    /// by <c>PlanLoader</c> — kept in sync by the loader/validator tests.
    /// </summary>
    [GeneratedRegex(@"^wave-([0-9]+)-[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WaveDirRegex();
}
