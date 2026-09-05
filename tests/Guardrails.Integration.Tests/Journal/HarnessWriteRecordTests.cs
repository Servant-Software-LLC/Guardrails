using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.Journal;

/// <summary>
/// Issue #532 gap 1 — a <c>needsHarnessWrite</c> left NO trace in <c>run.json</c>, applied or not.
///
/// <para><b>The gap.</b> The dispositions already existed as first-class values —
/// <c>HarnessWriteOutcome.Rejected(…)</c> / <c>.Denied(…)</c> / <c>.NotApplied(…)</c>, each carrying a
/// reason, plus the applied paths. They were computed in <c>HarnessWrite.ValidateAndApply</c>, spent on the
/// retry feedback, and then DROPPED. So when a task requested harness writes on three consecutive attempts
/// and all three were silently ignored (#531), nothing in the journal recorded that a write had ever been
/// requested, let alone what became of it: diagnosing it required reading a raw
/// <c>action-out-fragment.json</c> out of the log dir and then reading harness SOURCE to learn where the key
/// is looked up. That is archaeology, and it is exactly what a self-healing agent (#529) cannot do
/// cheaply.</para>
///
/// <para><b>Not gap 2.</b> Provenance on failed attempts shipped separately in <c>3129919</c>; nothing here
/// re-does it.</para>
///
/// <para><b>The end-to-end assertions parse the RAW JSON</b>, per the <c>DeliveryRecordTests</c> precedent:
/// through the typed model a <c>[JsonIgnore]</c> regression would pass while the field never reached disk,
/// and disk is the entire point.</para>
/// </summary>
public sealed class HarnessWriteRecordTests
{
    /// <summary>
    /// A runner that writes <paramref name="fragmentJson"/> to the harness's own state-out path — the same
    /// file a real agent writes, which is where <c>HarnessWrite.RequestFrom</c> reads the control key from.
    /// </summary>
    private sealed class FragmentWritingRunner(string fragmentJson) : IPromptRunner
    {
        public string Name => "claude";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            string stateOut = invocation.Environment["GUARDRAILS_STATE_OUT"];
            Directory.CreateDirectory(Path.GetDirectoryName(stateOut)!);
            File.WriteAllText(stateOut, fragmentJson);

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "done",
                FailureKind = PromptFailureKind.None,
                Summary = "claude completed"
            });
        }
    }

    private sealed record RunOutcome(string Root, string RunJson);

    /// <summary>
    /// Run a one-task prompt plan whose agent emits <paramref name="fragmentJson"/>, through the REAL
    /// <see cref="TaskExecutor"/> + <see cref="Scheduler"/>. <c>defaultRetries: 0</c>, so the task settles
    /// on exactly ONE recorded attempt whichever way the write goes.
    /// </summary>
    private static async Task<RunOutcome> RunOneTaskAsync(string fragmentJson, string writeScopeJson)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-hwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "01-task", "guardrails"));

        File.WriteAllText(Path.Combine(root, "guardrails.json"),
            """
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 60,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", "model": "claude-sonnet-5" }
              }
            }
            """);

        string taskDir = Path.Combine(root, "tasks", "01-task");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "a task that asks the harness to write for it",
              "dependsOn": [],
              "writeScope": {{writeScopeJson}},
              "action": { "path": "action.prompt.md" }
            }
            """);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        bool win = OperatingSystem.IsWindows();
        string guardrailPath = Path.Combine(taskDir, "guardrails", win ? "01-check.cmd" : "01-check.sh");
        File.WriteAllText(guardrailPath, win ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!win)
        {
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        PlanLoadResult load = new PlanLoader().Load(root);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        PromptRunnerRegistry registry =
            PromptRunnerRegistry.Build(load.Plan!.Config, _ => new FragmentWritingRunner(fragmentJson));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var scheduler = new Scheduler(load.Plan!, executor, journal, observer: IRunObserver.Null);
        await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        return new RunOutcome(root, File.ReadAllText(RunJournal.PathFor(root)));
    }

    private static JsonElement OnlyAttempt(string runJson, out JsonDocument owner)
    {
        owner = JsonDocument.Parse(runJson);
        JsonElement attempts = owner.RootElement.GetProperty("tasks").GetProperty("01-task").GetProperty("attempts");
        Assert.Equal(1, attempts.GetArrayLength());
        return attempts[0];
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// THE case this gap was filed from: a write the harness REFUSED. The record must say a write was
    /// requested, that none of it landed, what kind of refusal it was, why, and which file was at stake —
    /// all without opening a log directory.
    /// </summary>
    [Fact]
    public async Task ARejectedHarnessWrite_IsRecordedOnTheAttempt_WithItsDispositionReasonAndPath()
    {
        RunOutcome run = await RunOneTaskAsync(
            """{ "needsHarnessWrite": { "path": "docs/NOTES.md", "content": "hi", "reason": "cannot write it myself" } }""",
            writeScopeJson: """["out/**"]""");
        try
        {
            JsonElement attempt = OnlyAttempt(run.RunJson, out JsonDocument doc);
            using (doc)
            {
                Assert.True(
                    attempt.TryGetProperty("harnessWrite", out JsonElement write),
                    "run.json recorded NOTHING about a needsHarnessWrite that was rejected — the #532 gap 1 "
                    + "defect: the disposition was computed, spent on feedback, and dropped.");

                Assert.Equal(1, write.GetProperty("requested").GetInt32());
                Assert.Equal(0, write.GetProperty("applied").GetInt32());
                Assert.Equal("rejected", write.GetProperty("disposition").GetString());

                string reason = write.GetProperty("reason").GetString()!;
                Assert.Contains("writeScope", reason, StringComparison.Ordinal);
                Assert.Contains("docs/NOTES.md", reason, StringComparison.Ordinal);

                JsonElement entry = Assert.Single(write.GetProperty("entries").EnumerateArray().ToList());
                Assert.Equal("docs/NOTES.md", entry.GetProperty("path").GetString());
                Assert.Equal("rejected", entry.GetProperty("disposition").GetString());
            }
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// The APPLIED case, and it is not a formality: an applied write settles on the SUCCESS path, which in
    /// worktree mode builds its own <c>AttemptRecord</c> from <see cref="PendingAttempt"/> and never
    /// consults the journaller. Recording the disposition on failure paths alone would leave the journal
    /// able to say a harness write was refused and never that one succeeded — the survivorship shape
    /// #532 gap 2 was about, one field over.
    /// </summary>
    [Fact]
    public async Task AnAppliedHarnessWrite_IsRecordedToo_NotOnlyTheRefusedOnes()
    {
        RunOutcome run = await RunOneTaskAsync(
            """{ "needsHarnessWrite": { "path": "out/NOTES.md", "content": "hi", "reason": "cannot write it myself" } }""",
            writeScopeJson: """["out/**"]""");
        try
        {
            JsonElement attempt = OnlyAttempt(run.RunJson, out JsonDocument doc);
            using (doc)
            {
                Assert.Equal("succeeded", attempt.GetProperty("outcome").GetString());

                Assert.True(
                    attempt.TryGetProperty("harnessWrite", out JsonElement write),
                    "run.json recorded NOTHING about a needsHarnessWrite that was APPLIED — the journal "
                    + "could say a harness write was refused and never that one succeeded (#532 gap 1).");

                Assert.Equal(1, write.GetProperty("requested").GetInt32());
                Assert.Equal(1, write.GetProperty("applied").GetInt32());
                Assert.Equal("applied", write.GetProperty("disposition").GetString());

                // No reason for a write that happened — the field exists to explain a refusal.
                Assert.False(write.TryGetProperty("reason", out _));

                JsonElement entry = Assert.Single(write.GetProperty("entries").EnumerateArray().ToList());
                Assert.Equal("out/NOTES.md", entry.GetProperty("path").GetString());
                Assert.Equal("applied", entry.GetProperty("disposition").GetString());
            }

            // And the harness really did write it — the record is not describing an imaginary event.
            Assert.True(File.Exists(Path.Combine(run.Root, "out", "NOTES.md")));
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// The #321 permission-file carve-out keeps its OWN token. Collapsing <c>denied</c> into
    /// <c>rejected</c> would throw away the one distinction that tells a reader the remedy is "a human must
    /// author this file", not "widen your writeScope" — and this is the request most likely to be retried
    /// forever by an agent that was never told which of the two it hit.
    /// </summary>
    [Fact]
    public async Task ADeniedPermissionFileWrite_KeepsItsOwnDispositionToken()
    {
        RunOutcome run = await RunOneTaskAsync(
            """{ "needsHarnessWrite": { "path": ".claude/settings.json", "content": "{}", "reason": "grant me tools" } }""",
            writeScopeJson: """[".claude/**"]""");
        try
        {
            JsonElement attempt = OnlyAttempt(run.RunJson, out JsonDocument doc);
            using (doc)
            {
                Assert.True(
                    attempt.TryGetProperty("harnessWrite", out JsonElement write),
                    "run.json recorded NOTHING about a needsHarnessWrite the #321 carve-out DENIED "
                    + "(#532 gap 1) — the one refusal whose remedy is 'a human must author this file'.");

                Assert.Equal("denied", write.GetProperty("disposition").GetString());
                Assert.Equal(0, write.GetProperty("applied").GetInt32());
                Assert.Contains(
                    "permission-granting", write.GetProperty("reason").GetString()!, StringComparison.Ordinal);
            }

            // The carve-out held: declaring `.claude/**` in scope did not buy the write.
            Assert.False(File.Exists(Path.Combine(run.Root, ".claude", "settings.json")));
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// The load-bearing negative. Nearly every attempt in every run requests no harness write, so a key
    /// here would be a new line on every attempt record ever written — absent, never <c>null</c> noise.
    /// </summary>
    [Fact]
    public async Task AnAttemptThatRequestedNoHarnessWrite_CarriesNoHarnessWriteKeyAtAll()
    {
        RunOutcome run = await RunOneTaskAsync("""{ "01-task": { "ok": true } }""", writeScopeJson: """["out/**"]""");
        try
        {
            JsonElement attempt = OnlyAttempt(run.RunJson, out JsonDocument doc);
            using (doc)
            {
                Assert.Equal("succeeded", attempt.GetProperty("outcome").GetString());
                Assert.False(
                    attempt.TryGetProperty("harnessWrite", out _),
                    "an attempt that requested no harness write grew a harnessWrite key.");
            }
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// A multi-file batch (#445) names every file it would have touched, not just the offending one: the
    /// batch is ATOMIC, so the other entries were genuinely not written either, and a reader chasing "which
    /// of my files landed?" must not have to infer the answer from a message about one of them.
    /// <para>The reason stays BATCH grain — one string, not N copies of it. It already names the offending
    /// array index, and duplicating it per entry would be a second copy of a single fact.</para>
    /// </summary>
    [Fact]
    public async Task AMultiEntryBatch_NamesEveryRequestedPath_WithOneBatchLevelReason()
    {
        RunOutcome run = await RunOneTaskAsync(
            """
            { "needsHarnessWrite": [
                { "path": "out/a.md",  "content": "a", "reason": "x" },
                { "path": "docs/b.md", "content": "b", "reason": "y" } ] }
            """,
            writeScopeJson: """["out/**"]""");
        try
        {
            JsonElement attempt = OnlyAttempt(run.RunJson, out JsonDocument doc);
            using (doc)
            {
                Assert.True(
                    attempt.TryGetProperty("harnessWrite", out JsonElement write),
                    "run.json recorded NOTHING about a multi-file needsHarnessWrite batch (#532 gap 1) — "
                    + "so 'which of my files landed?' had no answer outside the log dir.");

                Assert.Equal(2, write.GetProperty("requested").GetInt32());
                Assert.Equal(0, write.GetProperty("applied").GetInt32());

                List<JsonElement> entries = [.. write.GetProperty("entries").EnumerateArray()];
                Assert.Equal(
                    new[] { "out/a.md", "docs/b.md" },
                    entries.Select(e => e.GetProperty("path").GetString()).ToArray());
                Assert.All(entries, e => Assert.Equal("rejected", e.GetProperty("disposition").GetString()));

                // The batch was abandoned, so the in-scope sibling is byte-absent too.
                Assert.Contains(
                    "NOTHING was written", write.GetProperty("reason").GetString()!, StringComparison.Ordinal);
            }

            Assert.False(File.Exists(Path.Combine(run.Root, "out", "a.md")));
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// The mapping from <see cref="HarnessWriteOutcome"/>'s flags to a disposition token, asserted at its
    /// one producer. The flag order is load-bearing: <c>IsNotApplied</c> and <c>IsPolicyDenied</c> both
    /// IMPLY <c>WasRejected</c>, so a switch that tested the generic arm first would report every #437
    /// anchor failure and every #321 denial as a scope rejection — and send the agent to fix its
    /// <c>writeScope</c> for a problem that has nothing to do with scope.
    /// </summary>
    [Theory]
    [InlineData("ok", HarnessWriteDisposition.Applied)]
    [InlineData("rejected", HarnessWriteDisposition.Rejected)]
    [InlineData("denied", HarnessWriteDisposition.Denied)]
    [InlineData("not-applied", HarnessWriteDisposition.NotApplied)]
    [InlineData("failed", HarnessWriteDisposition.Failed)]
    public void EachOutcomeMapsToItsOwnDisposition(string outcomeKind, HarnessWriteDisposition expected)
    {
        HarnessWriteBatch batch = HarnessWriteBatch.Of(
            new HarnessWriteRequest { Path = "out/a.md", Content = "a" });

        HarnessWriteOutcome outcome = outcomeKind switch
        {
            "ok" => HarnessWriteOutcome.Ok(["out/a.md"]),
            "rejected" => HarnessWriteOutcome.Rejected("outside writeScope"),
            "denied" => HarnessWriteOutcome.Denied("permission-granting file"),
            "not-applied" => HarnessWriteOutcome.NotApplied("anchor not found"),
            _ => HarnessWriteOutcome.Failed("disk full")
        };

        HarnessWriteRecord record = HarnessWrite.Describe(batch, outcome);

        Assert.Equal(expected, record.Disposition);
        Assert.Equal(expected, Assert.Single(record.Entries).Disposition);
        Assert.Equal(1, record.Requested);
        Assert.Equal(expected == HarnessWriteDisposition.Applied ? 1 : 0, record.Applied);
    }

    /// <summary>
    /// An UNUSABLE payload (#437/#445 — no <c>path</c>, both or neither of <c>content</c>/<c>edits</c>, a
    /// malformed edit) still names what it asked for. This is the shape a confused agent produces, so it is
    /// exactly the one whose disposition a reader most needs; a record that went blank here would leave the
    /// hardest case as archaeological as before.
    /// </summary>
    [Fact]
    public void AnUnusablePayload_StillRecordsTheRequestItNamed()
    {
        HarnessWriteBatch batch = HarnessWriteBatch.Invalid(
            "needsHarnessWrite[0] carries neither `content` nor `edits`", ["out/a.md"]);

        HarnessWriteRecord record = HarnessWrite.Describe(
            batch, HarnessWriteOutcome.NotApplied(batch.InvalidReason!));

        Assert.Equal(1, record.Requested);
        Assert.Equal(0, record.Applied);
        Assert.Equal(HarnessWriteDisposition.NotApplied, record.Disposition);
        Assert.Equal("out/a.md", Assert.Single(record.Entries).Path);
    }

    /// <summary>
    /// The kebab wire tokens survive the round-trip that makes the record durable, in BOTH directions —
    /// <c>guardrails status</c>, the static log-site export and any post-mortem tool read the journal back
    /// through the typed model, so a write-only field would be half a feature. <c>not-applied</c> is the
    /// value asserted because it is the one whose C# name is not its wire spelling.
    /// </summary>
    [Fact]
    public void TheRecordRoundTrips_WithItsKebabTokens()
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-05T00-00-00Z-abcd",
            PlanHash = "sha256:abc",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                ["01-task"] = new TaskJournalEntry
                {
                    Status = Core.Journal.TaskStatus.NeedsHuman,
                    Attempts =
                    [
                        new AttemptRecord
                        {
                            Attempt = 1,
                            StartedAt = DateTimeOffset.UnixEpoch,
                            EndedAt = DateTimeOffset.UnixEpoch,
                            Outcome = AttemptOutcome.GuardrailFailed,
                            LogDir = "logs/r/01-task/attempt-1",
                            HarnessWrite = new HarnessWriteRecord
                            {
                                Requested = 1,
                                Applied = 0,
                                Disposition = HarnessWriteDisposition.NotApplied,
                                Reason = "edits[0].old was NOT FOUND in the file",
                                Entries =
                                [
                                    new HarnessWriteEntry
                                    {
                                        Path = ".claude/skills/foo/SKILL.md",
                                        Disposition = HarnessWriteDisposition.NotApplied
                                    }
                                ]
                            }
                        }
                    ]
                }
            }
        };

        string json = JsonSerializer.Serialize(document, JournalJson.Options);
        Assert.Contains("\"disposition\": \"not-applied\"", json, StringComparison.Ordinal);

        JournalDocument back = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        HarnessWriteRecord record = back.Tasks["01-task"].Attempts[0].HarnessWrite!;

        Assert.Equal(HarnessWriteDisposition.NotApplied, record.Disposition);
        Assert.Equal(1, record.Requested);
        Assert.Equal(0, record.Applied);
        Assert.Equal(".claude/skills/foo/SKILL.md", Assert.Single(record.Entries).Path);
    }
}
