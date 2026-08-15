using System.CommandLine;
using System.Text;
using Guardrails.Cli;
using Guardrails.Core.Providers;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// <c>guardrails providers init</c> end to end (SSOT §9.7, model-tiering Stage 1 charter §B).
///
/// <para>Every case drives the REAL production dispatch through
/// <see cref="CommandFactory.BuildRootCommand"/> rather than a hand-assembled <c>RootCommand</c>, so a
/// green test proves the verb is actually WIRED IN — the issue #120 convention every other
/// <c>*CliTests</c> file in this project follows.</para>
///
/// <para><b>What "a diff to accept" means here (charter §B criterion 4).</b> A bare
/// <c>providers init</c> is a PREVIEW: it prints a unified diff and leaves the file byte-identical. The
/// human accepts it by re-running with <c>--write</c>. That is what these tests pin — the safe direction
/// is the default, and the direction that mutates a config the user hand-maintains needs a flag.</para>
/// </summary>
public sealed class ProvidersInitCliTests : IDisposable
{
    private readonly ScriptPlanBuilder _plan;
    private readonly string _root;

    /// <summary>
    /// A REAL, runnable plan's configuration, carrying a hand-written comment and a registry with no
    /// axes stated. It keeps <see cref="ScriptPlanBuilder"/>'s own settings so the fixture stays a plan
    /// <c>guardrails validate</c> passes — the annotation has to survive the real validator, not a
    /// stripped-down stand-in.
    /// </summary>
    private const string Config =
        """
        {
          // hand-written, and it stays hand-written
          "version": 1,
          "guardrailMode": "failFast",
          "workspace": ".",
          "defaultRetries": 0,
          "maxParallelism": 1,
          "promptRunners": {
            "default": "claude",
            "claude": {
              "command": "claude",
              "maxTurns": 25
            }
          }
        }
        """;

