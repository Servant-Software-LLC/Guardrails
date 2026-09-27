using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Bundle;

public sealed partial class BundleBuilder
{
    /// <summary>The full-bundle warning of §17.9, verbatim: stderr before the write, and SUMMARY.md's first block.</summary>
    public const string FullBundleWarning =
        "WARNING: this bundle includes your code, your prompts and model output (transcripts, stream logs, gateway\n" +
        "sessions, patches). Credentials were redacted, but redaction cannot catch everything (REDACTIONS.md, CC1-CC6).\n" +
        "If your code is private, re-run with --lean before attaching this to a public issue.";

    /// <summary>The one-line lean note (stderr, and SUMMARY.md's first block under <c>--lean</c>).</summary>
    public static string LeanNote(int withheldCount) =>
        $"Lean bundle (--lean): withheld {withheldCount} file(s) of prompts, transcripts, stream logs, gateway sessions "
        + "and patches; each is a MANIFEST.md row (reason lean).";

    private const int SummaryTextLimit = 300;

    private IReadOnlyList<BundleToolVersion>? _toolVersions;
    private RunLivenessState? _liveness;

    private (string Text, IReadOnlyList<string> Labels) RenderSummary(bool trimmed)
    {
        var labels = new List<string>();
        var s = new StringBuilder();

        // The opening lines, each only when it applies, in the §17.4 order.
        if (_options.NoRedact)
        {
            s.Append(NotRedactedLine).Append("\n\n");
        }

        if (!_options.Lean)
        {
            s.Append(FullBundleWarning).Append("\n\n");
        }
        else
        {
            s.Append(LeanNote(LeanWithheldPaths().Count)).Append("\n\n");
        }

        if (_options.WithoutAgentText)
        {
            s.Append(WithoutAgentTextStatement()).Append("\n\n");
        }

        if (trimmed)
        {
            s.Append("Trimmed to fit --max-size: see MANIFEST.md (").Append(string.Join(", ", _trimNotes)).Append(" rows).\n\n");
        }

        if (_earlierRun)
        {
            s.Append("Earlier run: this bundle is run ").Append(_runId)
                .Append(", not the journal's current run, so this SUMMARY is built from its logs alone.\n\n");
        }

        s.Append("# Guardrails run bundle: ").Append(PlanName).Append(" / ").Append(_runId).Append("\n\n");

        // 1. Bundled at.
        string environmentLine = EnvironmentLine();
        s.Append("## 1. Bundled at\n\n");
        s.Append("Bundled at: ").Append(_probes.Now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append('\n');
        foreach (string line in VersionLines())
        {
            s.Append("- ").Append(line).Append('\n');
        }

        // 2. Run.
        s.Append("\n## 2. Run\n\n");
        s.Append("- Run id: ").Append(_runId).Append('\n');
        s.Append("- Liveness: ").Append(LivenessText()).Append('\n');
        s.Append("- Plan preflight: ").Append(PhaseText(RunJournalDoc?.PlanPreflights?.Status, RunJournalDoc?.PlanPreflights?.Checks)).Append('\n');
        s.Append("- Terminal gate: ").Append(PhaseText(RunJournalDoc?.PlanGuardrails?.Status, RunJournalDoc?.PlanGuardrails?.Checks)).Append('\n');
        s.Append("- Last halt or needs-human reason: ").Append(LastHaltOrNeedsHuman(labels)).Append('\n');

        // 3. Per task.
        s.Append("\n## 3. Tasks\n");
        var inFlightLines = new List<string>();
        foreach (string taskId in _selectedTasks)
        {
            RenderTask(s, taskId, labels, inFlightLines);
        }

        // 4. Gateway and endpoint blocks.
        s.Append("\n## 4. Gateway and endpoint blocks\n\n");
        List<PromptRunnerConfig> blocks =
        [
            .. _plan.Config.PromptRunners.Values
                .Where(b => b.IsClaudeGateway || b.Kind == PromptRunnerKind.OpenAiCompat)
                .OrderBy(b => b.Name, StringComparer.Ordinal)
        ];
        if (blocks.Count == 0)
        {
            s.Append("No claude gateway or openai-compat block is declared.\n");
        }

        foreach (PromptRunnerConfig block in blocks)
        {
            RenderBlock(s, block, labels);
        }

        s.Append("\n### Redaction coverage (the bundling shell, not the run)\n\n");
        foreach (string variable in BundleD1.BlockVariables(_plan).Concat(BundleSecrets.FixedVariables)
                     .Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal))
        {
            bool set = _probes.Environment.TryGetValue(variable, out string? value) && !string.IsNullOrEmpty(value);
            s.Append("- ").Append(variable).Append(": ").Append(set ? "set" : "unset").Append('\n');
        }

        // 5. Withheld.
        s.Append("\n## 5. Withheld\n\n");
        if (_options.Lean)
        {
            s.Append("- --lean withheld ").Append(LeanWithheldPaths().Count.ToString(CultureInfo.InvariantCulture))
                .Append(" file(s): prompts, transcripts, stream logs, gateway sessions and patches (MANIFEST.md, reason lean).\n");
        }

        if (_options.WithoutAgentText)
        {
            s.Append("- ").Append(WithoutAgentTextStatement()).Append('\n');
        }

        if (!_options.Lean && !_options.WithoutAgentText)
        {
            s.Append("- Nothing was withheld by a flag.\n");
        }

        s.Append("- File names: `git status` and `git diff --stat` name files, even in a --lean bundle.\n");

        // 6. Issue skeleton.
        s.Append("\n## 6. Issue skeleton\n\n");
        s.Append("**Observed:** ");
        if (RunJournalDoc?.Halt is { } halt)
        {
            s.Append(Free(halt.Headline, labels)).Append('\n');
        }
        else
        {
            s.Append("\n\n**Candidate facts:**\n\n");
            List<string> facts = CandidateFacts(labels);
            facts.AddRange(inFlightLines);
            if (facts.Count == 0)
            {
                s.Append("- none: every selected task succeeded and nothing is in flight\n");
            }

            foreach (string fact in facts)
            {
                s.Append("- ").Append(fact).Append('\n');
            }
        }

        s.Append("\n**Expected:** \n\n**Evidence:**\n\n");
        foreach (string path in EvidencePaths())
        {
            s.Append("- ").Append(path).Append('\n');
        }

        s.Append("\n**Environment:** ").Append(environmentLine).Append('\n');
        return (s.ToString(), labels);
    }

