using System.CommandLine;
using System.Text.RegularExpressions;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails graph [folder] [--check] [--stdout] [--format mermaid]</c> — render the
/// plan's task/guardrail DAG as a Mermaid <c>flowchart TD</c> (SSOT §10). Default: write
/// <c>&lt;folder&gt;/diagram.md</c> (a provenance comment + a fenced <c>mermaid</c> block).
/// <c>--check</c> writes nothing and reports staleness via exit code. <c>--stdout</c> prints
/// the diagram instead of writing a file. Defaults to the current directory when the folder
/// is omitted.
/// </summary>
public static partial class GraphCommand
{
    private const string DiagramFileName = "diagram.md";

    public static Command Create()
    {
        var folderArgument = FolderArgument.Create();

        var checkOption = new Option<bool>("--check")
        {
            Description = "Report whether diagram.md is up to date (exit 0 fresh, 1 stale/missing); writes nothing."
        };

        var stdoutOption = new Option<bool>("--stdout")
        {
            Description = "Print the diagram to stdout instead of writing diagram.md (writes nothing to disk)."
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

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument));
            bool check = parseResult.GetValue(checkOption);
            bool toStdout = parseResult.GetValue(stdoutOption);
            return Execute(folder, check, toStdout);
        });

        return command;
    }

    private static int Execute(string folder, bool check, bool toStdout)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics);
            return ExitCodes.HarnessError;
        }

        PlanDefinition plan = probe.Plan;
        string sourceHash = GraphSourceHash.Compute(plan);
        string diagramPath = Path.Combine(plan.PlanDirectory, DiagramFileName);

        if (check)
        {
            return Check(diagramPath, sourceHash);
        }

        string diagram = MermaidRenderer.Render(plan);

        if (toStdout)
        {
            Console.WriteLine(diagram);
            return ExitCodes.Success;
        }

        string document = ComposeDocument(diagram, sourceHash);
        AtomicFile.WriteAllText(diagramPath, document);
        Console.WriteLine($"Wrote {diagramPath}");
        return ExitCodes.Success;
    }

    /// <summary>
    /// <c>--check</c>: recompute the source hash and compare it to the one embedded in an
    /// existing <c>diagram.md</c> provenance comment. Fresh (present and equal) → exit 0;
    /// stale or missing → one actionable line on stdout and exit 1.
    /// </summary>
    private static int Check(string diagramPath, string sourceHash)
    {
        if (!File.Exists(diagramPath))
        {
            Console.WriteLine($"{DiagramFileName} missing — run: guardrails graph {QuoteIfNeeded(Path.GetDirectoryName(diagramPath)!)}");
            return ExitCodes.HarnessError;
        }

        string? embedded = ReadEmbeddedHash(File.ReadAllText(diagramPath));
        if (string.Equals(embedded, sourceHash, StringComparison.Ordinal))
        {
            return ExitCodes.Success;
        }

        Console.WriteLine($"{DiagramFileName} is stale — run: guardrails graph {QuoteIfNeeded(Path.GetDirectoryName(diagramPath)!)}");
        return ExitCodes.HarnessError;
    }

    /// <summary>
    /// Compose the persisted artifact: a single-line provenance comment (SSOT §10) followed
    /// by a fenced <c>mermaid</c> block holding the rendered diagram. The comment carries only
    /// the <c>source-sha256</c> identity — no timestamp — so re-running <c>graph</c> on an
    /// unchanged plan yields a byte-identical file (a deterministic projection, no git churn).
    /// </summary>
    private static string ComposeDocument(string diagram, string sourceHash)
    {
        string provenance = $"<!-- guardrails:graph v1 source-sha256={sourceHash} -->";

        return provenance + "\n\n```mermaid\n" + diagram.TrimEnd('\n') + "\n```\n";
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