    public ProvidersInitCliTests()
    {
        _plan = new ScriptPlanBuilder().AddTask("01-first");
        _root = _plan.PlanDir;
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), Config, new UTF8Encoding(false));
    }

    public void Dispose() => _plan.Dispose();

    private string ConfigPath => Path.Combine(_root, "guardrails.json");

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText, io.ErrorText);
    }

    // ── criterion 4: the diff is the output, and --write is the acceptance ───────────────────

    /// <summary>
    /// The default is a PREVIEW. A unified diff is printed, the file is byte-identical afterwards, and
    /// the user is told exactly how to accept it. A "write it and show a receipt" design would fail the
    /// second assertion, which is the one that matters.
    /// </summary>
    [Fact]
    public async Task Init_PreviewsByDefaultAndWritesNothing()
    {
        byte[] before = File.ReadAllBytes(ConfigPath);

        (int exit, string output, _) = await InvokeAsync("providers", "init", _root);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));

        Assert.Contains("--- a/guardrails.json", output, StringComparison.Ordinal);
        Assert.Contains("+++ b/guardrails.json", output, StringComparison.Ordinal);
        Assert.Contains("@@ line ", output, StringComparison.Ordinal);
        Assert.Contains("+      \"costly\": null", output, StringComparison.Ordinal);

        Assert.Contains("PREVIEW ONLY — nothing was written.", output, StringComparison.Ordinal);
        Assert.Contains("--write", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--write</c> accepts the diff. What lands on disk is exactly what the preview printed — the
    /// hunks are derived from the insertions themselves, so preview and write cannot diverge.
    /// </summary>
    [Fact]
    public async Task Init_WriteAppliesExactlyWhatThePreviewShowed()
    {
        (_, string preview, _) = await InvokeAsync("providers", "init", _root);
        (int exit, string written, _) = await InvokeAsync("providers", "init", _root, "--write");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Wrote ", written, StringComparison.Ordinal);

        string[] previewAdds = [.. Lines(preview).Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal))];
        string onDisk = File.ReadAllText(ConfigPath);

        Assert.NotEmpty(previewAdds);
        Assert.All(previewAdds, add => Assert.Contains(add[1..], onDisk, StringComparison.Ordinal));
    }

    // ── criterion 1: idempotent, byte-identical over a human's annotation ────────────────────

    /// <summary>
    /// THE acceptance criterion, through the CLI: annotate, let a human answer and add their own note,
    /// re-run with <c>--write</c>, and assert the file's BYTES did not move.
    /// </summary>
    [Fact]
    public async Task Init_RerunLeavesAHumanAnnotationByteIdentical()
    {
        await InvokeAsync("providers", "init", _root, "--write");

        // The human does what the annotated file asks: answers two axes and adds a note of their own.
        string annotated = File.ReadAllText(ConfigPath)
            .Replace("\"costly\": null", "\"costly\": true", StringComparison.Ordinal)
            .Replace(
                "\"strength\": null",
                "\"strength\": 7 // measured against our own refactor suite",
                StringComparison.Ordinal);
        File.WriteAllText(ConfigPath, annotated, new UTF8Encoding(false));

        byte[] humanBytes = File.ReadAllBytes(ConfigPath);

        (int exit, string output, _) = await InvokeAsync("providers", "init", _root, "--write");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(humanBytes, File.ReadAllBytes(ConfigPath));
        Assert.Contains("already annotated — no change", output, StringComparison.Ordinal);
        Assert.Contains("nothing was reordered, rewritten or removed", output, StringComparison.Ordinal);
    }

    // ── criterion 2: never fabricates a model id, and exits 0 anyway ─────────────────────────

    /// <summary>
    /// A kind with no enumeration surface — which in this build is every kind — gets its EXISTING blocks
    /// annotated plus an explicit "could not enumerate" comment, writes no model identifier, and exits 0.
    /// The exit code is half the criterion: degrading honestly is not the same as failing.
    /// </summary>
    [Fact]
    public async Task Init_WithNoEnumerationSurfaceAnnotatesExitsZeroAndWritesNoModelId()
    {
        (int exit, string output, string error) = await InvokeAsync("providers", "init", _root, "--write");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(error);
        Assert.Contains("Could not enumerate models for kind 'claude'", output, StringComparison.Ordinal);
        Assert.Contains("routing target, not documentation", output, StringComparison.Ordinal);

        string onDisk = File.ReadAllText(ConfigPath);
        Assert.Contains(RegistryAxes.CouldNotEnumerateMarker("claude"), onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\"", onDisk, StringComparison.Ordinal);

        // No block was invented: the registry still holds exactly what the human wrote.
        Assert.Equal(1, Occurrences(onDisk, "\"command\": \"claude\""));
    }

    // ── criterion 5: the tri-state payoff, surfaced prominently ──────────────────────────────

    /// <summary>
    /// The report the third state exists for: the command names every block whose cost nobody has ruled
    /// on and says plainly that <c>null</c> is not <c>false</c>. It keeps saying so on a re-run, because
    /// the <c>null</c> it wrote is a prompt and not an answer — and it stops the moment a human answers.
    /// </summary>
    [Fact]
    public async Task Init_NamesEveryBlockWhoseCostIsUnstatedAndKeepsAsking()
    {
        (_, string first, _) = await InvokeAsync("providers", "init", _root, "--write");

        Assert.Contains("UNSTATED", first, StringComparison.Ordinal);
        Assert.Contains("costly", first, StringComparison.Ordinal);
        Assert.Contains("null is NOT false", first, StringComparison.Ordinal);
        Assert.Contains("1 of 1 block(s): claude", first, StringComparison.Ordinal);

        (_, string second, _) = await InvokeAsync("providers", "init", _root);
        Assert.Contains("null is NOT false", second, StringComparison.Ordinal);

        File.WriteAllText(
            ConfigPath,
            File.ReadAllText(ConfigPath).Replace("\"costly\": null", "\"costly\": false", StringComparison.Ordinal),
            new UTF8Encoding(false));

        (_, string answered, _) = await InvokeAsync("providers", "init", _root);
        Assert.DoesNotContain("null is NOT false", answered, StringComparison.Ordinal);
    }

    // ── the annotated config is still a valid config ────────────────────────────────────────

    /// <summary>
    /// The annotation must not merely look right — the plan must still LOAD and VALIDATE, with every
    /// written <c>null</c> read as "not stated" exactly as an absent key is. A generator whose output
    /// broke <c>guardrails validate</c> would be worse than useless.
    /// </summary>
    [Fact]
    public async Task Init_LeavesThePlanValidating()
    {
        (int before, string beforeOutput, _) = await InvokeAsync("validate", _root);
        Assert.Equal(ExitCodes.Success, before);

        await InvokeAsync("providers", "init", _root, "--write");

        (int after, string afterOutput, _) = await InvokeAsync("validate", _root);

        Assert.Equal(ExitCodes.Success, after);
        Assert.Contains("OK: plan is valid.", afterOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("GR2045", afterOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("GR2047", afterOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("GR2049", afterOutput, StringComparison.Ordinal);

        // And it did not change what validate had to say: the annotation is inert, by construction.
        Assert.Equal(beforeOutput, afterOutput);
    }

    // ── refusals ────────────────────────────────────────────────────────────────────────────

    /// <summary>A folder with no configuration is an actionable error, not a created file.</summary>
    [Fact]
    public async Task Init_WithNoConfigFailsAndCreatesNothing()
    {
        string empty = Path.Combine(_root, "not-a-plan");
        Directory.CreateDirectory(empty);

        (int exit, _, string error) = await InvokeAsync("providers", "init", empty, "--write");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("No guardrails.json", error, StringComparison.Ordinal);
        Assert.Contains("it does not create one", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(empty, "guardrails.json")));
    }

    /// <summary>An unparseable configuration is refused, and the bytes on disk do not move.</summary>
    [Fact]
    public async Task Init_WithUnparseableConfigFailsAndLeavesItByteIdentical()
    {
        File.WriteAllText(ConfigPath, "{ \"promptRunners\": { \"claude\": { ", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(ConfigPath);

        (int exit, _, string error) = await InvokeAsync("providers", "init", _root, "--write");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("not parseable JSON", error, StringComparison.Ordinal);
        Assert.Contains("byte-identical", error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int from = 0;
        while ((from = haystack.IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += needle.Length;
        }

        return count;
    }
}
