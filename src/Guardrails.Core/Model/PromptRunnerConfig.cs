using System.Text.Json;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Model;

/// <summary>
/// One named prompt-runner configuration from <c>guardrails.json: promptRunners</c>
/// (SSOT §2/§9). Carries the data a runner needs; the runner CLASS
/// (<c>ClaudePromptRunner</c>) carries the flag-spelling and output-parsing code, so a
/// new CLI is a new class + a config block, never a schema change.
/// </summary>
public sealed record PromptRunnerConfig
{
    /// <summary>The runner's name (the <c>promptRunners</c> map key), e.g. "claude".</summary>
    public required string Name { get; init; }

    /// <summary>The executable to launch (e.g. "claude"). Defaults to the runner name.</summary>
    public required string Command { get; init; }

    /// <summary>The base settings used for action prompts.</summary>
    public required PromptRunnerSettings Settings { get; init; }

    /// <summary>
    /// Which runner IMPLEMENTATION serves this block (SSOT §9, issue #224). ABSENT ⇒
    /// <see cref="PromptRunnerKind.Claude"/>, which is what keeps the provider-registry change
    /// ADDITIVE: every config written before the discriminator existed is implicitly Claude and must
    /// validate and run unchanged.
    /// </summary>
    public PromptRunnerKind Kind { get; init; } = PromptRunnerKinds.Default;

    /// <summary>
    /// The per-block thinking-effort knob (SSOT §9, issue #201) — e.g. <c>"low"</c>, <c>"xhigh"</c>.
    /// OPAQUE to the harness: shape-validated only (GR2050, mirroring GR2030's <c>model</c> check) and
    /// TRANSLATED by the runner CLASS into whatever its CLI/API exposes, so the vendor spelling stays
    /// quarantined there exactly as <c>maxOutputTokens</c> → <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> does.
    /// Wanting the same model at two efforts is TWO blocks (<c>"opus"</c>, <c>"opus-xhigh"</c>), which is
    /// what makes the three model axes per-effort as well as per-model. Null = the key was absent.
    /// Nothing READS it yet — the Stage 2 resolver (#226) is its first consumer.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Axis 1 of 3 (charter Decision 7): does spending on this model warrant restraint? OPTIONAL and
    /// TRI-STATE — <c>null</c> means "not stated", deliberately distinct from an explicit <c>false</c>
    /// ("stated to be cheap"). Nothing READS it in Stage 1; the resolver (#226) is the first consumer.
    /// </summary>
    public bool? Costly { get; init; }

    /// <summary>
    /// Axis 2 of 3: relative capability, higher = stronger, minimum 1 (a non-positive value is a
    /// validation error). OPTIONAL; <c>null</c> = not stated. This is the ORDERING key — candidates for
    /// a tier are ordered by ASCENDING strength, so the weakest model that can serve the tier goes
    /// first. <c>routing.rank</c> is retired precisely because this axis subsumes it.
    /// </summary>
    public int? Strength { get; init; }

    /// <summary>
    /// Axis 3 of 3: what the model is FOR. OPTIONAL — an absent key resolves to
    /// <see cref="PromptRunnerSpecialization.Unspecified"/>, the enum member that exists so
    /// "not stated" is a first-class, writable value rather than a null.
    /// </summary>
    public PromptRunnerSpecialization Specialization { get; init; } = PromptRunnerSpecialization.Unspecified;

    /// <summary>
    /// The per-model <c>routing</c> block (SSOT §9, issue #224): which difficulty rungs this block may
    /// serve (<see cref="PromptRunnerRouting.Tiers"/>, the machine-consumed half) plus human-facing prose.
    /// Null = the block carried no <c>routing</c> key, which means the block is NEVER a tier target —
    /// reachable only explicitly (<c>action.runner</c>/<c>action.model</c>) or as the <c>default</c>
    /// pointer, exactly today's behaviour.
    /// </summary>
    public PromptRunnerRouting? Routing { get; init; }

