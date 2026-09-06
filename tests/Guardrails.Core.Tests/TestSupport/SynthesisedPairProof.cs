using Guardrails.Core.Execution;

namespace Guardrails.TestSupport;

/// <summary>
/// Issue #530 — proof that a guardrail body a TEST synthesised actually DISCRIMINATES the two halves of
/// the sample pair the test hands it.
///
/// <para>
/// The defect this exists to catch was measured in run <c>2026-08-29T08-35-58Z-6b90</c>, plan
/// <c>26-guardrail-quality-gate</c>. A test fixture synthesised the body
/// <c>if ((Get-Content $SubjectPath -Raw) -match 'DEFECT') { exit 1 } else { exit 0 }</c> and a valid half
/// reading <c>"a clean artifact, no defect here"</c>. PowerShell's <c>-match</c> is case-INSENSITIVE, so
/// the valid half matched too and BOTH halves exited 1. The pair was decorative: the body could not tell
/// them apart, so nothing it reported was evidence about anything.
/// </para>
///
/// <para>
/// What makes that class expensive is WHERE it hides. A test-author task's red census demands each
/// enumerated behaviour be observed <c>Failed</c> on the stub tree — so a test that can NEVER pass is
/// exactly as green to the census as a test that fails for the right reason. Red is the census's success
/// condition, which makes it structurally blind to a test that is red for a reason no implementation can
/// remove. The bill arrives one task later, when the implementation task cannot go green and halts
/// <c>needs-human</c>: four such tests halted that run. GR2055 (a guardrail that cannot pass for any
/// input, #484) already lints the same shape in a GUARDRAIL; inside a test's own synthesised fixture it
/// had no check at all.
/// </para>
///
/// <para>
/// The property asserted here is DISTINCTNESS, deliberately, and not polarity. A fixture that inverts its
/// pair on purpose — to drive the verifier's rejection path — is sound and must stay assertable, so the
/// direction of the discrimination belongs to the caller's own assertions. What no fixture may be is
/// undiscriminating: two halves that produce the SAME exit code prove the body is reading something other
/// than the difference between them, whichever code that is.
/// </para>
/// </summary>
internal static class SynthesisedPairProof
{
    /// <summary>The subject variable a sample-pair guardrail reads (mirrors <c>SampleVerifier</c>).</summary>
    private const string SubjectEnvironmentVariable = "GR_SUBJECT";

    private static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Execute <paramref name="guardrailPath"/> against the two halves the fixture committed and assert
    /// their exit codes DIFFER. Call this on the files the fixture actually wrote — reading them back off
    /// disk rather than re-deriving them keeps the proof honest about what the run will see, which is the
    /// same reason a guardrail is tested where it runs.
    /// </summary>
    public static async Task AssertHalvesAreDiscriminatedAsync(
        string guardrailPath,
        string validHalfPath,
        string invalidHalfPath,
        CancellationToken cancellationToken = default)
    {
        Assert.True(File.Exists(guardrailPath), $"synthesised guardrail '{guardrailPath}' was not written");
        Assert.True(File.Exists(validHalfPath), $"valid half '{validHalfPath}' was not written");
        Assert.True(File.Exists(invalidHalfPath), $"invalid half '{invalidHalfPath}' was not written");

        var runner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

        int validExit = await RunHalfAsync(runner, guardrailPath, validHalfPath, cancellationToken)
            .ConfigureAwait(false);
        int invalidExit = await RunHalfAsync(runner, guardrailPath, invalidHalfPath, cancellationToken)
            .ConfigureAwait(false);

        Assert.False(
            validExit == invalidExit,
            $"""
            #530: the synthesised guardrail '{Path.GetFileName(guardrailPath)}' returned the SAME exit code
            ({validExit}) for BOTH halves of its pair, so it does not discriminate them and every assertion
            downstream of it is about nothing.

              valid   half: {validHalfPath}
              invalid half: {invalidHalfPath}

            The usual cause is an operator that is less strict than the fixture's text assumes. On
            PowerShell, `-match` is case-INSENSITIVE and `-cmatch` is not, so a valid half that spells the
            marker in another case still matches; the bash sibling `grep -q` IS case-sensitive, which makes
            the same fixture discriminate on Linux and not on Windows. Read both halves and the body
            together before changing either.
            """);
    }

