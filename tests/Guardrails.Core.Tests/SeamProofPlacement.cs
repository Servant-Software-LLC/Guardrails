using System.Text.RegularExpressions;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// The <b>T\*</b> placement rule of #382 (<c>docs/plans/18-integration-proof-proximity.md</c> §1.4),
/// implemented as a folder-observable audit so it can be asserted by a test instead of only by a
/// reviewer's eye.
///
/// <para><b>Why this lives in tests/ and not in the validator.</b> Decision <b>D1</b> of the design of
/// record: #382 v1 ships <i>no</i> <c>guardrails validate</c> code and <i>no</i> GR code, because the
/// defect's carrier does not exist at validate time and the only pre-run signal is prose whose correct
/// and incorrect forms are identical. <c>GR2061</c> stays reserved behind §3.4's evidence gate. This
/// type is therefore deliberately NOT production code — it is the meta-test's own reference
/// implementation of the rule, and the committed fixtures are what keep it honest.</para>
///
/// <para><b>What it computes.</b> §1.4: <i>"a type exists at a task when that task's</i>
/// <c>writeScope</c> <i>contains the file that declares it, or an ancestor's does"</i>, so
/// <b>T\*</b> — the earliest task at which BOTH the component's production type and the seam's
/// production type exist — is computable from the emitted DAG alone. That computability is the whole
/// reason the rule could replace the unfalsifiable <i>"where feasible"</i>, and until something
/// executes it, it is a claim.</para>
///
/// <para><b>The #378 boundary holds (plan-breakdown Step 4 rule 7, doc 18 §6/D9).</b> This audit reads
/// <c>writeScope</c> as a <i>lookup</i> — "which task declares this type?" — and derives no SIZE
/// verdict from its cardinality, from <c>action.maxTurns</c>, or from <c>dependsOn</c> fan-in. Rule 7
/// names that reading explicitly as NOT a boundary crossing. Its verdict is only ever
/// <i>"this proof is in the wrong task"</i>, never <i>"this task is too big"</i> (GR2042 owns that).</para>
///
/// <para><b>The honest limit — see <see cref="RealSeamProofMarkers"/>.</b> Per <b>D13</b> the seam
/// ledger has no home on disk, so this audit cannot read the authoritative statement of which seam a
/// guardrail proves; it recovers the seam from the guardrail's own <c>catches:</c> declaration against
/// the plan's declared type universe. A guardrail this audit cannot resolve is reported as
/// <see cref="SeamProofFindingKind.SeamNotResolvable"/> — never silently passed. Silence over an
/// unreadable proof would be the same passing-but-blind shape #382 exists to remove.</para>
/// </summary>
public static class SeamProofPlacement
{
    /// <summary>
    /// The two folder-observable tells that a guardrail IS a real-seam proof. Neither is a declared
    /// contract today — <c>03-real-seam-tests-pass.ps1</c> is the filename the plan-breakdown ledger
    /// example emits, and <c>passing-but-blind</c> is the token the catalogue's <c># catches:</c>
    /// template carries. <c>SeamDoctrineAnchorTests</c> pins BOTH to their skill sources, so a skill
    /// edit that stops emitting them turns red there rather than silently emptying this audit.
    /// </summary>
    public static class RealSeamProofMarkers
    {
        /// <summary>The name fragment of the guardrail file the ledger's <c>proof</c> column points at.</summary>
        public const string NameFragment = "real-seam";

        /// <summary>The catalogue <c># catches:</c> template's distinctive token.</summary>
        public const string CatchesToken = "passing-but-blind";
    }

