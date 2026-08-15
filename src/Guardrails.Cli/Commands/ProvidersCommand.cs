using System.CommandLine;
using System.Text;
using Guardrails.Core.Providers;
using Guardrails.Core.State;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails providers init [folder] [--write]</c> — the generated registry (SSOT §9.7, DoR §4.3).
/// It annotates the <c>promptRunners</c> blocks of the plan's own <c>guardrails.json</c> with the LEGAL
/// VALUES of <c>costly</c> / <c>strength</c> / <c>specialization</c> / <c>routing</c> as <c>//</c>
/// comments, adds the keys a block has not stated (as <c>null</c> — "not stated", never a guess), and
/// names every block whose cost nobody has ruled on.
///
/// <para><b>PREVIEW IS THE DEFAULT; <c>--write</c> IS THE ACCEPTANCE.</b> DoR ruling 5 requires the
/// output to be "a diff for the human to accept… not a silent config mutation", and this is what that
/// means concretely: a bare <c>providers init</c> prints the unified diff and writes NOTHING, and the
/// human accepts it by re-running with <c>--write</c>. An interactive y/n was rejected because it cannot
/// be the acceptance in a non-interactive session (CI, a script, a piped terminal) and this repo's
/// console seam is output-only by design; "write first, print the diff afterwards" was rejected because
/// a receipt for a mutation that already happened is not a review. The safe direction is the default, and
/// the direction that changes a file requires a flag.</para>
///
/// <para><b>It exits 0 even though it enumerates nothing.</b> No <c>kind</c> in this build has a model
/// enumeration surface, so the command annotates what is there, states plainly that it could not
/// enumerate and why, and succeeds. Failing would be the wrong shape: the annotation half of the job did
/// succeed, and that half is most of the value.</para>
///
/// <para>Shaped after <see cref="SkillsCommand"/> — a noun parent (<c>providers</c>) with a verb leaf
/// (<c>init</c>), parallel to <c>guardrails skills install</c>. <c>providers status</c>, the live-state
/// inspector, is a v2 verb in the same noun-space (DoR §4.3).</para>
/// </summary>
public static class ProvidersCommand
{
    /// <summary>
    /// The plan's run configuration. Same literal <c>PlanLoader</c> uses; this command reads and rewrites
    /// the file as TEXT rather than through the loader, because the loader's parse cannot preserve
    /// comments and comments are the entire deliverable.
    /// </summary>
    private const string ConfigFileName = "guardrails.json";

    /// <summary>The order the unstated report walks the solicited keys in — the emission order.</summary>
    private static readonly string[] ReportOrder =
    [
        RegistryAxes.Costly, RegistryAxes.Strength, RegistryAxes.Specialization, RegistryAxes.Routing
    ];

    /// <summary>The <c>providers</c> command group (<c>guardrails providers init</c>).</summary>
    public static Command Create(IConsoleIo io)
    {
        var command = new Command(
            "providers",
            "Inspect and annotate the prompt-runner registry in a plan's guardrails.json.");
        command.Add(BuildInitLeaf(io));
        return command;
    }

    private static Command BuildInitLeaf(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var writeOption = new Option<bool>("--write")
        {
            Description = "Accept the printed diff and write it to guardrails.json. Without this the "
                + "command only PREVIEWS the change and leaves the file untouched."
        };

        var command = new Command(
            "init",
            "Annotate guardrails.json's promptRunners blocks with the legal model-axis values, "
            + "and report every axis still unstated. Previews by default; --write accepts.");
        command.Add(folderArgument);
        command.Add(writeOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return RunInit(folder, parseResult.GetValue(writeOption), io);
        });

