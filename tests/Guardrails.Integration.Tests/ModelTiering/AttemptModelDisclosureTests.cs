using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The #349 OPERATOR surfaces of one attempt's model, proven through the REAL attempt loop: the
/// per-attempt <c>attempt-route.log</c> preamble, and the <see cref="IRunObserver.AttemptModelResolved"/>
/// raise the CLI renderers hang off. The TDD red for
/// <c>05-implement-route-log-and-observer-raise</c>.
///
/// <para><b>Nothing here re-derives the model.</b> Wave 2 already made "what did this attempt actually
/// run on" a recorded fact: <c>AttemptProvenance.Model</c> is best-known-actual — the model the runner
/// reported itself running on, else the resolved route's, else the <c>"(cli default)"</c> sentinel — and
/// <c>AttemptProvenance.RequestedModel</c> carries the ROUTE's model, written ONLY when the two
/// disagree. Every value asserted below is one of those two, read off the surface that is supposed to be
/// SHOWING them. A surface that re-computed "did the provider serve something else" would be a second
/// owner of the rule and would drift from the <c>run.json</c> it displays.</para>
///
/// <para><b><c>requestedModel</c>'s PRESENCE is the mismatch signal.</b> There is no separate flag, so a
/// surface that always prints one string throws away the entire reason #349 exists, and a surface that
/// always prints two is equally wrong — the two-string form then carries no information at all. Three of
/// the five clauses below exist purely to pin that asymmetry: one for each half, plus the ABSENCE half,
/// whose fixture floor is the whole clause (see
/// <see cref="RouteLog_CarriesNoRequestedModelLine_WhenTheObservedMatchesTheRoute"/>).</para>
///
/// <para><b>The disclosure is read back OFF DISK</b> — via <see cref="Stage2RunResult.AttemptLogDir"/>,
/// never off an in-memory value the harness returned. A datum computed, assigned to something in memory
/// and never written satisfies any assertion made against that in-memory object. That is not
/// hypothetical in this repo: <c>AttemptRecord.Usage</c> shipped DECLARED, READ by the per-tier spend
/// aggregation, and assigned by no construction site at all (#475), with every guardrail in its wave
/// green.</para>
///
/// <para><b>The one faked seam is the process boundary</b> — <c>IPromptRunner</c>, via
/// <see cref="Stage2PlanHarness"/>. The loader, the <see cref="Scheduler"/>, the
/// <see cref="TaskExecutor"/> and its attempt loop are the SHIPPED ones, because the question this file
/// asks is whether anything CALLS the disclosure, which a suite whose seam sat above the wiring could
/// never answer (#382).</para>
///
/// <para><b>The class name is load-bearing.</b> Both this task's per-test red census and task 05's
/// tests-pass gate select on <c>FullyQualifiedName~AttemptModelDisclosureTests</c>, so a behaviour that
/// drifted into another class would be invisible to both — red proven nowhere, green certified over
/// nothing.</para>
/// </summary>
[Trait("Category", "AttemptModelDisclosure")]
public sealed class AttemptModelDisclosureTests
{
    /// <summary>The route disclosure file, in the attempt's own log dir (a sibling of <c>attempt-provenance.json</c>).</summary>
    private const string RouteLogName = "attempt-route.log";

    /// <summary>
    /// The disclosure's key for the attempt's best-known-actual model — the line
    /// <c>WriteRouteDisclosure</c> already writes, whose VALUE is what this wave changes.
    /// </summary>
    private const string ModelKey = "model";

    /// <summary>
    /// The disclosure's key for the ROUTE's model. Not invented here: <c>attempt-route.log</c> is one
    /// <c>key: value</c> per line (<c>runner block: </c>, <c>model: </c>, <c>effort: </c>,
    /// <c>requested tier: </c>, <c>served tier: </c>, <c>tierSource: </c>), and this is the exact sibling
    /// of the <c>requested tier: </c> line already sitting in that file.
    /// </summary>
    private const string RequestedModelKey = "requested model";