    private string WithoutAgentTextStatement()
    {
        IReadOnlyList<string> forced = BundleD1.UnsetVariables(_plan, _probes.Environment);
        string by = forced.Count > 0 ? string.Join(", ", forced) : "none: requested explicitly";
        return $"All agent-derived free text was removed, run-wide (--without-agent-text). Variables that forced it: {by}.";
    }

    // ------------------------------------------------------------------ block 1

    private IReadOnlyList<BundleToolVersion> ToolVersions() => _toolVersions ??= _probes.ToolVersions();

    private IEnumerable<string> VersionLines()
    {
        string? recorded = RunJournalDoc?.Environment?.HarnessVersion;
        yield return $"Guardrails (this binary): {_probes.HarnessVersion}";
        yield return recorded is null
            ? "Guardrails (the run's journal): not recorded"
            : $"Guardrails (the run's journal): {recorded}"
              + (string.Equals(recorded, _probes.HarnessVersion, StringComparison.Ordinal) ? string.Empty : " (DIFFERS from this binary)");
        foreach (BundleToolVersion tool in ToolVersions())
        {
            yield return $"{tool.Tool}: {tool.Version}";
        }

        yield return $"OS (bundling machine): {_probes.BundlingOs}";
        yield return $"OS (the run): {RunJournalDoc?.Environment?.Os ?? "not recorded"}";
        yield return $"Worktree mode: {WorktreeModeText()}";
    }

    private string EnvironmentLine() => string.Join("; ", VersionLines());

