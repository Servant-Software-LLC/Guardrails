using System.Text;

namespace Guardrails.Core.Execution;

/// <summary>
/// The ONE encoding the harness pins on every redirected child-process stream (issues #55, #457).
/// <para>
/// Without an explicit <see cref="System.Diagnostics.ProcessStartInfo.StandardOutputEncoding"/>, .NET
/// decodes a redirected child's stdout/stderr with the host CONSOLE code page — on Windows the OEM code
/// page (CP437/850), on Unix <see cref="Console.OutputEncoding"/> — NOT UTF-8. Every tool the harness
/// spawns (git, pwsh, bash, <c>claude</c>) emits UTF-8, so an unpinned stream mis-decodes every
/// multi-byte character: an em dash <c>—</c> (<c>E2 80 94</c>) becomes <c>ΓÇö</c> under CP437.
/// </para>
/// <para>
/// <b>Why this is a data-integrity concern, not a cosmetic one (issue #457).</b> Where the captured
/// text is only LOGGED the damage is ugly; where it is written BACK to a tracked file the damage is
/// permanent. <see cref="AiMergeResolver"/> does exactly that: it captures <c>git show &lt;ref&gt;:&lt;file&gt;</c>
/// to build the three-way MERGE_BASE/OURS/THEIRS inputs, and the resolution derived from them overwrites
/// the conflicted file in the integration worktree. One unpinned stream there destroyed every multi-byte
/// character in a 388 KB tracked document (1077 em dashes, 503 section signs, 146 box-drawing, 126 arrows,
/// 86 ellipses → ZERO survivors) and inflated it 388 KB → 404 KB, because the mojibake was re-encoded as
/// UTF-8 on the way back to disk.
/// </para>
/// <para>
/// The no-BOM form is load-bearing specifically on the
/// <see cref="System.Diagnostics.ProcessStartInfo.StandardInputEncoding"/> (encode) path: a BOM-emitting
/// encoder would prepend <c>EF BB BF</c> to the child's stdin, corrupting the head of a composed prompt
/// fed to <c>claude -p</c>. On the stdout/stderr (decode) paths the BOM flag is irrelevant — a decoder
/// strips a leading BOM either way — but one shared no-BOM instance keeps every stream consistent and
/// matches the harness's own UTF-8-no-BOM writes (<see cref="State.AtomicFile"/>).
/// </para>
/// <para>
/// <b>Applies to UTF-8 children only.</b> Every <c>git</c> invocation qualifies (git emits UTF-8 for
/// paths, messages, and blob bytes alike), as do the prompt/guardrail processes behind
/// <see cref="ProcessRunner"/>. It must NOT be applied to a child that genuinely speaks the OEM code
/// page — <c>cmd.exe</c> built-ins such as <c>mklink</c> (see <see cref="WorktreeJunction"/>) write in
/// the console code page, and pinning UTF-8 there would introduce the mirror-image defect.
/// </para>
/// </summary>
internal static class ChildProcessEncoding
{
    /// <summary>UTF-8 without a byte-order mark — the single shared instance for every pinned stream.</summary>
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
