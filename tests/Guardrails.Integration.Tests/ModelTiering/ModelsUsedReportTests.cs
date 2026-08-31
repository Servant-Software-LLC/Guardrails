using System.CommandLine;
using System.Globalization;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The REAL-SEAM half of #349's models-used summary line, surface 5 of 5: a whole <c>guardrails run</c> driven
/// through the actual CLI pipeline, with the assertions made against what an OPERATOR sees on stdout. The
/// shipped <c>DryRunCliTests.Run_PromptPlan_PrintsTotalCostLine</c> /
/// <c>Run_DeterministicPlan_OmitsTotalCostLine</c> pair is the precedent for both tests here — the same
/// <see cref="StringConsoleIo"/> capture, the same two plan shapes, and the same
/// present-then-absent structure — because the models-used line sits in the same end-of-run report and must
/// obey the same suppression rule.
///
/// <para><b>The recorded model is READ FROM THE JOURNAL, never hardcoded.</b> The whole point of this line is
/// that it reports what the run actually recorded, so the expectation is taken from
/// <c>state/run.json</c>'s own <c>provenance.model</c> via <see cref="JournalReader"/>. A literal in this file
/// would pin the fixture's incidental model id and stop being a statement about the report at all.</para>
///
/// <para><b>The <c>Models used:</c> line is ISOLATED before anything is asserted about it.</b> Wave 3 already
/// prints the attempt's model elsewhere in <c>--no-ui</c> output — <c>ConsoleRunObserver</c> writes a
/// <c>[model] &lt;task&gt; attempt N: …</c> line for every attempt — so a bare
/// <c>Assert.Contains(model, output)</c> is GREEN on this wave's entry tree and proves nothing whatsoever.
/// Every assertion below is made against the single line that begins <see cref="LineLabel"/>.</para>
///
/// <para><b>A summary line asserts a NON-EMPTY QUANTITY.</b> Checking that the label was printed, or that the
/// command exited 0, passes a run that aggregated ZERO models and printed an empty list — so the attempt count
/// is parsed back out of the rendered line and required to be strictly positive.</para>
/// </summary>
[Trait("Category", "ModelTieringStage3")]
public sealed class ModelsUsedReportTests
{
    /// <summary>
    /// The label that introduces the line, matching the shape of the report lines already there
    /// (<c>"Total prompt cost: …"</c>, <c>"Per-tier spend: …"</c>). Used to LOCATE the line, never as the
    /// assertion itself.
    /// </summary>
    private const string LineLabel = "Models used:";

    /// <summary>The segment separator and count marker <c>JournalModelsUsed.Render</c> emits.</summary>
    private const string Separator = " · ";
    private const string CountMarker = " ×";

    /// <summary>
    /// A prompt plan's run report names the model the run JOURNALLED, with a strictly positive attempt count.
    ///
    /// <para>The expected id comes out of <c>state/run.json</c> after the run, so this test states the actual
    /// contract — "the line reports what the run recorded" — rather than agreeing with a hardcoded fixture
    /// value that could drift from the journal without either side noticing.</para>
    /// </summary>
    [Fact]
    public async Task Run_PromptPlan_PrintsModelsUsedLine_NamingTheModelTheJournalRecorded()
    {
        using var plan = new FakeClaudePlanBuilder()
            .AddPromptTask("01-generate", mode: "fragment", cost: "0.0150");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);

        // What the RUN recorded — the same field the report is aggregating, read back off the journal.
        (string recorded, int attempts) = JournalledModel(plan.RunJsonPath);

        // Isolated FIRST: wave 3's `[model] <task> attempt N: …` lines already carry this id, so an
        // assertion against the whole output would be satisfied by a report that printed nothing new.
        string line = ModelsUsedLine(output);

        Assert.Contains(recorded, line, StringComparison.Ordinal);

