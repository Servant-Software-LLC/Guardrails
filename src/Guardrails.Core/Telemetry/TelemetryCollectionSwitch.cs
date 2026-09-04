namespace Guardrails.Core.Telemetry;

/// <summary>
/// The single definition of the collection opt-out (SSOT §15.6): <c>GUARDRAILS_TELEMETRY=off</c>
/// (case-insensitive) disables collection; any other value, or unset, leaves it ON.
///
/// <para><b>Why the rule moved OUT of <see cref="TelemetryCorpusStore"/>.</b> It used to be read from the
/// environment inside <c>Append</c> — that is, at WRITE time, on every row. An environment variable is
/// PROCESS-GLOBAL state, so a store's behavior depended on what the whole process happened to look like at
/// the instant of the write rather than on anything the caller had said. Under the concurrent
/// whole-solution test profile (#566) that produced a real, measured failure: one test set the variable to
/// <c>off</c> around its own invocation, and every OTHER test writing to its own perfectly isolated corpus
/// root during that window silently wrote nothing and then failed reading its rows back. Six tests, all
/// with correct isolation, all defeated by ambient state — and the harness's own rule is injectable probes
/// over machine-state dependence.</para>
///
/// <para><b>The split.</b> <see cref="IsEnabled"/> is a PURE function of a value, so the token semantics
/// can be tested exhaustively without mutating anything. <see cref="IsEnabledFromEnvironment"/> is the one
/// place the process environment is consulted, and it is called only from a composition root — the
/// <c>telemetry</c> verb and run-end ingest — which then hands the resolved decision to the store. Two
/// mechanisms for one decision is how a machine ends up opted out of one path and not the other; this is
/// still one mechanism, just resolved at the edge instead of in the leaf.</para>
/// </summary>
public static class TelemetryCollectionSwitch
{
    /// <summary>
    /// The opt-out environment variable. Named here rather than on the store because the store no longer
    /// reads it — see the class doc.
    /// </summary>
    public const string OptOutEnvVar = "GUARDRAILS_TELEMETRY";

    /// <summary>The one value that disables collection.</summary>
    private const string OffValue = "off";

    /// <summary>
    /// Whether collection is on, given <paramref name="rawValue"/> as the opt-out variable's value
    /// (<see langword="null"/> for unset). ONLY the exact token <c>off</c>, case-insensitively, disables
    /// collection.
    ///
    /// <para>Deliberately NOT a general truthiness check. Treating <c>0</c>, <c>false</c> or <c>no</c> as
    /// "off" too would look helpful and would mean an operator who typed one of them believed collection
    /// was off for months while a different code path — or a different version — read it as on. One
    /// spelling, documented, and everything else is on.</para>
    /// </summary>
    public static bool IsEnabled(string? rawValue) =>
        !string.Equals(rawValue, OffValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="IsEnabled"/> applied to the live process environment. The ONE environment read in the
    /// telemetry stack; call it from a composition root and pass the result down, never from inside a
    /// write path.
    /// </summary>
    public static bool IsEnabledFromEnvironment() =>
        IsEnabled(Environment.GetEnvironmentVariable(OptOutEnvVar));
}