    /// <summary>
    /// The plan-root form of the same proof. A <c>&lt;plan&gt;/samples/&lt;check&gt;/{valid,invalid}/</c>
    /// pair binds its subject as a WORKSPACE TREE rather than a file — <c>GUARDRAILS_WORKSPACE</c> plus
    /// the working directory — so a proof that bound <c>GR_SUBJECT</c> instead would leave every such
    /// body reading an unset variable and returning its no-subject exit code twice. Same property,
    /// different binding; testing a guardrail where it runs is not optional here either.
    /// </summary>
    public static async Task AssertWorkspaceHalvesAreDiscriminatedAsync(
        string guardrailPath,
        string validWorkspace,
        string invalidWorkspace,
        CancellationToken cancellationToken = default)
    {
        Assert.True(File.Exists(guardrailPath), $"synthesised guardrail '{guardrailPath}' was not written");
        Assert.True(Directory.Exists(validWorkspace), $"valid half '{validWorkspace}' was not written");
        Assert.True(Directory.Exists(invalidWorkspace), $"invalid half '{invalidWorkspace}' was not written");

        var runner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

        int validExit = await RunWorkspaceHalfAsync(runner, guardrailPath, validWorkspace, cancellationToken)
            .ConfigureAwait(false);
        int invalidExit = await RunWorkspaceHalfAsync(runner, guardrailPath, invalidWorkspace, cancellationToken)
            .ConfigureAwait(false);

        Assert.False(
            validExit == invalidExit,
            $"""
            #530: the synthesised plan-root check '{Path.GetFileName(guardrailPath)}' returned the SAME exit
            code ({validExit}) for BOTH workspace halves of its pair, so it does not discriminate them and
            every assertion downstream of it is about nothing.

              valid   half: {validWorkspace}
              invalid half: {invalidWorkspace}

            Two causes are common here and they look identical from the outside. Either the body reads
            something both trees share — the case-sensitivity trap, or a marker present in neither — or it
            never resolved the workspace at all and took its "no subject" early exit twice. Check the
            GUARDRAILS_WORKSPACE fallback before you change the marker text.
            """);
    }

    private static async Task<int> RunHalfAsync(
        ScriptUnitRunner runner,
        string guardrailPath,
        string halfPath,
        CancellationToken cancellationToken)
    {
        // The subject reaches a sample-pair guardrail two ways — argv[0] and GR_SUBJECT — and a body may
        // read either, so the proof must supply both exactly as SampleVerifier does. Supplying only one
        // would let a body that reads the other fall through its `if (-not $SubjectPath) { exit 0 }` guard
        // and return the same code twice for a reason that has nothing to do with the halves.
        ProcessResult result = await runner.RunAsync(
                guardrailPath,
                [halfPath],
                Path.GetDirectoryName(halfPath)!,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SubjectEnvironmentVariable] = halfPath
                },
                ProofTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        Assert.False(
            result.TimedOut,
            $"#530: the synthesised guardrail timed out on '{halfPath}' - a timed-out run has no exit code "
            + "to compare, so the pair is unproven rather than proven sound.");

        return result.ExitCode;
    }

    private static async Task<int> RunWorkspaceHalfAsync(
        ScriptUnitRunner runner,
        string guardrailPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        // No GR_SUBJECT and no positional argument: a plan-root check gets its subject from the workspace
        // it is run IN, and supplying a file subject as well would let a body that reads the wrong one
        // still appear to discriminate.
        ProcessResult result = await runner.RunAsync(
                guardrailPath,
                [],
                workspace,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GUARDRAILS_WORKSPACE"] = workspace
                },
                ProofTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        Assert.False(
            result.TimedOut,
            $"#530: the synthesised plan-root check timed out in '{workspace}' - a timed-out run has no "
            + "exit code to compare, so the pair is unproven rather than proven sound.");

        return result.ExitCode;
    }
}
