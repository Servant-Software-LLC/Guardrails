using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Cli;

/// <summary>
/// The pre-DAG plan-preflight phase (preflights-impl deliverable 3, design 09-preflight-first-class,
/// SSOT §7). Evaluates <c>&lt;plan&gt;/preflights/</c> ONCE, before the Scheduler builds any wave,
/// against the run's STARTING bytes — the integration worktree on the plan branch at the user's HEAD
/// in worktree mode, or the plan workspace directly in serial mode — via the unconditional
/// <see cref="IReVerifier"/> seam (deliverable 1). Read-only: no task action ever runs here.
/// <para>
/// A plan with no <c>preflights/</c> folder (<see cref="PlanDefinition.PlanPreflights"/> empty) is
/// untouched: no evaluation, no <c>planPreflights</c> journal section written (SSOT §7 — the section is
/// additive and OMITTED, never null noise, for a plan that doesn't opt in).
/// </para>
/// <para>
/// <b>Resume SKIP (the B1 fix, SSOT §7).</b> When the journal already carries a
/// <c>planPreflights.status == "passed"</c> marker whose <c>planHash</c> matches the CURRENT plan hash,
/// the phase is skipped — the marker (and its <c>evaluatedAt</c>) is left byte-for-byte untouched. A
/// negative-baseline check (true only at the very start of a plan's lifecycle) must be evaluated exactly
/// ONCE across the whole run, or a resume after a mid-DAG crash would re-run it against
/// partially-merged bytes and false-halt a run that is actually fine. The phase re-evaluates only when
/// the marker is absent, its status is failed, or its planHash is stale — or after <c>--fresh</c>, which
/// deletes <c>run.json</c> (and so the marker) before this phase ever runs.
/// </para>
/// <para>
/// <b>Committed sample pairs (plan of record 26 §3/§7, issue #510).</b> BEFORE either short-circuit above,
/// every <c>tasks/&lt;id&gt;/samples/</c> pair is executed against its guardrail through the SHARED
/// <see cref="SampleVerifier"/> — the same type <c>guardrails samples verify</c> drives, so the verb and
/// this phase can never disagree about whether a pair is sound. Any finding halts the run here, before the
/// Scheduler builds a wave and before any task spends a token.
/// </para>
/// <para>
/// <b>The <c>openai-compat</c> endpoint preflight (plan of record 28 §6.6/§7, issue #223).</b> Beside the
/// sample pairs and on the same terms: every DISTINCT endpoint the registry declares is reached once
/// (<c>GET {endpoint}/models</c>), every declared model is asserted to be listed there, and every distinct
/// (endpoint, model) answers one tool-capability probe. <c>guardrails validate</c> stays static and offline
/// (plan 26 §3), so this is the ONE place a dead endpoint, an unpulled model, or — the failure the probe
/// exists for — a server that accepts a <c>tools</c> array and never calls one is caught before a token is
/// spent. That last shape is §6.6's false GREEN: nothing on the wire distinguishes <i>"I considered the
/// tools and needed none"</i> from <i>"I do not implement tools"</i>, so a verifier that read no evidence
/// returns an immaculate <c>{"pass": true}</c> and the guardrail goes green over work nobody checked.
/// </para>
/// </summary>
public static class PlanPreflightPhase
{
    /// <summary>
    /// Per-sample wall clock, matching <c>guardrails samples verify</c>. A guardrail that hangs on a sample
    /// yields no usable exit code, so the verifier reports it rather than treating it as a silent pass.
    /// </summary>
    private static readonly TimeSpan PerSampleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The "short timeout" plan 28 §7 puts on each endpoint probe. Short on purpose: this phase runs before
    /// the DAG, so an endpoint that is merely SLOW to answer a two-message probe is one the run should be
    /// told about now rather than discovering it a judge at a time.
    /// </summary>
    private static readonly TimeSpan EndpointProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The trivial function the tool-capability probe offers. It does nothing and takes no arguments — the
    /// probe asks one question only (<i>can this (endpoint, model) emit a <c>tool_calls</c> entry at all?</i>),
    /// and anything the tool actually DID would be a second variable in the answer.
    /// </summary>
    private const string ToolProbeName = "probe_tool";

