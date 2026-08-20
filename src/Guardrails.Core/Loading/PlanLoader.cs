using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// Loads a plan folder from disk into a <see cref="PlanDefinition"/> (SSOT §1–§4).
/// Responsible for the *structural* concerns: parsing JSON (comments + trailing commas
/// allowed), discovering action files by convention, and resolving guardrails while
/// distinguishing metadata sidecars from real guardrails. Semantic checks (the DAG,
/// interpreter probing) live in <see cref="PlanValidator"/>.
/// </summary>
public sealed class PlanLoader
{
    private const string ConfigFileName = "guardrails.json";
    private const string TasksDirName = "tasks";
    private const string TaskManifestName = "task.json";
    private const string GuardrailsDirName = "guardrails";
    private const string PreflightsDirName = "preflights";
    private const string PromptExtension = ".prompt.md";
    private const string ActionFilePrefix = "action.";

    /// <summary>
    /// The wave-directory convention (SSOT §14.1, Open Decision F): <c>wave-</c>, a numeric prefix (group 1,
    /// load-bearing — drives the strict total order), a hyphen, then a kebab slug (group 2). Anchored.
    /// Owned by <see cref="WaveFolder"/> so detection here and wave-TARGET resolution (issue #472) can
    /// never disagree about what a wave folder is.
    /// </summary>
    private static readonly Regex WaveDirPattern = WaveFolder.DirectoryPattern;

    /// <summary>
    /// Plan-root subdirectories that are NOT waves and must not be mistaken for a non-conforming wave dir
    /// (GR2033). These are the harness/runtime folders that legitimately sit alongside the wave dirs.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownPlanRootFolders =
        new HashSet<string>(StringComparer.Ordinal) { "state", "logs", "guardrails", "preflights", "captured", "tasks" };