    /// <summary>
    /// The ONE candidacy predicate (SSOT §9.6, DoR §6.2), written once here so every reader agrees:
    /// <c>routing</c> present AND <paramref name="tier"/> ∈ <c>routing.tiers</c> AND <c>costly</c> is not
    /// <c>true</c>. It is a CORRECTNESS requirement that GR2048's validate-time check, the Stage 2
    /// resolver, the <c>no-route</c> outcome and the verifier route all use this same predicate — if
    /// validation counted a costly block as serving a rung and the resolver did not, validation would
    /// pass and every task at that rung would die at runtime.
    ///
    /// <para><b>Three states in the schema, TWO here.</b> <see cref="Costly"/> is tri-state — <c>null</c>
    /// is "not stated", deliberately distinct from an explicit <c>false</c> — but at THIS predicate
    /// <c>null</c> behaves as NOT-costly, because an un-annotated registry must stay routable. Only an
    /// explicit <c>true</c> excludes a block. (The distinction survives for <c>providers init</c>, which
    /// exists to name every block whose cost is unstated and ask.)</para>
    /// </summary>
    /// <param name="tier">The rung to test candidacy for — one of <see cref="ActionTiers.All"/>.</param>
    public bool ServesTier(string tier) =>
        Costly is not true
        && Routing is { } routing
        && routing.Tiers.Contains(tier, StringComparer.Ordinal);

    /// <summary>
    /// True when this block DECLARES <paramref name="tier"/> in its <c>routing.tiers</c>, ignoring the
    /// costly floor. The difference between this and <see cref="ServesTier"/> is exactly what GR2048's
    /// message must distinguish: a rung nothing declares needs a new/widened block, while a rung declared
    /// only by <c>costly: true</c> blocks needs a pin or a cleared flag. Two problems, two fixes.
    /// </summary>
    /// <param name="tier">The rung to test declaration for — one of <see cref="ActionTiers.All"/>.</param>
    public bool DeclaresTier(string tier) =>
        Routing is { } routing && routing.Tiers.Contains(tier, StringComparer.Ordinal);

    /// <summary>
    /// A partial override block applied for GUARDRAIL prompts only — the tighter,
    /// read-mostly verifier profile (SSOT §2 <c>guardrailOverrides</c>). Null fields
    /// inherit from <see cref="Settings"/>. Null = no overrides (guardrails use the base).
    /// </summary>
    public PromptRunnerOverrides? GuardrailOverrides { get; init; }

    /// <summary>
    /// The openai-compat base URL (plan 28 §4, issue #223) — REQUIRED for
    /// <see cref="PromptRunnerKind.OpenAiCompat"/>, an absolute http/https URL (GR2065). Null = the key
    /// was absent, which is every other kind, or a malformed block ahead of <c>guardrails validate</c>.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The model's context window in tokens (plan 28 §4/§6.1) — REQUIRED for
    /// <see cref="PromptRunnerKind.OpenAiCompat"/>, must be at least 1 (GR2065). Null = the key was
    /// absent. The runner's own before/after overflow checks (§6.1) are this field's only readers.
    /// </summary>
    public int? ContextTokens { get; init; }

    /// <summary>
    /// The NAME of an env var holding a bearer token for the openai-compat endpoint (plan 28 §4) —
    /// NEVER the token itself: <c>guardrails.json</c> is committed and hashed into
    /// <c>PlanDefinitionHash</c>, which keys the review attestation. Null = no auth header is sent.
    /// </summary>
    public string? ApiKeyEnv { get; init; }

    /// <summary>
    /// A verbatim request-body passthrough map for the openai-compat wire protocol (plan 28 §4) — the
    /// HTTP sibling of <see cref="PromptRunnerSettings.Env"/>. Merged into the outgoing JSON body by
    /// the runner; a top-level key that shadows a harness-owned field (<c>model</c>, <c>messages</c>,
    /// <c>stream</c>, <c>stream_options</c>, <c>tools</c>, <c>max_tokens</c>) is GR2065, not a runtime
    /// throw. Values are held as raw <see cref="JsonElement"/> so a nested knob (e.g.
    /// <c>options.num_ctx</c>) round-trips untouched. Null = the key was absent.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Wire { get; init; }

