using Guardrails.Cli;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The JIT breakdown's OPERATOR-FACING surfaces (design of record
/// <c>docs/plans/23-jit-breakdown-visibility.md</c>, issue #469). Before this, a 30-minute authoring session
/// was invisible on every one of them: the live table emits rows per <c>wave.Tasks</c> and a JIT stub has
/// none, so a completed wave 1 collapsed (#379) to a single green line and the run rendered as FINISHED
/// while it was mid-authoring.
///
/// <para>These pin the PURE seams — the row plan, the probe, and the formatters — exactly as
/// <see cref="LiveRunObserver.StatusMarkup"/> and <c>GuardrailHeartbeat.FormatLine</c> are pinned: no live
/// terminal renders in a non-interactive test, so the public pure function IS the test seam. And they pin
/// the two properties that make the feature safe to ship: <b>the #485 rule</b> (a flat plan and a
/// fully-authored waved plan render byte-identically to before) and <b>silence over a lie</b> (a signal that
/// was not observed is omitted, never fabricated).</para>
/// </summary>
public sealed class JitBreakdownVisibilityTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private static TaskNode WaveTask(string waveDir, string folder) => new()
    {
        Id = $"{waveDir}/{folder}",
        WaveDir = waveDir,
        Directory = $"/fake/plan/{waveDir}/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private static WaveNode Wave(string dir, int number, params TaskNode[] tasks) => new()
    {
        Dir = dir,
        Number = number,
        Slug = dir.Split('-', 3)[2],
        Directory = $"/fake/plan/{dir}",
        Tasks = tasks
    };

    private static IReadOnlySet<string> Completed(params string[] dirs) =>
        new HashSet<string>(dirs, StringComparer.Ordinal);

    private static WaveBreakdownContext Context(string waveDir, string logDir, string tasksDir, string? manifest = null) => new()
    {
        WaveDir = waveDir,
        Index = 2,
        Total = 2,
        BreakdownLogDir = logDir,
        StreamLogPath = Path.Combine(logDir, "claude-stream.jsonl"),
        TasksDirectory = tasksDir,
        ComposedPromptBytes = 232_396,
        Ceiling = TimeSpan.FromMinutes(30),
        IntentManifestPath = manifest
    };

    /// <summary>A throwaway directory tree for the probe + log-site tests.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-469-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public string Dir(params string[] parts)
        {
            string path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T1 — the #485 rule: the dominant cases render byte-identically to today.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    // A FLAT plan's row list, asserted against the LITERAL list — not a Contains, not a count. This is the
    // case that dominates and the one a rendering change silently breaks, so the assertion is equality on
    // the whole ordered sequence: any extra row, any reordering, any changed payload fails it.
    [Fact]
    public void T1_FlatPlan_RowList_IsExactlyOneTaskRowPerTask_ByteIdenticalToBefore()
    {
        IReadOnlyList<TaskNode> tasks = [FlatTask("01-first"), FlatTask("02-second"), FlatTask("03-third")];

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(tasks, [], Completed("anything"), showAllTasks: false);

        Assert.Equal<LiveTableRow>(
            [
                new TaskLiveRow("01-first"),
                new TaskLiveRow("02-second"),
                new TaskLiveRow("03-third")
            ],
            rows);
    }

    // A fully-AUTHORED waved plan: every wave has tasks, so not one phase row is emitted and the list is
    // byte-identical to the pre-#469 one — in every combination of collapse and the --all-tasks opt-out.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T1_FullyAuthoredWavedPlan_RowList_IsUnchanged_WithAndWithoutTheOptOut(bool showAllTasks)
    {
        TaskNode a1 = WaveTask("wave-01-alpha", "01-a");
        TaskNode a2 = WaveTask("wave-01-alpha", "02-b");
        TaskNode b1 = WaveTask("wave-02-beta", "01-a");
        IReadOnlyList<WaveNode> waves = [Wave("wave-01-alpha", 1, a1, a2), Wave("wave-02-beta", 2, b1)];

        IReadOnlyList<LiveTableRow> fresh = LiveTableRows.Plan([a1, a2, b1], waves, Completed(), showAllTasks);
        Assert.Equal<LiveTableRow>(
            [
                new TaskLiveRow("wave-01-alpha/01-a"),
                new TaskLiveRow("wave-01-alpha/02-b"),
                new TaskLiveRow("wave-02-beta/01-a")
            ],
            fresh);

        // …and once wave-01 settles, exactly the pre-#469 collapse (or, under the opt-out, no collapse).
        IReadOnlyList<LiveTableRow> settled =
            LiveTableRows.Plan([a1, a2, b1], waves, Completed("wave-01-alpha"), showAllTasks);
        Assert.Equal<LiveTableRow>(
            showAllTasks
                ?
                [
                    new TaskLiveRow("wave-01-alpha/01-a"),
                    new TaskLiveRow("wave-01-alpha/02-b"),
                    new TaskLiveRow("wave-02-beta/01-a")
                ]
                :
                [
                    new WaveSummaryLiveRow("wave-01-alpha", 2),
                    new TaskLiveRow("wave-02-beta/01-a")
                ],
            settled);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T2 — the JIT stub gets exactly one phase row, leading its block, from RUN START.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T2_ZeroTaskWave_YieldsExactlyOnePhaseRow_FirstInThatWavesBlock()
    {
        TaskNode a1 = WaveTask("wave-01-alpha", "01-a");
        IReadOnlyList<WaveNode> waves = [Wave("wave-01-alpha", 1, a1), Wave("wave-02-beta", 2)];

        // Run start: nothing has completed yet. Before #469 this list was ONE row and said nothing about a
        // second wave existing at all.
        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan([a1], waves, Completed(), showAllTasks: false);

        Assert.Equal<LiveTableRow>(
            [new TaskLiveRow("wave-01-alpha/01-a"), new WavePhaseLiveRow("wave-02-beta")], rows);

        // Mid-breakdown, with wave-01 collapsed (#379): the screen that was one motionless green line.
        IReadOnlyList<LiveTableRow> collapsed =
            LiveTableRows.Plan([a1], waves, Completed("wave-01-alpha"), showAllTasks: false);
        Assert.Equal<LiveTableRow>(
            [new WaveSummaryLiveRow("wave-01-alpha", 1), new WavePhaseLiveRow("wave-02-beta")], collapsed);
    }

    // --all-tasks suppresses the COLLAPSE, and only the collapse: an unauthored wave has no task rows to
    // expand, so hiding its phase row under the opt-out would re-create the exact silence #469 is closing.
    [Fact]
    public void T2_AllTasksOptOut_StillEmitsThePhaseRow()
    {
        TaskNode a1 = WaveTask("wave-01-alpha", "01-a");
        IReadOnlyList<WaveNode> waves = [Wave("wave-01-alpha", 1, a1), Wave("wave-02-beta", 2)];

        IReadOnlyList<LiveTableRow> rows =
            LiveTableRows.Plan([a1], waves, Completed("wave-01-alpha"), showAllTasks: true);

        Assert.Equal<LiveTableRow>(
            [new TaskLiveRow("wave-01-alpha/01-a"), new WavePhaseLiveRow("wave-02-beta")], rows);
    }

    // The key is deliberately shaped so #476's wave gates can reuse the row as a CONTENT change, and so it
    // can never collide with an SSOT §14.2 wave-qualified task id.
    [Fact]
    public void PhaseRowKey_IsPhaseScoped_AndCannotCollideWithATaskId()
    {
        var row = new WavePhaseLiveRow("wave-02-beta");

        Assert.Equal("wave-02-beta/(breakdown)", row.BreakdownKey);
        Assert.Equal("wave-02-beta/(exit-gate)", WavePhaseLiveRow.KeyFor("wave-02-beta", "exit-gate"));
        Assert.NotEqual("wave-02-beta/breakdown", row.BreakdownKey);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T3 / T4 — the probe: what it counts, and what it refuses to claim.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_Probe_CountsTaskFoldersWithATaskJson_AndTheStreamsAge()
    {
        using var tree = new TempTree();
        string tasks = tree.Dir("tasks");
        foreach (string folder in new[] { "01-a", "02-b", "03-c", "04-d", "05-e" })
        {
            File.WriteAllText(Path.Combine(tree.Dir("tasks", folder), "task.json"), "{}");
        }

        tree.Dir("tasks", "06-in-flight"); // created, nothing written yet — not counted

        string logDir = tree.Dir("breakdown");
        string stream = Path.Combine(logDir, "claude-stream.jsonl");
        File.WriteAllText(stream, "{}\n");
        var written = new DateTimeOffset(2026, 8, 20, 5, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(stream, written.UtcDateTime);

        BreakdownProgress.Snapshot s = BreakdownProgress.Probe(
            tasks, stream, intentManifestPath: null, now: written.AddSeconds(3));

        Assert.Equal(5, s.TaskFolders);
        Assert.Null(s.DeclaredTotal);
        Assert.True(s.StreamSeen);
        Assert.Equal(TimeSpan.FromSeconds(3), s.StreamIdle);
    }

    // Silence over a lie. If the stream file never existed the fragment is omitted ENTIRELY — rendering
    // `idle 12m` would be a fabricated alarm about a file nobody promised to write, and the residual risk
    // the design accepts is that the signal disappears quietly, never that it lies.
    [Fact]
    public void T4_Probe_WithNoStreamFileEver_ReportsNotSeen_AndTheDetailOmitsTheFragment()
    {
        using var tree = new TempTree();
        string tasks = tree.Dir("tasks");
        File.WriteAllText(Path.Combine(tree.Dir("tasks", "01-a"), "task.json"), "{}");
        File.WriteAllText(Path.Combine(tree.Dir("tasks", "02-b"), "task.json"), "{}");

        BreakdownProgress.Snapshot s = BreakdownProgress.Probe(
            tasks, Path.Combine(tree.Root, "breakdown", "claude-stream.jsonl"),
            intentManifestPath: null, now: DateTimeOffset.UtcNow);

        Assert.False(s.StreamSeen);
        Assert.Null(s.StreamIdle);

        string detail = BreakdownProgress.DetailMarkup(s);
        Assert.Equal("2 task folders", detail);
        Assert.DoesNotContain("idle", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("stream", detail, StringComparison.Ordinal);
    }

    // An unreadable tasks/ omits the COUNT fragment rather than reporting zero — "zero folders written" and
    // "I could not look" are different facts and only one of them is alarming.
    [Fact]
    public void Probe_UnknownTaskCount_OmitsTheCountFragment_RatherThanClaimingZero()
    {
        var unknown = new BreakdownProgress.Snapshot(
            BreakdownProgress.UnknownTaskFolders, null, TimeSpan.FromSeconds(2), true);

        Assert.Null(BreakdownProgress.CountFragment(unknown));
        Assert.Equal("stream ok", BreakdownProgress.DetailMarkup(unknown));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T5 — the status cell. The denominator is the BUDGET, and it is the ONLY denominator.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_StatusMarkup_RendersElapsedAgainstTheCeiling_InTheShippedStopwatchFormat()
    {
        Assert.Equal(
            "authoring 7:12 / 30:00",
            BreakdownProgress.StatusMarkup(
                TimeSpan.FromSeconds(432), TimeSpan.FromMinutes(30), BreakdownProgress.AuthoringPhase));

        Assert.Equal(
            "authoring 0:01 / 30:00",
            BreakdownProgress.StatusMarkup(
                TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(30), BreakdownProgress.AuthoringPhase));

        // Settled rows drop the ceiling: a finished session's remaining budget is not a fact anyone needs.
        Assert.Equal("authored 18:42", BreakdownProgress.TerminalStatus("authored", TimeSpan.FromSeconds(1122)));
    }

    // The forbidden renderings (design 23 §4): no bar, no percentage, no inferred denominator. The task
    // count is a RESULT of the session, so nothing may present it as one.
    [Fact]
    public void NoLiveFragment_EverRendersAPercentageOrABar()
    {
        var s = new BreakdownProgress.Snapshot(5, null, TimeSpan.FromSeconds(2), true);
        string status = BreakdownProgress.StatusMarkup(
            TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(30), BreakdownProgress.AuthoringPhase);
        string detail = BreakdownProgress.DetailMarkup(s);
        string plain = BreakdownProgress.PlainLine("wave-02-beta", TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(30), s);

        foreach (string rendered in new[] { status, detail, plain })
        {
            Assert.DoesNotContain("%", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("█", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("of 1", rendered, StringComparison.Ordinal); // no "task 5 of 11"
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T6 — the freshness threshold, both sides. A number appears only when it carries information.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_StreamFragment_IsAWordBelowTheThreshold_AndANumberAtIt()
    {
        Assert.Equal(60, BreakdownProgress.StreamFreshSeconds);

        var fresh = new BreakdownProgress.Snapshot(5, null, TimeSpan.FromSeconds(59), true);
        var stale = new BreakdownProgress.Snapshot(5, null, TimeSpan.FromSeconds(60), true);
        var older = new BreakdownProgress.Snapshot(5, null, TimeSpan.FromSeconds(258), true);

        Assert.Equal("5 task folders · stream ok", BreakdownProgress.DetailMarkup(fresh));
        Assert.Equal("5 task folders · stream idle 1m00s", BreakdownProgress.DetailMarkup(stale));
        Assert.Equal("5 task folders · stream idle 4m18s", BreakdownProgress.DetailMarkup(older));
    }

    // The pairing IS the design: "0 task folders" alone alarms, "stream ok" alone proves only that the
    // harness is alive. Together they read correctly as "alive, not yet producing" — normal for the first
    // ten minutes while the agent reads the materialized worktree.
    [Fact]
    public void EarlyAndHealthy_ReadsAsAliveButNotYetProducing()
    {
        var early = new BreakdownProgress.Snapshot(0, null, TimeSpan.FromSeconds(2), true);
        Assert.Equal("0 task folders · stream ok", BreakdownProgress.DetailMarkup(early));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T7 — a declared denominator is rendered; a missing one is NEVER synthesised.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T7_DeclaredTotal_IsRenderedOnlyWhenTheSessionDeclaredIt()
    {
        var declared = new BreakdownProgress.Snapshot(9, 14, TimeSpan.FromSeconds(2), true);
        var undeclared = new BreakdownProgress.Snapshot(9, null, TimeSpan.FromSeconds(2), true);

        Assert.Equal("9/14 declared · stream ok", BreakdownProgress.DetailMarkup(declared));
        Assert.Equal("9 task folders · stream ok", BreakdownProgress.DetailMarkup(undeclared));

        // Without a manifest there is no denominator anywhere in the rendering — not "9/9", not "9/?".
        Assert.DoesNotContain("/", BreakdownProgress.CountFragment(undeclared)!, StringComparison.Ordinal);
    }

    [Fact]
    public void T7_Probe_ReadsTheDeclaredTotalFromTheWavesIntentManifest()
    {
        using var tree = new TempTree();
        string tasks = tree.Dir("wave-02-beta", "tasks");
        File.WriteAllText(Path.Combine(tree.Dir("wave-02-beta", "tasks", "01-a"), "task.json"), "{}");

        string manifest = Path.Combine(tree.Dir("wave-02-beta", "state"), "breakdown-intent.json");
        File.WriteAllText(manifest,
            """
            {
              "version": 1,
              "declaredAt": "2026-08-20T05:00:00Z",
              "tasks": [
                { "folder": "01-a", "purpose": "a" },
                { "folder": "02-b", "purpose": "b" },
                { "folder": "03-c", "purpose": "c" }
              ]
            }
            """);

        BreakdownProgress.Snapshot s = BreakdownProgress.Probe(
            tasks, Path.Combine(tree.Root, "nope.jsonl"), manifest, DateTimeOffset.UtcNow);

        Assert.Equal(1, s.TaskFolders);
        Assert.Equal(3, s.DeclaredTotal);
        Assert.Equal("1/3 declared", BreakdownProgress.DetailMarkup(s));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T8 — the anti-drift test: both surfaces read ONE snapshot through ONE set of fragments.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T8_PlainLineAndDetailMarkup_ReportTheSameFragments_FromOneSnapshot()
    {
        var s = new BreakdownProgress.Snapshot(5, null, TimeSpan.FromSeconds(258), true);
        IReadOnlyList<string> fragments = BreakdownProgress.Fragments(s);

        string detail = BreakdownProgress.DetailMarkup(s);
        string plain = BreakdownProgress.PlainLine(
            "wave-02-beta", TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), s);

        Assert.Equal(["5 task folders", "stream idle 4m18s"], fragments);
        foreach (string fragment in fragments)
        {
            Assert.Contains(fragment, detail, StringComparison.Ordinal);
            Assert.Contains(fragment, plain, StringComparison.Ordinal);
        }

        Assert.Equal(
            "[breakdown] wave-02-beta: 9m30s / 30m00s — 5 task folders, stream idle 4m18s", plain);
    }

    // The settlement words and their detail are shared too, so the live row's terminal cell and the --no-ui
    // finish line can never describe the same outcome differently.
    [Theory]
    [InlineData(null, "authored", "11 task folders")]
    [InlineData("timeout", "cut off", "timeout after 11 task folders")]
    [InlineData("max-turns", "cut off", "max-turns after 11 task folders")]
    [InlineData("incomplete", "incomplete", "11 task folders — prefix kept")]
    [InlineData("invalid", "invalid", "the authored wave failed 'guardrails validate' — see the halt below")]
    [InlineData("error", "faulted", "runner fault — see the halt below")]
    public void SettlementWordAndDetail_AreSharedByBothSurfaces(string? failureKind, string word, string detail)
    {
        var s = new BreakdownProgress.Snapshot(11, null, TimeSpan.FromSeconds(2), true);

        Assert.Equal(word, BreakdownProgress.TerminalWord(failureKind));
        Assert.Equal(detail, BreakdownProgress.TerminalDetail(failureKind, s));

        string plain = BreakdownProgress.PlainFinishLine(
            "wave-02-beta", failureKind, TimeSpan.FromSeconds(1122), s);
        Assert.Equal($"[breakdown] wave-02-beta: {word.ToUpperInvariant()} after 18m42s — {detail}", plain);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The --no-ui surface: the tailed log IS the record, so the phase cannot be silent there either.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoUi_AnnouncesThePhaseAtStart_AndItsSettlementAtFinish()
    {
        using var tree = new TempTree();
        string logDir = tree.Dir("breakdown");
        string tasks = tree.Dir("tasks");
        var writer = new StringWriter();
        var observer = new ConsoleRunObserver(writer);
        WaveBreakdownContext context = Context("wave-02-beta", logDir, tasks);

        observer.WaveBreakdownStarting(context);
        observer.WaveBreakdownFinished(context, TimeSpan.FromSeconds(1122), 0, "timeout", authoredWave: null);

        string output = writer.ToString();
        Assert.Contains("===== Wave 2/2: wave-02-beta — JIT breakdown (no tasks authored yet) =====", output);
        Assert.Contains("[breakdown] wave-02-beta: authoring tasks; ceiling 30m00s", output);
        Assert.Contains($"[breakdown]   log dir: {logDir}", output);
        Assert.Contains("[breakdown] wave-02-beta: CUT OFF after 18m42s — timeout after 0 task folders", output);

        Assert.Equal(30, BreakdownProgress.HeartbeatIntervalSeconds); // NOT GuardrailHeartbeat's 15s
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The live observer drives the phase row through the SHIPPED UpdateCell path (#145/#372).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveObserver_DrivesThePhaseRowThroughTheTable_AndToleratesAPhaseWithNoRow()
    {
        using var tree = new TempTree();
        TaskNode a1 = WaveTask("wave-01-alpha", "01-a");
        IReadOnlyList<WaveNode> waves = [Wave("wave-01-alpha", 1, a1), Wave("wave-02-beta", 2)];

        await using var observer = new LiveRunObserver(
            [a1], planDirectory: tree.Root, runId: "test-run", waves: waves, showAllTasks: false);

        WaveBreakdownContext context =
            Context("wave-02-beta", tree.Dir("breakdown"), tree.Dir("wave-02-beta", "tasks"));

        Exception? ex = Record.Exception(() =>
        {
            observer.WaveBreakdownStarting(context);
            observer.WaveBreakdownFinished(context, TimeSpan.FromSeconds(11), 0, "timeout", authoredWave: null);

            // A phase for a wave the table has no row for (a flat plan, or a stale replay) must be a silent
            // no-op, not a KeyNotFoundException — the same tolerance the #379 collapse needed.
            WaveBreakdownContext orphan =
                Context("wave-99-nope", tree.Dir("breakdown"), tree.Dir("wave-99-nope", "tasks"));
            observer.WaveBreakdownStarting(orphan);
            observer.WaveBreakdownFinished(orphan, TimeSpan.FromSeconds(1), 0, null, authoredWave: null);
        });

        Assert.Null(ex);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T11 — the log site: a page with no breakdown is byte-identical to the pre-#469 render.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T11_WavePageWithNoBreakdown_IsByteIdentical_NoPhaseCssAndNoPhaseSection()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        TaskNode t = WaveTask("wave-01-alpha", "01-a");
        WaveNode wave = Wave("wave-01-alpha", 1, t);

        string withoutPanel = File.ReadAllText(LogSiteRenderer.WriteWaveIndex(
            logsRoot, "test-run", wave,
            statusResolver: _ => "succeeded",
            linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
            includeRefresh: false));

        // Explicitly passing null must produce the SAME bytes as omitting the parameter entirely, so the
        // new optional argument cannot change the dominant page even by a whitespace.
        string explicitNull = File.ReadAllText(LogSiteRenderer.WriteWaveIndex(
            logsRoot, "test-run", wave,
            statusResolver: _ => "succeeded",
            linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
            includeRefresh: false,
            halt: null,
            claimResolver: null,
            phase: null));

        Assert.Equal(withoutPanel, explicitNull);
        Assert.DoesNotContain("class=\"phase\"", withoutPanel);
        Assert.DoesNotContain("section.phase", withoutPanel); // the panel CSS is not emitted either

        // And the whole page, pinned literally — the assertion a rendering change cannot slip past.
        // Line endings are normalised on BOTH sides because they are a property of the CHECKOUT, not the
        // renderer (the template and this golden are both C# raw string literals); every other character is
        // compared exactly. `{style}` stands in for the shared CSS constant, which is not what this pins.
        string expected =
            """
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>wave-01-alpha — Guardrails wave log (test-run)</title>
            <style>
            {style}
            </style>
            </head>
            <body>
            <h1>wave-01-alpha — wave log</h1>
            <div class="bar"><a href="../index.html">&larr; all waves</a> &middot; 1/1 complete</div>
            <p>Static export of this wave. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.</p>
            <table>
            <thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>
            <tbody>
            <tr><td>01-a</td><td class="status" data-status="succeeded">succeeded</td><td>fixture — 01-a</td></tr>
            </tbody>
            </table>
            </body>
            </html>
            """.Replace("{style}", LogSiteRenderer.SharedStyle);

        Assert.Equal(expected.ReplaceLineEndings("\n"), withoutPanel.ReplaceLineEndings("\n"));
    }

    // A FLAT run has no wave pages at all, and its plan index must be untouched: BreakdownPanel is only ever
    // consulted per wave, so there is nothing to leak.
    [Fact]
    public void T11_FlatRunIndex_CarriesNoPhaseMarkup()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");

        string index = File.ReadAllText(LogSiteRenderer.WriteIndex(
            logsRoot, "test-run", [FlatTask("01-first")],
            statusResolver: _ => "succeeded",
            linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
            includeRefresh: false));

        Assert.DoesNotContain("class=\"phase\"", index);
        Assert.DoesNotContain("section.phase", index);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The log site's DURABLE post-mortem — the gap this design names as worse than the live silence.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FailedBreakdown_LeavesADurablePostMortemOnTheWavePage_NotAnEmptyTable()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        WaveNode stub = Wave("wave-02-beta", 2); // reverted: the wave is an empty stub again

        // The evidence the panel must link, on disk exactly as the invoker tees it.
        string breakdown = tree.Dir("logs", "test-run", "wave-02-beta", "breakdown");
        File.WriteAllText(Path.Combine(breakdown, "composed-prompt.md"), new string('x', 4096));
        File.WriteAllText(Path.Combine(breakdown, "claude-stream.jsonl"), "{}\n");
        tree.Dir("logs", "test-run", "wave-02-beta", "breakdown", "rejected");

        var journal = new JournalDocument
        {
            RunId = "test-run",
            PlanHash = "sha256:deadbeef",
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal),
            Decisions =
            [
                DriftDecisions.WaveBreakdownFailed(
                    AutonomyPolicy.Auto, "wave-02-beta", "auto-applied",
                    "The breakdown session was CUT OFF by the breakdown timeout.\n"
                    + "Next: this checkpoint re-fires on the next 'guardrails run', and the breakdown starts "
                    + "FROM SCRATCH.")
            ]
        };

        LogSiteRenderer.ExportSite(logsRoot, [], [stub], journal);

        string page = File.ReadAllText(Path.Combine(logsRoot, "wave-02-beta", "index.html"));

        // Before #469 this page carried the wave name, 0/0 tasks and an empty table — permanently.
        Assert.Contains("<section class=\"phase\" data-phase=\"breakdown\" data-state=\"cut-off\">", page);
        Assert.Contains("section.phase {", page); // the panel CSS, emitted only alongside a panel
        Assert.Contains("breakdown FAILED validation", page);
        Assert.Contains("starts FROM SCRATCH", page);

        // The panel sits ABOVE the table: a reader must not have to scroll past an empty table to learn why
        // it is empty.
        Assert.InRange(
            page.IndexOf("<section class=\"phase\"", StringComparison.Ordinal),
            0,
            page.IndexOf("<table>", StringComparison.Ordinal));

        // Every evidence link is a file that is really there, with its size — including the composed-prompt
        // figure that is deliberately kept OFF every live surface.
        Assert.Contains("href=\"breakdown/composed-prompt.md\"", page);
        Assert.Contains("(4 KB)", page);
        Assert.Contains("href=\"breakdown/claude-stream.jsonl\"", page);
        Assert.Contains("href=\"breakdown/rejected/\"", page);
    }

    [Fact]
    public void UnauthoredWave_WithNoBreakdownYet_SaysSoInsteadOfShowingAnUnexplainedEmptyTable()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        WaveNode stub = Wave("wave-02-beta", 2);

        LogSiteRenderer.PhasePanel? panel = LogSiteRenderer.BreakdownPanel(logsRoot, stub, decisions: null);

        Assert.NotNull(panel);
        Assert.Equal("pending", panel.State);

        // And an AUTHORED wave with no breakdown decision gets no panel at all — the byte-identity rule.
        Assert.Null(LogSiteRenderer.BreakdownPanel(
            logsRoot, Wave("wave-01-alpha", 1, WaveTask("wave-01-alpha", "01-a")), decisions: null));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // T12 — the swallowed-decorator regression. Both decorators are in BOTH chains.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class CountingObserver : IRunObserver
    {
        public int Started { get; private set; }

        public int Finished { get; private set; }

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void WaveBreakdownStarting(WaveBreakdownContext context) => Started++;

        public void WaveBreakdownFinished(
            WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
            WaveNode? authoredWave) => Finished++;
    }

    [Fact]
    public void T12_OnTheFlyLogSiteObserver_ForwardsBothPhaseMembers()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        var inner = new CountingObserver();
        WaveNode stub = Wave("wave-02-beta", 2);
        var decorator = new OnTheFlyLogSiteObserver(inner, logsRoot, "test-run", [], null, [stub]);
        WaveBreakdownContext context = Context("wave-02-beta", tree.Dir("breakdown"), tree.Dir("tasks"));

        decorator.WaveBreakdownStarting(context);
        decorator.WaveBreakdownFinished(context, TimeSpan.FromSeconds(5), 0, "timeout", authoredWave: null);

        // The interface defaults are empty bodies, so a decorator that merely OMITS these swallows the
        // phase with no trace — in every mode, because this decorator wraps both the live and plain paths.
        Assert.Equal(1, inner.Started);
        Assert.Equal(1, inner.Finished);
    }

    [Fact]
    public void T12_OnTheFlyDiagramObserver_ForwardsBothPhaseMembers()
    {
        using var tree = new TempTree();
        var inner = new CountingObserver();
        var plan = new PlanDefinition
        {
            PlanDirectory = "/fake/plan",
            Workspace = "/fake",
            Config = new RunConfig { Version = 1 },
            Tasks = [FlatTask("01-first")]
        };
        var decorator = new OnTheFlyDiagramObserver(inner, tree.Dir("logs"), plan, journalForSeed: null);
        WaveBreakdownContext context = Context("wave-02-beta", tree.Dir("breakdown"), tree.Dir("tasks"));

        decorator.WaveBreakdownStarting(context);
        decorator.WaveBreakdownFinished(context, TimeSpan.FromSeconds(5), 3, null, authoredWave: null);

        Assert.Equal(1, inner.Started);
        Assert.Equal(1, inner.Finished);
    }
}
