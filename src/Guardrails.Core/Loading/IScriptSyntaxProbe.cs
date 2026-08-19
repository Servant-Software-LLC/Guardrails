namespace Guardrails.Core.Loading;

/// <summary>
/// Parse-checks guardrail SCRIPTS without running them (issue #473, the safe core of #478).
///
/// <para>An unparseable guardrail fails unconditionally and no retry can fix it: the task burns its
/// whole budget and dead-ends at <c>needs-human</c>. Measured cost of one instance: two attempts plus
/// a halt, on a script whose only defect was a stray backtick inside a double-quoted string.</para>
///
/// <para><b>Parsing is not executing, and the distinction is the whole design.</b> <c>validate</c> is
/// a fast, read-only check people run constantly and CI runs on every push; it must never execute a
/// plan's scripts, which build, test, and write files. So this probe asks an interpreter only whether
/// the text PARSES. The remaining checks #478 wanted — a guardrail that is already green, one that
/// throws at runtime, a filter matching nothing — genuinely require execution and therefore live in
/// the <c>/guardrails-review</c> and <c>/plan-breakdown</c> skill phases (#479), where a human or agent
/// is driving and can accept the cost and the side effects.</para>
///
/// <para>Injected rather than called directly, mirroring <c>IExecutableProbe</c>: the validator stays a
/// near-pure function over a <c>PlanDefinition</c>, and the check is unit-testable without an
/// interpreter on PATH.</para>
/// </summary>
public interface IScriptSyntaxProbe
{
    /// <summary>
    /// Parse-check <paramref name="scriptPaths"/> and return one entry per script that FAILED to parse,
    /// keyed by path, valued by the interpreter's message.
    ///
    /// <para><b>Absence of an entry never means "valid"</b> — it means "not reported as invalid". A
    /// script in a language this probe cannot check, or one it could not reach an interpreter for, is
    /// simply omitted. Silence is the correct behaviour for an unavailable interpreter: refusing to
    /// validate a plan because <c>pwsh</c> is missing would punish the operator for something the plan
    /// author cannot control, and a machine that cannot parse the script also cannot run it.</para>
    /// </summary>
    IReadOnlyDictionary<string, string> FindSyntaxErrors(IReadOnlyList<string> scriptPaths);
}

/// <summary>An <see cref="IScriptSyntaxProbe"/> that reports nothing — the no-interpreter default.</summary>
public sealed class NullScriptSyntaxProbe : IScriptSyntaxProbe
{
    /// <summary>The shared instance; the probe is stateless.</summary>
    public static readonly NullScriptSyntaxProbe Instance = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> FindSyntaxErrors(IReadOnlyList<string> scriptPaths) =>
        new Dictionary<string, string>(StringComparer.Ordinal);
}