    /// <summary>Load the plan rooted at <paramref name="planDirectory"/>.</summary>
    public PlanLoadResult Load(string planDirectory)
    {
        var diagnostics = new List<Diagnostic>();
        string planDir = Path.GetFullPath(planDirectory);

        if (!Directory.Exists(planDir))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, planDir, "Plan folder does not exist."));
            return new PlanLoadResult { Diagnostics = diagnostics };
        }

        string configPath = Path.Combine(planDir, ConfigFileName);
        RunConfig? config = LoadConfig(planDir, configPath, diagnostics);
        if (config is null)
        {
            return new PlanLoadResult { Diagnostics = diagnostics };
        }

        LoadTasksOrWaves(planDir, config, diagnostics, out IReadOnlyList<TaskNode> tasks, out IReadOnlyList<WaveNode> waves);

        // Plan-level preflights/guardrails folders (SSOT §1/§4) sit at the plan ROOT, siblings of
        // tasks/. They reuse the SAME guardrail-file parser as a task's guardrails/ — they differ only
        // in WHERE they live and WHEN they run — but the `catches:` declaration is enforced here
        // (GR2027), the canonical malformed-declaration diagnostic for the four-folder model.
        IReadOnlyList<GuardrailDefinition> planPreflights =
            LoadGuardrailsFromFolder(Path.Combine(planDir, PreflightsDirName), diagnostics, enforceCatches: true);
        IReadOnlyList<GuardrailDefinition> planGuardrails =
            LoadGuardrailsFromFolder(Path.Combine(planDir, GuardrailsDirName), diagnostics, enforceCatches: true);

        string workspace = Path.GetFullPath(Path.Combine(planDir, config.Workspace));

        var plan = new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = config,
            Tasks = tasks,
            Waves = waves,
            Workspace = workspace,
            PlanPreflights = planPreflights,
            PlanGuardrails = planGuardrails
        };

        return new PlanLoadResult { Plan = plan, Diagnostics = diagnostics };
    }

    // --- guardrails.json --------------------------------------------------------------

    private static RunConfig? LoadConfig(string planDir, string configPath, List<Diagnostic> diagnostics)
    {
        if (!File.Exists(configPath))
        {
            // Issue #472: pointing a verb at a WAVE folder is a common, reasonable mistake — the
            // /guardrails-review flow reviews one wave at a time — and a bare GR1001 ("guardrails.json is
            // required") is a dead end, because a wave has no config BY DESIGN (§14.1, one shared run
            // config). Still an ERROR: a wave is not independently loadable and silently validating
            // something other than what was asked would be worse. But it names the parent plan root and
            // the invocation that does work.
            diagnostics.Add(WaveFolder.TryResolveWaveTarget(planDir, out string planRoot, out string waveDir)
                ? Error(DiagnosticCodes.WaveFolderIsNotALoadablePlan, planDir,
                    $"'{waveDir}' is a WAVE of the plan at '{planRoot}', not a plan in its own right — a wave " +
                    $"carries no {ConfigFileName} by design (SSOT §14.1: ONE shared run config). Validate the " +
                    $"whole plan instead: `guardrails validate {planRoot}` (validate/plan/graph are wave-aware). " +
                    $"To stamp or hash just this wave, `guardrails plan-hash {Path.Combine(planRoot, waveDir)}` " +
                    "and `guardrails mark-reviewed <that same path>` DO accept a wave folder (SSOT §13).")
                : Error(DiagnosticCodes.MissingFile, configPath, $"{ConfigFileName} is required but was not found."));
            return null;
        }

        RawRunConfig? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawRunConfig>(File.ReadAllText(configPath), PlanJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, configPath, $"Could not parse {ConfigFileName}: {ex.Message}"));
            return null;
        }

        if (raw is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, configPath, $"{ConfigFileName} is empty or null."));
            return null;
        }

        if (raw.Version is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingRequiredField, configPath, "Required field 'version' is missing."));
            return null;
        }

        GuardrailMode mode = GuardrailMode.FailFast;
        if (raw.GuardrailMode is not null && !TryParseGuardrailMode(raw.GuardrailMode, out mode))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidFieldValue, configPath,
                $"Unknown guardrailMode '{raw.GuardrailMode}'. Expected 'failFast' or 'runAll'."));
            return null;
        }

        AutonomyPolicy autonomyPolicy = AutonomyPolicy.Prompt;
        if (raw.AutonomyPolicy is not null && !AutonomyPolicies.TryParse(raw.AutonomyPolicy, out autonomyPolicy))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidAutonomyPolicy, configPath,
                $"Unknown autonomyPolicy '{raw.AutonomyPolicy}'. Expected 'prompt' (default), 'halt', or 'auto' (SSOT §2.1/§7.2)."));
            return null;
        }

        PromptRunnersResult runners;
        try
        {
            runners = ReadPromptRunners(raw.PromptRunners, configPath, diagnostics);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, configPath,
                $"Could not parse promptRunners in {ConfigFileName}: {ex.Message}"));
            return null;
        }

        var interpreters = (raw.Interpreters ?? [])
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<string>)kvp.Value,
                StringComparer.OrdinalIgnoreCase);

        AutonomyConfig? autonomy = MapAutonomy(raw.Autonomy);
        TieringConfig? tiering = MapTiering(raw.Tiering);

        return new RunConfig
        {
            Version = raw.Version.Value,
            MaxParallelism = raw.MaxParallelism ?? 3,
            DefaultRetries = raw.DefaultRetries ?? 2,
            // #477: stays NULLABLE — an omitted key must remain distinguishable from any recorded count,
            // because "intent not recorded" is what makes GR2062 skip rather than fire on every legacy plan.
            IntendedWaves = raw.IntendedWaves,
            MaxCostUsd = raw.MaxCostUsd,
            DefaultTimeoutSeconds = raw.DefaultTimeoutSeconds ?? 1800,
            TransientPauseBudgetSeconds = raw.TransientPauseBudgetSeconds ?? 14400,
            GuardrailMode = mode,
            Workspace = string.IsNullOrWhiteSpace(raw.Workspace) ? ".." : raw.Workspace,
            WorktreeRoot = string.IsNullOrWhiteSpace(raw.WorktreeRoot) ? null : raw.WorktreeRoot.Trim(),
            RunOnCurrentBranch = raw.RunOnCurrentBranch ?? false,
            // #340: mergeOnSuccess defaults ON — a wholly-green run delivers by default ("green means
            // delivered"). The raw value stays nullable (RawManifests.MergeOnSuccess) so an OMITTED key
            // (→ true default) is distinguishable from an explicit value; the presence is preserved on
            // MergeOnSuccessExplicit for the CLI's one-time delivered-by-default notice.
            MergeOnSuccess = raw.MergeOnSuccess ?? true,
            MergeOnSuccessExplicit = raw.MergeOnSuccess,
            TriageAutoFile = raw.TriageAutoFile ?? false,
            AutonomyPolicy = autonomyPolicy,
            Autonomy = autonomy,
            Tiering = tiering,
            // #360 §14.4/§14.10: between-wave breakdown auto-invocation, DEFAULT true and decoupled from
            // autonomyPolicy. An omitted key resolves the true default (a present brief.md auto-fires the
            // JIT-checkpoint breakdown); set false to restore the #368 autonomyPolicy-gated invocation.
            AutoBreakdown = raw.AutoBreakdown ?? true,
            PreserveAttemptsForSalvage = raw.PreserveAttemptsForSalvage ?? true,
            Interpreters = interpreters,
            PromptRunnerNames = runners.Names,
            DefaultPromptRunner = runners.Default,
            PromptRunners = runners.Runners
        };
    }

    /// <summary>
    /// Map the optional <c>tiering</c> block (SSOT §2/§3, issue #225). Absent ⇒ <c>null</c>: NO plan-wide
    /// default exists, so every untagged task keeps a <c>null</c> tier. Nothing is substituted for an absent
    /// block — a hard-coded fallback would silently tier a single-model user's plan, the one thing the
    /// charter's gate forbids. A present <c>defaultTier</c> is carried VERBATIM (no trim, no case-fold) so an
    /// unrecognized value reaches the validator's GR2043 check as written. The optional
    /// <c>verifier</c> sub-block (DoR §6.5.1) maps the same way, for the same reason: its
    /// <c>minTier</c> is a FLOOR whose token is judged by the validator, never normalized here.
    /// </summary>
    private static TieringConfig? MapTiering(RawTieringConfig? raw) =>
        raw is null
            ? null
            : new TieringConfig
            {
                DefaultTier = raw.DefaultTier,
                Verifier = raw.Verifier is null
                    ? null
                    : new TieringVerifierConfig { MinTier = raw.Verifier.MinTier }
            };

    /// <summary>
    /// Map the raw <c>autonomy</c> block (issue #361, doc 12 §3.3–§3.5) onto <see cref="AutonomyConfig"/>. A
    /// config WITHOUT the block loads inertly — <c>null</c> ⇒ the dial is off, the doc 12 §3.2 back-compat
    /// guarantee. A PRESENT block (even <c>{}</c>) binds a non-null instance, resolving the decided defaults
    /// (§10 I/N) for any omitted field: <c>escalationThreshold: high</c>, <c>blockerRetry { maxAttempts: 5,
    /// totalWaitSeconds: 900 }</c>, <c>maxJudgeWidenings: 3</c>. This block COMPOSES with — and never
    /// redefines — <c>autonomyPolicy</c> (parsed separately, above). Value VALIDATION (GR2039 for an
    /// unrecognized threshold/gate value, GR2040 for the forbidden compound) is a SEPARATE task: an
    /// unrecognized value here falls back to the dial/default rather than being reported.
    /// </summary>
    private static AutonomyConfig? MapAutonomy(RawAutonomyConfig? raw)
    {
        if (raw is null)
        {
            return null; // block absent → inert dial (back-compat); no throw, so today's configs still load.
        }

        return new AutonomyConfig
        {
            EscalationThreshold = raw.EscalationThreshold is not null &&
                                  EscalationThresholds.TryParse(raw.EscalationThreshold, out EscalationThreshold t)
                ? t
                : EscalationThreshold.High,
            GateThresholds = MapGateThresholds(raw.GateThresholds),
            BlockerRetry = MapBlockerRetry(raw.BlockerRetry),
            MaxJudgeWidenings = raw.MaxJudgeWidenings ?? 3
        };
    }

    /// <summary>
    /// Map the optional <c>autonomy.gateThresholds</c> map (doc 12 §3.5). Absent ⇒ <c>null</c> (no overrides
    /// at all). Present ⇒ a <see cref="GateThresholds"/> whose members are the parsed per-gate overrides;
    /// <c>needs-human</c>/<c>wave-checkpoint</c> are criticality levels, <c>review-gate</c> is the
    /// escalate/<c>proceed-unreviewed</c> acknowledgment (NOT a criticality level). A gate key that is absent
    /// (or, pending GR2039, holds an unrecognized value) leaves that member <c>null</c> ⇒ it falls back to the
    /// run-wide dial.
    /// </summary>
    private static GateThresholds? MapGateThresholds(Dictionary<string, string>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return new GateThresholds
        {
            NeedsHuman = ParseGateThreshold(raw, "needs-human"),
            WaveCheckpoint = ParseGateThreshold(raw, "wave-checkpoint"),
            ReviewGate = ParseReviewGate(raw, "review-gate")
        };
    }

    /// <summary>Parse a criticality-level gate override; null when the key is absent or unrecognized.</summary>
    private static EscalationThreshold? ParseGateThreshold(Dictionary<string, string> gates, string key) =>
        TryGetGate(gates, key, out string? value) &&
        value is not null &&
        EscalationThresholds.TryParse(value, out EscalationThreshold threshold)
            ? threshold
            : null;

    /// <summary>Parse the <c>review-gate</c> acknowledgment; null when the key is absent or unrecognized.</summary>
    private static ReviewGateDecision? ParseReviewGate(Dictionary<string, string> gates, string key) =>
        TryGetGate(gates, key, out string? value) &&
        value is not null &&
        ReviewGateDecisions.TryParse(value, out ReviewGateDecision decision)
            ? decision
            : null;

    /// <summary>Case-insensitive lookup into the raw gate map (the wire keys are kebab-case, e.g. <c>needs-human</c>).</summary>
    private static bool TryGetGate(Dictionary<string, string> gates, string key, out string? value)
    {
        foreach (KeyValuePair<string, string> gate in gates)
        {
            if (string.Equals(gate.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = gate.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Map the optional <c>autonomy.blockerRetry</c> sub-block (doc 12 §4.2). Absent ⇒ the decided defaults
    /// (<c>{ maxAttempts: 5, totalWaitSeconds: 900 }</c>, §10 I); present ⇒ each field defaults independently.
    /// </summary>
    private static BlockerRetry MapBlockerRetry(RawBlockerRetry? raw)
    {
        if (raw is null)
        {
            return new BlockerRetry();
        }

        return new BlockerRetry
        {
            MaxAttempts = raw.MaxAttempts ?? 5,
            TotalWaitSeconds = raw.TotalWaitSeconds ?? 900
        };
    }

    private static bool TryParseGuardrailMode(string value, out GuardrailMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "failfast":
                mode = GuardrailMode.FailFast;
                return true;
            case "runall":
                mode = GuardrailMode.RunAll;
                return true;
            default:
                mode = GuardrailMode.FailFast;
                return false;
        }
    }

    /// <summary>
    /// Parse the <c>promptRunners</c> map (SSOT §2/§9): a <c>"default"</c> string pointer plus
    /// one config object per named runner. Each runner's settings get documented defaults; a
    /// <c>guardrailOverrides</c> sub-block is a partial override (only present keys override).
    ///
    /// <para>Takes <paramref name="diagnostics"/> because the Stage 1 schema surface (issue #224 — the
    /// <c>kind</c> discriminator, the three axes, the retired <c>routing.rank</c>) can only be judged
    /// HERE: by the time a <see cref="PromptRunnerConfig"/> exists the unrecognised token has already been
    /// normalized away, so a later validator pass could not see it without re-reading the file. A bad
    /// value is REPORTED and the block keeps loading with the documented default — not to let the run
    /// proceed (an error blocks it), but so one <c>guardrails validate</c> reports every problem in the
    /// config rather than one per invocation.</para>
    /// </summary>
    private static PromptRunnersResult ReadPromptRunners(
        JsonElement? promptRunners, string configPath, List<Diagnostic> diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var runners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal);
        string? defaultRunner = null;

        if (promptRunners is not { ValueKind: JsonValueKind.Object } element)
        {
            return new PromptRunnersResult(names, defaultRunner, runners);
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name == "default")
            {
                defaultRunner = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                continue;
            }

            names.Add(property.Name);

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                RawPromptRunner raw = property.Value.Deserialize<RawPromptRunner>(PlanJson.Options)!;
                runners[property.Name] = BuildRunnerConfig(property.Name, raw, configPath, diagnostics);
            }
        }

        return new PromptRunnersResult(names, defaultRunner, runners);
    }

    private static PromptRunnerConfig BuildRunnerConfig(
        string name, RawPromptRunner raw, string configPath, List<Diagnostic> diagnostics)
    {
        var settings = new PromptRunnerSettings
        {
            PermissionMode = string.IsNullOrWhiteSpace(raw.PermissionMode) ? "acceptEdits" : raw.PermissionMode,
            AllowedTools = raw.AllowedTools is null ? [] : [.. raw.AllowedTools],
            MaxTurns = raw.MaxTurns ?? 50,
            Model = raw.Model,
            ExtraArgs = raw.ExtraArgs is null ? [] : [.. raw.ExtraArgs],
            MaxOutputTokens = raw.MaxOutputTokens ?? PromptRunnerSettings.DefaultMaxOutputTokens,
            Env = raw.Env is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(raw.Env, StringComparer.Ordinal)
        };

        PromptRunnerOverrides? overrides = raw.GuardrailOverrides is null
            ? null
            : new PromptRunnerOverrides
            {
                PermissionMode = raw.GuardrailOverrides.PermissionMode,
                AllowedTools = raw.GuardrailOverrides.AllowedTools is null ? null : [.. raw.GuardrailOverrides.AllowedTools],
                MaxTurns = raw.GuardrailOverrides.MaxTurns,
                Model = raw.GuardrailOverrides.Model,
                ExtraArgs = raw.GuardrailOverrides.ExtraArgs is null ? null : [.. raw.GuardrailOverrides.ExtraArgs],
                MaxOutputTokens = raw.GuardrailOverrides.MaxOutputTokens,
                Env = raw.GuardrailOverrides.Env is null
                    ? null
                    : new Dictionary<string, string>(raw.GuardrailOverrides.Env, StringComparer.Ordinal)
            };

        return new PromptRunnerConfig
        {
            Name = name,
            Command = string.IsNullOrWhiteSpace(raw.Command) ? name : raw.Command,
            Settings = settings,
            Kind = ReadKind(name, raw.Kind, configPath, diagnostics),
            Effort = raw.Effort,
            Costly = ReadCostly(name, raw.Costly, configPath, diagnostics),
            Strength = ReadStrength(name, raw.Strength, configPath, diagnostics),
            Specialization = ReadSpecialization(name, raw.Specialization, configPath, diagnostics),
            Routing = ReadRouting(name, raw.Routing, configPath, diagnostics),
            GuardrailOverrides = overrides
        };
    }

    /// <summary>
    /// The <c>kind</c> discriminator (SSOT §9, issue #224). ABSENT ⇒ <c>claude</c> — the additive
    /// guarantee: every config written before the discriminator existed is implicitly Claude and loads
    /// unchanged. An UNRECOGNISED token is an error NAMING the value (an operator with several blocks
    /// should not have to hunt for which one is wrong); the block then falls back to the default purely so
    /// the remaining checks still run — the error itself blocks the run.
    ///
    /// <para>This half owns UNRECOGNISED only. A token that IS recognised but has no runner class in this
    /// build (everything except <c>claude</c> until #223) is the same GR2044, reported by
    /// <c>PlanValidator.ValidatePromptRunnerKindsImplemented</c> — see there for why the two halves live
    /// apart, and why registry construction stays the backstop rather than the gate.</para>
    /// </summary>
    private static PromptRunnerKind ReadKind(
        string name, string? rawKind, string configPath, List<Diagnostic> diagnostics)
    {
        if (rawKind is null)
        {
            return PromptRunnerKinds.Default;
        }

        if (PromptRunnerKinds.TryParse(rawKind, out PromptRunnerKind kind))
        {
            return kind;
        }

        diagnostics.Add(Error(DiagnosticCodes.InvalidPromptRunnerKind, configPath,
            $"promptRunners.{name}.kind '{rawKind}' is not a recognised runner kind; expected one of " +
            $"{PromptRunnerKinds.TokenList} — 'claude' is the default when the key is omitted (SSOT §9)."));
        return PromptRunnerKinds.Default;
    }

    /// <summary>
    /// Axis 1 of 3 — <c>costly</c> (SSOT §9). TRI-STATE: absent ⇒ <c>null</c> ("not stated"), which is
    /// deliberately distinct from an explicit <c>false</c> ("stated to be cheap"). A present non-boolean
    /// (the classic <c>"yes"</c>) is an error naming the axis, not a silent drop.
    /// </summary>
    private static bool? ReadCostly(
        string name, JsonElement? rawCostly, string configPath, List<Diagnostic> diagnostics)
    {
        if (AbsentAxis(rawCostly, out JsonElement value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                diagnostics.Add(Error(DiagnosticCodes.InvalidRunnerAxis, configPath,
                    $"promptRunners.{name}.costly must be a boolean (true or false), but was " +
                    $"{DescribeJson(value)}. Omit the key entirely to leave the axis unstated (SSOT §9)."));
                return null;
        }
    }

    /// <summary>
    /// Axis 2 of 3 — <c>strength</c> (SSOT §9): relative capability, higher = stronger, and the ORDERING
    /// key (ascending, so the weakest model that can serve a tier goes first). Absent ⇒ <c>null</c>. A
    /// present non-integer is reported here; the <c>&gt;= 1</c> RANGE check is
    /// <c>PlanValidator.ValidatePromptRunnerAxes</c>, which sits with the other optional-positive checks
    /// and reads the parsed value — a value that binds as an integer is well-formed enough to be carried
    /// verbatim into the diagnostic (the GR2030 doctrine).
    /// </summary>
    private static int? ReadStrength(
        string name, JsonElement? rawStrength, string configPath, List<Diagnostic> diagnostics)
    {
        if (AbsentAxis(rawStrength, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int strength))
        {
            return strength;
        }

        diagnostics.Add(Error(DiagnosticCodes.InvalidRunnerAxis, configPath,
            $"promptRunners.{name}.strength must be an integer of at least 1 (higher = stronger), but was " +
            $"{DescribeJson(value)}. Omit the key entirely to leave the axis unstated (SSOT §9)."));
        return null;
    }

    /// <summary>
    /// Axis 3 of 3 — <c>specialization</c> (SSOT §9): what the model is FOR. An absent key resolves to
    /// <see cref="PromptRunnerSpecialization.Unspecified"/> (a first-class value, not a null), and
    /// <c>unspecified</c> is writable explicitly. An out-of-enum token is an error naming the axis.
    /// </summary>
    private static PromptRunnerSpecialization ReadSpecialization(
        string name, string? rawSpecialization, string configPath, List<Diagnostic> diagnostics)
    {
        if (rawSpecialization is null)
        {
            return PromptRunnerSpecialization.Unspecified;
        }

        if (PromptRunnerSpecializations.TryParse(rawSpecialization, out PromptRunnerSpecialization parsed))
        {
            return parsed;
        }

        diagnostics.Add(Error(DiagnosticCodes.InvalidRunnerAxis, configPath,
            $"promptRunners.{name}.specialization '{rawSpecialization}' is not a recognised value; expected " +
            "'coding', 'planning-reasoning', 'general', or 'unspecified' (SSOT §9)."));
        return PromptRunnerSpecialization.Unspecified;
    }

    /// <summary>
    /// The optional per-model <c>routing</c> block (SSOT §9, issue #224). Absent ⇒ <c>null</c> ⇒ the block
    /// is never a tier target. PRESENT ⇒ it opts into tier resolution, and <c>tiers</c> is REQUIRED
    /// (GR2047) because it is the only key the candidacy predicate reads: a <c>routing</c> block without a
    /// usable <c>tiers</c> declares an eligibility it cannot express, and would simply never be selected
    /// while its author read the config as opting in.
    ///
    /// <para>A block still carrying the RETIRED <c>rank</c> key gets a WARNING (the config keeps loading,
    /// but never silently — ordering is ascending <c>strength</c>, and <c>rank</c> is ignored).</para>
    /// </summary>
    private static PromptRunnerRouting? ReadRouting(
        string name, RawPromptRunnerRouting? raw, string configPath, List<Diagnostic> diagnostics)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw.Extra is { } extra &&
            extra.Keys.Any(key => string.Equals(key, "rank", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Warning(DiagnosticCodes.RetiredRoutingRank, configPath,
                $"promptRunners.{name}.routing.rank is a RETIRED key and is IGNORED: ordering is ascending " +
                "'strength' — the weakest model that can serve the tier goes first — not a hand-written " +
                "rank. Remove 'rank' and express relative capability with 'strength' (SSOT §9)."));
        }

        return new PromptRunnerRouting
        {
            Tiers = ReadRoutingTiers(name, raw.Tiers, configPath, diagnostics),
            Notes = raw.Notes,
            Guidance = raw.Guidance,
            Tags = raw.Tags is null ? [] : [.. raw.Tags]
        };
    }

    /// <summary>
    /// <c>routing.tiers</c> (SSOT §9, DoR §4.2) — the MACHINE-CONSUMED half of a routing block: which
    /// rungs this (kind, model, effort) route may serve. REQUIRED and non-empty; every element must be
    /// exactly one of <see cref="ActionTiers.All"/>, matched VERBATIM (no trim, no case-fold — the
    /// GR2030/GR2043 "preserve the malformed signal" doctrine, so <c>"hard "</c> is reported rather than
    /// silently accepted). Each distinct problem gets its own GR2047 so a config with two bad tokens is
    /// told about both.
    ///
    /// <para>Every failure returns the tiers parsed SO FAR rather than bailing, so the rest of validation
    /// (including GR2048's servability check) still runs over whatever the author did express — the same
    /// "keep loading so the whole report arrives" posture as the axis reads above.</para>
    /// </summary>
    private static IReadOnlyList<string> ReadRoutingTiers(
        string name, JsonElement? rawTiers, string configPath, List<Diagnostic> diagnostics)
    {
        if (AbsentAxis(rawTiers, out JsonElement value))
        {
            diagnostics.Add(Error(DiagnosticCodes.MalformedRoutingGuidance, configPath,
                $"promptRunners.{name}.routing declares no 'tiers'. A routing block opts the runner into " +
                "tier resolution, and 'tiers' is the only key that says WHICH rungs it may serve — without " +
                $"it the block can never be selected. Add a non-empty subset of {ActionTiers.TokenList}, " +
                "or remove the routing block entirely to keep the runner reachable only by an explicit " +
                "pin (SSOT §9)."));
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Error(DiagnosticCodes.MalformedRoutingGuidance, configPath,
                $"promptRunners.{name}.routing.tiers must be an ARRAY of difficulty tiers, but was " +
                $"{DescribeJson(value)}. Expected a non-empty subset of {ActionTiers.TokenList} — e.g. " +
                "[\"medium\", \"hard\"] (SSOT §9)."));
            return [];
        }

        var tiers = new List<string>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(Error(DiagnosticCodes.MalformedRoutingGuidance, configPath,
                    $"promptRunners.{name}.routing.tiers contains {DescribeJson(element)}, which is not a " +
                    $"difficulty tier. Every entry must be exactly one of {ActionTiers.TokenList} (SSOT §9)."));
                continue;
            }

            string tier = element.GetString()!;
            if (!ActionTiers.IsRecognized(tier))
            {
                diagnostics.Add(Error(DiagnosticCodes.MalformedRoutingGuidance, configPath,
                    $"promptRunners.{name}.routing.tiers contains '{tier}', which is not a recognised " +
                    $"difficulty tier. Expected exactly one of {ActionTiers.TokenList} (matched verbatim " +
                    "— no surrounding whitespace, no case-folding) (SSOT §9)."));
                continue;
            }

            tiers.Add(tier);
        }

        if (tiers.Count == 0 && !value.EnumerateArray().Any())
        {
            diagnostics.Add(Error(DiagnosticCodes.MalformedRoutingGuidance, configPath,
                $"promptRunners.{name}.routing.tiers is EMPTY. An empty list serves no rung at all, which " +
                "is indistinguishable from having no routing block — say which rungs this route may serve " +
                $"(a non-empty subset of {ActionTiers.TokenList}), or remove the routing block (SSOT §9)."));
        }

        return tiers;
    }

    /// <summary>
    /// True when a raw axis element is ABSENT — the key was missing, or written as an explicit JSON
    /// <c>null</c>, which the schema treats identically to "not stated" (as <c>model: null</c> already
    /// does). Otherwise <paramref name="value"/> is the element to judge.
    /// </summary>
    private static bool AbsentAxis(JsonElement? raw, out JsonElement value)
    {
        value = raw ?? default;
        return raw is null || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }

    /// <summary>A short, quotable rendering of a malformed axis value for its diagnostic.</summary>
    private static string DescribeJson(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? $"the string '{value.GetString()}'" : $"'{value.GetRawText()}'";

    // --- layout detection: flat vs waved (SSOT §14.1) ---------------------------------

    /// <summary>
    /// Detect the plan layout and load its tasks (and waves). A plan is WAVED iff it has NO root
    /// <c>tasks/</c> AND ≥1 immediate subdirectory matching <see cref="WaveDirPattern"/>; otherwise FLAT
    /// (SSOT §14.1). MIXED (both a root <c>tasks/</c> and wave dirs) is <see cref="DiagnosticCodes.MixedWaveLayout"/>
    /// (GR2032) — reported, then loaded as flat so the remaining diagnostics still run (the error blocks the
    /// run regardless). For a flat plan <paramref name="waves"/> is empty and behaviour is unchanged.
    /// </summary>
    private void LoadTasksOrWaves(
        string planDir, RunConfig config, List<Diagnostic> diagnostics,
        out IReadOnlyList<TaskNode> tasks, out IReadOnlyList<WaveNode> waves)
    {
        string? defaultTier = PropagatableDefaultTier(config);
        bool hasRootTasks = Directory.Exists(Path.Combine(planDir, TasksDirName));

        List<(string Path, string Name)> subdirs = Directory
            .EnumerateDirectories(planDir)
            .Select(d => (Path: d, Name: Path.GetFileName(d)))
            .ToList();

        bool hasWaveDirs = subdirs.Any(s => WaveDirPattern.IsMatch(s.Name));

        if (hasRootTasks && hasWaveDirs)
        {
            diagnostics.Add(Error(DiagnosticCodes.MixedWaveLayout, planDir,
                "Plan has a MIXED layout: both a root 'tasks/' directory and 'wave-*/' subdirectories. A " +
                "plan is either FLAT (a root 'tasks/') or WAVED (no root 'tasks/', with ordered 'wave-NN-slug/' " +
                "subdirs) — never both (SSOT §14.1). Remove one layout."));
            tasks = LoadTasks(planDir, defaultTier, diagnostics); // best-effort so other checks still run; GR2032 blocks the run.
            waves = [];
            return;
        }

        if (!hasWaveDirs)
        {
            tasks = LoadTasks(planDir, defaultTier, diagnostics); // FLAT (or neither — LoadTasks reports the missing tasks/).
            waves = [];
            return;
        }

        LoadWaves(planDir, subdirs, defaultTier, diagnostics, out tasks, out waves);
    }

    /// <summary>
    /// The plan-wide default tier that fills in for every task declaring no <c>action.tier</c> (SSOT §3,
    /// issue #225) — but ONLY when it is a RECOGNIZED token. An unrecognized default is reported ONCE, at
    /// its declaration site, by the validator (GR2043); propagating it onto every untagged task as well
    /// would multiply one typo into an error per task and bury the single site that needs fixing. An absent
    /// block (or an absent <c>defaultTier</c> within it) ⇒ null ⇒ nothing is filled in anywhere.
    /// </summary>
    private static string? PropagatableDefaultTier(RunConfig config) =>
        ActionTiers.IsRecognized(config.Tiering?.DefaultTier) ? config.Tiering!.DefaultTier : null;

    /// <summary>
    /// Load a WAVED plan (SSOT §14). Validates wave numbering (<see cref="DiagnosticCodes.WaveNumbering"/> —
    /// duplicate <c>NN</c> or a non-conforming sibling dir = error; a numbering gap = warning), loads each
    /// wave's tasks with WAVE-QUALIFIED ids, then qualifies each task's intra-wave <c>dependsOn</c> and flags
    /// cross-wave edges (<see cref="DiagnosticCodes.CrossWaveDependency"/>, GR2034). <paramref name="tasks"/>
    /// is the flattened union of every wave's tasks in strict wave order.
    /// </summary>
    private void LoadWaves(
        string planDir, List<(string Path, string Name)> subdirs, string? defaultTier, List<Diagnostic> diagnostics,
        out IReadOnlyList<TaskNode> tasks, out IReadOnlyList<WaveNode> waves)
    {
        // GR2033: a subdirectory alongside the wave dirs that is neither wave-conforming nor a recognised
        // plan-root folder (a typo'd wave dir like "wave-scaffold" with no number, or a stray "tasks-old/").
        foreach ((string path, string name) in subdirs)
        {
            if (WaveDirPattern.IsMatch(name) || KnownPlanRootFolders.Contains(name) || name.StartsWith('.'))
            {
                continue;
            }

            diagnostics.Add(Error(DiagnosticCodes.WaveNumbering, path,
                $"Subdirectory '{name}' sits alongside wave directories but does not match the wave-dir " +
                "pattern '^wave-([0-9]+)-[a-z0-9-]+$' and is not a recognised plan-root folder. Rename it to " +
                "a conforming 'wave-NN-slug/' or remove it (SSOT §14.1)."));
        }

        // Parse the conforming wave dirs.
        var parsed = new List<(string Dir, int Number, string Slug, string Path)>();
        foreach ((string path, string name) in subdirs)
        {
            Match m = WaveDirPattern.Match(name);
            if (!m.Success)
            {
                continue;
            }

            if (!int.TryParse(m.Groups[1].Value, out int number))
            {
                diagnostics.Add(Error(DiagnosticCodes.WaveNumbering, path,
                    $"Wave directory '{name}' has a numeric prefix that is out of range. Use a small, " +
                    "unique wave number (SSOT §14.1)."));
                continue;
            }

            parsed.Add((name, number, m.Groups[2].Value, path));
        }

        // GR2033 error: a duplicate wave number makes the strict total order ambiguous.
        foreach (IGrouping<int, (string Dir, int Number, string Slug, string Path)> grp in
                 parsed.GroupBy(p => p.Number).Where(g => g.Count() > 1))
        {
            string dirs = string.Join(", ", grp.Select(p => p.Dir).OrderBy(d => d, StringComparer.Ordinal));
            diagnostics.Add(Error(DiagnosticCodes.WaveNumbering, planDir,
                $"Wave number {grp.Key} is used by more than one wave directory ({dirs}); the numeric prefix " +
                "drives the strict wave order and must be unique (SSOT §14.1)."));
        }

        // Strict order: by number, then dir name (ordinal) as a stable tiebreak even on a duplicate number.
        List<(string Dir, int Number, string Slug, string Path)> ordered =
            parsed.OrderBy(p => p.Number).ThenBy(p => p.Dir, StringComparer.Ordinal).ToList();

        // GR2033 warning: an internal numbering gap (order stays unambiguous; usually a missing/renamed wave).
        List<int> distinctNumbers = ordered.Select(p => p.Number).Distinct().OrderBy(n => n).ToList();
        for (int i = 1; i < distinctNumbers.Count; i++)
        {
            if (distinctNumbers[i] != distinctNumbers[i - 1] + 1)
            {
                diagnostics.Add(Warning(DiagnosticCodes.WaveNumbering, planDir,
                    $"Wave numbering has a gap ({distinctNumbers[i - 1]:D2} → {distinctNumbers[i]:D2}); this is " +
                    "allowed (the order stays unambiguous) but usually indicates a missing or renamed wave (SSOT §14.1)."));
            }
        }

        // Load each wave's tasks (wave-qualified ids, authored dependsOn) + its entry/exit gate folders.
        var waveNodes = new List<WaveNode>();
        foreach ((string dir, int number, string slug, string path) in ordered)
        {
            IReadOnlyList<TaskNode> waveTasks = LoadWaveTasks(path, dir, defaultTier, diagnostics);

            waveNodes.Add(new WaveNode
            {
                Dir = dir,
                Number = number,
                Slug = slug,
                Directory = path,
                Tasks = waveTasks,
                Preflights = LoadGuardrailsFromFolder(Path.Combine(path, PreflightsDirName), diagnostics, enforceCatches: true),
                Guardrails = LoadGuardrailsFromFolder(Path.Combine(path, GuardrailsDirName), diagnostics, enforceCatches: true)
            });
        }

        // Qualify intra-wave dependsOn + flag cross-wave edges (GR2034). Rebuilds the WaveNodes with the
        // qualified task edges, then flattens into the whole-plan task list.
        waves = QualifyWaveDependencies(waveNodes, diagnostics);
        tasks = waves.SelectMany(w => w.Tasks).ToList();

        if (tasks.Count == 0)
        {
            diagnostics.Add(Error(DiagnosticCodes.NoTasks, planDir,
                "Waved plan has no tasks in any wave; a plan needs at least one task (SSOT §14.1)."));
        }
    }

    /// <summary>
    /// Load one wave's task folders from <c>&lt;waveDir&gt;/tasks/</c> with WAVE-QUALIFIED ids. A wave with
    /// no <c>tasks/</c> (or an empty one) is a not-yet-authored (JIT) wave — it loads as zero tasks with no
    /// error (the between-wave runtime checkpoint honest-halts on an unauthored next wave; SSOT §14.4); the
    /// whole-plan empty check in <see cref="LoadWaves"/> catches a plan with NO tasks anywhere.
    /// </summary>
    private IReadOnlyList<TaskNode> LoadWaveTasks(
        string wavePath, string waveDir, string? defaultTier, List<Diagnostic> diagnostics)
    {
        string tasksDir = Path.Combine(wavePath, TasksDirName);
        if (!Directory.Exists(tasksDir))
        {
            return [];
        }

        var tasks = new List<TaskNode>();
        foreach (string taskFolder in Directory
                     .EnumerateDirectories(tasksDir)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            TaskNode? task = LoadTask(taskFolder, defaultTier, diagnostics, waveDir);
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    /// <summary>
    /// Qualify each waved task's authored <c>dependsOn</c> (plain sibling names) to the wave-qualified id
    /// <c>&lt;waveDir&gt;/&lt;name&gt;</c>, and flag cross-wave edges as
    /// <see cref="DiagnosticCodes.CrossWaveDependency"/> (GR2034, SSOT §14.2). A cross-wave edge — a
    /// wave-qualified reference to another wave, or a plain name that resolves to a task in a DIFFERENT wave —
    /// is DROPPED (not added to the qualified list) so it produces no phantom graph edge and no double GR2001.
    /// An unknown plain name (matching no task anywhere) is qualified to this wave so the validator's GR2001
    /// unknown-dependency check fires normally.
    /// </summary>
    private static IReadOnlyList<WaveNode> QualifyWaveDependencies(
        IReadOnlyList<WaveNode> waveNodes, List<Diagnostic> diagnostics)
    {
        // folderName -> the set of wave dirs that contain a task with that folder name (for cross-wave detection).
        var folderToWaves = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (WaveNode wave in waveNodes)
        {
            foreach (TaskNode task in wave.Tasks)
            {
                string folder = FolderNameOf(task);
                if (!folderToWaves.TryGetValue(folder, out HashSet<string>? set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    folderToWaves[folder] = set;
                }

                set.Add(wave.Dir);
            }
        }

        var rebuilt = new List<WaveNode>(waveNodes.Count);
        foreach (WaveNode wave in waveNodes)
        {
            var siblings = new HashSet<string>(wave.Tasks.Select(FolderNameOf), StringComparer.Ordinal);
            var qualifiedTasks = new List<TaskNode>(wave.Tasks.Count);

            foreach (TaskNode task in wave.Tasks)
            {
                var qualified = new List<string>();
                foreach (string authored in task.DependsOn)
                {
                    string entry = authored.Trim();
                    if (entry.StartsWith("./", StringComparison.Ordinal))
                    {
                        entry = entry[2..];
                    }

                    if (entry.Contains('/'))
                    {
                        // A wave-qualified reference. Only a SELF-qualified '<thisWave>/<name>' is legal.
                        string prefix = wave.Dir + "/";
                        if (entry.StartsWith(prefix, StringComparison.Ordinal) && !entry[prefix.Length..].Contains('/'))
                        {
                            qualified.Add(entry); // self-qualified sibling — keep (validator GR2001 if unknown).
                        }
                        else
                        {
                            diagnostics.Add(Error(DiagnosticCodes.CrossWaveDependency, task.Directory,
                                $"Task '{task.Id}' dependsOn '{authored}', a cross-wave reference. Cross-wave " +
                                "ordering is the wave barrier's job, not a task edge — each wave's DAG must be " +
                                "self-contained (SSOT §14.1/§14.2). dependsOn may only name a sibling in the " +
                                "SAME wave by its plain folder name."));
                        }

                        continue;
                    }

                    if (siblings.Contains(entry))
                    {
                        qualified.Add($"{wave.Dir}/{entry}"); // intra-wave sibling — qualify.
                    }
                    else if (folderToWaves.TryGetValue(entry, out HashSet<string>? owners) &&
                             owners.Any(w => !string.Equals(w, wave.Dir, StringComparison.Ordinal)))
                    {
                        // A plain name that resolves to a task in another wave — cross-wave (GR2034), drop it.
                        diagnostics.Add(Error(DiagnosticCodes.CrossWaveDependency, task.Directory,
                            $"Task '{task.Id}' dependsOn '{authored}', which is not a sibling in this wave but " +
                            $"names a task in another wave ({string.Join(", ", owners.OrderBy(w => w, StringComparer.Ordinal))}). " +
                            "Cross-wave ordering is the wave barrier's job, not a task edge (SSOT §14.1/§14.2)."));
                    }
                    else
                    {
                        // Unknown in this wave and nowhere else — qualify so the validator's GR2001 fires.
                        qualified.Add($"{wave.Dir}/{entry}");
                    }
                }

                qualifiedTasks.Add(task with { DependsOn = qualified });
            }

            rebuilt.Add(wave with { Tasks = qualifiedTasks });
        }

        return rebuilt;
    }

    /// <summary>The task's plain folder name — the segment of a wave-qualified id after the wave dir.</summary>
    private static string FolderNameOf(TaskNode task) =>
        task.WaveDir is { } wave && task.Id.StartsWith(wave + "/", StringComparison.Ordinal)
            ? task.Id[(wave.Length + 1)..]
            : task.Id;

    // --- tasks/* ----------------------------------------------------------------------

    private IReadOnlyList<TaskNode> LoadTasks(string planDir, string? defaultTier, List<Diagnostic> diagnostics)
    {
        string tasksDir = Path.Combine(planDir, TasksDirName);
        if (!Directory.Exists(tasksDir))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, tasksDir, "Plan has no 'tasks' directory."));
            return [];
        }

        var tasks = new List<TaskNode>();
        List<string> taskFolders = Directory
            .EnumerateDirectories(tasksDir)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        // An empty tasks/ directory is a malformed plan: it would otherwise validate clean and
        // "run" 0/0 green. (A dir with task folders that all fail to load is already reported by
        // the per-task diagnostics below, so only the truly-empty case needs flagging here.)
        if (taskFolders.Count == 0)
        {
            diagnostics.Add(Error(DiagnosticCodes.NoTasks, tasksDir,
                "Plan's 'tasks' directory is empty; a plan needs at least one task folder."));
            return [];
        }

        foreach (string taskFolder in taskFolders)
        {
            TaskNode? task = LoadTask(taskFolder, defaultTier, diagnostics);
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    /// <summary>
    /// Load one task folder. In a FLAT plan <paramref name="waveDir"/> is null and the task id is the
    /// folder name. In a WAVED plan (SSOT §14.2) <paramref name="waveDir"/> is the owning wave dir and the
    /// task id is the WAVE-QUALIFIED <c>&lt;waveDir&gt;/&lt;folder&gt;</c>. <c>dependsOn</c> is stored AS
    /// AUTHORED here (plain sibling names); the caller's <see cref="QualifyWaveDependencies"/> post-pass
    /// qualifies it intra-wave and flags cross-wave edges (GR2034). <paramref name="defaultTier"/> is the
    /// plan-wide tier that fills in when this task declares no <c>action.tier</c> (SSOT §3).
    /// </summary>
    private TaskNode? LoadTask(
        string taskFolder, string? defaultTier, List<Diagnostic> diagnostics, string? waveDir = null)
    {
        string folderName = Path.GetFileName(taskFolder);
        string taskId = waveDir is null ? folderName : $"{waveDir}/{folderName}";
        string manifestPath = Path.Combine(taskFolder, TaskManifestName);

        if (!File.Exists(manifestPath))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, manifestPath, $"Task '{taskId}' has no {TaskManifestName}."));
            return null;
        }

        RawTask? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawTask>(File.ReadAllText(manifestPath), PlanJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, manifestPath, $"Could not parse {TaskManifestName}: {ex.Message}"));
            return null;
        }

        if (raw is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, manifestPath, $"{TaskManifestName} is empty or null."));
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw.Description))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingRequiredField, manifestPath, "Required field 'description' is missing or empty."));
            return null;
        }

        ActionDefinition? action = ResolveAction(taskFolder, taskId, raw.Action, defaultTier, diagnostics);
        if (action is null)
        {
            return null;
        }

        // Postcondition guardrails/ (catches enforcement is NOT retrofitted onto this pre-existing
        // folder — its behavior is preserved) and the sibling JIT preflights/ folder (a NEW folder, so
        // its files DO carry the enforced `catches:` declaration, GR2027).
        IReadOnlyList<GuardrailDefinition> guardrails =
            LoadGuardrailsFromFolder(Path.Combine(taskFolder, GuardrailsDirName), diagnostics, enforceCatches: false);
        IReadOnlyList<GuardrailDefinition> preflights =
            LoadGuardrailsFromFolder(Path.Combine(taskFolder, PreflightsDirName), diagnostics, enforceCatches: true);

        return new TaskNode
        {
            Id = taskId,
            WaveDir = waveDir,
            StableId = string.IsNullOrWhiteSpace(raw.StableId) ? null : raw.StableId.Trim(),
            Directory = taskFolder,
            Description = raw.Description.Trim(),
            DependsOn = raw.DependsOn ?? [],
            Retries = raw.Retries,
            TimeoutSeconds = raw.TimeoutSeconds,
            IntegrationGate = raw.IntegrationGate ?? false,
            // #389: PRESERVE null-vs-empty. Absent in task.json ⇒ null (→ GR2041 at validate time); a
            // DELIBERATE present-empty [] ⇒ an empty list ("writes nothing to the repo", VALID). A
            // `Count: > 0` guard here would collapse [] to null and defeat GR2041, so match on presence
            // (`is { }`) only.
            WriteScope = raw.WriteScope is { } ws ? [.. ws] : null,
            StagingOutputs = BindStagingOutputs(raw.StagingOutputs),
            Action = action,
            Guardrails = guardrails,
            Preflights = preflights
        };
    }

    /// <summary>
    /// Bind the raw <c>stagingOutputs</c> list (SSOT §3.5). Null (absent) stays null — the
    /// no-staging default. A PRESENT list is bound faithfully, INCLUDING an empty array and entries
    /// with a missing/empty <c>from</c> or <c>to</c> (mapped to <c>""</c>): the validator turns those
    /// into GR2024 errors, so the loader must preserve the "present but malformed" signal rather than
    /// silently dropping it (which would let a malformed contract validate clean).
    /// </summary>
    private static IReadOnlyList<StagingOutput>? BindStagingOutputs(List<RawStagingOutput>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return raw
            .Select(entry => new StagingOutput
            {
                From = entry.From?.Trim() ?? string.Empty,
                To = entry.To?.Trim() ?? string.Empty
            })
            .ToList();
    }

    // --- action discovery (SSOT §3) ---------------------------------------------------

    private ActionDefinition? ResolveAction(
        string taskFolder, string taskId, RawAction? rawAction, string? defaultTier, List<Diagnostic> diagnostics)
    {
        string? actionPath;
        if (!string.IsNullOrWhiteSpace(rawAction?.Path))
        {
            actionPath = Path.GetFullPath(Path.Combine(taskFolder, rawAction.Path));
            if (!File.Exists(actionPath))
            {
                diagnostics.Add(Error(DiagnosticCodes.ActionPathNotFound, taskFolder,
                    $"Task '{taskId}' action.path '{rawAction.Path}' does not exist."));
                return null;
            }
        }
        else
        {
            actionPath = DiscoverActionByConvention(taskFolder, taskId, diagnostics);
            if (actionPath is null)
            {
                return null;
            }
        }

        (string? tier, TierOrigin tierOrigin) = ResolveTier(rawAction?.Tier, defaultTier);

        return new ActionDefinition
        {
            Path = actionPath,
            Kind = KindFor(actionPath),
            Args = rawAction?.Args ?? [],
            Runner = rawAction?.Runner,
            MaxTurns = rawAction?.MaxTurns,
            // Bound VERBATIM (no trim/nullify): a present-but-blank value (e.g. "   ") must reach the
            // validator's GR2030 check faithfully, the same "preserve the malformed signal" doctrine
            // BindStagingOutputs documents for stagingOutputs — silently normalizing it to null here
            // would let a malformed override validate clean.
            Model = rawAction?.Model,
            Tier = tier,
            TierOrigin = tierOrigin,
            // Bound VERBATIM like Model, whose shape it mirrors — GR2050 judges it. Deliberately NOT
            // defaulted from anything: there is no plan-wide effort, and an effort nobody wrote must not
            // be invented (the same "never fabricate an unstated axis" rule the registry axes follow).
            Effort = rawAction?.Effort,
            TimeoutSeconds = rawAction?.TimeoutSeconds,
            WorkingDirectory = rawAction?.WorkingDirectory,
            Env = (IReadOnlyDictionary<string, string>?)rawAction?.Env ?? new Dictionary<string, string>()
        };
    }

    /// <summary>
    /// The tier precedence AND its provenance, decided in ONE place (SSOT §3, issue #225; DoR §12.4):
    /// task <c>action.tier</c> &gt; the plan-wide <c>tiering.defaultTier</c> &gt; null. Resolving at LOAD
    /// rather than at breakdown is what makes the default reach a task a human hand-added to the folder
    /// afterwards, which no breakdown ever touched. Both sources are bound VERBATIM like <c>Model</c> — a
    /// malformed tier is the validator's to judge (GR2030/GR2043), not the loader's to normalize away.
    ///
    /// <para>The origin comes from the BRANCH THAT WON, never from comparing the resolved value against
    /// <paramref name="defaultTier"/> afterwards. A task whose own <c>action.tier</c> spells the SAME token
    /// as the plan default still authored it, and a comparison calls exactly that (thoroughly ordinary —
    /// it is the shape tiering is for) case <see cref="TierOrigin.PlanDefault"/>.</para>
    ///
    /// <para><paramref name="defaultTier"/> is already <see cref="PropagatableDefaultTier"/>'s output, so an
    /// unrecognized default arrives here as null and yields (null, <see cref="TierOrigin.None"/>): the origin
    /// never claims a source for a value that never landed. Hence the invariant the whole field rests on —
    /// <see cref="TierOrigin.None"/> iff the resolved tier is null.</para>
    /// </summary>
    private static (string? Tier, TierOrigin Origin) ResolveTier(string? actionTier, string? defaultTier) =>
        actionTier is not null ? (actionTier, TierOrigin.Task)
        : defaultTier is not null ? (defaultTier, TierOrigin.PlanDefault)
        : (null, TierOrigin.None);

    /// <summary>
    /// Convention discovery: exactly ONE <c>action.*</c> file in the task folder.
    /// Zero or multiple is a validation error (SSOT §3). A multi-part extension like
    /// <c>action.prompt.md</c> still counts as one action file.
    /// </summary>
    private string? DiscoverActionByConvention(string taskFolder, string taskId, List<Diagnostic> diagnostics)
    {
        List<string> candidates = Directory
            .EnumerateFiles(taskFolder)
            .Where(f => Path.GetFileName(f).StartsWith(ActionFilePrefix, StringComparison.Ordinal))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        switch (candidates.Count)
        {
            case 0:
                diagnostics.Add(Error(DiagnosticCodes.NoActionFile, taskFolder,
                    $"Task '{taskId}' has no action file (expected one 'action.*' file or an explicit action.path)."));
                return null;
            case 1:
                return candidates[0];
            default:
                string names = string.Join(", ", candidates.Select(Path.GetFileName));
                diagnostics.Add(Error(DiagnosticCodes.AmbiguousActionFile, taskFolder,
                    $"Task '{taskId}' has {candidates.Count} action files ({names}); expected exactly one or an explicit action.path."));
                return null;
        }
    }

    // --- guardrail discovery (SSOT §4 / §4.1) -----------------------------------------

    /// <summary>
    /// Discover the guardrail-shaped files in one of the four preflights/guardrails folders
    /// (<c>&lt;plan&gt;/preflights/</c>, <c>&lt;plan&gt;/guardrails/</c>, <c>tasks/&lt;id&gt;/preflights/</c>,
    /// <c>tasks/&lt;id&gt;/guardrails/</c>), ordered by filename ordinal sort. The folders share this ONE
    /// parser — they differ only in WHERE they live and WHEN they run (SSOT §4). A
    /// <c>&lt;basename&gt;.json</c> next to a same-basename script is a metadata sidecar (not a guardrail);
    /// a bare <c>.json</c> with no sibling script is an orphan sidecar (error). Sidecar metadata is loaded
    /// onto the matching deterministic guardrail. When <paramref name="enforceCatches"/> is set, a file
    /// that does not open with a <c>catches:</c> declaration is a malformed declaration (GR2027) — the
    /// canonical per-folder diagnostic for the four-folder model. (The pre-existing
    /// <c>tasks/&lt;id&gt;/guardrails/</c> folder is loaded WITHOUT catches enforcement to preserve its
    /// behavior; the three new folders enforce it.)
    /// </summary>
    private IReadOnlyList<GuardrailDefinition> LoadGuardrailsFromFolder(
        string guardrailsDir, List<Diagnostic> diagnostics, bool enforceCatches)
    {
        if (!Directory.Exists(guardrailsDir))
        {
            return [];
        }

        List<string> files = Directory
            .EnumerateFiles(guardrailsDir)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        // Basenames (without final extension) that have a non-.json file — these own any
        // sibling .json as metadata. Prompt guardrails (.prompt.md) have basename incl.
        // ".prompt"; their sidecars are not part of the M2 contract, but the lookup is by
        // the exact file basename so it remains correct.
        var scriptBasenames = new HashSet<string>(
            files.Where(f => !IsJson(f)).Select(GuardrailBasename),
            StringComparer.Ordinal);

        var guardrails = new List<GuardrailDefinition>();

        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            string basename = GuardrailBasename(file);

            if (IsJson(file))
            {
                if (scriptBasenames.Contains(basename))
                {
                    continue; // metadata sidecar for a sibling script — not a guardrail.
                }

                diagnostics.Add(Error(DiagnosticCodes.OrphanGuardrailMetadata, file,
                    $"Guardrail metadata '{fileName}' has no sibling guardrail script with basename '{basename}'."));
                continue;
            }

            ActionKind kind = KindFor(file);
            GuardrailDefinition guardrail = new()
            {
                Name = GuardrailName(file, kind),
                Path = file,
                Kind = kind
            };

            if (kind == ActionKind.Script)
            {
                guardrail = ApplySidecar(guardrail, guardrailsDir, basename, diagnostics);
            }
            else if (kind == ActionKind.Prompt)
            {
                guardrail = ApplyPromptFrontmatter(guardrail);
            }

            if (enforceCatches && !HasCatchesDeclaration(guardrail))
            {
                diagnostics.Add(Error(DiagnosticCodes.GuardrailMissingCatches, file,
                    $"Guardrail '{guardrail.Name}' does not open with a 'catches:' " +
                    (kind == ActionKind.Prompt ? "front-matter field" : "comment") +
                    " stating what wrong implementation it catches (SSOT §4). A guardrail whose author " +
                    "cannot state what it catches is decorative — declare it, or remove the file."));
            }

            guardrails.Add(guardrail);
        }

        return guardrails;
    }

    /// <summary>
    /// True when a guardrail file OPENS with a <c>catches:</c> declaration (SSOT §4): a leading comment
    /// for a script guardrail, or a <c>catches</c> YAML front-matter field for a prompt guardrail. An
    /// unreadable file returns true — the GR2027 malformed-declaration diagnostic must not double-report
    /// a file whose IO error other checks already surface.
    /// </summary>
    private static bool HasCatchesDeclaration(GuardrailDefinition guardrail)
    {
        string content;
        try
        {
            content = File.ReadAllText(guardrail.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return guardrail.Kind == ActionKind.Prompt
            ? FrontmatterDeclaresCatches(content)
            : LeadingCommentDeclaresCatches(content);
    }

    /// <summary>
    /// Scan the leading comment block — the contiguous comment/blank lines at the top — of a script
    /// guardrail for a <c>catches:</c> declaration. Returns false as soon as the first non-comment,
    /// non-blank line (real code) is reached without one: the declaration must OPEN the file. Recognises
    /// <c>#</c> (ps1/sh/py), <c>//</c>, and <c>REM</c>/<c>::</c> (cmd/bat) comment leaders; a shebang or
    /// other leading comment line before the <c>catches:</c> line is tolerated.
    /// </summary>
    private static bool LeadingCommentDeclaresCatches(string content)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string? comment = CommentBody(line);
            if (comment is null)
            {
                return false; // reached real code before any `catches:` comment
            }

            if (comment.StartsWith("catches:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text of a comment line after its leader, or null when the line is not a comment.</summary>
    private static string? CommentBody(string trimmedLine)
    {
        if (trimmedLine.StartsWith('#'))
        {
            return trimmedLine[1..].TrimStart();
        }
        if (trimmedLine.StartsWith("//", StringComparison.Ordinal))
        {
            return trimmedLine[2..].TrimStart();
        }
        if (trimmedLine.StartsWith("::", StringComparison.Ordinal))
        {
            return trimmedLine[2..].TrimStart();
        }
        if (trimmedLine.StartsWith("REM", StringComparison.OrdinalIgnoreCase) &&
            (trimmedLine.Length == 3 || char.IsWhiteSpace(trimmedLine[3])))
        {
            return trimmedLine.Length > 3 ? trimmedLine[3..].TrimStart() : string.Empty;
        }
        return null;
    }

    /// <summary>
    /// True when a prompt guardrail's YAML front-matter declares a non-empty <c>catches</c> field
    /// (SSOT §4/§4.2). Reuses the same front-matter extraction as <see cref="ApplyPromptFrontmatter"/>.
    /// </summary>
    private static bool FrontmatterDeclaresCatches(string content)
    {
        string? frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ParseFrontmatterScalar(frontmatter, "catches"));
    }

    private static GuardrailDefinition ApplySidecar(
        GuardrailDefinition guardrail,
        string guardrailsDir,
        string basename,
        List<Diagnostic> diagnostics)
    {
        string sidecarPath = Path.Combine(guardrailsDir, basename + ".json");
        if (!File.Exists(sidecarPath))
        {
            return guardrail;
        }

        RawGuardrailSidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<RawGuardrailSidecar>(File.ReadAllText(sidecarPath), PlanJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, sidecarPath, $"Could not parse guardrail sidecar: {ex.Message}"));
            return guardrail;
        }

        if (sidecar is null)
        {
            return guardrail;
        }

        return guardrail with
        {
            Description = sidecar.Description,
            Args = sidecar.Args ?? [],
            TimeoutSeconds = sidecar.TimeoutSeconds,
            ExpectedDurationSeconds = sidecar.ExpectedDurationSeconds,
            Scope = string.IsNullOrWhiteSpace(sidecar.Scope) ? null : sidecar.Scope.Trim().ToLowerInvariant()
        };
    }

    // --- prompt frontmatter (SSOT §4.2) -----------------------------------------------

    /// <summary>
    /// Reads the YAML front-matter block (between the opening and closing <c>---</c> delimiters)
    /// from a <c>.prompt.md</c> guardrail file and applies any recognised keys onto the guardrail:
    /// <c>scope</c> (SSOT §4.3) and <c>tier</c> (SSOT §4.2, issue #225). Unknown keys are silently
    /// ignored.
    ///
    /// <para>The two keys are harvested with deliberately DIFFERENT policies, and the difference is the
    /// point. <c>scope</c> is NORMALISED (lower-cased) because that is what it has always done and its
    /// GR2021 check judges the normalised token. <c>tier</c> keeps its CASE, because its GR2043 check is
    /// a verbatim membership test — case-folding here would silently repair <c>Hard</c> into validity at
    /// the one site the "preserve the malformed signal" doctrine says must report it. (Surrounding
    /// whitespace is stripped by <see cref="ParseFrontmatterScalar"/> for every key, as a YAML scalar
    /// reader does; the JSON sites — <c>action.tier</c>, <c>tiering.defaultTier</c> — are the ones where
    /// a stray <c>"hard "</c> survives to be reported.)</para>
    /// </summary>
    private static GuardrailDefinition ApplyPromptFrontmatter(GuardrailDefinition guardrail)
    {
        string content;
        try { content = File.ReadAllText(guardrail.Path); }
        catch (IOException) { return guardrail; }

        string? frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
            return guardrail;

        string? scope = ParseFrontmatterScalar(frontmatter, "scope");
        string? tier = ParseFrontmatterScalar(frontmatter, "tier");
        if (scope is null && tier is null)
            return guardrail;

        return guardrail with
        {
            Scope = scope is null
                ? guardrail.Scope
                : string.IsNullOrWhiteSpace(scope) ? null : scope.Trim().ToLowerInvariant(),
            Tier = tier is null || tier.Length == 0 ? guardrail.Tier : tier
        };
    }

    /// <summary>
    /// The YAML front-matter block of a <c>.prompt.md</c> file — the text between the opening
    /// <c>---</c> on the very first line and the next <c>---</c> line — or null when the file has no
    /// well-formed front-matter block.
    /// </summary>
    private static string? ExtractFrontmatter(string content)
    {
        // Front-matter must start with "---" on the very first line.
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;

        int firstNewline = content.IndexOfAny(['\r', '\n']);
        if (firstNewline < 0)
            return null;

        int bodyStart = firstNewline + 1;
        if (bodyStart < content.Length && content[firstNewline] == '\r' && content[bodyStart] == '\n')
            bodyStart++;

        // Find the closing "---" line.
        int closePos = FindFrontmatterClose(content, bodyStart);
        if (closePos < 0)
            return null;

        return content[bodyStart..closePos];
    }

    private static int FindFrontmatterClose(string content, int startPos)
    {
        int pos = startPos;
        while (pos < content.Length)
        {
            int lineStart = pos;
            int lineEnd = content.IndexOfAny(['\r', '\n'], pos);
            if (lineEnd < 0) break;

            string line = content[lineStart..lineEnd];
            if (line == "---")
                return lineStart;

            pos = lineEnd + 1;
            if (pos < content.Length && content[lineEnd] == '\r' && content[pos] == '\n')
                pos++;
        }
        return -1;
    }

    private static string? ParseFrontmatterScalar(string frontmatter, string key)
    {
        foreach (string line in frontmatter.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (string.Equals(line[..colon].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    // --- helpers ----------------------------------------------------------------------

    private static ActionKind KindFor(string path) =>
        path.EndsWith(PromptExtension, StringComparison.OrdinalIgnoreCase)
            ? ActionKind.Prompt
            : ActionKind.Script;

    private static bool IsJson(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The guardrail "basename" used to pair a sidecar with its script: the file name with
    /// its final extension removed (e.g. "01-build-passes.ps1" → "01-build-passes";
    /// "02-review.prompt.md" → "02-review.prompt"). A <c>.json</c> sidecar must share this
    /// exact basename with its script for the pairing to hold.
    /// </summary>
    private static string GuardrailBasename(string path) =>
        Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// The human/journal name for a guardrail: a prompt guardrail drops the whole
    /// <c>.prompt.md</c> suffix (so "02-review.prompt.md" → "02-review"); a deterministic
    /// guardrail drops only its final extension (so "01-build-passes.ps1" → "01-build-passes").
    /// </summary>
    private static string GuardrailName(string path, ActionKind kind)
    {
        string fileName = Path.GetFileName(path);
        return kind == ActionKind.Prompt
            ? fileName[..^PromptExtension.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    private static Diagnostic Error(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Error,
        Path = path,
        Message = message
    };

    private static Diagnostic Warning(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Warning,
        Path = path,
        Message = message
    };

    /// <summary>The parsed <c>promptRunners</c> map: names, the default pointer, and full configs.</summary>
    private readonly record struct PromptRunnersResult(
        IReadOnlySet<string> Names,
        string? Default,
        IReadOnlyDictionary<string, PromptRunnerConfig> Runners);
}