    private string WorktreeModeText()
    {
        List<AttemptProvenance> provenance = [.. AllProvenance()];
        if (provenance.Count == 0)
        {
            return "unknown (no attempt provenance recorded)";
        }

        return provenance.Any(p => !string.IsNullOrWhiteSpace(p.WorktreePath)) ? "yes" : "no (serial)";
    }

    // ------------------------------------------------------------------ block 2

    private RunLivenessState? Liveness()
    {
        if (RunJournalDoc is not { } journal)
        {
            return null;
        }

        return _liveness ??= _probes.Liveness(journal.Owner);
    }

    private string LivenessText()
    {
        if (Liveness() is not { } state)
        {
            return "not assessed: an earlier run, which state/run.json does not describe";
        }

        RunOwner? owner = RunJournalDoc!.Owner;
        return state switch
        {
            RunLivenessState.Running => $"Running (owner process {owner?.Pid} is alive)",
            RunLivenessState.ExitedWithoutFinishing =>
                $"ExitedWithoutFinishing (owner process {owner?.Pid} is gone and never recorded an end; nothing is running)",
            RunLivenessState.Ended => $"Ended (the owner recorded its end at {Timestamp(owner?.FinishedAt)})",
            RunLivenessState.OnAnotherHost =>
                $"OnAnotherHost (owner process {owner?.Pid} ran on another host; its liveness can only be checked there)",
            RunLivenessState.CannotCheck =>
                $"CannotCheck (owner process {owner?.Pid} could not be checked from here)",
            _ => "NotRecorded (the journal names no owner process, so a live run and a dead one look the same)",
        };
    }

    private static string PhaseText(PlanPhaseStatus? status, IReadOnlyList<PlanPreflightCheck>? checks)
    {
        if (status is null)
        {
            return "not recorded";
        }

        int count = checks?.Count ?? 0;
        int failed = checks?.Count(c => !c.Passed) ?? 0;
        string token = JsonNamingPolicy.KebabCaseLower.ConvertName(status.Value.ToString());
        return $"{token} ({count} check(s), {failed} failed)";
    }

    private string LastHaltOrNeedsHuman(List<string> labels)
    {
        if (_earlierRun)
        {
            return "unknown: no journal describes this run";
        }

        if (RunJournalDoc?.Halt is { } halt)
        {
            IEnumerable<string> checks = halt.FailedChecks.Where(c => c is not null).Select(c => $"{c.Name}: {Free(c.Reason, labels)}");
            string failedChecks = string.Join("; ", checks);
            return Free(halt.Headline, labels) + (failedChecks.Length > 0 ? $" — failed checks: {failedChecks}" : string.Empty);
        }

        var needsHuman = RunJournalDoc!.Tasks
            .Where(p => p.Value.Status == JournalTaskStatus.NeedsHuman && p.Value.Attempts.Count > 0)
            .OrderByDescending(p => p.Value.Attempts[^1].EndedAt)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (needsHuman.Value is { } entry)
        {
            return $"{needsHuman.Key}: {Free(NeedsHumanReason(needsHuman.Key, entry), labels)}";
        }

        return "none";
    }

    private string? NeedsHumanReason(string taskId, TaskJournalEntry entry)
    {
        AttemptRecord last = entry.Attempts[^1];
        if (last.FailedGuardrails.Count > 0)
        {
            return string.Join("; ", last.FailedGuardrails.Select(f => $"{f.Name}: {f.Reason}"));
        }

        return ActionSummary(taskId, last.Attempt) ?? last.NeedsHumanKind ?? "needs-human (no reason recorded)";
    }

    // ------------------------------------------------------------------ block 3

