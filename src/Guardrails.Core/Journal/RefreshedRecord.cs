namespace Guardrails.Core.Journal;

/// <summary>
/// One entry in <c>run.json</c>'s <c>refreshed[]</c> (design 39 §1c "How a refresh is recorded" / §5) —
/// provenance for a post-delivery merge that pulled the plan branch back onto the user's branch after a
/// non-fast-forward barrier delivery.
/// <para>
/// <b>NOT a supply.</b> A refresh admits content no task authored, exactly as a <see cref="SuppliedRecord"/>
/// does, but it has no caller to put in <c>by</c>, a <c>Supplied-By:</c> trailer on it would be a false
/// statement, and a merge has no honest <c>bytes</c>. So it gets its own top-level section — never a
/// <c>kind</c> field on <see cref="SuppliedRecord"/>, and never an entry in <c>supplied[]</c>. What the two
/// share is ONE reader, <see cref="UnauthoredContentNote"/>, which reads BOTH sections to answer "what is in
/// this tree that no task authored?".
/// </para>
/// <para>
/// STUB (task 24): every getter throws <see cref="NotImplementedException"/> until task 25 implements the
/// record for real; the <c>init</c> accessors are real so a caller can build one with object-initializer
/// syntax before the type is otherwise usable, matching the pattern <see cref="WaveDeliveredRecord"/> used
/// for the same reason.
/// </para>
/// </summary>
public sealed record RefreshedRecord
{
    /// <summary>UTC time the refresh commit was made (ISO-8601).</summary>
    public required DateTimeOffset At { get => throw new NotImplementedException(); init { } }

    /// <summary>The refresh merge commit on the plan branch.</summary>
    public required string Commit { get => throw new NotImplementedException(); init { } }

    /// <summary>The user's branch that was merged in — the delivery target pinned at run start.</summary>
    public required string From { get => throw new NotImplementedException(); init { } }

    /// <summary>The sha that was merged — the refresh commit's second parent.</summary>
    public required string Upstream { get => throw new NotImplementedException(); init { } }

    /// <summary>The wave whose non-fast-forward delivery triggered this refresh.</summary>
    public required string DeliveredWave { get => throw new NotImplementedException(); init { } }

    /// <summary>The paths the refresh changed, forward-slash, ordinal-sorted.</summary>
    public required IReadOnlyList<string> Paths { get => throw new NotImplementedException(); init { } }
}