    /// <summary>The <c>promptRunners.default</c> pointer's block — deliberately carries NO <c>routing</c>.</summary>
    private const string Pointer = "pointer";

    /// <summary>
    /// The pointer block's model, named distinctly on purpose: it is the wrong answer sitting right
    /// beside the right one, so a disclosure that fell back to the registry default instead of naming
    /// the resolved route is unmistakable in the failure message.
    /// </summary>
    private const string PointerModel = "pointer-model";

    /// <summary>The one routable block — every task here resolves to it.</summary>
    private const string RoutedRunner = "routed";

    /// <summary>That block's model: what the ROUTE ASKS FOR, on both tasks.</summary>
    private const string RouteModel = "route-asked-model";

    /// <summary>
    /// What the fake runner ECHOES back on <see cref="MismatchTask"/>. Named with no substring in common
    /// with <see cref="RouteModel"/> so a failure message makes the wrong answer obvious rather than a
    /// subtle difference between two plausible model names.
    /// </summary>
    private const string ServedModel = "provider-served-something-else";

    /// <summary>The task whose runner answers with a model DIFFERENT from the one the route asked for.</summary>
    private const string MismatchTask = "01-runner-answered-otherwise";

    /// <summary>The task whose runner echoes back EXACTLY what the route asked for.</summary>
    private const string MatchTask = "02-runner-echoed-the-route";

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // The per-attempt log preamble
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The attempt's <c>attempt-route.log</c> <c>model:</c> line carries the model that actually RAN, not
    /// the one the route asked for.
    ///
    /// <para><b>This is the single failure #349 exists to prevent.</b> The disclosure is written from the
    /// launch-time provenance, and at launch the harness knows only the REQUEST — the runner has not yet
    /// reported anything. An operator reading the log therefore sees a model that may never have served
    /// the attempt, stated with the same confidence as a fact. Once wave 2 made <c>provenance.model</c>
    /// best-known-actual, the prose twin of that object must say the same thing the object says, or the
    /// two surfaces disagree about the same attempt.</para>
    ///
    /// <para><b>Both fixture floors are taken first</b> — the route really asked for one model and the
    /// runner really answered with another. Without them "the observed value wins" would be asserted over
    /// a run where the two never disagreed, which every implementation passes, including none at all.</para>
    /// </summary>
    [Fact]
    public async Task RouteLog_NamesTheObservedModel_NotTheRequestedOne()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(DisclosurePlan());

        AssertSettledGreen(run, MismatchTask);
        AssertRouteAskedFor(run, MismatchTask);
        AssertRunnerAnswered(run, MismatchTask, ServedModel);

        string disclosure = RouteDisclosure(run, MismatchTask, 1);
        string? disclosed = DisclosedValue(disclosure, ModelKey);

