using System.CommandLine;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
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
///
/// <para>The second leaf is <c>providers check &lt;block-name&gt;</c> (plan 28 §8, issue #223) — the
/// MANUAL, OPT-IN probe that retires <b>dialect risk</b>: the seven assumptions the loopback
/// <c>FakeOpenAiServer</c> can never settle, because it is a fake we wrote and can therefore only ever
/// agree with us. Not in CI, not in <c>run</c>, not in <c>validate</c> — the same posture as M7's opt-in
/// real-Claude smoke.</para>
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

    /// <summary>The <c>providers</c> command group (<c>init</c> and <c>check</c>).</summary>
    public static Command Create(IConsoleIo io)
    {
        var command = new Command(
            "providers",
            "Inspect, annotate and check the prompt-runner registry in a plan's guardrails.json.");
        command.Add(BuildInitLeaf(io));
        command.Add(BuildCheckLeaf(io));
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

    // ── `providers check [folder] <block-name>` — the opt-in dialect probe (plan 28 §8) ─────────────

    /// <summary>
    /// Per-request wall clock. Longer than the pre-DAG preflight's 10s (<c>PlanPreflightPhase</c>) on
    /// purpose: that one guards a run that is about to start and must not sit on a slow endpoint, whereas
    /// this verb is a human's FIRST contact with a machine that may still be loading a model off disk.
    /// Failing it at ten seconds would report "cannot be reached" about a server that is merely waking up.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The trivial function the tool-capability probe offers. It does nothing and takes no arguments —
    /// the probe asks one question only (<i>can this (endpoint, model) emit a <c>tool_calls</c> entry at
    /// all?</i>), and anything the tool actually DID would be a second variable in the answer.
    /// <para>
    /// <b>Keep this probe in step with <c>PlanPreflightPhase</c>'s.</b> The pre-DAG preflight asks the
    /// SAME question of the same wire — one trivial function whose only correct response is to call it —
    /// and the two must never diverge in what they consider a capable endpoint, or an operator whose
    /// <c>providers check</c> reads "met" could still be halted at the start of a run. The two bodies are
    /// deliberately identical; the shared type they should both call belongs in <c>Guardrails.Core</c>.
    /// </para>
    /// </summary>
    private const string ToolProbeName = "probe_tool";

    /// <summary>
    /// The model id the model-not-found probe asks for. Deliberately unservable: the assumption under test
    /// is the SHAPE of the refusal, so the probe must provoke a refusal without depending on the operator
    /// having (or not having) any particular model.
    /// </summary>
    private const string AbsentModelId = "guardrails-providers-check-no-such-model";

    /// <summary>The <c>num_ctx</c> the probe sends when the block states no <c>contextTokens</c> (GR2065's job).</summary>
    private const int FallbackNumCtx = 4096;

    // The seven dialect assumptions of plan §8, in the plan's own order. These strings are the report's
    // stable vocabulary — an operator (and the test suite) finds an assumption by its name on the line.
    private const string AssumptionIncludeUsage = "stream_options.include_usage honoured";
    private const string AssumptionToolCalling = "tools accepted and called";
    private const string AssumptionNumCtx = "num_ctx honoured";
    private const string AssumptionModelNotFound = "model-not-found body shape";
    private const string AssumptionSseFraming = "SSE framing";
    private const string AssumptionReasoningEffort = "reasoning_effort tolerance";
    private const string AssumptionModelListing = "GET /models is served";

    /// <summary>Where the detail text under a verdict line starts, and the width it wraps at.</summary>
    private const int DetailIndent = 13;
    private const int ReportWidth = 96;

    private static Command BuildCheckLeaf(IConsoleIo io)
    {
        // FolderArgument first, then the required block name — `reset`'s own two-positional convention,
        // and the shape that lets this verb be driven from anywhere without mutating the process working
        // directory. The plan's usage line abbreviates to `<block-name>` alone; that still works, because
        // the folder positional defaults to the current directory.
        var folderArgument = FolderArgument.Create();

        var blockArgument = new Argument<string>("block-name")
        {
            Description = "The promptRunners block to probe. Must be an `openai-compat` block."
        };

        var command = new Command(
            "check",
            "Probe one openai-compat block's REAL endpoint for each dialect assumption the harness makes, "
            + "and report every one met / unmet / unknown. Opt-in and manual: never run by CI, `run` or `validate`.");
        command.Add(folderArgument);
        command.Add(blockArgument);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return await RunCheckAsync(folder, parseResult.GetValue(blockArgument) ?? "", io, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Resolve the named block, probe its endpoint once per assumption, and print the report.
    /// <para>
    /// <b>Exit code.</b> Non-zero ONLY when the endpoint cannot be reached at all — refused, DNS, TLS,
    /// silence past <see cref="ProbeTimeout"/> — or when the request is malformed before any socket opens
    /// (no such block, a block of another kind, a block with no endpoint). An <c>unmet</c> or
    /// <c>unknown</c> assumption exits 0: it is a fact about the operator's server, and a verb whose whole
    /// job is to report that fact calmly must not turn it into a failure.
    /// </para>
    /// </summary>
    private static async Task<int> RunCheckAsync(
        string folder, string blockName, IConsoleIo io, CancellationToken cancellationToken)
    {
        // PlanLoader, not PlanProbe.LoadAndValidate — `SamplesCommand`'s reasoning, and it matters more
        // here: this verb exists to be run BEFORE a plan is finished, against hardware nobody has pointed
        // the harness at yet. A folder that loads but carries validation diagnostics is still checkable.
        PlanLoadResult loaded = new PlanLoader().Load(folder);
        if (loaded.Plan is not PlanDefinition plan)
        {
            io.Error.WriteLine($"Could not load a plan from \"{folder}\" — there is no registry to check.");
            return ExitCodes.HarnessError;
        }

        if (!plan.Config.PromptRunners.TryGetValue(blockName, out PromptRunnerConfig? block))
        {
            io.Error.WriteLine($"No promptRunners block named '{blockName}' in \"{folder}\".");
            io.Error.WriteLine(plan.Config.PromptRunners.Count == 0
                ? "  The registry declares no blocks at all."
                : $"  Declared blocks: {string.Join(", ", plan.Config.PromptRunners.Keys.Select(n => $"'{n}'"))}.");
            io.Error.WriteLine(
                "  Nothing was probed: a block this command cannot find is a block whose endpoint it does not know.");
            return ExitCodes.HarnessError;
        }

        if (block.Kind != PromptRunnerKind.OpenAiCompat)
        {
            io.Error.WriteLine(
                $"Block '{blockName}' declares kind '{PromptRunnerKinds.Token(block.Kind)}', not 'openai-compat'.");
            io.Error.WriteLine(
                "  `providers check` retires DIALECT risk on the openai-compat HTTP wire (plan 28 §8). The seven "
                + "assumptions it probes are facts about that wire, so there is nothing here it could ask of a "
                + $"'{PromptRunnerKinds.Token(block.Kind)}' block — which declares no endpoint to ask.");
            return ExitCodes.HarnessError;
        }

        if (string.IsNullOrWhiteSpace(block.Endpoint))
        {
            io.Error.WriteLine($"Block '{blockName}' is `openai-compat` but declares no `endpoint`, so there is nothing to probe.");
            io.Error.WriteLine("  `guardrails validate` reports that as GR2065. Fix it there, then re-run this check.");
            return ExitCodes.HarnessError;
        }

        string? model = block.Settings.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            io.Error.WriteLine($"Block '{blockName}' is `openai-compat` but declares no `model`, so there is nothing to probe FOR.");
            io.Error.WriteLine(
                "  Every assumption below is a fact about one (endpoint, model) pair — one server can host a model "
                + "whose template emits tool calls and one whose template does not. `guardrails validate` reports "
                + "the missing key as GR2065.");
            return ExitCodes.HarnessError;
        }

        string endpoint = block.Endpoint.Trim().TrimEnd('/');

        using var http = new HttpClient { Timeout = ProbeTimeout };

        // Reachability FIRST, and it is the one outcome that gates the exit code. Everything below assumes
        // a server answered something; if nothing did, seven "unknown" lines would say less than one line
        // naming what went wrong on the socket.
        ProbeAnswer listing = await GetAsync(http, $"{endpoint}/models", block, cancellationToken).ConfigureAwait(false);
        if (listing.TransportFailure is { } unreachable)
        {
            io.Error.WriteLine();
            io.Error.WriteLine($"COULD NOT REACH {endpoint} — {unreachable}");
            io.Error.WriteLine(
                $"  Block '{blockName}' is unusable until that endpoint answers, so no dialect assumption could be "
                + "probed. Start the server, or correct the block's `endpoint`.");
            return ExitCodes.HarnessError;
        }

        var findings = new List<DialectFinding>
        {
            await ProbeIncludeUsageAsync(http, endpoint, block, model, cancellationToken).ConfigureAwait(false),
            await ProbeToolCallingAsync(http, endpoint, block, model, cancellationToken).ConfigureAwait(false),
            await ProbeNumCtxAsync(http, endpoint, block, model, cancellationToken).ConfigureAwait(false),
            await ProbeModelNotFoundShapeAsync(http, endpoint, block, cancellationToken).ConfigureAwait(false),
            await ProbeSseFramingAsync(http, endpoint, block, model, cancellationToken).ConfigureAwait(false),
            await ProbeReasoningEffortAsync(http, endpoint, block, model, cancellationToken).ConfigureAwait(false),
            JudgeModelListing(listing, model)
        };

        PrintCheckReport(blockName, block, endpoint, model, findings, io);
        return ExitCodes.Success;
    }

    // ── the seven probes, one per assumption ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET {endpoint}/models</c>, already performed (it doubles as the reachability probe). A 200
    /// carrying an OpenAI-shaped listing is the assumption holding; a 404/405 is the server answering
    /// plainly that it does not offer one — <b>unmet</b>, not unknown, and not fatal: an engine that
    /// serves chat perfectly while omitting the listing endpoint is a real and supported shape (plan §7).
    /// </summary>
    private static DialectFinding JudgeModelListing(ProbeAnswer listing, string model)
    {
        if (listing.StatusCode is not { } status)
        {
            return new DialectFinding(AssumptionModelListing, DialectVerdict.Unknown, listing.TransportFailure!);
        }

        if (status is 404 or 405)
        {
            return new DialectFinding(AssumptionModelListing, DialectVerdict.Unmet,
                $"HTTP {status} — this server does not offer a model listing. It may still serve chat perfectly; "
                + "what you lose is the run preflight's ability to confirm your declared model is actually loaded "
                + "there, which then degrades to a warning.");
        }

        if (status is < 200 or > 299)
        {
            return new DialectFinding(AssumptionModelListing, DialectVerdict.Unknown,
                $"HTTP {status} — the server answered, but with a malfunction rather than an answer to the "
                + $"question, so whether it serves a listing is undetermined. {Snippet(listing.Body)}");
        }

        if (ParseModelIds(listing.Body) is not { } ids)
        {
            return new DialectFinding(AssumptionModelListing, DialectVerdict.Unmet,
                $"HTTP {status}, but the body is not an OpenAI-shaped listing (no `data` array of objects with an "
                + $"`id`), so nothing can be read out of it. {Snippet(listing.Body)}");
        }

        string presence = ids.Contains(model, StringComparer.Ordinal)
            ? $"The declared model '{model}' IS listed."
            : $"The declared model '{model}' is NOT among them — the run preflight would halt on that; pull or load "
              + "it on the machine serving this endpoint.";

        return new DialectFinding(AssumptionModelListing, DialectVerdict.Met,
            $"HTTP {status} with an OpenAI-shaped listing of {ids.Count} model(s): {DescribeIds(ids)}. {presence}");
    }

    /// <summary>
    /// One streamed completion asking for <c>stream_options.include_usage</c>. Met when a chunk carrying a
    /// <c>usage</c> object actually arrives — the token counts <c>run.json</c> reports come from nowhere
    /// else, and a server that omits them leaves every invocation's cost unrecorded.
    /// </summary>
    private static async Task<DialectFinding> ProbeIncludeUsageAsync(
        HttpClient http, string endpoint, PromptRunnerConfig block, string model, CancellationToken cancellationToken)
    {
        JsonObject body = ChatBody(model, "Reply with the single word: ok.");
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        StreamAnswer answer = await PostStreamedAsync(http, endpoint, body, block, cancellationToken).ConfigureAwait(false);

        if (Refused(answer.StatusCode, answer.TransportFailure) is { } refusal)
        {
            return new DialectFinding(AssumptionIncludeUsage, refusal.Verdict, refusal.Detail);
        }

        return answer.Usage is { } usage
            ? new DialectFinding(AssumptionIncludeUsage, DialectVerdict.Met,
                $"a `usage` chunk arrived on the stream: {usage}. Per-invocation token counts will be recorded.")
            : new DialectFinding(AssumptionIncludeUsage, DialectVerdict.Unmet,
                $"the stream completed ({answer.DataFrames} data frame(s)) with NO `usage` chunk, even though "
                + "`stream_options.include_usage` was requested. Invocations against this endpoint will record no "
                + "token usage at all — null, never a fabricated zero.");
    }

    /// <summary>
    /// THE probe §6.6 exists for: one completion offering a single trivial function whose only correct
    /// response is to call it. A 200 that calls nothing is <b>unmet</b>, not unknown — the server made a
    /// complete, definitive answer, and that answer is the silent false-green shape: nothing on the wire
    /// distinguishes <i>"I considered the tools and needed none"</i> from <i>"I do not implement tools"</i>,
    /// so a verifier on such an endpoint can return an immaculate <c>{"pass": true}</c> having read nothing.
    /// </summary>
    private static async Task<DialectFinding> ProbeToolCallingAsync(
        HttpClient http, string endpoint, PromptRunnerConfig block, string model, CancellationToken cancellationToken)
    {
        ProbeAnswer answer = await PostAsync(http, endpoint, ToolProbeBody(model), block, cancellationToken)
            .ConfigureAwait(false);

        if (Refused(answer.StatusCode, answer.TransportFailure) is { } refusal)
        {
            return new DialectFinding(AssumptionToolCalling, refusal.Verdict,
                refusal.Verdict == DialectVerdict.Unmet
                    ? refusal.Detail + " " + VerifierNeedsTools
                    : refusal.Detail);
        }

        return HasToolCalls(answer.Body)
            ? new DialectFinding(AssumptionToolCalling, DialectVerdict.Met,
                $"HTTP {answer.StatusCode} carrying a `tool_calls` entry — this model calls `{ToolProbeName}` when "
                + "offered it, which is what a verifier needs to read the evidence it judges.")
            : new DialectFinding(AssumptionToolCalling, DialectVerdict.Unmet,
                $"HTTP {answer.StatusCode}: the `tools` array was ACCEPTED and NOTHING was called. The probe offers one "
                + "trivial function whose only correct response is to call it, so a completion that calls none means "
                + $"this model does not emit tool calls here. {VerifierNeedsTools} This is the quiet failure: trusting "
                + "it would let a judge answer from its prompt alone, having read no evidence, and still return a "
                + "well-formed pass.");
    }

    /// <summary>
    /// <c>options.num_ctx</c> — the one assumption no stateless HTTP probe can CONFIRM. An engine that
    /// honours it and an engine that ignores it answer identically, because no OpenAI-compatible response
    /// reports the context window the server actually used. So an accepted request reads <b>unknown</b>:
    /// collapsing it into "met" would be the report lying about what it could not determine.
    /// </summary>
    private static async Task<DialectFinding> ProbeNumCtxAsync(
        HttpClient http, string endpoint, PromptRunnerConfig block, string model, CancellationToken cancellationToken)
    {
        int numCtx = block.ContextTokens ?? FallbackNumCtx;

        JsonObject body = ChatBody(model, "Reply with the single word: ok.");
        body["options"] = new JsonObject { ["num_ctx"] = numCtx };

        ProbeAnswer answer = await PostAsync(http, endpoint, body, block, cancellationToken).ConfigureAwait(false);

        if (Refused(answer.StatusCode, answer.TransportFailure) is { } refusal)
        {
            return new DialectFinding(AssumptionNumCtx, refusal.Verdict,
                refusal.Verdict == DialectVerdict.Unmet
                    ? refusal.Detail + " This endpoint rejects the option outright, so the harness's context window "
                      + "is belt only — the runner's own before/after overflow checks are what actually protect you."
                    : refusal.Detail);
        }

        return new DialectFinding(AssumptionNumCtx, DialectVerdict.Unknown,
            $"HTTP {answer.StatusCode}: `options.num_ctx: {numCtx}` was ACCEPTED, but acceptance is not enforcement. No "
            + "OpenAI-compatible response reports the context window the server actually used, so an engine that "
            + "honours the option and one that silently ignores it (it is an Ollama option and means nothing to MLX) "
            + "answer identically. This cannot be confirmed over HTTP by anything — only the runner's own "
            + "before/after token checks catch a window that was not honoured.");
    }

    /// <summary>
    /// Ask for a model that cannot exist and read the SHAPE of the refusal. Met only when the body carries
    /// the <c>{ "error": { "message", "type"/"code" } }</c> object the runner's failure taxonomy reads;
    /// unmet when a 404 arrives in some other shape; unknown when the server never answered 404 at all,
    /// because then the shape was simply never demonstrated.
    /// </summary>
    private static async Task<DialectFinding> ProbeModelNotFoundShapeAsync(
        HttpClient http, string endpoint, PromptRunnerConfig block, CancellationToken cancellationToken)
    {
        ProbeAnswer answer = await PostAsync(
                http, endpoint, ChatBody(AbsentModelId, "Reply with the single word: ok."), block, cancellationToken)
            .ConfigureAwait(false);

        if (answer.TransportFailure is { } failure)
        {
            return new DialectFinding(AssumptionModelNotFound, DialectVerdict.Unknown, failure);
        }

        if (answer.StatusCode != 404)
        {
            return new DialectFinding(AssumptionModelNotFound, DialectVerdict.Unknown,
                $"a completion for the deliberately-absent model '{AbsentModelId}' answered HTTP {answer.StatusCode}, not "
                + "404, so this endpoint never demonstrated a model-not-found response and its shape is undetermined. "
                + $"{Snippet(answer.Body)}");
        }

        return DescribeErrorShape(answer.Body) is { } missing
            ? new DialectFinding(AssumptionModelNotFound, DialectVerdict.Unmet,
                $"HTTP 404, but the body is not the `{{ \"error\": {{ \"message\", \"type\"/\"code\" }} }}` object the "
                + $"runner's failure taxonomy reads — {missing}. A 404 in this shape is still classified as a "
                + $"permanent Error (never a transient pause), but the operator-facing message will be thinner. "
                + $"{Snippet(answer.Body)}")
            : new DialectFinding(AssumptionModelNotFound, DialectVerdict.Met,
                "HTTP 404 carrying the `{ \"error\": { \"message\", \"type\"/\"code\" } }` object the runner's failure "
                + "taxonomy reads, so a missing model here fails loudly with the engine-specific remedy rather than "
                + "as an anonymous 404.");
    }

    /// <summary>
    /// One streamed completion, read frame by frame. Streaming is REQUIRED of the runner (§6.3), so the
    /// question is only whether this endpoint's <c>text/event-stream</c> framing is one we can decode.
    /// </summary>
    private static async Task<DialectFinding> ProbeSseFramingAsync(
        HttpClient http, string endpoint, PromptRunnerConfig block, string model, CancellationToken cancellationToken)
    {
        JsonObject body = ChatBody(model, "Reply with the single word: ok.");
        body["stream"] = true;

        StreamAnswer answer = await PostStreamedAsync(http, endpoint, body, block, cancellationToken).ConfigureAwait(false);

        if (Refused(answer.StatusCode, answer.TransportFailure) is { } refusal)
        {
            return new DialectFinding(AssumptionSseFraming, refusal.Verdict, refusal.Detail);
        }

        if (answer.DataFrames == 0)
        {
            return new DialectFinding(AssumptionSseFraming, DialectVerdict.Unmet,
                $"HTTP {answer.StatusCode}, but `\"stream\": true` produced NO decodable `data:` frame. The runner streams "
                + "every turn, so it would read nothing back from this endpoint.");
        }

        string done = answer.SawDoneSentinel
            ? "terminated by the `[DONE]` sentinel"
            : "with NO `[DONE]` sentinel — the runner tolerates that (the stream simply ends) but it is a divergence "
              + "worth knowing about";

        return new DialectFinding(AssumptionSseFraming, DialectVerdict.Met,
            $"HTTP {answer.StatusCode}: {answer.DataFrames} decodable `data:` frame(s), {done}.");
    }

    /// <summary>
    /// <c>reasoning_effort</c> is sent to reasoning models and is meaningless to the rest; the assumption is
    /// only that an endpoint TOLERATES it rather than rejecting the whole request over an unknown parameter.
    /// </summary>
    private static async Task<DialectFinding> ProbeReasoningEffortAsync(
        HttpClient http, string endpoint, PromptRunnerConfig block, string model, CancellationToken cancellationToken)
    {
        JsonObject body = ChatBody(model, "Reply with the single word: ok.");
        body["reasoning_effort"] = "low";

        ProbeAnswer answer = await PostAsync(http, endpoint, body, block, cancellationToken).ConfigureAwait(false);

        if (Refused(answer.StatusCode, answer.TransportFailure) is { } refusal)
        {
            return new DialectFinding(AssumptionReasoningEffort, refusal.Verdict,
                refusal.Verdict == DialectVerdict.Unmet
                    ? refusal.Detail + " A request carrying `reasoning_effort` is rejected outright here, so the key "
                      + "must not be sent to this endpoint."
                    : refusal.Detail);
        }

        return new DialectFinding(AssumptionReasoningEffort, DialectVerdict.Met,
            $"HTTP {answer.StatusCode}: `reasoning_effort` was tolerated. (Tolerated, not necessarily acted on — a "
            + "non-reasoning model ignoring an unknown key looks the same, and that is fine: the assumption is only "
            + "that sending it does not break the request.)");
    }

    // ── the shared verdict rule ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one rule every probe shares for a response that did not succeed, so the tri-state means the same
    /// thing across all seven lines:
    /// <list type="bullet">
    /// <item><b>400/422</b> ⇒ <c>unmet</c>. A clean, informative rejection is a COMPLETE answer: the server
    /// understood the request and declined this specific thing.</item>
    /// <item><b>any other non-2xx</b> (500, 503, 401 …) ⇒ <c>unknown</c>. A malfunction is not an answer,
    /// and reporting it as "unmet" would blame the assumption for the server's bad day.</item>
    /// <item><b>a transport failure</b> ⇒ <c>unknown</c>, for the same reason.</item>
    /// </list>
    /// Returns null when the response succeeded and the probe must judge it itself.
    /// </summary>
    private static (DialectVerdict Verdict, string Detail)? Refused(int? statusCode, string? transportFailure)
    {
        if (transportFailure is not null)
        {
            return (DialectVerdict.Unknown, transportFailure);
        }

        int status = statusCode!.Value;

        if (status is 400 or 422)
        {
            return (DialectVerdict.Unmet, $"HTTP {status} — the endpoint understood the request and REJECTED it.");
        }

        return status is >= 200 and <= 299
            ? null
            : (DialectVerdict.Unknown,
                $"HTTP {status} — the endpoint answered with a malfunction rather than an answer to the question, so "
                + "this assumption is undetermined rather than disproved. Re-run the check once the server is healthy.");
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Print the verdicts. Every line NAMES its assumption and carries its verdict word, because this
    /// report's only job is to be read by a human deciding whether to trust a config against real hardware.
    /// </summary>
    private static void PrintCheckReport(
        string blockName,
        PromptRunnerConfig block,
        string endpoint,
        string model,
        IReadOnlyList<DialectFinding> findings,
        IConsoleIo io)
    {
        io.Out.WriteLine();
        io.Out.WriteLine($"providers check — block '{blockName}' (kind: openai-compat)");
        io.Out.WriteLine($"  endpoint       {endpoint}");
        io.Out.WriteLine($"  model          {model}");
        io.Out.WriteLine($"  contextTokens  {block.ContextTokens?.ToString(CultureInfo.InvariantCulture) ?? "(unstated)"}");
        io.Out.WriteLine($"  authorization  {DescribeAuthorization(block)}");
        io.Out.WriteLine();
        io.Out.WriteLine("The dialect assumptions the harness makes about this wire, each probed once against the");
        io.Out.WriteLine("REAL endpoint. No loopback fake can settle these — a fake we wrote can only agree with us.");
        io.Out.WriteLine();

        foreach (DialectFinding finding in findings)
        {
            io.Out.WriteLine($"  {$"[{Tag(finding.Verdict)}]",-10} {finding.Assumption}");
            foreach (string line in Wrap(finding.Detail, ReportWidth - DetailIndent))
            {
                io.Out.WriteLine(new string(' ', DetailIndent) + line);
            }

            io.Out.WriteLine();
        }

        int met = findings.Count(f => f.Verdict == DialectVerdict.Met);
        int unmet = findings.Count(f => f.Verdict == DialectVerdict.Unmet);
        int unknown = findings.Count(f => f.Verdict == DialectVerdict.Unknown);

        io.Out.WriteLine($"  {findings.Count} assumption(s) probed: {met} met, {unmet} unmet, {unknown} unknown.");
        io.Out.WriteLine();
        io.Out.WriteLine("  The endpoint answered, so this check EXITS 0. It is a REPORT, never a gate: an assumption");
        io.Out.WriteLine("  that came back unmet or unknown is a fact about the server you pointed at, and only an");
        io.Out.WriteLine("  endpoint that cannot be reached at all is a failure of this verb. Nothing here was written");
        io.Out.WriteLine("  to disk, and nothing here runs in CI, in `guardrails run`, or in `guardrails validate`.");
    }

    /// <summary>The verdict word, as the report prints it — the vocabulary plan §8 fixes.</summary>
    private static string Tag(DialectVerdict verdict) => verdict switch
    {
        DialectVerdict.Met => "MET",
        DialectVerdict.Unmet => "UNMET",
        _ => "UNKNOWN"
    };

    /// <summary>Whether a bearer token was actually sent, and if not, why not — the two facts a 401 turns on.</summary>
    private static string DescribeAuthorization(PromptRunnerConfig block)
    {
        if (string.IsNullOrWhiteSpace(block.ApiKeyEnv))
        {
            return "(none — the block declares no `apiKeyEnv`, so no Authorization header was sent)";
        }

        return System.Environment.GetEnvironmentVariable(block.ApiKeyEnv) is { Length: > 0 }
            ? $"Bearer, from ${block.ApiKeyEnv}"
            : $"(none — the block names ${block.ApiKeyEnv} but that variable is not set in this process)";
    }

    /// <summary>Greedy word wrap, so a long explanation stays readable without a wall of horizontal scroll.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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

    // ── the wire ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A minimal chat-completion body: one user turn and a tight output cap.</summary>
    private static JsonObject ChatBody(string model, string userText) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userText }
        },
        ["max_tokens"] = 16
    };

    /// <summary>
    /// The tool-capability probe body — two messages and one no-op function. Deliberately IDENTICAL to the
    /// pre-DAG preflight's (see <see cref="ToolProbeName"/>): the two must agree about what a tool-calling
    /// endpoint is, or an operator who reads "met" here could still be halted at the start of a run.
    /// </summary>
    private static JsonObject ToolProbeBody(string model) => new()
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

    private static async Task<ProbeAnswer> GetAsync(
        HttpClient http, string uri, PromptRunnerConfig block, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute))
        {
            return ProbeAnswer.Failed(
                $"\"{uri}\" is not an absolute URL, so no request could be made. `endpoint` must be an absolute "
                + "http/https base URL, e.g. \"http://127.0.0.1:11434/v1\".");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, absolute);
            Authorize(request, block);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ProbeAnswer((int)response.StatusCode, body, null);
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return ProbeAnswer.Failed(TransportFailure(exception));
        }
    }

    private static async Task<ProbeAnswer> PostAsync(
        HttpClient http, string endpoint, JsonObject body, PromptRunnerConfig block, CancellationToken cancellationToken)
    {
        string uri = $"{endpoint}/chat/completions";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute))
        {
            return ProbeAnswer.Failed($"\"{uri}\" is not an absolute URL, so no request could be made.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, absolute)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            Authorize(request, block);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ProbeAnswer((int)response.StatusCode, text, null);
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return ProbeAnswer.Failed(TransportFailure(exception));
        }
    }

    /// <summary>
    /// A streamed completion, read frame by frame off the socket rather than buffered — which is the whole
    /// point: the framing IS the assumption under test, so a probe that let the framework hand it a whole
    /// body would be testing our own HTTP stack.
    /// </summary>
    private static async Task<StreamAnswer> PostStreamedAsync(
        HttpClient http, string endpoint, JsonObject body, PromptRunnerConfig block, CancellationToken cancellationToken)
    {
        string uri = $"{endpoint}/chat/completions";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute))
        {
            return StreamAnswer.Failed($"\"{uri}\" is not an absolute URL, so no request could be made.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, absolute)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            Authorize(request, block);

            using HttpResponseMessage response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new StreamAnswer(status, errorBody, null, 0, false, null);
            }

            int frames = 0;
            bool sawDone = false;
            string? usage = null;

            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string payload = line["data:".Length..].Trim();
                    if (payload.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                    {
                        sawDone = true;
                        continue;
                    }

                    // Only a frame we could actually DECODE counts. A `data:` line carrying something we
                    // cannot parse is a framing divergence, not evidence that the framing works.
                    if (!TryReadFrame(payload, out string? frameUsage))
                    {
                        continue;
                    }

                    frames++;
                    usage ??= frameUsage;
                }
            }

            return new StreamAnswer(status, "", null, frames, sawDone, usage);
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return StreamAnswer.Failed(TransportFailure(exception));
        }
    }

    /// <summary>
    /// The bearer token, read from the env var the block NAMES (<c>apiKeyEnv</c>) — never from the block
    /// itself, because <c>guardrails.json</c> is committed and hashed into the plan definition. Mirrors the
    /// runner's own rule so this verb probes the endpoint the way the runner will actually talk to it.
    /// </summary>
    private static void Authorize(HttpRequestMessage request, PromptRunnerConfig block)
    {
        if (string.IsNullOrWhiteSpace(block.ApiKeyEnv))
        {
            return;
        }

        string? token = System.Environment.GetEnvironmentVariable(block.ApiKeyEnv);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>The exceptions that mean "the request did not complete", as distinct from a bad answer.</summary>
    private static bool IsTransport(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or IOException => true,
            _ => false
        };

    /// <summary>One operator-facing clause naming what went wrong on the socket.</summary>
    private static string TransportFailure(Exception exception) => exception switch
    {
        HttpRequestException http => http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"DNS did not resolve its host ({http.Message}).",
            HttpRequestError.ConnectionError => $"the connection was refused or reset ({http.Message}).",
            HttpRequestError.SecureConnectionError => $"the TLS handshake failed ({http.Message}).",
            HttpRequestError.ProxyTunnelError => $"the proxy refused to tunnel the connection ({http.Message}).",
            _ => $"the request never reached it ({http.Message})."
        },
        OperationCanceledException =>
            $"it did not answer within {ProbeTimeout.TotalSeconds:F0}s.",
        _ => $"the connection failed mid-exchange ({exception.Message})."
    };

    // ── reading what came back ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>data[].id</c> values of an OpenAI-shaped model listing, or null when the body is not one —
    /// which is a DIFFERENT fact from a listing that is merely empty, and the report says so.
    /// </summary>
    private static IReadOnlyList<string>? ParseModelIds(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
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
            // An unreadable body cannot evidence a tool call.
        }

        return false;
    }

    /// <summary>
    /// What is MISSING from an OpenAI-shaped error body, or null when the shape is intact. Returning the
    /// gap rather than a bare bool is what lets the unmet line say which field an engine omitted.
    /// </summary>
    private static string? DescribeErrorShape(string body)
    {
        JsonElement error;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out JsonElement found)
                || found.ValueKind != JsonValueKind.Object)
            {
                return "it carries no `error` object";
            }

            error = found.Clone();
        }
        catch (JsonException)
        {
            return "it is not JSON at all";
        }

        bool hasMessage = error.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.String
            && message.GetString() is { Length: > 0 };

        bool hasType = error.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String;
        bool hasCode = error.TryGetProperty("code", out JsonElement code)
            && code.ValueKind is JsonValueKind.String or JsonValueKind.Number;

        if (!hasMessage && !hasType && !hasCode)
        {
            return "its `error` object carries neither `message` nor `type`/`code`";
        }

        if (!hasMessage)
        {
            return "its `error` object carries no `message`";
        }

        return hasType || hasCode ? null : "its `error` object carries neither `type` nor `code`";
    }

    /// <summary>
    /// Decode one SSE payload. Returns false when it is not a JSON object we can read (a framing
    /// divergence); on success, <paramref name="usage"/> is the frame's token counts when it carries them.
    /// </summary>
    private static bool TryReadFrame(string payload, out string? usage)
    {
        usage = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("usage", out JsonElement block)
                && block.ValueKind == JsonValueKind.Object)
            {
                usage = $"prompt_tokens {Number(block, "prompt_tokens")}, "
                        + $"completion_tokens {Number(block, "completion_tokens")}";
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Number(JsonElement holder, string name) =>
        holder.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : "(absent)";

    private static string DescribeIds(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "none at all" : string.Join(", ", ids.Select(id => $"'{id}'"));

    /// <summary>A bounded slice of a response body — enough to diagnose, never enough to flood the report.</summary>
    private static string Snippet(string body)
    {
        string trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "(the response body was empty.)";
        }

        return trimmed.Length <= 200
            ? $"Response body: {trimmed}"
            : $"Response body (first 200 chars): {trimmed[..200]}…";
    }

    /// <summary>Why a non-tool-calling endpoint matters, said once and reused across the tool-calling line.</summary>
    private const string VerifierNeedsTools =
        "An `openai-compat` block serves the VERIFIER roles (plan 28 §3.2), and a verifier reads the evidence it "
        + "judges through its Read/Glob/Grep tools — so this block cannot be served by a model that does not call "
        + "tools.";

    /// <summary>The three-way outcome plan §8 fixes. An `unknown` collapsed into `unmet` makes the report lie.</summary>
    private enum DialectVerdict
    {
        /// <summary>The probe observed the assumption holding.</summary>
        Met,

        /// <summary>The probe observed, completely and definitively, that it does NOT hold.</summary>
        Unmet,

        /// <summary>The probe could not tell either way — which is a finding, not a failure to have one.</summary>
        Unknown
    }

    /// <summary>One assumption's verdict and the evidence behind it.</summary>
    private sealed record DialectFinding(string Assumption, DialectVerdict Verdict, string Detail);

    /// <summary>A whole (non-streamed) response, or the transport failure that replaced it.</summary>
    private sealed record ProbeAnswer(int? StatusCode, string Body, string? TransportFailure)
    {
        public static ProbeAnswer Failed(string failure) => new(null, "", failure);
    }

    /// <summary>A streamed response as it arrived on the wire: how it framed, and what usage it carried.</summary>
    private sealed record StreamAnswer(
        int? StatusCode,
        string Body,
        string? TransportFailure,
        int DataFrames,
        bool SawDoneSentinel,
        string? Usage)
    {
        public static StreamAnswer Failed(string failure) => new(null, "", failure, 0, false, null);
    }
}