    /// <summary>
    /// Every placement finding in <paramref name="plan"/>, ordered by guardrail identity so the result
    /// is deterministic. EMPTY means every real-seam proof this audit could see sits at its own T\*.
    /// </summary>
    public static IReadOnlyList<SeamProofFinding> Audit(PlanDefinition plan)
    {
        IReadOnlyList<TaskNode> order = TopologicalOrder(plan);
        Dictionary<string, HashSet<string>> availability = AvailabilityByTask(plan, order);
        HashSet<string> universe = [.. availability.Values.SelectMany(types => types)];

        var findings = new List<SeamProofFinding>();

        // The plan-root <plan>/guardrails/ folder is the OTHER terminal object §1.5 names. It runs once,
        // at run end, on the merged HEAD — so it is later than every T* by construction and can never be
        // the home of a real-seam proof, whatever the DAG looks like.
        foreach (GuardrailDefinition guardrail in plan.PlanGuardrails.Where(IsRealSeamProof))
        {
            findings.Add(new SeamProofFinding(
                guardrail.Name,
                PlanRootOwner,
                ExpectedTaskId: null,
                SeamTypes: [.. SeamTypesNamedBy(guardrail, universe)],
                Kind: SeamProofFindingKind.InPlanRootSink,
                Detail:
                    $"The real-seam proof '{guardrail.Name}' sits in the plan-root guardrails folder, which " +
                    "is evaluated ONCE on the merged HEAD at run end. That is later than every T* by " +
                    "construction. The terminal proof is a JOIN-CHECK (doc 18 §1.5): it may assert only " +
                    "ASSEMBLY, and its `# catches:` must name a defect that survives every upstream " +
                    "real-seam proof passing. Move the proof to T*."));
        }

        foreach (TaskNode task in order)
        {
            foreach (GuardrailDefinition guardrail in task.Guardrails.Where(IsRealSeamProof))
            {
                if (Evaluate(task, guardrail, order, availability, universe) is { } finding)
                {
                    findings.Add(finding);
                }
            }
        }

        return [.. findings.OrderBy(f => f.OwningTaskId, StringComparer.Ordinal)
                           .ThenBy(f => f.GuardrailName, StringComparer.Ordinal)];
    }

    /// <summary>The pseudo task-id used for a proof found in <c>&lt;plan&gt;/guardrails/</c>.</summary>
    public const string PlanRootOwner = "<plan>/guardrails";

    /// <summary>
    /// Every real-seam proof this audit can SEE, whether or not it is mis-placed — so a test can assert
    /// the audit was not vacuous. An audit that reports no findings because it found no proofs to check
    /// is the failure mode this method exists to make visible.
    /// </summary>
    public static IReadOnlyList<string> RealSeamProofs(PlanDefinition plan) =>
    [
        .. plan.PlanGuardrails.Where(IsRealSeamProof).Select(g => $"{PlanRootOwner}/{g.Name}")
             .Concat(plan.Tasks.SelectMany(t => t.Guardrails.Where(IsRealSeamProof)
                                                 .Select(g => $"{t.Id}/{g.Name}")))
             .OrderBy(s => s, StringComparer.Ordinal)
    ];

    /// <summary>
    /// True when <paramref name="guardrail"/> is a real-seam proof by either folder-observable tell.
    /// Prompt guardrails are excluded: the archetype has no rung-3 form and is always a TEST (#468).
    /// </summary>
    public static bool IsRealSeamProof(GuardrailDefinition guardrail) =>
        guardrail.Kind == ActionKind.Script
        && (guardrail.Name.Contains(RealSeamProofMarkers.NameFragment, StringComparison.OrdinalIgnoreCase)
            || CatchesDeclaration(guardrail)
                .Contains(RealSeamProofMarkers.CatchesToken, StringComparison.OrdinalIgnoreCase));

    // ---- the rule ------------------------------------------------------------------------------