        return command;
    }

    private static int RunInit(string folder, bool write, IConsoleIo io)
    {
        string configPath = Path.Combine(folder, ConfigFileName);

        if (!File.Exists(configPath))
        {
            io.Error.WriteLine($"No {ConfigFileName} at '{configPath}'.");
            io.Error.WriteLine(
                "`providers init` annotates an existing plan configuration; it does not create one. "
                + "Point it at a plan folder, or run it from inside one.");
            return ExitCodes.HarnessError;
        }

        (string? text, string? readFailure) = ReadConfig(configPath);
        if (text is null)
        {
            io.Error.WriteLine($"Could not read '{configPath}': {readFailure}");
            return ExitCodes.HarnessError;
        }

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(text);

        if (!result.Succeeded)
        {
            io.Error.WriteLine($"Could not annotate '{configPath}' — {result.Failure}");
            io.Error.WriteLine("Nothing was written; the file is byte-identical.");
            return ExitCodes.HarnessError;
        }

        if (result.Blocks.Count == 0)
        {
            io.Out.WriteLine();
            io.Out.WriteLine(
                $"{ConfigFileName} declares no promptRunners blocks, so there is nothing to annotate.");
            io.Out.WriteLine(
                "Add a block (a name, a `command`, and whatever settings it needs) and re-run — "
                + "`providers init` annotates blocks, it never invents them.");
            return ExitCodes.Success;
        }

        PrintDiff(result, io);

        if (write && result.HasChanges)
        {
            try
            {
                AtomicFile.WriteAllText(configPath, result.AnnotatedText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                io.Error.WriteLine($"Could not write '{configPath}': {ex.Message}");
                return ExitCodes.HarnessError;
            }
        }

        PrintEnumerationNotice(result, io);
        PrintUnstatedReport(result, io);
        PrintOutcome(result, folder, write, configPath, io);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Read the config as TEXT, preserving a UTF-8 BOM as an explicit leading U+FEFF so a file that had
    /// one gets it back (<c>AtomicFile</c> writes BOM-less UTF-8, so dropping it here would silently
    /// change three bytes no annotation ever named). A file that is not valid UTF-8 is refused rather than
    /// rewritten with replacement characters — the same throwing-decoder discipline as
    /// <c>HarnessWrite</c>'s anchored form.
    /// </summary>
    private static (string? Text, string? Failure) ReadConfig(string configPath)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }

        bool hasByteOrderMark = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        int offset = hasByteOrderMark ? 3 : 0;

        try
        {
            string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw, offset, raw.Length - offset);
            return (hasByteOrderMark ? "﻿" + text : text, null);
        }
        catch (DecoderFallbackException)
        {
            return (null,
                "it is not valid UTF-8 text. `providers init` will not rewrite a file whose bytes it "
                + "cannot decode, because the ones it could not read would be silently replaced.");
        }
    }

    /// <summary>
    /// Render the planned change as a unified diff. Every hunk is DERIVED from the insertion that
    /// produced it, so what is shown here is exactly what <c>--write</c> would splice — the preview and
    /// the write cannot disagree.
    /// </summary>
    private static void PrintDiff(RegistryAnnotationResult result, IConsoleIo io)
    {
        io.Out.WriteLine();

        if (!result.HasChanges)
        {
            io.Out.WriteLine(
                $"{ConfigFileName} is already annotated — no change. "
                + $"({result.Blocks.Count} block(s) inspected; nothing was reordered, rewritten or removed.)");
            return;
        }

        io.Out.WriteLine($"--- a/{ConfigFileName}");
        io.Out.WriteLine($"+++ b/{ConfigFileName}");

        foreach (RegistryAnnotationHunk hunk in result.Hunks)
        {
            io.Out.WriteLine($"@@ line {hunk.LineNumber} @@ {hunk.Context}");

            foreach (string line in hunk.Removed)
            {
                io.Out.WriteLine($"-{line}");
            }

            foreach (string line in hunk.Added)
            {
                io.Out.WriteLine($"+{line}");
            }
        }
    }

    /// <summary>
    /// State the enumeration outcome out loud. In v1 this always fires, and saying so plainly is the
    /// point: the command did not fail, it did not add blocks, and it wrote no model identifier.
    /// </summary>
    private static void PrintEnumerationNotice(RegistryAnnotationResult result, IConsoleIo io)
    {
        if (result.UnenumerableKinds.Count == 0)
        {
            return;
        }

        string kinds = string.Join(", ", result.UnenumerableKinds.Select(k => $"'{k}'"));

        io.Out.WriteLine();
        io.Out.WriteLine($"Could not enumerate models for kind {kinds} — NO block was added, and no model");
        io.Out.WriteLine("identifier was written. A registry entry is a routing target, not documentation: an");
        io.Out.WriteLine("invented or stale id would be spent against at a model that may not exist, so the");
        io.Out.WriteLine("generator does not guess. Add blocks by hand; the legal axis values are now in the file.");
    }

    /// <summary>
    /// The report the tri-state <c>costly</c> exists for. An UNSTATED axis is not an answered one, and
    /// this is where that distinction is cashed in: the command names every block nobody has ruled on and
    /// asks. It keeps asking on every re-run — the <c>null</c> the command itself wrote is a prompt, not
    /// an answer — which is exactly why <c>null</c> had to stay distinct from <c>false</c>.
    /// </summary>
    private static void PrintUnstatedReport(RegistryAnnotationResult result, IConsoleIo io)
    {
        IReadOnlyList<RegistryBlockReport> unstatedCostly = result.Unstated(RegistryAxes.Costly);

        io.Out.WriteLine();
        io.Out.WriteLine("UNSTATED — `providers init` will not answer these for you:");
        io.Out.WriteLine();

        bool any = false;
        foreach (string axis in ReportOrder)
        {
            IReadOnlyList<RegistryBlockReport> blocks = result.Unstated(axis);
            if (blocks.Count == 0)
            {
                continue;
            }

            any = true;
            io.Out.WriteLine(
                $"  {axis,-16}{blocks.Count} of {result.Blocks.Count} block(s): "
                + string.Join(", ", blocks.Select(b => b.Name)));
        }

        if (!any)
        {
            io.Out.WriteLine("  (none — every block states every axis.)");
            return;
        }

        io.Out.WriteLine();

        if (unstatedCostly.Count > 0)
        {
            io.Out.WriteLine(
                $"  `{RegistryAxes.Costly}` is TRI-STATE, and {unstatedCostly.Count} block(s) have not stated it: "
                + "null is NOT false.");
            io.Out.WriteLine(
                "  An unstated block stays SELECTABLE by the harness. Write `true` to reserve a model so");
            io.Out.WriteLine(
                "  that only a human may assign it, or `false` to state plainly that it is cheap to spend.");
        }
    }

    /// <summary>Say what happened to the file, and — in preview mode — how to accept the diff.</summary>
    private static void PrintOutcome(
        RegistryAnnotationResult result, string folder, bool write, string configPath, IConsoleIo io)
    {
        int addedKeys = result.Blocks.Sum(b => b.AddedKeys.Count);
        int addedComments = result.Blocks.Sum(b => b.AddedComments);

        io.Out.WriteLine();

        if (!result.HasChanges)
        {
            io.Out.WriteLine($"No changes. {configPath} is untouched.");
            return;
        }

        string summary =
            $"{addedKeys} unstated key(s) (each with its legal-value comment) and {addedComments} "
            + $"legal-value comment(s) above keys that already had a value, across "
            + $"{result.Blocks.Count} runner block(s)";

        if (write)
        {
            io.Out.WriteLine($"Wrote {configPath} — added {summary}.");
            io.Out.WriteLine(
                "Nothing was reordered, rewritten or removed; re-running is a no-op on what is now there.");
            return;
        }

        io.Out.WriteLine($"PREVIEW ONLY — nothing was written. {configPath} is byte-identical.");
        io.Out.WriteLine($"The diff above would add {summary}.");
        io.Out.WriteLine("Review it, then accept it by re-running with --write:");
        io.Out.WriteLine($"  guardrails providers init {Quote(folder)} --write");
    }

    /// <summary>Quote a path for the copy-pasteable command line when it contains a space.</summary>
    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
