namespace Guardrails.Cli;

/// <summary>
/// An EXPLICIT telemetry anchor for one construction of the CLI — where the corpus lives and whether
/// collection happens — so neither has to be read from the ambient process environment (issue #594).
///
/// <para><b>The defect this closes.</b> <c>guardrails run</c> resolved both from process-wide environment
/// variables, and there was no per-invocation channel. A test that needed its own corpus had exactly one
/// way to get it: <see cref="Environment.SetEnvironmentVariable(string,string)"/>, which mutates the WHOLE
/// PROCESS. A <c>try/finally</c> restores the variable but cannot close the window — xUnit runs classes in
/// parallel, and every other class spawning a run during it wrote its rows into the first class's
/// directory.</para>
///
/// <para><b>Measured</b>, by plan 34's terminal gate:
/// <c>RunEndTelemetryIngestTests.Run_CollectionDisabled_SuppressesIngestThatOtherwiseHappens</c> expected
/// 8 rows and found 10. Isolated it passed 3/3; in the full suite it failed. The two extra rows came from
/// plan 34's new <c>AttachReplayTests</c> and its seven real runs — a class that touches no telemetry code
/// at all, and was merely the first to spawn runs while the window was open.</para>
///
/// <para><b>Why not a collection guard.</b> The obvious repair is
/// <c>DisableParallelization</c> binding polluters to victims, as <c>GitEnvironmentCollection</c> does for
/// <c>GIT_DIR</c>. The exposure was measured first: FOUR classes set a process-wide telemetry variable and
/// TWENTY-PLUS spawn a real run. Serializing that costs a large slice of the integration suite's wall clock
/// on every CI run, forever — and it would not protect a test that does not exist yet. The git case was
/// cheap because its polluter lived in one class; here the polluter side is most of the assembly.</para>
///
/// <para><b>Explicit beats ambient; ambient still works.</b> Both fields are nullable and null means
/// "resolve as before" — the environment variables remain the operator's documented channel and their
/// behaviour is byte-identical for anyone who passes nothing. This is exactly the shape #593 gave
/// <c>GitLsFilesProbe</c>'s <c>workingDirectory</c>: a way to ASK, added without changing the answer for
/// callers who do not.</para>
///
/// <para>This is the THIRD time ambient environment has been the anchor in a defect worth filing (#547 at
/// the process boundary, #593 for git's repo discovery, #594 here), which is why the repair is a seam
/// rather than another guard around the symptom.</para>
/// </summary>
/// <param name="CorpusRoot">
/// Where corpus rows are written. Null resolves through <c>GUARDRAILS_TELEMETRY_CORPUS_ROOT</c> and then
/// the real <c>~/.guardrails/telemetry/</c>, exactly as before.
/// </param>
/// <param name="CollectionEnabled">
/// Whether collection happens at all. Null resolves through <c>GUARDRAILS_TELEMETRY</c>, exactly as before.
/// </param>
public sealed record TelemetryOverrides(string? CorpusRoot = null, bool? CollectionEnabled = null)
{
    /// <summary>Nothing overridden — the production default, where both answers come from the environment.</summary>
    public static readonly TelemetryOverrides None = new();
}