    private static SeamProofFinding? Evaluate(
        TaskNode owner,
        GuardrailDefinition guardrail,
        IReadOnlyList<TaskNode> order,
        IReadOnlyDictionary<string, HashSet<string>> availability,
        IReadOnlySet<string> universe)
    {
        IReadOnlyList<string> seamTypes = SeamTypesNamedBy(guardrail, universe);

        // §1.4 needs BOTH types. One or none and T* is undefined, so the audit cannot reach a verdict —
        // and says so rather than passing.
        if (seamTypes.Count < 2)
        {
            return new SeamProofFinding(
                guardrail.Name, owner.Id, ExpectedTaskId: null, seamTypes,
                SeamProofFindingKind.SeamNotResolvable,
                $"'{guardrail.Name}' reads as a real-seam proof, but its `catches:` declaration names " +
                $"{seamTypes.Count} of the 2 production types T* needs (found: " +
                $"{Describe(seamTypes)}). T* is the earliest task at which BOTH the component's and the " +
                "seam's production type exist, so it cannot be computed for this guardrail. Name both " +
                "types in `catches:` using the spelling their declaring file uses in some task's " +
                "writeScope. (Per D13 the seam ledger — the authoritative statement — has no home on " +
                "disk, so `catches:` is the only folder-observable source.)");
        }

        TaskNode? tStar = order.FirstOrDefault(t => seamTypes.All(availability[t.Id].Contains));
        if (tStar is null)
        {
            return new SeamProofFinding(
                guardrail.Name, owner.Id, ExpectedTaskId: null, seamTypes,
                SeamProofFindingKind.SeamNotResolvable,
                $"No task in the DAG has both {Describe(seamTypes)} available, so T* does not exist for " +
                $"'{guardrail.Name}'. Either a production type is missing from every writeScope (the " +
                "seam is bucket U and the proof is RELOCATED, not owed here), or a writeScope is wrong.");
        }

        if (string.Equals(tStar.Id, owner.Id, StringComparison.Ordinal))
        {
            return null;
        }

        int ownerIndex = IndexOf(order, owner.Id);
        int tStarIndex = IndexOf(order, tStar.Id);

        return ownerIndex > tStarIndex
            ? new SeamProofFinding(
                guardrail.Name, owner.Id, tStar.Id, seamTypes,
                SeamProofFindingKind.LaterThanTStar,
                $"'{guardrail.Name}' proves the {Describe(seamTypes)} seam but sits on '{owner.Id}', " +
                $"LATER than T* = '{tStar.Id}' — the earliest task at which both production types exist. " +
                "A proof placed later than T* is a finding EVEN WHEN IT EXISTS AND PASSES (doc 18 §1.4): " +
                "it surfaces the bug in a task whose writeScope cannot fix it, which is the needsHuman " +
                "this doctrine exists to remove. Move it to T*, or name T* and state why it cannot live " +
                "there.")
            : new SeamProofFinding(
                guardrail.Name, owner.Id, tStar.Id, seamTypes,
                SeamProofFindingKind.EarlierThanTStar,
                $"'{guardrail.Name}' proves the {Describe(seamTypes)} seam but sits on '{owner.Id}', " +
                $"EARLIER than T* = '{tStar.Id}'. Not a deferral — the opposite: at least one production " +
                "type does not exist yet at this task, so the proof cannot pass here and is red forever, " +
                "which no retry can fix. Move it to T*.");
    }

    // ---- DAG helpers ---------------------------------------------------------------------------

    /// <summary>
    /// A deterministic topological order: Kahn's algorithm with the ready set drained in ordinal
    /// task-id order, so the same plan always yields the same T*. Tasks in a cycle (which the loader
    /// already rejects as GR2007) are appended in id order rather than dropped, so this never silently
    /// loses a task.
    /// </summary>
    public static IReadOnlyList<TaskNode> TopologicalOrder(PlanDefinition plan)
    {
        Dictionary<string, TaskNode> byId = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        Dictionary<string, int> remaining = plan.Tasks.ToDictionary(
            t => t.Id, t => t.DependsOn.Count(byId.ContainsKey), StringComparer.Ordinal);

        var ready = new SortedSet<string>(
            remaining.Where(kv => kv.Value == 0).Select(kv => kv.Key), StringComparer.Ordinal);

        var order = new List<TaskNode>(plan.Tasks.Count);
        while (ready.Count > 0)
        {
            string next = ready.Min!;
            ready.Remove(next);
            order.Add(byId[next]);

            foreach (TaskNode dependent in plan.Tasks.Where(t => t.DependsOn.Contains(next, StringComparer.Ordinal)))
            {
                if (--remaining[dependent.Id] == 0)
                {
                    ready.Add(dependent.Id);
                }
            }
        }

        // Anything left is inside a cycle (GR2007, which the loader already rejects). Appended in id
        // order rather than dropped, so this never silently loses a task. Materialized before the add,
        // so the predicate reads a stable `order`.
        HashSet<string> placed = [.. order.Select(t => t.Id)];
        List<TaskNode> unreachable =
        [
            .. plan.Tasks.Where(t => !placed.Contains(t.Id)).OrderBy(t => t.Id, StringComparer.Ordinal)
        ];

        order.AddRange(unreachable);
        return order;
    }