    private void RenderTask(StringBuilder s, string taskId, List<string> labels, List<string> inFlightLines)
    {
        TaskJournalEntry? entry = RunJournalDoc?.Tasks.GetValueOrDefault(taskId);
        TaskNode? task = _plan.Tasks.FirstOrDefault(t => t.Id == taskId);
        s.Append("\n### ").Append(taskId).Append("\n\n");
        s.Append("- Status: ").Append(entry is null ? (_earlierRun ? "unknown (no journal for this run)" : "not in the journal")
            : JsonNamingPolicy.KebabCaseLower.ConvertName(entry.Status.ToString())).Append('\n');
        s.Append("- Definition drift: ").Append(DriftText(task, entry)).Append('\n');

        List<int> attempts = [.. AttemptsOnDisk(taskId)];
        var journaled = (entry?.Attempts ?? []).ToDictionary(a => a.Attempt);
        if (attempts.Count > 0)
        {
            s.Append("\n| Attempt | Outcome | Duration | Exit code | Summary | Model requested / served |\n");
            s.Append("|---|---|---|---|---|---|\n");
        }

        foreach (int attempt in attempts)
        {
            journaled.TryGetValue(attempt, out AttemptRecord? record);
            bool isMarker = entry?.InFlightAttempt?.Attempt == attempt;
            if (record is null && (isMarker || RunJournalDoc is not null))
            {
                // Not journaled: the in-flight attempt, reported below.
                continue;
            }

            AttemptProvenance? provenance = record?.Provenance ?? ProvenanceOf(taskId, attempt);
            string outcome = record is null ? "unknown (no journal)" : JournalJson.OutcomeToken(record.Outcome);
            string duration = record is null ? "-" : Duration(record.EndedAt - record.StartedAt);
            string exit = record?.ActionExitCode?.ToString(CultureInfo.InvariantCulture) ?? ExitCodeFromDisk(taskId, attempt) ?? "-";
            s.Append("| ").Append(attempt.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(outcome)
                .Append(" | ").Append(duration)
                .Append(" | ").Append(exit)
                .Append(" | ").Append(Cell(Free(ActionSummary(taskId, attempt), labels)))
                .Append(" | ").Append(Cell(ModelText(provenance)))
                .Append(" |\n");
        }

        string? inFlight = InFlightText(taskId, entry, attempts);
        if (inFlight is not null)
        {
            s.Append("\n- In flight: ").Append(inFlight).Append('\n');
            inFlightLines.Add($"{taskId}: {inFlight}");
        }
    }

    private static string DriftText(TaskNode? task, TaskJournalEntry? entry)
    {
        if (task?.DefinitionHashAtLoad is not { } current || entry?.DefinitionHash is not { } recorded)
        {
            return "unknown (no definitionHash to compare)";
        }

        return string.Equals(current, recorded, StringComparison.Ordinal)
            ? "none (task.json matches the journal's definitionHash)"
            : "CHANGED since the run recorded its definitionHash";
    }

    // #798: the marker when present; otherwise inferred from disk vs journal. Both are contract.
    private string? InFlightText(string taskId, TaskJournalEntry? entry, List<int> attemptsOnDisk)
    {
        if (entry?.InFlightAttempt is { } marker)
        {
            return $"attempt-{marker.Attempt} (phase {marker.Phase}, started {Timestamp(marker.StartedAt)}; from run.json inFlightAttempt; "
                   + $"liveness: {Liveness()})";
        }

        if (RunJournalDoc is null)
        {
            return null;
        }

        var journaled = (entry?.Attempts ?? []).Select(a => a.Attempt).ToHashSet();
        List<int> unlisted = [.. attemptsOnDisk.Where(n => !journaled.Contains(n))];
        if (unlisted.Count == 0)
        {
            return null;
        }

        return string.Join("; ", unlisted.Select(n =>
            $"attempt-{n}/ exists on disk and is not in the journal: in flight, or the run died during it (liveness: {Liveness()})"));
    }

    private static string ModelText(AttemptProvenance? provenance)
    {
        if (provenance?.Model is null && provenance?.RequestedModel is null)
        {
            return "-";
        }

        string requested = provenance.RequestedModel ?? provenance.Model ?? "-";
        string served = provenance.Model ?? "-";
        if (provenance.BackendModel is { } backend)
        {
            served += $" (backend {backend})";
        }

        return $"{requested} / {served}";
    }

    private string? ActionSummary(string taskId, int attempt) =>
        ActionResultField(taskId, attempt, "summary");

    private string? ExitCodeFromDisk(string taskId, int attempt) => ActionResultField(taskId, attempt, "exitCode");

    private string? ActionResultField(string taskId, int attempt, string field)
    {
        Entry? file = _entries.FirstOrDefault(e =>
            e.TaskId == taskId && e.Attempt == attempt && e.Kind?.Kind == "action-result" && e.Raw is not null);
        if (file is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(file.Raw!);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(field, out JsonElement value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            }
        }
        catch (JsonException)
        {
            // An unparsable result is reported as no summary.
        }

        return null;
    }

    // ------------------------------------------------------------------ block 4

    private void RenderBlock(StringBuilder s, PromptRunnerConfig block, List<string> labels)
    {
        bool gateway = block.IsClaudeGateway;
        s.Append("### ").Append(block.Name).Append(gateway ? " (claude gateway)" : " (openai-compat)").Append("\n\n");
        s.Append("- baseUrl: ").Append(Free(gateway ? block.BaseUrl : block.Endpoint, labels, agentText: false)).Append('\n');
        s.Append("- Model: ").Append(block.Settings.Model ?? "-").Append('\n');
        s.Append("- Backend identity: ").Append(BackendIdentity(block)).Append('\n');
        s.Append("- Token in the run: ").Append(gateway ? GatewayToken(block) : EndpointToken(block)).Append("\n\n");
    }

    private IEnumerable<AttemptProvenance> ProvenanceFor(PromptRunnerConfig block) =>
        AllProvenance().Where(p => string.Equals(p.Runner, block.Name, StringComparison.Ordinal));

    private string BackendIdentity(PromptRunnerConfig block)
    {
        string? backend = ProvenanceFor(block).Select(p => p.BackendModel).LastOrDefault(b => b is not null);
        if (backend is null)
        {
            string? served = ProvenanceFor(block).Select(p => p.Model).LastOrDefault(m => m is not null);
            return served is null ? "not recorded (no attempt through this block)" : $"not verified (served model as echoed: {served})";
        }

        return string.Equals(backend, "unverified", StringComparison.Ordinal) ? "unverified" : $"verified: {backend}";
    }

    private string GatewayToken(PromptRunnerConfig block)
    {
        if (block.AuthTokenEnv is not { } variable)
        {
            return $"the placeholder `{ClaudeGatewayEnvironment.PlaceholderToken}` was sent (the block names no authTokenEnv)";
        }

        if (RecordedTexts().Any(t => t.Contains($"authTokenEnv names '{variable}', which is unset or empty", StringComparison.Ordinal)))
        {
            return $"`{variable}` was NOT set: a gateway launch recorded the refusal";
        }

        if (ProvenanceFor(block).Any(p => p.Gateway is not null))
        {
            return $"`{variable}` was set: a gateway attempt launched, and a gateway launch refuses when it is unset";
        }

        return "unknown";
    }

    private string EndpointToken(PromptRunnerConfig block)
    {
        if (block.ApiKeyEnv is not { } variable)
        {
            return "no apiKeyEnv: no Authorization header was sent";
        }

        List<string> texts = [.. RecordedTexts()];
        if (texts.Any(t => t.Contains($"`apiKeyEnv`: \"{variable}\", and that variable WAS set", StringComparison.Ordinal)))
        {
            return $"`{variable}` was set: a recorded 401 diagnosis says a bearer token was sent";
        }

        if (texts.Any(t => t.Contains($"`apiKeyEnv`: \"{variable}\", and that variable was NOT set", StringComparison.Ordinal)))
        {
            return $"`{variable}` was NOT set: a recorded 401 diagnosis says no Authorization header was sent";
        }

        return "unknown";
    }

    // The recorded text a token fact can be read from: action results, failed-guardrail reasons, the halt.
    private IEnumerable<string> RecordedTexts()
    {
        foreach (Entry entry in _entries.Where(e => e.Kind?.Kind == "action-result" && e.Raw is not null))
        {
            yield return Encoding.UTF8.GetString(entry.Raw!);
        }

        if (RunJournalDoc is { } journal)
        {
            foreach (FailedGuardrail failed in journal.Tasks.Values.SelectMany(t => t.Attempts).SelectMany(a => a.FailedGuardrails))
            {
                yield return failed.Reason;
            }

            if (journal.Halt is { } halt)
            {
                yield return halt.Headline;
                foreach (FailedGuardrail failed in halt.FailedChecks)
                {
                    yield return failed.Reason;
                }
            }
        }
    }

    // ------------------------------------------------------------------ block 6

    private List<string> CandidateFacts(List<string> labels)
    {
        var facts = new List<string>();
        foreach (string taskId in _selectedTasks)
        {
            TaskJournalEntry? entry = RunJournalDoc?.Tasks.GetValueOrDefault(taskId);
            if (entry is null || entry.Status == JournalTaskStatus.Succeeded || entry.Attempts.Count == 0)
            {
                continue;
            }

            AttemptRecord last = entry.Attempts[^1];
            facts.Add($"{taskId}: {JsonNamingPolicy.KebabCaseLower.ConvertName(entry.Status.ToString())}; last attempt "
                      + $"{last.Attempt} {JournalJson.OutcomeToken(last.Outcome)}: {Free(ActionSummary(taskId, last.Attempt), labels)}");
        }

        return facts;
    }

    private IEnumerable<string> EvidencePaths()
    {
        var paths = new List<string> { "SUMMARY.md", "MANIFEST.md", "REDACTIONS.md" };
        foreach (string fixedPath in new[] { "state/run.json", PlanDir + "validate.txt", PlanDir + "guardrails.json" })
        {
            if (_entries.Any(e => e.HasContent && e.BundlePath == fixedPath))
            {
                paths.Add(fixedPath);
            }
        }

        List<string> focus =
        [
            .. _selectedTasks.Where(t => RunJournalDoc?.Tasks.GetValueOrDefault(t) is not { Status: JournalTaskStatus.Succeeded })
        ];
        foreach (string taskId in focus)
        {
            int? latest = AttemptsOnDisk(taskId).Cast<int?>().LastOrDefault();
            paths.AddRange(_entries
                .Where(e => e.HasContent && e.TaskId == taskId && (e.Attempt is null || e.Attempt == latest) && e.BundlePath!.StartsWith("tasks/", StringComparison.Ordinal))
                .Select(e => e.BundlePath!)
                .OrderBy(p => p, StringComparer.Ordinal));
        }

        return paths.Distinct(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ free text

    /// <summary>
    /// A free-text field rendered into SUMMARY.md: withheld under <c>--without-agent-text</c>, otherwise redacted (its
    /// labels counted in REDACTIONS.md), one line, and cut at a length that never splits a label.
    /// </summary>
    private string Free(string? text, List<string> labels, bool agentText = true)
    {
        if (text is null)
        {
            return "-";
        }

        if (agentText && _options.WithoutAgentText)
        {
            return AgentTextProjection.Withheld;
        }

        string oneLine = WhitespaceRun().Replace(text, " ").Trim();
        string redacted;
        if (_options.NoRedact)
        {
            redacted = _anonymizer is null ? oneLine : _anonymizer.Apply(oneLine);
        }
        else
        {
            BundleRedactionResult result = BundleRedactor.Redact(oneLine, ContextFor("SUMMARY.md"));
            labels.AddRange(result.Labels);
            redacted = result.Text;
        }

        return Truncate(redacted);
    }

    private static string Truncate(string text)
    {
        if (text.Length <= SummaryTextLimit)
        {
            return text;
        }

        int cut = SummaryTextLimit;
        int open = text.LastIndexOf("[REDACTED:", cut, StringComparison.Ordinal);
        if (open >= 0 && text.IndexOf(']', open) >= cut)
        {
            cut = open;
        }

        return text[..cut].TrimEnd() + " …";
    }

    private static string Timestamp(DateTimeOffset? at) =>
        at?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "-";

    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            return "-";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m {span.Seconds:00}s"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s" : $"{span.Seconds}s";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