        int rendered = RenderedCount(line, recorded);
        Assert.True(
            rendered > 0,
            $"'{LineLabel}' names '{recorded}' with a non-positive count ({rendered}) in: {line}. A "
            + "models-used line asserts a non-empty quantity — a run that aggregated zero models and "
            + "printed an empty list must not be reported as a success.");
        Assert.Equal(attempts, rendered);
    }

    /// <summary>
    /// A script-only plan records no model at all, so the report carries NO models-used line — not a labelled
    /// empty one.
    ///
    /// <para>Mirrors the shipped <c>Run_DeterministicPlan_OmitsTotalCostLine</c> verbatim in form, and is
    /// deliberately the one test in this file that is GREEN before the feature exists: it asserts an ABSENCE,
    /// which is trivially true on this wave's entry tree, and the per-test red census excludes it by name for
    /// exactly that reason. It is the regression guard that stops the implementation from printing an empty
    /// <c>Models used:</c> line on every deterministic run — where most shipped plans live.</para>
    /// </summary>
    [Fact]
    public async Task Run_DeterministicPlan_OmitsModelsUsedLine()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        // A script-only plan records no provenance.model, so the run summary omits the models-used line.
        Assert.DoesNotContain("Models used", output);
    }

    /// <summary>
    /// The same per-invocation capture the shipped <c>DryRunCliTests</c> uses: a real
    /// <see cref="RootCommand"/> over a <see cref="StringConsoleIo"/>, so no <c>Console.SetOut</c>, no global
    /// state, parallel-safe. Defined here rather than shared because the shipped file is outside this task's
    /// scope.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        root.Add(ValidateCommand.Create(io));

        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// The ONE model <c>state/run.json</c> recorded, and how many attempts recorded it. Read from the journal
    /// the run just wrote — the source the report itself aggregates — so the expectation cannot drift from it.
    /// </summary>
    private static (string Model, int Attempts) JournalledModel(string runJsonPath)
    {
        Assert.True(
            File.Exists(runJsonPath),
            $"the run wrote no journal at {runJsonPath}, so there is nothing the report could be reporting.");

        JournalDocument document = JournalReader.Read(runJsonPath);

        IReadOnlyList<string> models = document.Tasks.Values
            .SelectMany(task => task.Attempts)
            .Select(attempt => attempt.Provenance?.Model)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!)
            .ToList();

        // The fixture is a one-task, one-attempt prompt plan, so the journal carries exactly one recorded
        // model. Asserted rather than assumed: a fixture that grew a second attempt would otherwise make the
        // count expectation below silently wrong.
        string only = Assert.Single(models.Distinct(StringComparer.Ordinal));
        return (only, models.Count);
    }

    /// <summary>
    /// The single output line that begins <see cref="LineLabel"/>, with the label stripped — the ONLY text any
    /// assertion in this file is made against.
    ///
    /// <para>This isolation is the load-bearing part of the end-to-end test. <c>--no-ui</c> output already
    /// contains the attempt's model on a <c>[model] …</c> line raised by <c>ConsoleRunObserver</c>, so a
    /// substring check over the whole output passes on a tree where the models-used report does not exist.
    /// </para>
    /// </summary>
    private static string ModelsUsedLine(string output)
    {
        IReadOnlyList<string> matches = output
            .Split('\n')
            .Select(line => line.Trim('\r', ' '))
            .Where(line => line.StartsWith(LineLabel, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"expected exactly one '{LineLabel}' line in the run report; found {matches.Count}. Full "
            + $"output:\n{output}");

        return matches[0][LineLabel.Length..].Trim();
    }

    /// <summary>
    /// The attempt count the LINE reports for <paramref name="model"/>, parsed back out of its own segment —
    /// so "the label was printed" and "the line reports a real quantity" cannot be confused for each other.
    /// </summary>
    private static int RenderedCount(string line, string model)
    {
        string? segment = line
            .Split(Separator, StringSplitOptions.None)
            .SingleOrDefault(s => s.StartsWith(model + CountMarker, StringComparison.Ordinal));

        Assert.True(
            segment is not null,
            $"'{LineLabel}' carries no '{model}{CountMarker}<count>' segment; the line was: {line}");

        string tail = segment![(segment.IndexOf(CountMarker, StringComparison.Ordinal) + CountMarker.Length)..];
        string digits = new(tail.TakeWhile(char.IsAsciiDigit).ToArray());

        Assert.True(
            digits.Length > 0,
            $"segment '{segment}' names no attempt count after '{CountMarker}' — the line must report a "
            + "quantity, not just a model id.");

        return int.Parse(digits, CultureInfo.InvariantCulture);
    }
}