        Assert.True(
            disclosed == ServedModel,
            $"'{MismatchTask}' attempt 1's {RouteLogName} states '{ModelKey}: {Describe(disclosed)}', but the " +
            $"runner reported running on '{ServedModel}' while the route merely ASKED for '{RouteModel}'. " +
            "The log line is the prose twin of provenance.model, which is best-known-actual — observed, " +
            "else the resolved route, else the sentinel — so the moment an attempt learns what actually " +
            "served it, that is what the preamble names. Printing the request here tells the operator a " +
            "model ran that may never have, in the one place they look while the run is still going.\n\n" +
            "The disclosure as written was:\n" + disclosure);
    }

    /// <summary>
    /// That same disagreeing attempt's log ALSO carries a <c>requested model:</c> line naming the ROUTE's
    /// model — the one fact the <c>model:</c> line can no longer carry.
    ///
    /// <para><b>Nothing is lost by the swap, and that is the point.</b> The request is not disposable: it
    /// is what the operator's <c>promptRunners</c> block and <c>tiering</c> config actually selected, so
    /// it is the only evidence that separates "the provider served something else" from "my routing is
    /// misconfigured". From a single model line those two look identical, and their fixes are
    /// opposites.</para>
    ///
    /// <para><b>The key is the file's own idiom, not a new format.</b> <c>attempt-route.log</c> is one
    /// <c>key: value</c> per line and already carries <c>requested tier: </c> beside <c>served tier: </c>;
    /// <c>requested model: </c> is that line's exact sibling. A second format inside one file would make
    /// the disclosure unreadable by the eye it exists for.</para>
    /// </summary>
    [Fact]
    public async Task RouteLog_CarriesARequestedModelLine_WhenTheObservedDiffersFromTheRoute()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(DisclosurePlan());

        AssertSettledGreen(run, MismatchTask);
        AssertRouteAskedFor(run, MismatchTask);
        AssertRunnerAnswered(run, MismatchTask, ServedModel);

        string disclosure = RouteDisclosure(run, MismatchTask, 1);
        string? disclosed = DisclosedValue(disclosure, RequestedModelKey);

        Assert.True(
            disclosed == RouteModel,
            $"'{MismatchTask}' attempt 1's {RouteLogName} states " +
            $"'{RequestedModelKey}: {Describe(disclosed)}', expected the route's '{RouteModel}' — the " +
            $"runner answered '{ServedModel}', so the request and the reality disagree and the request " +
            "needs its own line. Its absence leaves an operator unable to tell a provider substitution " +
            "from a routing config that selected the wrong block, which are the two causes with opposite " +
            $"fixes. The line is the exact sibling of the 'requested tier: ' line already in this file.\n\n" +
            "The disclosure as written was:\n" + disclosure);
    }

    /// <summary>
    /// An attempt whose runner echoed back EXACTLY what the route asked for carries NO
    /// <c>requested model:</c> line at all. Its presence IS the mismatch signal, so a line written on
    /// every attempt makes the signal indistinguishable from the ordinary case — the one thing it exists
    /// to distinguish.
    ///
    /// <para><b>The floor is the whole clause.</b> "The line is absent" is trivially satisfied by an
    /// implementation that writes it nowhere — which is precisely the state of this file before the wave.
    /// So the DISAGREEING sibling, in the SAME run on the same plan and the same block, must carry the
    /// line first; only then does absence over here mean "the two agreed" rather than "the mechanism does
    /// not exist".</para>
    /// </summary>
    [Fact]
    public async Task RouteLog_CarriesNoRequestedModelLine_WhenTheObservedMatchesTheRoute()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(DisclosurePlan());

        AssertSettledGreen(run, MatchTask);
        AssertSettledGreen(run, MismatchTask);

        // FLOOR: the mismatching sibling really does disclose the request, so the absence asserted below
        // is a decision this run made rather than a mechanism nobody built.
        string siblingDisclosure = RouteDisclosure(run, MismatchTask, 1);
        Assert.True(
            DisclosedValue(siblingDisclosure, RequestedModelKey) == RouteModel,
            $"'{MismatchTask}' — whose runner answered '{ServedModel}' to a route asking for " +
            $"'{RouteModel}' — discloses " +
            $"'{RequestedModelKey}: {Describe(DisclosedValue(siblingDisclosure, RequestedModelKey))}', so " +
            "this run writes the line NOWHERE. An absence asserted where the mechanism does not exist " +
            "proves nothing about the agreeing case; wire the mismatch line first.\n\n" +
            "The sibling's disclosure was:\n" + siblingDisclosure);

        AssertRouteAskedFor(run, MatchTask);
        AssertRunnerAnswered(run, MatchTask, RouteModel);

        string disclosure = RouteDisclosure(run, MatchTask, 1);

        Assert.True(
            DisclosedValue(disclosure, ModelKey) == RouteModel,
            $"'{MatchTask}' attempt 1's {RouteLogName} states '{ModelKey}: " +
            $"{Describe(DisclosedValue(disclosure, ModelKey))}' for an attempt whose route and runner both " +
            $"say '{RouteModel}'. best-known-actual and the request are the same string here; there is no " +
            "third answer.\n\nThe disclosure as written was:\n" + disclosure);

        string? disclosedRequest = DisclosedValue(disclosure, RequestedModelKey);
        Assert.True(
            disclosedRequest is null,
            $"'{MatchTask}' attempt 1's {RouteLogName} carries " +
            $"'{RequestedModelKey}: {Describe(disclosedRequest)}' on an attempt where the runner echoed " +
            "exactly what the route asked for. The line is written ONLY on a disagreement — its presence " +
            "IS the mismatch signal, and there is no separate flag beside it. Printed on every attempt it " +
            "says nothing, and the operator loses the one line that was worth reading.\n\n" +
            "The disclosure as written was:\n" + disclosure);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // The raise from the attempt loop
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The attempt loop RAISES <see cref="IRunObserver.AttemptModelResolved"/> for the prompt attempt,
    /// handing the observer BOTH strings: the best-known-actual model, and the route's model as the
    /// requested one.
    ///
    /// <para><b>The log preamble is not enough on its own.</b> It is a file an operator opens after the
    /// fact; the event is what reaches the live table and the plain <c>--no-ui</c> stream WHILE the run is
    /// going, which is when a substituted model is still worth knowing. The event exists so the renderers
    /// (tasks 06/07) have something to render — and until something raises it, every one of those surfaces
    /// would be wired to a call site that never fires.</para>
    ///
    /// <para><b>Both strings are passed VERBATIM from the folded provenance.</b> The harness folds the
    /// observed model over the requested one ONCE, at the attempt, and hands both down; no observer
    /// re-derives the comparison. The event is asserted to fire EXACTLY ONCE for the attempt, because two
    /// raises are two lines on the operator's screen about one attempt.</para>
    /// </summary>
    [Fact]
    public async Task AttemptLoop_RaisesAttemptModelResolved_WithBothStrings_OnMismatch()
    {
        using var harness = new Stage2PlanHarness();
        var observer = new RecordingObserver();
        Stage2RunResult run = await harness.RunAsync(DisclosurePlan(), observer);

        AssertSettledGreen(run, MismatchTask);
        AssertRouteAskedFor(run, MismatchTask);
        AssertRunnerAnswered(run, MismatchTask, ServedModel);

        AttemptModelRaise raise = RaiseFor(observer, MismatchTask, 1);

        Assert.True(
            raise.Model == ServedModel,
            $"the attempt loop raised {nameof(IRunObserver.AttemptModelResolved)} for '{MismatchTask}' " +
            $"attempt 1 with model '{Describe(raise.Model)}', expected the model that actually ran, " +
            $"'{ServedModel}'. The route asked for '{RouteModel}'; the event carries provenance.Model, " +
            "which is best-known-actual, so an operator watching the run learns what served the attempt " +
            "rather than what was ordered.");

        Assert.True(
            raise.RequestedModel == RouteModel,
            $"the attempt loop raised {nameof(IRunObserver.AttemptModelResolved)} for '{MismatchTask}' " +
            $"attempt 1 with requestedModel '{Describe(raise.RequestedModel)}', expected the route's " +
            $"'{RouteModel}'. The second string is the whole reason the event takes two: with only the " +
            "model that ran, 'the provider substituted' and 'my routing selected the wrong block' arrive " +
            "at the operator as the same line.");
    }

    /// <summary>
    /// The SAME run, with an agreeing runner, raises the event too — with <c>requestedModel</c> NULL. The
    /// event fires either way; only the second string is conditional.
    ///
    /// <para><b>Why the event is not conditional.</b> Raising only on a disagreement would make the
    /// operator's model line appear exactly when something is odd and vanish the rest of the time, so the
    /// renderers could never show an attempt's model as an ordinary fact — and a surface that only ever
    /// appears in trouble teaches an operator nothing about the healthy case it is being compared
    /// against.</para>
    ///
    /// <para><b>The floor is the same asymmetry the disclosure clause guards.</b> A null second string is
    /// trivially satisfied by an implementation that never populates it, so the disagreeing sibling in
    /// this same run must carry a non-null one first.</para>
    /// </summary>
    [Fact]
    public async Task AttemptLoop_RaisesAttemptModelResolved_WithNoRequestedModel_WhenTheRunnerEchoedTheRoute()
    {
        using var harness = new Stage2PlanHarness();
        var observer = new RecordingObserver();
        Stage2RunResult run = await harness.RunAsync(DisclosurePlan(), observer);

        AssertSettledGreen(run, MatchTask);
        AssertSettledGreen(run, MismatchTask);

        // FLOOR: the disagreeing sibling really does hand the observer a second string, so the null below
        // is a decision this run made rather than a parameter nobody ever populates.
        AttemptModelRaise sibling = RaiseFor(observer, MismatchTask, 1);
        Assert.True(
            sibling.RequestedModel == RouteModel,
            $"'{MismatchTask}' — whose runner answered '{ServedModel}' to a route asking for " +
            $"'{RouteModel}' — was raised with requestedModel '{Describe(sibling.RequestedModel)}', so " +
            "this run populates the second string NOWHERE. A null asserted where nothing is ever written " +
            "proves nothing about the agreeing case; wire the mismatch raise first.");

        AssertRouteAskedFor(run, MatchTask);
        AssertRunnerAnswered(run, MatchTask, RouteModel);

        AttemptModelRaise raise = RaiseFor(observer, MatchTask, 1);

        Assert.True(
            raise.Model == RouteModel,
            $"the attempt loop raised {nameof(IRunObserver.AttemptModelResolved)} for '{MatchTask}' " +
            $"attempt 1 with model '{Describe(raise.Model)}', expected '{RouteModel}' — the route and the " +
            "runner name the same string here, so best-known-actual and the request are one answer.");

        Assert.True(
            raise.RequestedModel is null,
            $"the attempt loop raised {nameof(IRunObserver.AttemptModelResolved)} for '{MatchTask}' " +
            $"attempt 1 with requestedModel '{Describe(raise.RequestedModel)}' on an attempt where the " +
            "runner echoed exactly what the route asked for. Non-null is the mismatch signal itself, and " +
            "there is no separate flag beside it — passing a copy of the model on every raise makes every " +
            "attempt look like a disagreement, which is the same as flagging none of them.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // The fixture
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The #349 disclosure fixture: one registry, two tasks, and the two halves of the asymmetry — a
    /// runner that answers with a model OTHER than the route's, and one that echoes the route back
    /// exactly. Both scripted EXPLICITLY, so "what the runner reported" is a stated fixture rather than a
    /// default nobody looked at.
    ///
    /// <para>Both run in ONE plan on purpose: the two ABSENCE clauses need a task that DOES carry the
    /// second string sitting in the same run as the task that must not. Otherwise "it is absent here" is
    /// satisfied by a run that writes it nowhere, which is the state of every run before this wave.</para>
    ///
    /// <para>The <c>pointer</c> block carries no <c>routing</c>, so it is never a tier target — it exists
    /// only so a disclosure that fell back to the registry default instead of naming the resolved route
    /// names a distinctly wrong string.</para>
    /// </summary>
    private static Stage2PlanSpec DisclosurePlan() => new()
    {
        DefaultRunner = Pointer,
        Runners =
        [
            new Stage2RunnerBlock { Name = Pointer, Model = PointerModel },
            new Stage2RunnerBlock
            {
                Name = RoutedRunner,
                Model = RouteModel,
                Strength = 2,
                Tiers = [ActionTiers.Easy]
            }
        ],
        Tasks =
        [
            new Stage2TaskSpec
            {
                Id = MismatchTask,
                Tier = ActionTiers.Easy,
                Results = [Stage2PlanHarness.Success() with { ObservedModel = ServedModel }]
            },
            new Stage2TaskSpec
            {
                Id = MatchTask,
                Tier = ActionTiers.Easy,
                Results = [Stage2PlanHarness.Success() with { ObservedModel = RouteModel }]
            }
        ]
    };

    /// <summary>
    /// One <see cref="IRunObserver.AttemptModelResolved"/> raise, captured exactly as the shipped attempt
    /// loop handed it over — the task, the attempt, and the two strings.
    /// </summary>
    private sealed record AttemptModelRaise(string TaskId, int Attempt, string Model, string? RequestedModel);

    /// <summary>
    /// An observer that records nothing but the #349 raises. Everything else is a no-op: this file is
    /// about ONE event, and an observer that also asserted on task lifecycle would fail for reasons that
    /// have nothing to do with the surface under test.
    ///
    /// <para>Locked because <see cref="IRunObserver"/> requires thread safety — the harness runs one
    /// worker, but the contract is the contract and a suite that quietly relied on the worker count would
    /// be the wrong example to leave behind.</para>
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        private readonly List<AttemptModelRaise> _raised = [];
        private readonly object _gate = new();

        /// <summary>Every raise this run made, in order.</summary>
        public IReadOnlyList<AttemptModelRaise> Raised
        {
            get
            {
                lock (_gate)
                {
                    return [.. _raised];
                }
            }
        }

        /// <inheritdoc />
        public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
        {
            lock (_gate)
            {
                _raised.Add(new AttemptModelRaise(task.Id, attempt, model, requestedModel));
            }
        }

        /// <inheritdoc />
        public void TaskStarting(TaskNode task) { }

        /// <inheritdoc />
        public void TaskFinished(TaskResult result) { }

        /// <inheritdoc />
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Observation helpers
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The ONE raise for <paramref name="taskId"/>'s <paramref name="attempt"/> — asserted to be exactly
    /// one, so neither "the event never fired" nor "it fired twice for one attempt" can pass as the
    /// single line an operator is supposed to see.
    /// </summary>
    private static AttemptModelRaise RaiseFor(RecordingObserver observer, string taskId, int attempt)
    {
        IReadOnlyList<AttemptModelRaise> all = observer.Raised;
        IReadOnlyList<AttemptModelRaise> matching =
        [
            .. all.Where(r => string.Equals(r.TaskId, taskId, StringComparison.Ordinal) && r.Attempt == attempt)
        ];

        Assert.True(
            matching.Count == 1,
            $"the run raised {nameof(IRunObserver.AttemptModelResolved)} {matching.Count} time(s) for " +
            $"'{taskId}' attempt {attempt}, expected exactly 1. Across the whole run it raised it " +
            $"{all.Count} time(s): [{Join(all.Select(r => $"{r.TaskId}#{r.Attempt} model={Describe(r.Model)} " +
            $"requested={Describe(r.RequestedModel)}"))}]. The observer is handed to BOTH the TaskExecutor " +
            "and the Scheduler, so a raise from either is visible here — zero means nothing in the attempt " +
            "loop raises it at all, and the event's default no-op body swallows it in every mode.");

        return matching[0];
    }

    /// <summary>
    /// The attempt's route disclosure, asserted PRESENT — the human-facing preamble written beside
    /// <c>attempt-provenance.json</c> in the attempt's own log dir, read back from the path the journal
    /// itself recorded.
    /// </summary>
    private static string RouteDisclosure(Stage2RunResult run, string taskId, int attempt)
    {
        string path = Path.Combine(run.AttemptLogDir(taskId, attempt), RouteLogName);
        Assert.True(
            File.Exists(path),
            $"'{taskId}' attempt {attempt} wrote no {RouteLogName}. The attempt's log dir holds " +
            $"[{Join(run.AttemptLogFiles(taskId, attempt))}]. The disclosure is the prose twin of the " +
            "provenance object — the surface an operator reads without opening run.json — and every " +
            "clause about the preamble reads it from disk, because a line composed in memory and never " +
            "written satisfies an in-memory assertion and nothing else.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The value of one <c>key: value</c> line of a disclosure, or null when the file carries no such
    /// line — the distinction three clauses turn on. Matched on the WHOLE key so
    /// <c>requested model: </c> can never be read as the <c>model: </c> line.
    /// </summary>
    private static string? DisclosedValue(string disclosure, string key)
    {
        string prefix = key + ": ";
        foreach (string raw in disclosure.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// The task settled SUCCEEDED — the floor under everything else, since a task that never reached a
    /// settled attempt has neither a journaled log dir to read nor an attempt loop that got far enough to
    /// raise anything. Without it a broken fixture reports "no disclosure was written", which names the
    /// wrong defect.
    /// </summary>
    private static void AssertSettledGreen(Stage2RunResult run, string taskId)
    {
        TaskResult? result =
            run.Report.Tasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));

        Assert.True(
            result is not null,
            $"the run report has no entry for '{taskId}' (it reported: " +
            $"[{Join(run.Report.Tasks.Select(t => t.TaskId))}]).");

        Assert.True(
            result!.Outcome == TaskOutcome.Succeeded,
            $"'{taskId}' settled '{result.Outcome}', expected '{TaskOutcome.Succeeded}'. Summary: " +
            result.Summary);
    }

    /// <summary>
    /// FIXTURE FLOOR — the route really asked for <see cref="RouteModel"/>, observed as the model the
    /// invocation actually carried to the process boundary. A fixture whose route resolved somewhere else
    /// (the pointer block, say) would pin the wrong string in every clause below it.
    /// </summary>
    private static void AssertRouteAskedFor(Stage2RunResult run, string taskId)
    {
        Stage2RecordedCall call = ActionCall(run, taskId, 1);
        Assert.True(
            call.Model == RouteModel,
            $"'{taskId}' attempt 1 RAN on '{Describe(call.Model)}', expected the route's '{RouteModel}' " +
            $"(it dispatched to block '{call.RunnerName}'). The requested half of this fixture is not in " +
            "place, so there is nothing here for an observed model to differ FROM.");
    }

    /// <summary>
    /// FIXTURE FLOOR — the fake runner really answered with <paramref name="expected"/>. The scripted
    /// <c>PromptResult.ObservedModel</c> is the entire setup for these clauses: a runner that reported
    /// nothing leaves the wave-2 fold with nothing to fold.
    /// </summary>
    private static void AssertRunnerAnswered(Stage2RunResult run, string taskId, string expected)
    {
        Stage2RecordedCall call = ActionCall(run, taskId, 1);
        Assert.True(
            call.Result.ObservedModel == expected,
            $"'{taskId}' attempt 1's fake runner reported observedModel " +
            $"'{Describe(call.Result.ObservedModel)}', expected '{expected}'.");
    }

    /// <summary>The one ACTION invocation of <paramref name="taskId"/>'s <paramref name="attempt"/>.</summary>
    private static Stage2RecordedCall ActionCall(Stage2RunResult run, string taskId, int attempt)
    {
        IReadOnlyList<Stage2RecordedCall> matching =
            [.. run.ActionCallsFor(taskId).Where(c => c.Attempt == attempt)];

        Assert.True(
            matching.Count == 1,
            $"expected exactly 1 action invocation for '{taskId}' attempt {attempt}, saw " +
            $"{matching.Count} (the run made {run.ActionCallsFor(taskId).Count} action call(s) for this task).");
        return matching[0];
    }

    /// <summary>Renders an absent value as a readable token, so a failure never reads "expected x, got ''".</summary>
    private static string Describe(string? value) => value ?? "(absent)";

    /// <summary>Joins a diagnostic list, naming emptiness rather than rendering as a blank.</summary>
    private static string Join(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }
}