    /// <summary>
    /// OPERATOR-FACING TEXT ONLY (plan 28 §6.2) — <c>ollama</c> | <c>llama.cpp</c> | <c>mlx</c> |
    /// <c>lm-studio</c> | <c>vllm</c>. Selects the model-not-found remedy SENTENCE and nothing else: it
    /// never selects a code path, never changes a request, and is absent from
    /// <see cref="PromptRunnerKinds.ServesRoles"/>, the containment rules and the wire body. Null = the
    /// neutral remedy sentence naming the model and the endpoint.
    /// </summary>
    public string? Engine { get; init; }

    /// <summary>The effective settings for a prompt of the given kind (base, or base + guardrail overrides).</summary>
    public PromptRunnerSettings EffectiveSettings(bool isGuardrail) =>
        isGuardrail && GuardrailOverrides is not null
            ? Settings.With(GuardrailOverrides)
            : Settings;
}

/// <summary>
/// The fully-resolved knobs that govern one prompt invocation (SSOT §2). All fields are
/// concrete (no nulls) so the runner does not re-apply defaults.
/// </summary>
public sealed record PromptRunnerSettings
{
    /// <summary>Permission mode passed to the runner (e.g. "acceptEdits", "default"). Default "acceptEdits".</summary>
    public string PermissionMode { get; init; } = "acceptEdits";

    /// <summary>Tools the runner is allowed to use. Empty = pass no <c>--allowedTools</c> (runner default).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Turn ceiling for the runner. Default 50 (SSOT §2 example).</summary>
    public int MaxTurns { get; init; } = 50;

    /// <summary>Model override; null = the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>Extra CLI arguments appended verbatim. Empty by default.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>
    /// The output-token cap handed to the runner (issue #114). Defaults to
    /// <see cref="DefaultMaxOutputTokens"/> — deliberately ABOVE Claude Code's 32 000 default so a
    /// well-formed single-response task is not blocked by the cap the harness never used to configure.
    /// The runner CLASS translates this into the CLI's env var
    /// (<c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>) — the env-var NAME stays quarantined in
    /// <see cref="Prompts.ClaudePromptRunner"/>, never in this model. A non-positive value is a
    /// validation error (GR2023).
    /// </summary>
    public int MaxOutputTokens { get; init; } = DefaultMaxOutputTokens;

