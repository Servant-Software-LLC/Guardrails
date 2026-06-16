using System.CommandLine;
using System.Text.RegularExpressions;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails graph [folder] [--check] [--stdout] [--format mermaid]</c> — render the
/// plan's task/guardrail DAG as a Mermaid <c>flowchart TD</c> (SSOT §10). Default: write
/// <c>&lt;folder&gt;/diagram.md</c> (a provenance comment + a fenced <c>mermaid</c> block + a
/// one-line structure-only caption after the fence). <c>--check</c> writes nothing and reports
/// staleness via exit code (0 fresh, 2 stale/missing, 1 on a load/validate error). <c>--stdout</c>
/// prints the diagram instead of writing a file. Defaults to the current directory when the
/// folder is omitted.
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
            Description = "Report whether diagram.md is up to date (exit 0 fresh, 2 stale/missing, 1 on a load/validate error); writes nothing."
        };

        var stdoutOption = new Option<bool>("--stdout")
        {
            Description = "Print the diagram to stdout instead of writing diagram.md (writes nothing to disk)."
        };

        var noHtmlOption = new Option<bool>("--no-html")
        {
            Description = "Write only diagram.md; skip the interactive diagram.html navigation companion."
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

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return ExitCodes.HarnessError;
        }

        PlanDefinition plan = probe.Plan;
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
            AtomicFile.WriteAllText(diagramHtmlPath, HtmlDiagramRenderer.Render(interactive, sourceHash));
            output.WriteLine($"Wrote {diagramHtmlPath}");
        }

        return ExitCodes.Success;
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
    /// <c>mermaid</c> block holding the rendered diagram, and a one-line italic caption after the
    /// fence (<see cref="DiagramCaption"/>). The comment carries only the <c>source-sha256</c>
    /// identity — no timestamp — and the caption is outside the hashed content, so re-running
    /// <c>graph</c> on an unchanged plan yields a byte-identical file (a deterministic projection,
    /// no git churn).
    /// </summary>
    private static string ComposeDocument(string diagram, string sourceHash)
    {
        string provenance = $"<!-- guardrails:graph v1 source-sha256={sourceHash} -->";

        return provenance + "\n\n```mermaid\n" + diagram.TrimEnd('\n') + "\n```\n\n" + DiagramCaption + "\n";
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
