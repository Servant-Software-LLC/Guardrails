using System.CommandLine;
using Guardrails.Core.Loading;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails diagnostics [code] [--ladder GR20]</c> — the glossary for the codes the tool emits
/// (issue #558).
///
/// <para><b>The gap it closes.</b> Diagnostic codes are the primary way the harness tells an operator that
/// their plan is wrong, and they were the one part of that conversation with no glossary. The only
/// authoritative catalogue was the XML doc comments in <c>DiagnosticCodes.cs</c> — a source file, which a
/// consumer of the packaged dotnet tool does not have. An operator meeting <c>GR2042</c> for the first time
/// had nowhere to go.</para>
///
/// <para><b>Read-only, offline, no plan folder.</b> It describes the TOOL, not a plan, so unlike every
/// other command here it takes no folder and loads nothing. That is also why it can be the thing
/// <c>validate</c> points at: it works when the plan does not.</para>
///
/// <para>The command is the surface; the catalogue and its tests are the substance. See
/// <see cref="DiagnosticCatalogue"/> for why it parses the embedded source instead of declaring a
/// table.</para>
/// </summary>
public static class DiagnosticsCommand
{
    public static Command Create(IConsoleIo io)
    {
        ArgumentNullException.ThrowIfNull(io);

        var codeArgument = new Argument<string?>("code")
        {
            Description = "A single code to explain, e.g. GR2042 (case-insensitive). Omit to list every code.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var ladderOption = new Option<string?>("--ladder")
        {
            Description = "Only list codes on one ladder: GR10 (plan load) or GR20 (validation)."
        };

        var command = new Command(
            "diagnostics",
            "Explain the GR diagnostic codes this tool emits — read-only, offline, no plan folder needed.");
        command.Add(codeArgument);
        command.Add(ladderOption);

        command.SetAction(parseResult => Run(
            parseResult.GetValue(codeArgument),
            parseResult.GetValue(ladderOption),
            io));

        return command;
    }

    private static int Run(string? code, string? ladder, IConsoleIo io)
    {
        return string.IsNullOrWhiteSpace(code)
            ? List(ladder, io)
            : Explain(code, io);
    }

    private static int Explain(string code, IConsoleIo io)
    {
        DiagnosticEntry? entry = DiagnosticCatalogue.Find(code);

        if (entry is null)
        {
            // Naming the nearest ladder beats a bare "unknown": the commonest way to get here is a typo or a
            // code read off an older build, and both are one keystroke from an answer.
            io.Out.WriteLine($"No diagnostic '{code.Trim()}' is defined in this build.");
            io.Out.WriteLine("Run 'guardrails diagnostics' to list every code, or --ladder GR10 / --ladder GR20.");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine($"{entry.Code}  {entry.Name}  [{Label(entry.Kind)}]");
        io.Out.WriteLine();

        // The header above already carries code and severity; repeating the marker as the first
        // words of the body reads like a stutter.
        string body = DiagnosticCatalogue.WithoutMarker(entry.Body);

        foreach (string paragraph in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string wrapped in Wrap(paragraph, 96))
            {
                io.Out.WriteLine(wrapped);
            }

            io.Out.WriteLine();
        }

        return ExitCodes.Success;
    }

    private static int List(string? ladder, IConsoleIo io)
    {
        IReadOnlyList<DiagnosticEntry> entries =
        [
            .. string.IsNullOrWhiteSpace(ladder)
                ? DiagnosticCatalogue.Entries
                : DiagnosticCatalogue.InLadder(ladder)
        ];

        if (entries.Count == 0)
        {
            io.Out.WriteLine($"No codes on ladder '{ladder}'. The ladders are GR10 (plan load) and GR20 (validation).");
            return ExitCodes.HarnessError;
        }

        int nameWidth = entries.Max(e => e.Name.Length);

        foreach (DiagnosticEntry entry in entries)
        {
            io.Out.WriteLine(
                $"{entry.Code}  {Label(entry.Kind),-8}  {entry.Name.PadRight(nameWidth)}  {entry.Summary}");
        }

        io.Out.WriteLine();
        io.Out.WriteLine($"{entries.Count} code(s). Run 'guardrails diagnostics <code>' for the full rationale.");
        return ExitCodes.Success;
    }

    private static string Label(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.Error => "ERROR",
        DiagnosticKind.Warning => "WARNING",
        DiagnosticKind.Retired => "RETIRED",
        _ => "EITHER"
    };

    /// <summary>
    /// Wrap to <paramref name="width"/> on word boundaries. The doc bodies are paragraphs written for a
    /// source file, so they arrive as one long line each; printing them unwrapped makes the longest and
    /// most useful entries the least readable.
    /// </summary>
    private static IEnumerable<string> Wrap(string paragraph, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
