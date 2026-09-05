using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// The end-to-end proof that <c>--on-event</c> actually delivers (issue #585 layer 3, design doc 36) —
/// design §10 calls this row "the row that matters most (#382)". Every DELIVERY test here starts a real
/// loopback <see cref="HttpListener"/>, drives the REAL CLI (<see cref="CommandFactory.BuildRootCommand"/>
/// + <c>root.Parse(args).InvokeAsync()</c> — never a hand-built <c>WebhookEventSink</c> injected directly,
/// which would prove nothing about whether <c>RunCommand</c> itself wires the feature) over a real
/// <see cref="ScriptPlanBuilder"/> plan, and asserts on what the listener actually captured. The three
/// STARTUP-VALIDATION tests (11-13) bind no listener at all: the CLI must reject a bad configuration
/// before the DAG starts, so a listener would only hide a delivery that must never happen.
///
/// <para><b>Written RED.</b> Task 08 (this file) authors these thirteen tests against a CLI that declares
/// <c>--on-event</c>/<c>--on-event-detail</c> but never wires them (the two stub edits in
/// <c>RunCommand.cs</c>): twelve of the thirteen behaviours below therefore FAIL on this tree by
/// construction. <see cref="AReceiverThatNeverBindsLeavesExitCodeUntouched"/> is a declared exemption —
/// its whole content is that delivery does not affect the run, which is trivially true when nothing
/// delivers at all, so it is green here AND green after task 09 wires the feature.</para>
///
/// <para>Two of the ten delivery behaviours (<see cref="RunFinishedArrives"/> and
/// <see cref="RunFinishedArrivesWhenTheReceiverIsSlow"/>) are specifically the assertions plan 35
/// MEASURED as missing: <c>LogServer</c>'s "best-effort" final delivery of <c>run-finished</c> failed
/// EVERY SINGLE TIME across ~10 measured variants because the drain ran after the transport was already
/// torn down. Per plan 35's own finding, these are the tests that would have caught that and did not
/// exist.</para>
/// </summary>
[Collection(WebhookDeliveryCollection.Name)]
public sealed class WebhookDeliveryTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CLI + journal helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(int Exit, string Out)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync().ConfigureAwait(false);
        return (exit, io.OutText);
    }

    private static string RunIdOf(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    private static string EventsPathFor(string planDir) =>
        Path.Combine(planDir, "logs", RunIdOf(planDir), "events.jsonl");

    /// <summary>Parses a captured request's body into a standalone (document-detached) <see cref="JsonElement"/>.</summary>
    private static JsonElement ParseBody(CapturedRequest request)
    {
        using JsonDocument doc = JsonDocument.Parse(request.Body);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Same probe→bind technique as <c>LogServer.FreeLoopbackPort</c> (<c>src/Guardrails.Cli/Ui/LogServer.cs</c>):
    /// a <see cref="TcpListener"/> bound to port 0 hands back a free ephemeral port. Used directly (no
    /// <see cref="LoopbackReceiver"/>) for the "nothing is listening here" fixtures (10, 12, 13).
    /// </summary>
    private static int ReserveFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. Rows reach a real loopback receiver at all.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task RowsArriveAtALoopbackReceiver()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);
        Assert.Equal(ExitCodes.Success, exit);

        List<JsonElement> bodies = receiver.Requests.Select(ParseBody).ToList();
        Assert.NotEmpty(bodies); // at least one POST arrived — a single stray request must not satisfy this alone.

        List<string?> kinds = bodies.Select(b => b.GetProperty("kind").GetString()).ToList();
        Assert.Contains("task-started", kinds);
        Assert.Contains("task-settled", kinds);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. The terminal run-finished row — the plan-35 assertion that did not exist.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task RunFinishedArrives()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);

        List<JsonElement> bodies = receiver.Requests.Select(ParseBody).ToList();
        List<JsonElement> runFinishedRows = bodies.Where(b => b.GetProperty("kind").GetString() == "run-finished").ToList();
        Assert.NotEmpty(runFinishedRows); // the single most valuable delivery in the whole feature.

        JsonElement runFinished = runFinishedRows[0];
        Assert.True(runFinished.TryGetProperty("exitCode", out JsonElement exitCodeProp), "delivered run-finished carried no exitCode.");
        Assert.Equal(exit, exitCodeProp.GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. run-finished still arrives when the receiver is slow enough to back the pump up.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task RunFinishedArrivesWhenTheReceiverIsSlow()
    {
        // A slow-but-alive receiver is the half plan 35's defect would have survived: a drain that runs
        // AFTER the transport is torn down looks fine against an instant receiver and fails against a
        // real one. Two dependent tasks -> more rows than the pump can clear before the run's own trivial
        // work finishes, so the backlog is still genuinely non-empty when DisposeAsync begins.
        await using var receiver = new LoopbackReceiver { ResponseDelay = TimeSpan.FromMilliseconds(300) };
        using var plan = new ScriptPlanBuilder().AddTask("01-first").AddTask("02-second", dependsOn: "01-first");

        (int exit, _) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);

        List<JsonElement> bodies = receiver.Requests.Select(ParseBody).ToList();
        List<JsonElement> runFinishedRows = bodies.Where(b => b.GetProperty("kind").GetString() == "run-finished").ToList();
        Assert.NotEmpty(runFinishedRows);

        JsonElement runFinished = runFinishedRows[0];
        Assert.True(runFinished.TryGetProperty("exitCode", out JsonElement exitCodeProp), "delivered run-finished carried no exitCode.");
        Assert.Equal(exit, exitCodeProp.GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. Delivered bodies match events.jsonl line for line.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task DeliveredBodiesMatchEventsJsonlLineForLine()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-first").AddTask("02-second", dependsOn: "01-first");

        (int exit, _) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);
        Assert.Equal(ExitCodes.Success, exit);

        List<CapturedRequest> requests = receiver.Requests;
        Assert.NotEmpty(requests);

        string eventsPath = EventsPathFor(plan.PlanDir);
        Assert.True(File.Exists(eventsPath), $"events.jsonl never appeared at '{eventsPath}'.");

        // key "<bracket>:<seq>" -> (the raw file line, whether that line carries a `detail` field).
        var fileLinesByKey = new Dictionary<string, (string Line, bool HasDetail)>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(eventsPath))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            string bracket = root.GetProperty("bracket").GetString()!;
            int seq = root.GetProperty("seq").GetInt32();
            fileLinesByKey[$"{bracket}:{seq}"] = (line, root.TryGetProperty("detail", out _));
        }

        bool anyByteIdentical = false;
        foreach (CapturedRequest request in requests)
        {
            JsonElement body = ParseBody(request);
            string bracket = body.GetProperty("bracket").GetString()!;
            int seq = body.GetProperty("seq").GetInt32();
            string key = $"{bracket}:{seq}";

            Assert.True(
                fileLinesByKey.TryGetValue(key, out (string Line, bool HasDetail) fileEntry),
                $"delivered body (bracket={bracket}, seq={seq}) names no line in events.jsonl.");

            if (!fileEntry.HasDetail)
            {
                Assert.Equal(fileEntry.Line, request.Body);
                anyByteIdentical = true;
            }
        }

        // Assert the delivered set is non-empty AND that at least one row took the byte-identical branch
        // BEFORE asserting anything universal over it — a foreach over an empty collection passes, which
        // is exactly the hollow shape the red census is built to catch.
        Assert.True(anyByteIdentical, "no delivered row took the byte-identical (no-detail) branch.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5. Headers are exactly the section 4.3 contract.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task HeadersAreExactlyTheContract()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);
        Assert.Equal(ExitCodes.Success, exit);

        List<CapturedRequest> requests = receiver.Requests;
        Assert.NotEmpty(requests); // at least one captured request, before asserting anything about it.

        CapturedRequest request = requests[0];
        JsonElement body = ParseBody(request);

        Assert.True(request.Headers.TryGetValue("Content-Type", out string? contentType));
        Assert.Equal("application/json; charset=utf-8", contentType);

        // The version string is INJECTED by the CLI, not read from Core (§4.3's trap: reading the
        // executing assembly from Core would silently report Guardrails.Core's own 1.0.0).
        Assert.True(request.Headers.TryGetValue("User-Agent", out string? userAgent));
        Assert.Equal($"guardrails/{GuardrailsVersion.Current}", userAgent);

        // Reassembled from the body's OWN fields — never hard-coded.
        string runId = body.GetProperty("runId").GetString()!;
        string bracket = body.GetProperty("bracket").GetString()!;
        int seq = body.GetProperty("seq").GetInt32();
        string expectedDeliveryId = $"{runId}:{bracket}:{seq}";

        Assert.True(request.Headers.TryGetValue("X-Guardrails-Delivery-Id", out string? deliveryId));
        Assert.Equal(expectedDeliveryId, deliveryId);

        Assert.True(request.Headers.TryGetValue("X-Guardrails-Event-Kind", out string? eventKind));
        Assert.Equal(body.GetProperty("kind").GetString(), eventKind);

        // A receiver returning 200 always succeeds on the first try, so any captured request's attempt is 1.
        Assert.True(request.Headers.TryGetValue("X-Guardrails-Delivery-Attempt", out string? attempt));
        Assert.Equal("1", attempt);

        // The headers §4.3 rejected are absent.
        Assert.False(request.Headers.ContainsKey("X-Guardrails-Signature"));
        Assert.False(request.Headers.ContainsKey("X-Guardrails-Run-Id"));
        Assert.False(request.Headers.ContainsKey("X-Guardrails-Seq"));
        Assert.False(request.Headers.ContainsKey("X-Guardrails-Bracket"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 6/7. detail withheld by default; present with --on-event-detail.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static JsonElement FindFileLine(string eventsPath, Func<JsonElement, bool> predicate)
    {
        foreach (string line in File.ReadAllLines(eventsPath))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            if (predicate(doc.RootElement))
            {
                return doc.RootElement.Clone();
            }
        }

        throw new InvalidOperationException($"no matching line found in '{eventsPath}'.");
    }

    private static bool IsFailingGuardrailFinished(JsonElement row) =>
        row.GetProperty("kind").GetString() == "guardrail-finished"
        && row.TryGetProperty("passed", out JsonElement passed)
        && !passed.GetBoolean();

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task DetailIsWithheldWithoutTheFlag()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-fails", guardrailPasses: false);

        await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);

        List<JsonElement> guardrailFailures = receiver.Requests
            .Select(ParseBody)
            .Where(IsFailingGuardrailFinished)
            .ToList();
        Assert.NotEmpty(guardrailFailures); // (a) such a row arrived.

        Assert.True(guardrailFailures[0].TryGetProperty("detail", out JsonElement deliveredDetail));
        Assert.Equal("(detail withheld; pass --on-event-detail)", deliveredDetail.GetString()); // (b)

        // (c) events.jsonl itself is never affected — the real text is still there.
        JsonElement fileRow = FindFileLine(EventsPathFor(plan.PlanDir), IsFailingGuardrailFinished);
        Assert.Equal("guardrail failed deliberately", fileRow.GetProperty("detail").GetString());
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task DetailIsPresentWithTheFlag()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-fails", guardrailPasses: false);

        await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url, "--on-event-detail");

        List<JsonElement> guardrailFailures = receiver.Requests
            .Select(ParseBody)
            .Where(IsFailingGuardrailFinished)
            .ToList();
        Assert.NotEmpty(guardrailFailures);

        string deliveredDetail = guardrailFailures[0].GetProperty("detail").GetString()!;

        JsonElement fileRow = FindFileLine(EventsPathFor(plan.PlanDir), IsFailingGuardrailFinished);
        string fileDetail = fileRow.GetProperty("detail").GetString()!;

        Assert.Equal(fileDetail, deliveredDetail);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 8. A 500 causes retries, then a recorded drop; the exit code is unchanged.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task AFiveHundredCausesRetriesThenARecordedDropWithExitCodeUnchanged()
    {
        // A distinctive path segment: §6.6 requires the console summary to show <scheme>://<host>[:<port>]/…
        // and never the path — this is what makes that check meaningful rather than vacuous.
        await using var receiver = new LoopbackReceiver(pathSegment: "distinctive-webhook-path/")
        {
            ResponseStatusCode = 500
        };
        using var planWithWebhook = new ScriptPlanBuilder().AddTask("01-first");

        (int exitWithWebhook, string outText) = await InvokeAsync(
            "run", planWithWebhook.PlanDir, "--no-ui", "--no-log-server", "--on-event", receiver.Url);

        // Retries happened: the FIRST delivery id observed arrived more than once with increasing attempts.
        List<(string Id, int Attempt)> deliveryAttempts = [];
        foreach (CapturedRequest r in receiver.Requests)
        {
            if (r.Headers.TryGetValue("X-Guardrails-Delivery-Id", out string? id) &&
                r.Headers.TryGetValue("X-Guardrails-Delivery-Attempt", out string? attemptText) &&
                int.TryParse(attemptText, out int attemptNumber))
            {
                deliveryAttempts.Add((id, attemptNumber));
            }
        }

        Assert.NotEmpty(deliveryAttempts);

        // The property under test is "a 500 causes retries", which is about SOME delivery, not about
        // whichever one the receiver happened to record first. Pinning it to deliveryAttempts[0] made the
        // test depend on emission order and on how much drain time each delivery got before the run
        // exited: a run emits several events, and one emitted late can be dropped at the drain deadline
        // after a single attempt while earlier ones retried normally. That read as "retries never
        // happened" on the loaded solution-wide job and passed everywhere else — an over-specified
        // assertion, not a product fault.
        List<IGrouping<string, (string Id, int Attempt)>> retried =
            [.. deliveryAttempts.GroupBy(t => t.Id).Where(g => g.Count() > 1)];

        Assert.True(
            retried.Count > 0,
            "no delivery id was attempted more than once — retries never happened. Observed: " +
            string.Join(", ", deliveryAttempts.Select(t => $"{t.Id}#{t.Attempt}")));

        // Every retried delivery must also count UP; a repeat that reuses attempt 1 is not a retry.
        foreach (IGrouping<string, (string Id, int Attempt)> group in retried)
        {
            List<int> attempts = [.. group.Select(t => t.Attempt)];
            for (int i = 1; i < attempts.Count; i++)
            {
                Assert.True(
                    attempts[i] > attempts[i - 1],
                    $"attempt numbers did not increase across retries for '{group.Key}': {string.Join(",", attempts)}");
            }
        }

        // The drop was recorded: a "Webhook: N delivered, M dropped -> url" line with M > 0, and the
        // receiver's distinctive path segment never appears anywhere in the console output (§6.6).
        string[] lines = outText.Split('\n');
        string? summaryLine = lines.FirstOrDefault(l => l.Contains(" dropped ->", StringComparison.Ordinal));
        Assert.True(summaryLine is not null, $"no 'Webhook: N delivered, M dropped -> url' summary line was printed. Full output:\n{outText}");

        int droppedStart = summaryLine!.IndexOf(", ", StringComparison.Ordinal) + 2;
        int droppedEnd = summaryLine.IndexOf(" dropped", StringComparison.Ordinal);
        string droppedCountText = summaryLine[droppedStart..droppedEnd];
        Assert.True(
            int.TryParse(droppedCountText, out int droppedCount) && droppedCount > 0,
            $"dropped count was not a positive integer: '{droppedCountText}' in line '{summaryLine}'");

        Assert.DoesNotContain("distinctive-webhook-path", outText, StringComparison.Ordinal);

        // The exit code is unchanged versus the identical plan run without --on-event.
        using var planWithoutWebhook = new ScriptPlanBuilder().AddTask("01-first");
        (int exitWithoutWebhook, _) = await InvokeAsync(
            "run", planWithoutWebhook.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(exitWithoutWebhook, exitWithWebhook);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 9. The environment fallback supplies the endpoint AND its auth when no flag is passed.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task EnvVarSuppliesTheEndpointWhenTheFlagIsAbsent()
    {
        await using var receiver = new LoopbackReceiver();
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        const string authValue = "Bearer test-token-36";
        string? previousUrl = Environment.GetEnvironmentVariable("GUARDRAILS_ON_EVENT");
        string? previousAuth = Environment.GetEnvironmentVariable("GUARDRAILS_ON_EVENT_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("GUARDRAILS_ON_EVENT", receiver.Url);
            Environment.SetEnvironmentVariable("GUARDRAILS_ON_EVENT_AUTH", authValue);

            await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GUARDRAILS_ON_EVENT", previousUrl);
            Environment.SetEnvironmentVariable("GUARDRAILS_ON_EVENT_AUTH", previousAuth);
        }

        List<CapturedRequest> requests = receiver.Requests;
        List<JsonElement> bodies = requests.Select(ParseBody).ToList();
        Assert.NotEmpty(bodies); // rows arrived, before asserting which kinds.

        List<string?> kinds = bodies.Select(b => b.GetProperty("kind").GetString()).ToList();
        Assert.Contains("run-finished", kinds);

        CapturedRequest sample = requests[0];
        Assert.True(sample.Headers.TryGetValue("Authorization", out string? authorization));
        Assert.Equal(authValue, authorization);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 10. DECLARED EXEMPTION — a receiver that never binds leaves the exit code untouched.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task AReceiverThatNeverBindsLeavesExitCodeUntouched()
    {
        int port = ReserveFreeLoopbackPort();
        string deadUrl = $"http://127.0.0.1:{port}/";

        using var planWithWebhook = new ScriptPlanBuilder().AddTask("01-first");
        (int exitWithWebhook, _) = await InvokeAsync(
            "run", planWithWebhook.PlanDir, "--no-ui", "--no-log-server", "--on-event", deadUrl);

        using var planWithoutWebhook = new ScriptPlanBuilder().AddTask("01-first");
        (int exitWithoutWebhook, _) = await InvokeAsync(
            "run", planWithoutWebhook.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(exitWithoutWebhook, exitWithWebhook);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 11-13. Startup validation (§6.4/§6.5) — no listener, no delivery, an immediate exit.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task ABadSchemeExitsOneBeforeTheRun()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string outText) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", "ftp://example.invalid/hook");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("--on-event", outText, StringComparison.Ordinal);
        Assert.Contains("ftp", outText, StringComparison.Ordinal); // names the scheme it found.
        Assert.False(
            File.Exists(RunJournal.PathFor(plan.PlanDir)),
            "run.json exists — the bad scheme was not rejected before run state was touched.");
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task ARepeatedOnEventFlagIsRejected()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        int portA = ReserveFreeLoopbackPort();
        int portB = ReserveFreeLoopbackPort();

        (int exit, string outText) = await InvokeAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server",
            "--on-event", $"http://127.0.0.1:{portA}/",
            "--on-event", $"http://127.0.0.1:{portB}/");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("--on-event", outText, StringComparison.Ordinal);
        Assert.Contains("once", outText, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            File.Exists(RunJournal.PathFor(plan.PlanDir)),
            "run.json exists — the repeated flag was not rejected before run state was touched.");
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public async Task ACrLfAuthValueIsRejected()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        int port = ReserveFreeLoopbackPort();
        const string secretToken = "sup3r-s3cret-t0k3n";
        string authValue = $"Bearer {secretToken}\r\nX-Injected: 1";

        string? previousAuth = Environment.GetEnvironmentVariable("GUARDRAILS_ON_EVENT_AUTH");
        int exit;
        string outText;
        try
        {
            Environment.SetEnvironmentVariable("GUARDRAILS_ON_EVENT_AUTH", authValue);
            (exit, outText) = await InvokeAsync(
                "run", plan.PlanDir, "--no-ui", "--no-log-server", "--on-event", $"http://127.0.0.1:{port}/");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GUARDRAILS_ON_EVENT_AUTH", previousAuth);
        }

        Assert.Equal(ExitCodes.HarnessError, exit);

        // POSITIVE first (#176): the message names the variable and the rule — otherwise a console that
        // printed nothing at all would vacuously satisfy the negative half below.
        Assert.Contains("GUARDRAILS_ON_EVENT_AUTH", outText, StringComparison.Ordinal);
        Assert.True(
            outText.Contains("CR", StringComparison.Ordinal)
            || outText.Contains("LF", StringComparison.Ordinal)
            || outText.Contains("carriage return", StringComparison.OrdinalIgnoreCase)
            || outText.Contains("line feed", StringComparison.OrdinalIgnoreCase),
            $"message does not name the CR/LF rule. Full output:\n{outText}");

        // NEGATIVE: neither the whole value nor the distinctive token is ever echoed (§6.4: never logged,
        // echoed, journaled, or written to any file).
        Assert.DoesNotContain(authValue, outText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, outText, StringComparison.Ordinal);

        Assert.False(
            File.Exists(RunJournal.PathFor(plan.PlanDir)),
            "run.json exists — the CR/LF auth value was not rejected before run state was touched.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One HTTP request as the loopback receiver saw it: the full body and every header.</summary>
    private sealed record CapturedRequest(string Body, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// A real <see cref="HttpListener"/> bound to <c>127.0.0.1</c> on a free port, recording every
    /// request's body, headers, and arrival order (appended under a lock as soon as the body is read —
    /// before any configured response delay — so <see cref="Requests"/>' order is arrival order, not
    /// response-completion order). <see cref="ResponseStatusCode"/> and <see cref="ResponseDelay"/> let a
    /// test script the receiver's behaviour (always-500, or a slow-but-eventual 200).
    /// </summary>
    private sealed class LoopbackReceiver : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _acceptLoop;
        private readonly List<CapturedRequest> _requests = [];
        private readonly object _gate = new();

        public int ResponseStatusCode { get; init; } = 200;

        public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

        public string Url { get; }

        public LoopbackReceiver(string pathSegment = "")
        {
            // Same probe-then-bind retry idiom as LogServer.TryStart: the probe->bind gap is a TOCTOU
            // window, so retry a few times against a fresh probe if another process wins the race.
            HttpListenerException? lastBindFailure = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int boundPort = ReserveFreeLoopbackPort();
                string url = $"http://127.0.0.1:{boundPort}/{pathSegment}";
                var listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    ((IDisposable)listener).Dispose();
                    lastBindFailure = ex;
                    continue;
                }

                _listener = listener;
                Url = url;
                _acceptLoop = Task.Run(AcceptLoopAsync);
                return;
            }

            throw lastBindFailure!;
        }

        public List<CapturedRequest> Requests
        {
            get { lock (_gate) { return [.. _requests]; } }
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // listener stopped/disposed.
                }

                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in ctx.Request.Headers.AllKeys)
            {
                if (key is null)
                {
                    continue;
                }

                string? value = ctx.Request.Headers[key];
                if (value is not null)
                {
                    headers[key] = value;
                }
            }

            if (ctx.Request.ContentType is not null)
            {
                headers["Content-Type"] = ctx.Request.ContentType;
            }

            lock (_gate)
            {
                _requests.Add(new CapturedRequest(body, headers));
            }

            if (ResponseDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(ResponseDelay).ConfigureAwait(false); } catch (Exception) { /* best effort */ }
            }

            try
            {
                ctx.Response.StatusCode = ResponseStatusCode;
                ctx.Response.ContentLength64 = 0;
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // A torn-down listener mid-response must never crash the accept loop.
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch (Exception) { /* best-effort teardown */ }
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { /* best-effort teardown */ }
            try { _listener.Close(); } catch (Exception) { /* best-effort teardown */ }
        }
    }
}

/// <summary>
/// Serializes every test in <see cref="WebhookDeliveryTests"/> against every OTHER test in the same
/// class: <see cref="WebhookDeliveryTests.EnvVarSuppliesTheEndpointWhenTheFlagIsAbsent"/> and
/// <see cref="WebhookDeliveryTests.ACrLfAuthValueIsRejected"/> mutate the PROCESS-WIDE
/// <c>GUARDRAILS_ON_EVENT</c> / <c>GUARDRAILS_ON_EVENT_AUTH</c> environment variables while driving a
/// real <c>guardrails run</c>, and a concurrently-running sibling test's own real CLI invocation would
/// read the very same ambient variables once task 09 wires the env fallback in — a leak that reads as
/// flakiness, not a clear failure. xUnit already runs the facts WITHIN one class sequentially by
/// default; this collection makes that guarantee explicit rather than resting on a default that could
/// change under a future xunit configuration. It does NOT protect against a DIFFERENT test class running
/// concurrently and reading the same leaked variable — that cross-class hazard is unchanged and belongs
/// to the suite's wider parallelization settings, outside this task's write scope.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebhookDeliveryCollection
{
    public const string Name = "webhook-delivery (serialized: mutates GUARDRAILS_ON_EVENT / _AUTH)";
}