    /// <summary>
    /// Extra environment variables passed verbatim to the runner process (SSOT §2/§9, issue #114) —
    /// a general passthrough for runner/provider knobs the harness does not model. These overlay (and
    /// may override) the harness <c>GUARDRAILS_*</c> env only for keys the user explicitly sets.
    /// Empty by default.
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The default output-token cap (issue #114): 64 000, double Claude Code's 32 000 default.</summary>
    public const int DefaultMaxOutputTokens = 64_000;

    /// <summary>Return a copy with any non-null fields of <paramref name="overrides"/> applied.</summary>
    public PromptRunnerSettings With(PromptRunnerOverrides overrides) => this with
    {
        PermissionMode = overrides.PermissionMode ?? PermissionMode,
        AllowedTools = overrides.AllowedTools ?? AllowedTools,
        MaxTurns = overrides.MaxTurns ?? MaxTurns,
        Model = overrides.Model ?? Model,
        ExtraArgs = overrides.ExtraArgs ?? ExtraArgs,
        MaxOutputTokens = overrides.MaxOutputTokens ?? MaxOutputTokens,
        Env = overrides.Env ?? Env
    };
}

/// <summary>
/// A partial settings block: every field is nullable so only the keys actually present in
/// <c>guardrailOverrides</c> override the base (SSOT §2). Used solely for the merge.
/// </summary>
public sealed record PromptRunnerOverrides
{
    public string? PermissionMode { get; init; }
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public int? MaxTurns { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string>? ExtraArgs { get; init; }
    public int? MaxOutputTokens { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Which runner IMPLEMENTATION a <c>promptRunners</c> block selects (SSOT §9, issue #224).
/// <see cref="Claude"/> and <see cref="OpenAiCompat"/> have concrete runners; <see cref="Codex"/>,
/// <see cref="OpenRouter"/> and <see cref="Local"/> remain RESERVED NAMES (plan 28 §3.1). Declaring one
/// of those is a <c>guardrails validate</c> ERROR (GR2044), not a silent load: registry construction
/// still refuses it, but as the BACKSTOP rather than the gate (see
/// <see cref="PromptRunnerKinds.Implemented"/>).
/// </summary>
public enum PromptRunnerKind
{
    /// <summary>Claude Code (<c>ClaudePromptRunner</c>) — the DEFAULT for a block with no <c>kind</c>.</summary>
    Claude,

    /// <summary>OpenAI Codex CLI. A reserved name; no concrete runner (#223).</summary>
    Codex,

    /// <summary>An OpenRouter-hosted model. A reserved name; no concrete runner (#223).</summary>
    OpenRouter,

    /// <summary>A locally-hosted model. A reserved name; no concrete runner (#223).</summary>
    Local,

    /// <summary>
    /// An OpenAI-compatible endpoint (wire token <c>openai-compat</c>) — ONE kind covering Ollama,
    /// llama.cpp, LM Studio and vLLM, because they share the wire protocol. This is the reserved seam
    /// #223 fills, and it deliberately spans BOTH a loopback local endpoint and a cloud
    /// OpenAI-compatible API: that is precisely why the DoR's provider-kind "weak" fallback (§4.1) is
    /// VERIFIER-ONLY and may never be used for actor ordering — this kind cannot tell the two apart.
    /// Served by <see cref="OpenAiCompatPromptRunner"/> (#223), for the <see cref="PromptRole.Guardrail"/>
    /// and <see cref="PromptRole.Advisory"/> roles only — see <see cref="PromptRunnerKinds.ServesRoles"/>.
    /// </summary>
    OpenAiCompat
}

/// <summary>
/// The single source of truth for the <see cref="PromptRunnerKind"/> wire tokens
/// (<c>claude</c> / <c>codex</c> / <c>openrouter</c> / <c>local</c> / <c>openai-compat</c>), mirroring
/// <see cref="AutonomyPolicies"/>. Shared by the loader, validation, the registry's dispatch backstop,
/// and the <c>providers init</c> generator so the spelling never forks.
/// </summary>
public static class PromptRunnerKinds
{
    /// <summary>The kind an omitted <c>kind</c> key resolves to — the additive-compatibility default.</summary>
    public const PromptRunnerKind Default = PromptRunnerKind.Claude;

    /// <summary>
    /// The kinds this BUILD can actually serve — the single source of truth for "implemented", read by
    /// the validate-time gate (GR2044) and asserted against <c>PromptRunnerRegistry.FromConfig</c>'s
    /// dispatch switch, which is the runtime backstop. Two places state the same fact, so a test pins
    /// them together rather than letting them drift.
    /// </summary>
    public static IReadOnlyList<PromptRunnerKind> Implemented { get; } =
        [PromptRunnerKind.Claude, PromptRunnerKind.OpenAiCompat];

    /// <summary>The recognised wire tokens, in declaration order — for diagnostic messages.</summary>
    public static IReadOnlyList<PromptRunnerKind> All { get; } =
        [PromptRunnerKind.Claude, PromptRunnerKind.Codex, PromptRunnerKind.OpenRouter,
         PromptRunnerKind.Local, PromptRunnerKind.OpenAiCompat];

    /// <summary>True when this build carries a concrete <c>IPromptRunner</c> for <paramref name="kind"/>.</summary>
    public static bool IsImplemented(PromptRunnerKind kind) => Implemented.Contains(kind);

    /// <summary>
    /// The kinds this BUILD can ENUMERATE the models of — a statement of fact about this build, in the
    /// same shape as <see cref="Implemented"/> right above it, and read by <c>guardrails providers init</c>
    /// (SSOT §9.7) to decide whether it may add blocks or must degrade to annotating the ones already there.
    ///
    /// <para><b><see cref="PromptRunnerKind.OpenAiCompat"/> is here because the LISTING ENDPOINT is what
    /// this member describes</b> (plan 28 §7). The kind's blocks declare an <c>endpoint</c>, and
    /// <c>GET {endpoint}/models</c> is a real, near-universal surface this build now speaks: the pre-DAG
    /// endpoint preflight reads it to assert every declared model is present before a token is spent.
    /// <see cref="PromptRunnerKind.Claude"/> stays out — the Claude CLI exposes no model list at all
    /// (settled OD-E) — so <c>providers init</c> still takes its "could not enumerate" path for a
    /// Claude-only registry, exactly as before.</para>
    ///
    /// <para><b>Enumerable is not the same as invented.</b> This member answers <i>can this kind be
    /// ASKED?</i>, and nothing about it licenses writing a model id no provider reported: a registry entry
    /// is a ROUTING TARGET, not documentation (SSOT §9.7, DoR §4.3 ruling 2). Its first reader is the
    /// pre-DAG preflight; <c>providers init</c> has no <c>openai-compat</c> enumerator wired yet, so for
    /// such a block it neither adds a block nor emits the "could not enumerate" note — the generator half
    /// of plan 28 §12 item 10 is still to land.</para>
    ///
    /// <para><b>This is a list, not a seam.</b> There is deliberately no <c>IModelEnumerator</c> stub to
    /// fill: an interface with no implementation is dead code that cannot be tested, and the generator
    /// needs exactly one fact from this file — <i>can this kind be enumerated?</i> — which a list answers
    /// honestly today.</para>
    /// </summary>
    public static IReadOnlyList<PromptRunnerKind> ModelEnumerable { get; } = [PromptRunnerKind.OpenAiCompat];

    /// <summary>
    /// True when this build can ask <paramref name="kind"/> for its model list — <c>openai-compat</c>
    /// (<c>GET {endpoint}/models</c>) and nothing else. False for <see cref="PromptRunnerKind.Claude"/>,
    /// which is what routes <c>providers init</c> down its "could not enumerate" path for a Claude-only
    /// registry, where it annotates the blocks already present, says plainly that it added none, and exits
    /// 0. It never synthesises a model id: a registry entry is a ROUTING TARGET, not documentation, so a
    /// fabricated or stale id would be spent against at a model that may not exist (SSOT §9.7, DoR §4.3
    /// ruling 2).
    /// </summary>
    public static bool HasModelEnumeration(PromptRunnerKind kind) => ModelEnumerable.Contains(kind);

    /// <summary>Every role — what an agent CLI that can write files and run commands serves.</summary>
    private static readonly IReadOnlySet<PromptRole> AllRoles =
        new HashSet<PromptRole> { PromptRole.Action, PromptRole.Guardrail, PromptRole.Advisory };

    /// <summary>
    /// The two roles a read-only HTTP verifier can honestly serve (plan 28 §3.2: v1 is a verifier, not
    /// an actor) — no <see cref="PromptRole.Action"/>, because a runner with no write tool and no shell
    /// cannot produce work.
    /// </summary>
    private static readonly IReadOnlySet<PromptRole> VerifierRoles =
        new HashSet<PromptRole> { PromptRole.Guardrail, PromptRole.Advisory };

    /// <summary>No role at all — a reserved kind with no runner class serves nothing.</summary>
    private static readonly IReadOnlySet<PromptRole> NoRoles = new HashSet<PromptRole>();

    /// <summary>
    /// Which <see cref="PromptRole"/>s a real, constructed runner of this kind actually accepts — a
    /// statement of fact about the BUILD, never a config key (plan 28 §3.5). A <c>roles:</c> key on the
    /// block would invite an operator to DECLARE a capability the assembly does not have; the operator
    /// declares preference (<c>routing</c>, <c>strength</c>), the assembly declares capability.
    ///
    /// <para>This is the SINGLE source the runner itself consults, so the fact and the refusal cannot
    /// drift: <see cref="OpenAiCompatPromptRunner"/> reads this set and refuses anything outside it,
    /// which is what lets the tests pin the answer BY CONSTRUCTION — build the real runner, hand it a
    /// real invocation of each role, and watch it proceed or refuse — rather than by reading back the
    /// same field the check reads, which would be an echo of itself.</para>
    ///
    /// <para>A kind with no implementation serves NOTHING rather than throwing: this answers a question
    /// about the build, and "which roles does a kind we cannot construct serve?" has an honest answer.
    /// The refusal for such a kind belongs to <see cref="Implemented"/> (GR2044) and to
    /// <see cref="PromptRunnerRegistry"/>'s dispatch backstop, and duplicating it here would give the
    /// same mistake two different failure shapes.</para>
    /// </summary>
    public static IReadOnlySet<PromptRole> ServesRoles(PromptRunnerKind kind) => kind switch
    {
        PromptRunnerKind.Claude => AllRoles,
        PromptRunnerKind.OpenAiCompat => VerifierRoles,
        _ => NoRoles
    };

    /// <summary>
    /// True when this kind's runner is an agent whose file writes need the SSOT §9.4 containment hook
    /// (plan 28 §3.5/§3.6). The hook polices <c>Write</c>/<c>Edit</c>/<c>MultiEdit</c>/<c>NotebookEdit</c>/
    /// <c>Bash</c> tool calls; a runner that offers none of them has nothing for it to police, and
    /// generating a Claude <c>settings.json</c> to pass as a CLI flag to an HTTP client is litter, not
    /// containment. Conditioning the splice on this is what makes the runner's own <c>--settings</c>
    /// refusal a TRUE backstop — reachable only when the splice and this fact disagree.
    ///
    /// <para><b>An unlisted kind gets TRUE, and the default direction is deliberate.</b> A future
    /// file-writing runner whose author forgets to register it here inherits the boundary rather than
    /// silently losing it; the wrong answer is then a hook that polices nothing, not an agent writing
    /// outside its worktree unobserved.</para>
    /// </summary>
    public static bool NeedsContainmentHook(PromptRunnerKind kind) => kind != PromptRunnerKind.OpenAiCompat;

    /// <summary>
    /// True when this kind's runner has a write tool and so gets the shipped verdict-file contract
    /// (<i>"you MUST end by writing your verdict as a JSON object to this absolute path"</i>); false when
    /// it can only TRANSCRIBE — emit the verdict as the last fenced <c>```json</c> block of its final
    /// message, for the harness to write (plan 28 §6.4). The composer reads this ONE boolean and emits
    /// one instruction or the other, never both, so the weakest model in the system is never holding two
    /// opposite instructions — and it learns a CAPABILITY, never a vendor name, which is what keeps the
    /// SSOT §9 quarantine intact.
    ///
    /// <para>An unlisted kind gets TRUE for the same reason as
    /// <see cref="NeedsContainmentHook"/>: a runner that CAN write files and was never registered here
    /// would otherwise be told to transcribe, and the verdict it wrote to the path would be ignored.</para>
    /// </summary>
    public static bool WritesFiles(PromptRunnerKind kind) => kind != PromptRunnerKind.OpenAiCompat;

    /// <summary>The implemented kinds as a comma-separated quoted list, for diagnostic messages.</summary>
    public static string ImplementedTokenList => string.Join(", ", Implemented.Select(k => $"'{Token(k)}'"));

    /// <summary>The recognised kinds as a comma-separated quoted list, for diagnostic messages.</summary>
    public static string TokenList => string.Join(", ", All.Select(k => $"'{Token(k)}'"));

    /// <summary>The canonical wire token for <paramref name="kind"/> (e.g. <see cref="PromptRunnerKind.OpenRouter"/> ⇒ <c>openrouter</c>).</summary>
    public static string Token(PromptRunnerKind kind) => kind switch
    {
        PromptRunnerKind.Claude => "claude",
        PromptRunnerKind.Codex => "codex",
        PromptRunnerKind.OpenRouter => "openrouter",
        PromptRunnerKind.Local => "local",
        PromptRunnerKind.OpenAiCompat => "openai-compat",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled prompt-runner kind.")
    };

    /// <summary>
    /// Parse a <c>kind</c> string (trim + case-insensitive, mirroring <c>AutonomyPolicies.TryParse</c>):
    /// <c>claude</c>, <c>codex</c>, <c>openrouter</c>, <c>local</c>, or <c>openai-compat</c> (note the
    /// hyphen — which is why this mapping is explicit rather than <c>Enum.TryParse</c>). Any other value
    /// returns <c>false</c> with <paramref name="kind"/> left at <see cref="Default"/> and the caller
    /// reports it — an unrecognised kind is REPORTED, never silently served by the default.
    /// </summary>
    public static bool TryParse(string value, out PromptRunnerKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "claude":
                kind = PromptRunnerKind.Claude;
                return true;
            case "codex":
                kind = PromptRunnerKind.Codex;
                return true;
            case "openrouter":
                kind = PromptRunnerKind.OpenRouter;
                return true;
            case "local":
                kind = PromptRunnerKind.Local;
                return true;
            case "openai-compat":
                kind = PromptRunnerKind.OpenAiCompat;
                return true;
            default:
                kind = Default;
                return false;
        }
    }
}

/// <summary>
/// What a model is FOR (charter Decision 7 / DoR §4.1) — the third per-model axis. Read by the
/// Stage 2 resolver (#226); Stage 1 only declares and validates it.
/// </summary>
public enum PromptRunnerSpecialization
{
    /// <summary>Not stated. What an absent <c>specialization</c> key resolves to, and legal to write explicitly.</summary>
    Unspecified,

    /// <summary>Writing and editing code.</summary>
    Coding,

    /// <summary>Planning, decomposition, and reasoning-heavy work (wire token <c>planning-reasoning</c>).</summary>
    PlanningReasoning,

    /// <summary>No particular strength — a generalist.</summary>
    General
}

/// <summary>
/// The single source of truth for the <see cref="PromptRunnerSpecialization"/> wire tokens
/// (<c>coding</c> / <c>planning-reasoning</c> / <c>general</c> / <c>unspecified</c>). The
/// hyphenated <c>planning-reasoning</c> spelling is why this mapping is explicit rather than
/// <c>Enum.ToString</c>.
/// </summary>
public static class PromptRunnerSpecializations
{
    /// <summary>The recognised values, in declaration order — for diagnostic and generator messages.</summary>
    public static IReadOnlyList<PromptRunnerSpecialization> All { get; } =
        [PromptRunnerSpecialization.Coding, PromptRunnerSpecialization.PlanningReasoning,
         PromptRunnerSpecialization.General, PromptRunnerSpecialization.Unspecified];

    /// <summary>
    /// The recognised tokens as a comma-separated quoted list, mirroring
    /// <see cref="PromptRunnerKinds.TokenList"/> and <see cref="ActionTiers.TokenList"/>. Read by the
    /// <c>providers init</c> generator so the legal values it comments into a user's own
    /// <c>guardrails.json</c> (SSOT §9.7) are the same spelling the validator enforces — the enum is
    /// declared once and can never drift from the form that solicits it.
    /// </summary>
    public static string TokenList => string.Join(", ", All.Select(s => $"'{Token(s)}'"));

    /// <summary>The canonical wire token for <paramref name="specialization"/>.</summary>
    public static string Token(PromptRunnerSpecialization specialization) => specialization switch
    {
        PromptRunnerSpecialization.Unspecified => "unspecified",
        PromptRunnerSpecialization.Coding => "coding",
        PromptRunnerSpecialization.PlanningReasoning => "planning-reasoning",
        PromptRunnerSpecialization.General => "general",
        _ => throw new ArgumentOutOfRangeException(
            nameof(specialization), specialization, "Unhandled prompt-runner specialization.")
    };

    /// <summary>
    /// Parse a <c>specialization</c> string (trim + case-insensitive): <c>coding</c>,
    /// <c>planning-reasoning</c>, <c>general</c>, or <c>unspecified</c>. <c>unspecified</c> is
    /// WRITABLE, not merely the absent-key fallback. Any other value returns <c>false</c> and the caller
    /// reports it rather than quietly resolving to
    /// <see cref="PromptRunnerSpecialization.Unspecified"/>, which would leave the operator believing
    /// they had expressed a routing preference they had not.
    /// </summary>
    public static bool TryParse(string value, out PromptRunnerSpecialization specialization)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "unspecified":
                specialization = PromptRunnerSpecialization.Unspecified;
                return true;
            case "coding":
                specialization = PromptRunnerSpecialization.Coding;
                return true;
            case "planning-reasoning":
                specialization = PromptRunnerSpecialization.PlanningReasoning;
                return true;
            case "general":
                specialization = PromptRunnerSpecialization.General;
                return true;
            default:
                specialization = PromptRunnerSpecialization.Unspecified;
                return false;
        }
    }
}

/// <summary>
/// The optional per-model <c>routing</c> block (SSOT §9, issue #224): it opts the block INTO tier
/// resolution. Its presence is what makes tiering "configured" for a plan; its absence means the block
/// is never a tier target at all.
///
/// <para><b>The block splits hard along one line: <see cref="Tiers"/> is MACHINE-CONSUMED, everything
/// else is for humans.</b> <see cref="Tiers"/> is the only key the candidacy predicate
/// (<see cref="PromptRunnerConfig.ServesTier"/>) reads, and it is REQUIRED (GR2047) precisely because a
/// <c>routing</c> block without it declares an eligibility it cannot express. <see cref="Notes"/> /
/// <see cref="Guidance"/> / <see cref="Tags"/> are surfaced to humans and MAY be appended to a composed
/// prompt as context — they are <b>NEVER parsed to make a routing decision</b> (DoR invariant 1: no LLM
/// and no prose ever picks a model).</para>
///
/// <para><b><c>rank</c> is deliberately absent.</b> It is a RETIRED key (settled OD-F): ordering is
/// ascending <see cref="PromptRunnerConfig.Strength"/>, not a hand-written rank. A config still
/// carrying <c>routing.rank</c> gets a retired-field WARNING (GR2046) — modelling it here would be the
/// silent acceptance the warning exists to prevent. To say "this block should not serve that rung",
/// remove the rung from <see cref="Tiers"/>; eligibility says <i>may</i>, <c>strength</c> says <i>how
/// strong</i>, and nothing needs to say <i>prefer</i>.</para>
/// </summary>
public sealed record PromptRunnerRouting
{
    /// <summary>
    /// The difficulty rungs this (kind, model, effort) route MAY serve — a non-empty subset of
    /// <see cref="ActionTiers.All"/>, REQUIRED whenever a <c>routing</c> block is present (GR2047).
    /// This is the machine-consumed half of routing and the only key
    /// <see cref="PromptRunnerConfig.ServesTier"/> reads. Held VERBATIM as declared (the loader rejects
    /// anything outside the enum rather than normalising it), so the order here is the author's.
    /// </summary>
    public IReadOnlyList<string> Tiers { get; init; } = [];

    /// <summary>
    /// Human-authored prose rationale for this route (the DoR's <c>notes</c>). Surfaced to humans and
    /// MAY be appended to a composed prompt as context — never parsed for a routing decision. Null = the
    /// key was absent.
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Free prose describing the work this model suits — the Stage-1 spelling, kept because it is
    /// additive and harmless. Semantically the same human-facing surface as <see cref="Notes"/>; neither
    /// is ever parsed. Null = the key was absent.
    /// </summary>
    public string? Guidance { get; init; }

    /// <summary>
    /// Free-form guidance tags from Stage 1. Advisory context for humans and composed prompts — these
    /// are NOT tier tokens and nothing routes on them (<see cref="Tiers"/> is the routing surface).
    /// Empty = the key was absent.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
