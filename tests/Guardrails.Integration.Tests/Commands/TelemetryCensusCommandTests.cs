using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// The <c>guardrails telemetry census</c> verb (plan 30 §3.3a, issue #577) — the CLI surface over
/// <c>TelemetryAttributionCensus</c>, which prints the three-way split of the rows that name no model.
///
/// <para><b>Why this class is authored by task 23 and not by task 24, which writes the verb.</b> A test
/// authored by the same task that writes the thing it tests has NO RED HALF — nothing ever observes it
/// failing, so a hollow assertion (<c>exit == 0</c>, or an output check that passes over an empty string)
/// is indistinguishable from a real one, and the file is not write-scope protected against the task that
/// could most cheaply weaken it. So it is authored red here, and task 24 makes it green without being able
/// to touch it.</para>
///
/// <para><b>Red at RUN time, not compile time, and for a DIFFERENT reason than the Core seven.</b>
/// <c>AttributionCensusTests</c> is red because <c>Census</c> throws. These two are red because
/// <c>telemetry census</c> is not a registered verb yet — task 24 registers it — so the real root command
/// cannot reach it and cannot print anything. Nothing in this file names a type that does not exist on
/// today's tree: the verb is addressed by the literal argv tokens <c>"telemetry", "census",
/// &lt;folder&gt;</c>, never by a <c>census</c> symbol.</para>
///
/// <para><b>Through the REAL root.</b> <see cref="TelemetryCommandWiringTests"/> is the precedent and the
/// reason: a source grep proving a registration exists is defeatable by a dead call or a registration that
/// never reaches the real root at runtime, either of which leaves the shipped binary without the verb
/// while the grep still passes. So both tests invoke through <see cref="CommandFactory.BuildRootCommand"/>
/// — the root <c>Program.cs</c> actually builds, not a hand-built one.</para>
///
/// <para><b>The fixture is built on disk, never by driving a real run.</b> <c>state/run.json</c> is
/// written through the journal's own <see cref="JournalJson.Options"/>, with a
/// <c>tasks/&lt;id&gt;/task.json</c> and a real action file beside it so the action kind is genuine. The
/// census's subject IS what is on disk, and driving a <c>guardrails run</c> to produce it would make this
/// test depend on the whole harness to prove one verb prints one table. <c>TelemetryCorpusIsolation</c> is
/// a module initializer covering this whole assembly, so corpus isolation is already in force — which is
/// not a licence to touch the corpus: the census reads plan folders and no corpus at all.</para>
///
/// <para><b>What <see cref="Census_PrintsTheThreeWaySplit"/> pins is a CONTRACT, not a wording, and here
/// is where the line is drawn.</b>
/// <list type="bullet">
///   <item><description><b>The four numbers are all DIFFERENT by construction</b> (2 task-grain, 3
///   script-action, 4 recording-gap, 9 total). This is the load-bearing half: a verb that prints one
///   aggregate figure three times, or prints the total in place of a category, FAILS — the missing
///   distinct numbers are simply not in the output. With a 1/1/1 fixture it would pass and the test would
///   certify nothing.</description></item>
///   <item><description><b>The label STEMS are the contract; the surrounding prose is task 24's to
///   choose.</b> Each number must appear next to a label naming its category, matched
///   case-INSENSITIVELY on a stem the census's own vocabulary already uses — <c>task-grain</c>,
///   <c>script</c>, <c>recording gap</c>, <c>total</c> — never on a full sentence. Binding label to number
///   is per LINE, because that is the only way to tell "3 script-action rows" from a bare 3 somewhere else
///   in the table; it therefore does ask for one category per line rather than a wide columnar layout, and
///   that is the one shape decision stated here on purpose rather than smuggled in.</description></item>
///   <item><description><b>Nothing else is asserted.</b> No column widths, no ordering, no punctuation, no
///   colour, no banner line. Those are task 24's rendering decisions, and pinning them here would be the
///   over-reach §5 of the plan warns about from the other direction.</description></item>
/// </list></para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryCensusCommandTests
{
    /// <summary>The four numbers the split fixture produces, all DIFFERENT on purpose — see the class doc.</summary>
    private const int ScriptAttempts = 3;
    private const int GapAttempts = 4;
    private const int TaskGrainRows = 2;      // one sentinel per task, and the fixture has two tasks
    private const int TotalRowsNamingNoModel = TaskGrainRows + ScriptAttempts + GapAttempts;   // 9

    private static readonly DateTimeOffset FixtureStart = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(int Exit, string Output)> InvokeThroughRealRootAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// Registration itself: <c>telemetry census</c> must be reachable through the root the shipped binary
    /// builds. The observable is not merely the exit code — on today's tree the token is not a command
    /// <see cref="CommandFactory.BuildRootCommand"/> recognizes, so parsing fails before any action runs
    /// and NOTHING is ever written to the injected <see cref="IConsoleIo"/> (System.CommandLine's own
    /// parse-error output does not go through it). A non-empty capture can therefore only be explained by a
    /// registered census action having actually run. What that output must SAY is
    /// <see cref="Census_PrintsTheThreeWaySplit"/>'s contract, not this test's.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task TelemetryVerbCensus_IsReachableFromTheCommandFactory()
    {
        using var temp = new TempDir();
        string planFolder = WritePlanFolder(temp.Path, "plan-reachable", "run-reachable", scriptAttempts: 1, gapAttempts: 1);

        (int exit, string output) = await InvokeThroughRealRootAsync("telemetry", "census", planFolder);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.False(
            string.IsNullOrWhiteSpace(output),
            "expected the real root's 'telemetry census' to have written a census to the injected console; "
            + "an empty capture means the verb never ran (unregistered, or registered inert)");
    }

    /// <summary>
    /// The three-way split reaches the operator. Over a fixture whose four numbers are all different, each
    /// must appear on a line labelled with its own category's stem — see the class doc for exactly which
    /// half of this is the contract and which would be over-reach.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Census_PrintsTheThreeWaySplit()
    {
        using var temp = new TempDir();
        string planFolder = WritePlanFolder(
            temp.Path, "plan-split", "run-split", scriptAttempts: ScriptAttempts, gapAttempts: GapAttempts);

        (int exit, string output) = await InvokeThroughRealRootAsync("telemetry", "census", planFolder);

        Assert.Equal(ExitCodes.Success, exit);

        // The stems are the contract; the prose around them is task 24's to choose.
        AssertLabelledNumber(output, "task-grain", TaskGrainRows);
        AssertLabelledNumber(output, "script", ScriptAttempts);
        AssertLabelledNumber(output, "recording gap", GapAttempts);
        AssertLabelledNumber(output, "total", TotalRowsNamingNoModel);
    }

    // --- assertions ------------------------------------------------------------------------------------

    /// <summary>
    /// Some line of <paramref name="output"/> names <paramref name="labelStem"/> and carries
    /// <paramref name="value"/> as a whole number — never as a fragment of a longer one, so a 3 found
    /// inside "13" or "3.5" does not satisfy the 3.
    ///
    /// <para>The stem is matched case-INSENSITIVELY and with hyphens read as spaces, so "recording gap",
    /// "recording-gap", "Recording Gap" and "task grain" all name their category. That tolerance is about
    /// the SPELLING OF A SEPARATOR only — it does not admit a different word, and the vocabulary itself
    /// stays the contract.</para>
    /// </summary>
    private static void AssertLabelledNumber(string output, string labelStem, int value)
    {
        string stem = labelStem.Replace('-', ' ');

        bool found = output
            .Split('\n')
            .Any(line =>
                line.Replace('-', ' ').Contains(stem, StringComparison.OrdinalIgnoreCase)
                && CarriesTheNumber(line, value));

        Assert.True(
            found,
            $"expected a line naming '{labelStem}' and carrying the number {value}. The census's three "
            + "categories and its total must each be printed next to a label naming the category — the "
            + $"stems ('task-grain', 'script', 'recording gap', 'total') are the contract. Output was:{Environment.NewLine}{output}");
    }

    private static bool CarriesTheNumber(string line, int value)
    {
        string needle = value.ToString(CultureInfo.InvariantCulture);

        int index = line.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            int after = index + needle.Length;
            bool leftClear = index == 0 || !IsNumeric(line[index - 1]);
            bool rightClear = after >= line.Length || !IsNumeric(line[after]);
            if (leftClear && rightClear)
            {
                return true;
            }

            // index + 1 is always <= line.Length here (index is a valid match start), which is the
            // largest start IndexOf accepts.
            index = line.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsNumeric(char c) => char.IsDigit(c) || c == '.' || c == ',';

    // --- fixtures --------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a real plan folder with exactly two tasks: a SCRIPT task carrying
    /// <paramref name="scriptAttempts"/> attempts (correct by construction — a script invokes no model) and
    /// a PROMPT task carrying <paramref name="gapAttempts"/> attempts journalled with no provenance (the
    /// recording gap). Each gets a <c>task.json</c> and a REAL action file, so the action kind is genuine
    /// rather than asserted; the journal goes through the SAME <see cref="JournalJson.Options"/> production
    /// writes with.
    /// </summary>
    private static string WritePlanFolder(
        string parent, string planName, string runId, int scriptAttempts, int gapAttempts)
    {
        string planFolder = Path.Combine(parent, planName);

        WriteTaskFolder(planFolder, "01-script", "action.ps1", "Write-Output 'fixture work'\n");
        WriteTaskFolder(planFolder, "02-prompt", "action.prompt.md", "Do the fixture work.\n");

        var journal = new JournalDocument
        {
            RunId = runId,
            PlanHash = "sha256:" + new string('a', 64),
            NextMergeSequence = 1,
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            {
                ["01-script"] = TaskEntry(runId, "01-script", scriptAttempts),
                ["02-prompt"] = TaskEntry(runId, "02-prompt", gapAttempts)
            }
        };

        string journalPath = RunJournal.PathFor(planFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, JournalJson.Options));

        return planFolder;
    }

    private static void WriteTaskFolder(string planFolder, string taskId, string actionFileName, string actionBody)
    {
        string taskFolder = Path.Combine(planFolder, "tasks", taskId);
        Directory.CreateDirectory(taskFolder);

        File.WriteAllText(Path.Combine(taskFolder, actionFileName), actionBody);
        File.WriteAllText(
            Path.Combine(taskFolder, "task.json"),
            $"{{\n  \"description\": \"fixture task {taskId}\",\n  \"dependsOn\": []\n}}\n");
    }

    /// <summary>
    /// One task's journal entry. Every attempt journals NO provenance — for the script task that is what a
    /// script attempt records, and for the prompt task that is exactly the recording gap being counted.
    /// </summary>
    private static TaskJournalEntry TaskEntry(string runId, string taskId, int attempts)
    {
        var records = new List<AttemptRecord>();
        for (int i = 0; i < attempts; i++)
        {
            records.Add(new AttemptRecord
            {
                Attempt = i + 1,
                StartedAt = FixtureStart.AddMinutes(i),
                EndedAt = FixtureStart.AddMinutes(i + 1),
                ActionExitCode = 0,
                Outcome = AttemptOutcome.Succeeded,
                LogDir = $"logs/{runId}/{taskId}/attempt-{i + 1}"
            });
        }

        return new TaskJournalEntry
        {
            Status = JournalTaskStatus.Succeeded,
            Attempts = records
        };
    }

    /// <summary>A fresh temp directory, deleted on <see cref="Dispose"/>. Never <c>~/.guardrails/telemetry/</c>.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gr-telemetrycensus-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try { Directory.Delete(Path, recursive: true); }
                catch (IOException) { }
            }
        }
    }
}
