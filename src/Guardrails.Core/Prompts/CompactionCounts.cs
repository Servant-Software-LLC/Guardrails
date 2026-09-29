namespace Guardrails.Core.Prompts;

/// <summary>
/// How often a session's context was compacted (#817), counted from the runner's own stream. Null wherever it is
/// carried when the session compacted nothing (or its runner does not report compactions), so an attempt with no
/// compaction records no key at all rather than a row of zeros.
/// <para>
/// <b>A compaction is an EPISODE, not a status line.</b> Claude Code re-emits
/// <c>{"type":"system","subtype":"status","status":"compacting"}</c> as a keep-alive for as long as one compaction
/// runs: the #817 dogfood attempt that carried 114 such lines compacted 7 times (one run of 73 lines was a single
/// compaction that timed out). An episode opens on the first <c>compacting</c> line and closes on its
/// <c>compact_result</c> (success or failure) or its <c>compact_boundary</c>, whichever comes first; the other one,
/// arriving straight after, belongs to the same compaction. A close with no open episode is still one compaction.
/// </para>
/// </summary>
/// <param name="Compactions">The number of compaction episodes, including any still running when the stream ended.</param>
/// <param name="Failures">How many of them reported <c>"compact_result":"failed"</c>.</param>
public sealed record CompactionCounts(int Compactions, int Failures);