    /// <summary>
    /// Evaluate (or skip) the pre-DAG phase for <paramref name="plan"/>, whose journal
    /// <paramref name="journal"/> was just loaded/seeded by <see cref="RunJournal.LoadOrCreate"/>.
    /// Returns true when scheduling may proceed (passed, skipped, or no preflights declared at all);
    /// false when the run must halt BEFORE any task is scheduled — a failed <c>planPreflights</c>
    /// section (with per-check reasons) has already been journaled by the time this returns.
    /// <para>
    /// When <paramref name="heartbeatOut"/> is supplied, a per-guardrail wall-clock heartbeat (issue
    /// #331) is written to it while each Full Flight Check runs. This phase runs BEFORE the Spectre live
    /// region is constructed, so plain heartbeat lines are #145-safe. Null ⇒ no heartbeat.
    /// </para>
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        RunJournal journal,
        ProcessRunner processRunner,
        TextWriter? heartbeatOut,
        CancellationToken cancellationToken,
        string? junctionRoot = null)
    {
        // Committed sample pairs come FIRST — before BOTH short-circuits below — and this placement is
        // the whole point of the step (plan of record 26 §3/§7, issue #510).
        //
        //  * After `PlanPreflights.Count == 0` it would gate only the plans that already opted into Full
        //    Flight Checks, i.e. the plans least likely to need it, while most plans in the repo declare
        //    no preflights/ folder at all and would keep a reversed pair indistinguishable from a sound one.
        //  * After the B1 resume SKIP it would be skippable through the resume door. That marker exists
        //    because a NEGATIVE-BASELINE check is true only at the very start of a plan's lifecycle, so
        //    re-running it against partially-merged bytes would false-halt a healthy run. Samples are plan
        //    INPUTS, not run outputs — re-verifying them mid-run can never false-halt — so the reasoning
        //    does not transfer, and skipping them would reintroduce "recorded but never executed".
        //
        // Cost, which §7 states as a CONDITION rather than a preference: the verifier DISCOVERS pairs
        // before it runs anything, so a plan carrying none pays one Directory.Exists per task and launches
        // no process. That path returns true below having written nothing and printed nothing — byte-
        // identical to the behaviour before this step existed.
        if (!await SamplePairsPassAsync(plan, journal, processRunner, heartbeatOut, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        // The openai-compat endpoint preflight (plan 28 §6.6/§7), placed beside the sample pairs and for
        // the same reason: BEFORE both short-circuits below. A dead endpoint or a model that cannot call
        // tools is fatal to every judge pinned to it, whether or not the plan happens to declare a
        // preflights/ folder, and it must not be reachable through the resume door either — unlike a
        // negative-baseline Full Flight Check, re-probing an endpoint mid-lifecycle can never false-halt a
        // healthy run, so the B1 marker's reasoning does not transfer.
        //
        // Cost is a CONDITION, not a preference (plan §7): discovery is a registry scan, so a plan with no
        // openai-compat block returns below having opened ZERO connections — and having constructed no
        // HttpClient at all, which is what makes that a property of the code rather than of a guard that
        // happened to be hit first.
        if (!await OpenAiCompatEndpointsPassAsync(plan, journal, heartbeatOut, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        if (plan.PlanPreflights.Count == 0)
        {
            // No <plan>/preflights/ folder at all — the feature is not in use for this plan. Additive
            // per SSOT §7: omit the section entirely, never write a vacuous "passed" marker.
            return true;
        }

        string currentHash = journal.Document.PlanHash;

        if (journal.Document.PlanPreflights is { } marker
            && marker.Status == PlanPhaseStatus.Passed
            && string.Equals(marker.PlanHash, currentHash, StringComparison.Ordinal))
        {
            return true;
        }

        string evalWorkspace = PlanPhaseWorkspace.Resolve(plan, cancellationToken, junctionRoot);

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        var reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

        using GuardrailHeartbeat? heartbeat = heartbeatOut is null ? null : GuardrailHeartbeat.StartConsole(heartbeatOut);

        // Issue #432: capture each Full Flight Check's stdout/stderr under logs/<runId>/preflights/<name>/.
        // A failing check halts the run before ANY task is scheduled, so there is no attempt dir anywhere —
        // without this the only trace of WHY is the operator's scrollback.
        string runId = journal.Document.RunId;
        string? artifactDir = GateArtifacts.DirectoryFor(
            plan.PlanDirectory, runId, waveDir: null, GateArtifacts.PreflightsFolder);
        string? relativeLogDir = GateArtifacts.RelativeDirectoryFor(
            runId, waveDir: null, GateArtifacts.PreflightsFolder);

        ReVerifyResult result = await reVerifier
            .ReVerifyAsync(
                evalWorkspace,
                plan.PlanPreflights,
                new ReVerifyOptions { Progress = heartbeat, ArtifactDirectory = artifactDir },
                cancellationToken)
            .ConfigureAwait(false);

        List<PlanPreflightCheck> checks = plan.PlanPreflights
            .Select(g =>
            {
                GuardrailResult? failure = result.FailedGuardrails
                    .FirstOrDefault(f => string.Equals(f.Name, g.Name, StringComparison.Ordinal));
                return new PlanPreflightCheck
                {
                    Name = g.Name,
                    Passed = failure is null,
                    Reason = failure?.Reason
                };
            })
            .ToList();

        var section = new PlanPreflightsSection
        {
            Status = result.Passed ? PlanPhaseStatus.Passed : PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = currentHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            LogDir = relativeLogDir
        };

        // Issue #432: on FAILURE also record the uniform top-level `halt` — the one field a post-mortem
        // reader can consult without knowing the four-folder model, so a halted run's journal never reads
        // as a wall of silent pending tasks.
        RunHalt? halt = result.Passed ? null : BuildHalt(result, relativeLogDir);

        PlanPhaseJournalWriter.Update(plan.PlanDirectory, document => halt is null
            ? document with { PlanPreflights = section }
            : document with { PlanPreflights = section, Halt = halt });

        return result.Passed;
    }

    /// <summary>
    /// Execute every committed <c>tasks/&lt;id&gt;/samples/</c> pair against its guardrail and return whether
    /// scheduling may proceed. True — with NOTHING journaled and NOTHING printed — both when the plan carries
    /// no pairs at all (the overwhelmingly common case) and when every pair it does carry is sound. False,
    /// with the failure recorded in the phase's existing shapes, as soon as one finding is reported.
    /// </summary>
    private static async Task<bool> SamplePairsPassAsync(
        PlanDefinition plan,
        RunJournal journal,
        ProcessRunner processRunner,
        TextWriter? consoleOut,
        CancellationToken cancellationToken)
    {
        SampleVerifyResult result = await SampleVerifier
            .VerifyAsync(plan, processRunner, PerSampleTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.Passed)
        {
            // Nothing to verify, or nothing wrong: no journal section, no console line, no marker touched.
            return true;
        }

        List<PlanPreflightCheck> checks = result.Findings
            .Select(f => new PlanPreflightCheck
            {
                // NAME the offending pair (issue #432). A pre-DAG halt settles no task, so tasks{} is a wall
                // of silent `pending` entries; "a sample pair failed" cannot tell a post-mortem reader which
                // of a plan's pairs to open.
                Name = SampleCheckName(f.SamplePath),
                Passed = false,
                Reason = $"{f.Kind}: {f.Message}"
            })
            .ToList();

        // The existing failure posture, unchanged in shape: a plan-preflight-failed section carrying one
        // check entry per finding, plus the uniform top-level `halt` (#432). Additive — no new section, no
        // new field. The section REPLACES any earlier passed marker on purpose: this run did not pass the
        // pre-DAG phase, and a journal that still reads `passed` beside a halt is the "recorded but not
        // true" failure this whole feature exists to end. It is also what makes the next resume re-evaluate
        // rather than skip, exactly as a failed Full Flight Check already does.
        var section = new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = journal.Document.PlanHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks
        };

        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanPreflightFailed,
            HaltedAt = DateTimeOffset.UtcNow,
            Headline = "Sample-pair verification FAILED — halting before scheduling any task: "
                       + string.Join(", ", checks.Select(c => c.Name).Distinct(StringComparer.Ordinal)),
            FailedChecks = checks
                .Select(c => new FailedGuardrail { Name = c.Name, Reason = c.Reason! })
                .ToList()
        };

        PlanPhaseJournalWriter.Update(
            plan.PlanDirectory, document => document with { PlanPreflights = section, Halt = halt });

        WriteSampleFailureReport(result, consoleOut);
        return false;
    }

    /// <summary>
    /// The journal/console name of a bad pair: its base name (the sample filename with its
    /// <c>.valid</c>/<c>.invalid</c> and content extensions stripped), which is also the name of the
    /// guardrail it is matched to.
    /// </summary>
    private static string SampleCheckName(string samplePath) =>
        "sample pair '"
        + Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(samplePath))
        + "'";

    /// <summary>
    /// The operator-facing halt report. This phase runs BEFORE the Spectre live region is constructed, so
    /// plain lines are #145-safe; null <paramref name="consoleOut"/> ⇒ silent (the direct-call and
    /// revalidate paths supply their own writer).
    /// <para>
    /// It says WHY the check exists, not just that it failed, and that is deliberate. The harness already
    /// lints the guardrail that can never PASS (GR2055); running the <c>.invalid</c> half is the ONLY
    /// detector for the opposite and far more dangerous polarity — the guardrail that can never FAIL, which
    /// certifies every implementation including no implementation at all. An operator who understands that
    /// fixes the pair; one who reads only "sample mismatch" deletes it and restores the blind spot.
    /// </para>
    /// </summary>
    private static void WriteSampleFailureReport(SampleVerifyResult result, TextWriter? consoleOut)
    {
        if (consoleOut is null)
        {
            return;
        }

        consoleOut.WriteLine();
        consoleOut.WriteLine(
            $"Sample-pair verification FAILED — {result.Findings.Count} finding(s) over "
            + $"{result.PairsVerified} executed pair(s). Halting before scheduling any task.");

        foreach (SampleFinding finding in result.Findings)
        {
            consoleOut.WriteLine(
                $"  {finding.Kind}: {finding.SamplePath} against {finding.GuardrailPath ?? "(no matching guardrail)"} → "
                + $"exit {finding.ObservedExitCode?.ToString() ?? "(none)"}");
            consoleOut.WriteLine($"    {finding.Message}");
        }

        consoleOut.WriteLine(
            "  A tasks/<id>/samples/ pair asserts exactly two facts — the .valid half's guardrail exits 0, "
            + "the .invalid half's exits non-zero. The harness already lints the guardrail that can never "
            + "PASS (GR2055); running the .invalid half is the only detector for the opposite and far more "
            + "dangerous polarity, the guardrail that can never FAIL — one that certifies every "
            + "implementation, including no implementation at all. Fix the pair or the guardrail; deleting "
            + "the pair only restores the blind spot. Re-check with `guardrails samples verify`.");
    }

    // ── the openai-compat endpoint preflight (plan 28 §6.6/§7, issue #223) ──────────────────────────

    /// <summary>
    /// Reach every distinct <c>openai-compat</c> endpoint the registry declares, assert every declared model
    /// is listed there, and prove each distinct (endpoint, model) can actually CALL a tool — then return
    /// whether scheduling may proceed. True, with NOTHING journaled and NOTHING printed, both when the plan
    /// declares no such block (the overwhelmingly common case, and the one that must open zero connections)
    /// and when every endpoint answers correctly.
    /// </summary>
    private static async Task<bool> OpenAiCompatEndpointsPassAsync(
        PlanDefinition plan,
        RunJournal journal,
        TextWriter? consoleOut,
        CancellationToken cancellationToken)
    {
        // Discovery is a REGISTRY SCAN (plan §7) — it reads guardrails.json and nothing else. Deciding here,
        // before an HttpClient exists, is what makes the zero-connection condition structural.
        List<EndpointProbeTarget> targets = DiscoverOpenAiCompatTargets(plan);
        if (targets.Count == 0)
        {
            return true;
        }

        var failures = new List<PlanPreflightCheck>();

        using var http = new HttpClient { Timeout = EndpointProbeTimeout };
        foreach (EndpointProbeTarget target in targets)
        {
            await ProbeEndpointAsync(http, target, failures, consoleOut, cancellationToken).ConfigureAwait(false);
        }

        if (failures.Count == 0)
        {
            return true;
        }

        // The phase's existing failure posture, unchanged in shape: a plan-preflight-failed section carrying
        // one check per finding, plus the uniform top-level `halt` (#432) a post-mortem reader consults
        // without knowing the four-folder model.
        var section = new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = journal.Document.PlanHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = failures
        };

        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanPreflightFailed,
            HaltedAt = DateTimeOffset.UtcNow,
            Headline = "openai-compat endpoint preflight FAILED — halting before scheduling any task: "
                       + string.Join(", ", failures.Select(c => c.Name).Distinct(StringComparer.Ordinal)),
            FailedChecks = failures
                .Select(c => new FailedGuardrail { Name = c.Name, Reason = c.Reason! })
                .ToList()
        };

        PlanPhaseJournalWriter.Update(
            plan.PlanDirectory, document => document with { PlanPreflights = section, Halt = halt });

        WriteEndpointFailureReport(failures, consoleOut);
        return false;
    }

    /// <summary>
    /// The registry scan: every <c>openai-compat</c> block, grouped by DISTINCT endpoint, each carrying the
    /// distinct models declared against it. Grouping is what buys "once per endpoint" and "once per
    /// (endpoint, model)" — two blocks naming the same pair are one probe, and one server hosting two models
    /// is one listing and two probes, because a template that emits tool calls is a per-MODEL fact.
    /// <para>
    /// A block whose <c>endpoint</c> is absent is skipped rather than reported: that is GR2065's job at
    /// validate, and a second failure shape for the same mistake helps nobody.
    /// </para>
    /// </summary>
    private static List<EndpointProbeTarget> DiscoverOpenAiCompatTargets(PlanDefinition plan)
    {
        var byEndpoint = new Dictionary<string, EndpointProbeTarget>(StringComparer.Ordinal);
        var inDeclarationOrder = new List<EndpointProbeTarget>();

        foreach (PromptRunnerConfig block in plan.Config.PromptRunners.Values)
        {
            if (block.Kind != PromptRunnerKind.OpenAiCompat || string.IsNullOrWhiteSpace(block.Endpoint))
            {
                continue;
            }

            string endpoint = block.Endpoint.Trim().TrimEnd('/');
            if (!byEndpoint.TryGetValue(endpoint, out EndpointProbeTarget? target))
            {
                target = new EndpointProbeTarget(endpoint);
                byEndpoint.Add(endpoint, target);
                inDeclarationOrder.Add(target);
            }

            // BOTH declared models: this runner serves guardrail prompts, and a guardrailOverrides.model is
            // exactly as reachable there as the block's own — an unprobed override is an unprobed model.
            target.Declare(block.Settings.Model, block);
            target.Declare(block.GuardrailOverrides?.Model, block);
        }

        return inDeclarationOrder;
    }

    /// <summary>
    /// One endpoint: reachability, then model presence, then the per-model tool-capability probe. Findings
    /// are appended to <paramref name="failures"/>; a model the listing does not carry is NOT probed, because
    /// asking a server to complete against a model it just said it does not have would report the same
    /// mistake twice in two different shapes.
    /// </summary>
    private static async Task ProbeEndpointAsync(
        HttpClient http,
        EndpointProbeTarget target,
        List<PlanPreflightCheck> failures,
        TextWriter? consoleOut,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Endpoint + "/models", UriKind.Absolute, out Uri? modelsUri))
        {
            failures.Add(target.Failure(
                model: null,
                $"`endpoint` is not an absolute URL, so nothing could be probed: \"{target.Endpoint}\". "
                + "It must be an absolute http/https base URL, e.g. \"http://127.0.0.1:11434/v1\"."));
            return;
        }

        ModelListing listing;
        try
        {
            listing = await ReadModelListingAsync(http, modelsUri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            failures.Add(target.Failure(model: null, UnreachableReason(target, TransportCause(exception), exception.Message)));
            return;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            failures.Add(target.Failure(
                model: null,
                UnreachableReason(
                    target,
                    $"the endpoint did not answer within {EndpointProbeTimeout.TotalSeconds:F0}s",
                    exception.Message)));
            return;
        }

        if (listing.HaltReason is { } listingFailure)
        {
            failures.Add(target.Failure(model: null, listingFailure));
            return;
        }

        if (listing.Ids is null)
        {
            // 404/405: "the server answered, but does not offer this" — NOT "there is no server". An engine
            // that serves chat perfectly while omitting the listing endpoint must not be locked out by a
            // check that exists to help, so this downgrades to a warning and skips ONLY the model-presence
            // assertion. The tool-capability probe below still runs: it is the one that closes §6.6.
            consoleOut?.WriteLine(
                $"  WARNING: {target.Endpoint} answered HTTP {listing.StatusCode} for GET /models, so this run cannot "
                + "confirm the declared model(s) are present there. The endpoint is up and its tool-calling "
                + "capability is still being probed; only the model-presence check is skipped.");
        }

        foreach (DeclaredModel declared in target.Models)
        {
            if (listing.Ids is { } ids
                && !ids.Contains(declared.Model, StringComparer.Ordinal))
            {
                failures.Add(target.Failure(
                    declared.Model,
                    $"{target.Endpoint} does not list the model '{declared.Model}' that block "
                    + $"'{declared.Block.Name}' declares — it reported {DescribeListing(ids)}. "
                    + ModelNotFoundRemedy(declared)));
                continue;
            }

            string? probeFailure = await ProbeToolCapabilityAsync(http, target, declared, cancellationToken)
                .ConfigureAwait(false);
            if (probeFailure is not null)
            {
                failures.Add(target.Failure(declared.Model, probeFailure));
            }
        }
    }

    /// <summary>
    /// <c>GET {endpoint}/models</c>. A 200 yields the listed ids; 404/405 yield a null id list (the §7
    /// downgrade); anything else — 5xx included — yields a halt reason, because "the server is broken" is
    /// not "the server does not offer this".
    /// </summary>
    private static async Task<ModelListing> ReadModelListingAsync(
        HttpClient http, Uri modelsUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(modelsUri, cancellationToken).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (status is 404 or 405)
        {
            return new ModelListing(status, Ids: null, HaltReason: null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ModelListing(
                status,
                Ids: null,
                HaltReason: $"GET {modelsUri} answered HTTP {status}. That is the endpoint reporting itself broken, "
                            + "not merely declining to offer a model listing (404/405 would be), so the run halts "
                            + $"before any task spends a token against it. {Snippet(body)}");
        }

        return new ModelListing(status, ParseModelIds(body), HaltReason: null);
    }

    /// <summary>The <c>data[].id</c> values of an OpenAI-shaped model listing; empty when the body is not one.</summary>
    private static IReadOnlyList<string> ParseModelIds(string body)
    {
        var ids = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // A listing we cannot read is an EMPTY listing, which fails the model-presence assertion loudly
            // rather than passing it silently — the direction that cannot certify an absent model.
        }

        return ids;
    }

    /// <summary>
    /// THE check §6.6 exists for: one minimal chat completion carrying a single trivial tool whose only
    /// correct response is to call it. Null ⇒ capable, proceed. Otherwise the halt reason — for a 400/422
    /// rejecting <c>tools</c>, and for the silent shape, a 200 that called nothing.
    /// </summary>
    private static async Task<string?> ProbeToolCapabilityAsync(
        HttpClient http, EndpointProbeTarget target, DeclaredModel declared, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Endpoint + "/chat/completions", UriKind.Absolute, out Uri? chatUri))
        {
            return $"`endpoint` is not an absolute URL, so '{declared.Model}' could not be probed: \"{target.Endpoint}\".";
        }

        using var content = new StringContent(ToolProbeBody(declared.Model), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(chatUri, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return UnreachableReason(target, TransportCause(exception), exception.Message);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return UnreachableReason(
                target,
                $"the endpoint did not answer the tool-capability probe within {EndpointProbeTimeout.TotalSeconds:F0}s",
                exception.Message);
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (status is 400 or 422)
            {
                return $"{target.Endpoint} answered HTTP {status} to a chat completion that offered one trivial tool, "
                       + $"for the model '{declared.Model}' declared by block '{declared.Block.Name}': it REJECTED the "
                       + "`tools` array. " + VerifierNeedsToolsSentence(declared) + " " + Snippet(body);
            }

            if (!response.IsSuccessStatusCode)
            {
                return $"{target.Endpoint} answered HTTP {status} to the tool-capability probe for '{declared.Model}' "
                       + $"(block '{declared.Block.Name}'), so this run cannot establish that the model can call a "
                       + "tool. " + VerifierNeedsToolsSentence(declared) + " " + Snippet(body);
            }

            if (HasToolCalls(body))
            {
                return null;
            }

            // The SILENT shape, and the whole reason the probe exists. Nothing on the wire distinguishes
            // "I considered the tools and needed none" from "I do not implement tools" — so trusting this
            // response means every judge on this endpoint may answer from the composed prompt alone, having
            // read nothing, and still emit a perfectly well-formed `{"pass": true}`. Every malformedness
            // check downstream passes on that answer; the guardrail goes GREEN over evidence nobody read.
            return $"{target.Endpoint} accepted a `tools` array for '{declared.Model}' (block "
                   + $"'{declared.Block.Name}') and answered HTTP 200 WITHOUT calling the tool. The probe offers one "
                   + "trivial function whose only correct response is to call it, so a completion that calls nothing "
                   + "means this model does not emit tool calls here. " + VerifierNeedsToolsSentence(declared)
                   + " Trusting it instead would let a judge answer from its prompt alone, having read no evidence, "
                   + "and still return a well-formed pass — a green guardrail over work nobody checked. " + Snippet(body);
        }
    }

    /// <summary>
    /// The probe body: two messages and one no-op function, deliberately minimal. Non-streaming — the probe
    /// asks a yes/no capability question and an SSE frame carries no more of the answer than a whole response
    /// does.
    /// </summary>
    private static string ToolProbeBody(string model)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "You are a tool-calling capability probe. Call the function you are offered. "
                                  + "Do not answer in prose."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Call the `{ToolProbeName}` function once, with no arguments. Calling it is the "
                                  + "only correct response."
                }
            },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = ToolProbeName,
                        ["description"] = "A no-op capability probe. Call it once, with no arguments.",
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject(),
                            ["additionalProperties"] = false
                        }
                    }
                }
            },
            ["max_tokens"] = 64
        };

        return body.ToJsonString();
    }

    /// <summary>Whether any choice's message carries a non-empty <c>tool_calls</c> array.</summary>
    private static bool HasToolCalls(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.ValueKind == JsonValueKind.Object
                    && choice.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("tool_calls", out JsonElement calls)
                    && calls.ValueKind == JsonValueKind.Array
                    && calls.GetArrayLength() > 0)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable body cannot evidence a tool call, and "no evidence" is the halting direction.
        }

        return false;
    }

    /// <summary>The endpoint was never reached: DNS, refused, reset, TLS, or silence past the short timeout.</summary>
    private static string UnreachableReason(EndpointProbeTarget target, string cause, string detail) =>
        $"{target.Endpoint} could not be reached — {cause} ({detail}). Every block that names this endpoint "
        + $"({string.Join(", ", target.BlockNames.Select(n => $"'{n}'"))}) is unusable, so the run halts here rather "
        + "than letting a task spend a token against an endpoint that is not there. Start the server, or correct "
        + "the block's `endpoint`.";

    /// <summary>The framework's own typed transport signal, rendered as one operator-facing clause.</summary>
    private static string TransportCause(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "DNS did not resolve its host",
        HttpRequestError.ConnectionError => "the connection was refused or reset",
        HttpRequestError.SecureConnectionError => "the TLS handshake failed",
        HttpRequestError.ProxyTunnelError => "the proxy refused to tunnel the connection",
        _ => "the request never reached it"
    };

    /// <summary>
    /// Why a non-tool-calling endpoint is fatal rather than degradable, said once and reused: v1's
    /// <c>openai-compat</c> runner serves the verifier roles, and a verifier reads the evidence it judges.
    /// </summary>
    private static string VerifierNeedsToolsSentence(DeclaredModel declared) =>
        $"An `openai-compat` block serves the VERIFIER roles (plan 28 §3.2), and a verifier reads the evidence it "
        + $"judges through its `Read`/`Glob`/`Grep` tools — so block '{declared.Block.Name}' cannot be served by a "
        + "model that does not call tools. Point it at a tool-calling model or endpoint.";

    /// <summary>
    /// The model-not-found remedy, keyed off the block's OPTIONAL <c>engine</c> hint — the one place an engine
    /// name may appear (plan 28 §6.2/§9), and operator-facing TEXT only: it selects a sentence, never a code
    /// path and never a request field. <c>ollama pull</c> is right for one engine and actively misleading for
    /// the others, which is the whole reason the hint exists.
    /// <para>
    /// The runner carries its own copy for the 404-mid-run case (<c>OpenAiCompatPromptRunner</c>), which is a
    /// different failure at a different time; these are two sentences for two moments, not one helper split
    /// in half.
    /// </para>
    /// </summary>
    private static string ModelNotFoundRemedy(DeclaredModel declared)
    {
        string engine = declared.Block.Engine?.Trim() ?? string.Empty;
        string model = declared.Model;

        if (IsEngine(engine, "ollama"))
        {
            return $"Run `ollama pull {model}` on the machine serving that endpoint, then re-run.";
        }

        if (IsEngine(engine, "mlx"))
        {
            return $"Download it first — `mlx_lm.download --hf-repo {model}` for `mlx_lm.server`, or LM Studio's "
                   + "model manager if you serve MLX through LM Studio — then re-run.";
        }

        if (IsEngine(engine, "lm-studio"))
        {
            return $"Download `{model}` in LM Studio's model manager and make sure it is loaded in the running server.";
        }

        if (IsEngine(engine, "llama.cpp") || IsEngine(engine, "vllm"))
        {
            return $"Start the server with `--model {model}` (it serves the model it was launched with).";
        }

        if (IsEngine(engine, "apple-fm"))
        {
            return $"Apple's system models are not downloadable — the `fm` stack serves a fixed set under Apple's "
                   + $"own ids, so `{model}` is either misspelled or not one this build serves. Run `fm --help` on "
                   + "the machine serving that endpoint. NB: `apple-fm` is macOS-only, and its OpenAI-compatible "
                   + "server is undocumented by Apple and beta as of macOS 27.";
        }

        return $"Make `{model}` available there. The block declares no `engine` hint, so there is no engine-specific "
               + $"command to suggest — add one ({SuggestableEngines(declared.Block.Endpoint)}) and this message names "
               + "the exact command next time.";
    }

    /// <summary>
    /// The engine hints worth SUGGESTING for this block. <c>apple-fm</c> names a macOS-only stack, but the
    /// machine serving the block's endpoint is not necessarily this one — a Windows operator pointing at a Mac
    /// across the LAN is the entire point of a separate inference box — so it is withheld only when the endpoint
    /// is LOOPBACK and this host is not macOS, the one case where the server is provably not a Mac. SUGGESTION
    /// TEXT only: <c>engine</c> is free text, validated nowhere, so a plan naming <c>apple-fm</c> loads and
    /// validates unchanged on every OS (plan 28 §6.2).
    /// </summary>
    private static string SuggestableEngines(string? endpoint)
    {
        const string Portable = "ollama | llama.cpp | mlx | lm-studio | vllm";
        return ServerIsProvablyNotMac(endpoint) ? Portable : Portable + " | apple-fm (macOS host only)";
    }

    /// <summary>
    /// True only when we KNOW the endpoint is served by a non-Mac: the URL is loopback (so the server is this
    /// machine) and this machine is not macOS. A remote or unparseable endpoint returns false — unknown is not
    /// the same as no, and suppressing a valid suggestion is the worse error of the two.
    /// </summary>
    private static bool ServerIsProvablyNotMac(string? endpoint) =>
        !OperatingSystem.IsMacOS()
        && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
        && uri.IsLoopback;

    private static bool IsEngine(string declared, string candidate) =>
        string.Equals(declared, candidate, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the endpoint said it has, for the operator comparing it against what they declared.</summary>
    private static string DescribeListing(IReadOnlyList<string> ids) =>
        ids.Count == 0
            ? "no models at all"
            : "these: " + string.Join(", ", ids.Select(id => $"'{id}'"));

    /// <summary>A bounded slice of a response body — enough to diagnose, never enough to flood the journal.</summary>
    private static string Snippet(string body)
    {
        string trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "(the response body was empty.)";
        }

        return trimmed.Length <= 400
            ? $"Response body: {trimmed}"
            : $"Response body (first 400 chars): {trimmed[..400]}…";
    }

    /// <summary>The operator-facing halt report, in the same plain, pre-Spectre shape the sample report uses.</summary>
    private static void WriteEndpointFailureReport(IReadOnlyList<PlanPreflightCheck> failures, TextWriter? consoleOut)
    {
        if (consoleOut is null)
        {
            return;
        }

        consoleOut.WriteLine();
        consoleOut.WriteLine(
            $"openai-compat endpoint preflight FAILED — {failures.Count} finding(s). Halting before scheduling any task.");

        foreach (PlanPreflightCheck failure in failures)
        {
            consoleOut.WriteLine($"  {failure.Name}");
            consoleOut.WriteLine($"    {failure.Reason}");
        }
    }

    /// <summary>One declared model, and the block that declared it (which carries the `engine` remedy hint).</summary>
    private sealed record DeclaredModel(string Model, PromptRunnerConfig Block);

    /// <summary>The outcome of <c>GET {endpoint}/models</c>: listed ids, the §7 downgrade, or a halt reason.</summary>
    /// <param name="StatusCode">The status the endpoint answered with.</param>
    /// <param name="Ids">The listed model ids, or null when the listing was not offered (404/405).</param>
    /// <param name="HaltReason">Non-null when the endpoint answered in a way that halts the run.</param>
    private sealed record ModelListing(int StatusCode, IReadOnlyList<string>? Ids, string? HaltReason);

    /// <summary>One distinct endpoint and the distinct models declared against it across every block.</summary>
    private sealed class EndpointProbeTarget(string endpoint)
    {
        private readonly List<DeclaredModel> _models = [];
        private readonly List<string> _blockNames = [];

        /// <summary>The declared base URL, trimmed of a trailing slash so two spellings of one server are one probe.</summary>
        public string Endpoint { get; } = endpoint;

        /// <summary>The distinct models declared against this endpoint, in declaration order.</summary>
        public IReadOnlyList<DeclaredModel> Models => _models;

        /// <summary>The names of every block pointing here — what an unreachable endpoint takes down with it.</summary>
        public IReadOnlyList<string> BlockNames => _blockNames;

        /// <summary>
        /// Record a model declared against this endpoint. Blank is ignored (a missing `model` is GR2065's to
        /// report at validate) and a repeat is ignored, which is what makes the probe once-per-(endpoint, model).
        /// </summary>
        public void Declare(string? model, PromptRunnerConfig block)
        {
            if (!_blockNames.Contains(block.Name, StringComparer.Ordinal))
            {
                _blockNames.Add(block.Name);
            }

            if (string.IsNullOrWhiteSpace(model)
                || _models.Any(declared => string.Equals(declared.Model, model, StringComparison.Ordinal)))
            {
                return;
            }

            _models.Add(new DeclaredModel(model, block));
        }

        /// <summary>
        /// One journal check entry. It NAMES the endpoint and (where there is one) the model, because a pre-DAG
        /// halt settles no task: without the name, `tasks{}` is a wall of silent `pending` entries and
        /// "an endpoint preflight failed" cannot tell a post-mortem reader which block to open.
        /// </summary>
        public PlanPreflightCheck Failure(string? model, string reason) => new()
        {
            Name = model is null
                ? $"openai-compat endpoint {Endpoint}"
                : $"openai-compat endpoint {Endpoint} (model '{model}')",
            Passed = false,
            Reason = reason
        };
    }

    /// <summary>
    /// The machine-readable stop reason for a failed pre-DAG phase (SSOT §7 <c>halt</c>): the same headline
    /// the console prints, the failing check names + reasons, and where their captured output landed.
    /// </summary>
    private static RunHalt BuildHalt(ReVerifyResult result, string? relativeLogDir) => new()
    {
        Kind = RunHaltKind.PlanPreflightFailed,
        HaltedAt = DateTimeOffset.UtcNow,
        Headline = "Plan preflight FAILED — halting before scheduling any task: "
                   + string.Join(", ", result.FailedGuardrails.Select(f => f.Name)),
        FailedChecks = result.FailedGuardrails
            .Select(f => new FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList(),
        LogDir = relativeLogDir
    };
}