    /// <summary>
    /// Per task, the production types available AT it: the types its own <c>writeScope</c> declares,
    /// unioned with every ancestor's — §1.4's "or an ancestor's does". Computed in topological order so
    /// each task's ancestors are already resolved.
    /// </summary>
    private static Dictionary<string, HashSet<string>> AvailabilityByTask(
        PlanDefinition plan, IReadOnlyList<TaskNode> order)
    {
        var availability = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (TaskNode task in order)
        {
            var available = new HashSet<string>(TypesDeclaredBy(task), StringComparer.Ordinal);
            foreach (string parent in task.DependsOn)
            {
                if (availability.TryGetValue(parent, out HashSet<string>? inherited))
                {
                    available.UnionWith(inherited);
                }
            }

            availability[task.Id] = available;
        }

        return availability;
    }

    /// <summary>
    /// The types a task's own <c>writeScope</c> declares: one per C# source file, named by the file.
    /// This is the file-declares-the-type convention §1.4 states, and it is the reason T* is computable
    /// without reading any source.
    /// </summary>
    private static IEnumerable<string> TypesDeclaredBy(TaskNode task)
    {
        IEnumerable<string> writeScope = task.WriteScope ?? Enumerable.Empty<string>();

        return writeScope
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'))!)
            .Where(name => name.Length > 0);
    }

    // ---- reading the guardrail -------------------------------------------------------------------

    /// <summary>
    /// The production types a guardrail's <c>catches:</c> declaration names, intersected with the
    /// plan's own declared type universe. A CLOSED-VOCABULARY match, not free-form parsing: the audit
    /// never invents a type, it only recognises one the DAG already declares. Word-boundary matched, so
    /// <c>CriticalityJudge</c> does not match inside <c>CriticalityJudgeTests</c>.
    /// </summary>
    private static IReadOnlyList<string> SeamTypesNamedBy(
        GuardrailDefinition guardrail, IReadOnlySet<string> universe)
    {
        string catches = CatchesDeclaration(guardrail);
        return
        [
            .. universe.Where(type => Regex.IsMatch(catches, $@"\b{Regex.Escape(type)}\b"))
                       .OrderBy(type => type, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The guardrail's LEADING comment block — the <c>catches:</c> declaration SSOT §4 requires a
    /// guardrail to open with. Deliberately not the whole file: reading the body would let an
    /// incidental mention of a type in a command line change which seam the audit thinks is proven.
    /// An unreadable file yields empty, which surfaces as <see cref="SeamProofFindingKind.SeamNotResolvable"/>
    /// rather than as a pass.
    /// </summary>
    private static string CatchesDeclaration(GuardrailDefinition guardrail)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(guardrail.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }

        var block = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith('#') && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            block.Add(trimmed);
        }

        return string.Join("\n", block);
    }

    private static int IndexOf(IReadOnlyList<TaskNode> order, string id)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Describe(IReadOnlyList<string> types) =>
        types.Count == 0 ? "(none)" : string.Join(" -> ", types);
}

/// <summary>Why a real-seam proof's placement is a finding.</summary>
public enum SeamProofFindingKind
{
    /// <summary>
    /// The defect #382 exists to remove: the proof is deferred past T*, into a task whose
    /// <c>writeScope</c> cannot fix what it finds.
    /// </summary>
    LaterThanTStar,

    /// <summary>The proof sits before its component exists — red forever, and no retry can fix it.</summary>
    EarlierThanTStar,

    /// <summary>
    /// The proof is in <c>&lt;plan&gt;/guardrails/</c>, which runs once on the merged HEAD and is
    /// therefore later than every T* by construction (doc 18 §1.5).
    /// </summary>
    InPlanRootSink,

    /// <summary>
    /// T* could not be computed for this guardrail. Reported, never passed — the audit says what it
    /// could not check.
    /// </summary>
    SeamNotResolvable
}

/// <summary>One mis-placed (or unreadable) real-seam proof, with the remedy spelled out.</summary>
/// <param name="GuardrailName">The guardrail as the harness names it.</param>
/// <param name="OwningTaskId">The task folder the proof actually sits in.</param>
/// <param name="ExpectedTaskId">T*, when it could be computed.</param>
/// <param name="SeamTypes">The production types the audit recovered from the <c>catches:</c> declaration.</param>
/// <param name="Kind">Which placement rule was broken.</param>
/// <param name="Detail">An actionable sentence naming the remedy.</param>
public sealed record SeamProofFinding(
    string GuardrailName,
    string OwningTaskId,
    string? ExpectedTaskId,
    IReadOnlyList<string> SeamTypes,
    SeamProofFindingKind Kind,
    string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind} [{OwningTaskId}/{GuardrailName}]: {Detail}";
}
