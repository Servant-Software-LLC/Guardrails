using System.Reflection;
using Guardrails.Cli;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The TDD red for #349's two RENDERERS — the shared formatter
/// <see cref="LiveRunObserver.AttemptModelSummary"/>, and both operator surfaces going through it.
/// Made green by <c>06-render-attempt-model-in-live-and-console</c>; every clause here fails on this
/// wave's entry tree, where the formatter throws and neither renderer declares the event.
///
/// <para><b>Nothing here re-derives what wave 2 already recorded.</b> The two values every clause
/// carries are the two fields the attempt already folded —
/// <see cref="Core.Journal.AttemptProvenance.Model"/> (best-known-actual: the model the runner reported
/// itself running on, else the resolved route's, else the <c>"(cli default)"</c> sentinel) and
/// <see cref="Core.Journal.AttemptProvenance.RequestedModel"/> (written ONLY when it differs). No
/// stream is re-parsed and no <c>--model</c> is forced; a surface that recomputed the comparison would
/// be a second owner of the rule and would drift from the <c>run.json</c> it is showing (D22a).</para>
///
/// <para><b>The presence of the requested model IS the mismatch signal</b>, so the one-string and
/// two-string forms have to be DISTINGUISHABLE. That is why
/// <see cref="Summary_OmitsTheRequestedModel_WhenItIsAbsent"/> asserts inequality against the
/// two-argument form as well as absence of the requested id: a formatter that always printed one string
/// throws away the entire reason #349 exists, and one that always printed two makes the two-string form
/// carry no information at all. Neither failure is visible to a test that only checks "the observed
/// model appears somewhere".</para>
///
/// <para><b>The agreement clause.</b> <see cref="ConsoleObserver_WritesTheSharedSummary_ForAttemptModelResolved"/>
/// drives a REAL <see cref="ConsoleRunObserver"/> over a <see cref="StringWriter"/> and requires its line
/// to CONTAIN the shared formatter's own output. Without it the plain surface is free to grow a second,
/// separately-worded copy of the same fact — the drift the one-formatter design exists to prevent — and
/// the two surfaces would then disagree with nothing failing.</para>
///
/// <para><b>Why the live half is reflection and not output.</b> No Spectre live region renders in a
/// non-interactive test, so there is no string to read back from <see cref="LiveRunObserver"/>. What
/// CAN be observed is the failure mode that matters: <c>AttemptModelResolved</c> is an interface member
/// with an empty DEFAULT body, so a class that merely implements <see cref="IRunObserver"/> gets no
/// member of its own and every raise is swallowed silently, in every mode, with the whole solution
/// still compiling. Reflection over the declared members is exactly the "silently inherited the empty
/// body" detector.</para>
/// </summary>
[Trait("Category", "ModelTieringStage3")]
public sealed class AttemptModelRenderingTests
{
    /// <summary>
    /// The attempt's best-known-actual model — what the runner reported itself running on, i.e. what
    /// wave 2 folded onto <see cref="Core.Journal.AttemptProvenance.Model"/>.
    /// </summary>
    private const string ObservedModel = "claude-sonnet-5-20260101";

    /// <summary>
    /// The model the ROUTE asked for. Deliberately shares no substring with
    /// <see cref="ObservedModel"/> beyond the vendor prefix, so the absence assertion in
    /// <see cref="Summary_OmitsTheRequestedModel_WhenItIsAbsent"/> cannot be satisfied — or defeated —
    /// by one id being contained in the other.
    /// </summary>
    private const string RequestedModel = "claude-opus-5";

    /// <summary>
    /// The exact parameter list <see cref="IRunObserver.AttemptModelResolved"/> declares. Pinned as a
    /// field rather than built inline so the reflection lookup asks for the ONE signature the interface
    /// defines: a same-named member of some other shape would satisfy a name-only probe while still
    /// leaving the interface call resolving to the empty default.
    /// </summary>
    private static readonly Type[] AttemptModelResolvedSignature =
        [typeof(TaskNode), typeof(int), typeof(string), typeof(string)];

    /// <summary>
    /// Both models are named when the route asked for something else. This is the mismatch line — the
    /// only place an operator can tell "the provider served something else" apart from "my routing is
    /// misconfigured", which is the distinction #349 exists to restore.
    /// </summary>
    [Fact]
    public void Summary_NamesBothModels_WhenTheRequestedModelIsPresent()
    {
        string summary = LiveRunObserver.AttemptModelSummary(ObservedModel, RequestedModel);

        Assert.Contains(ObservedModel, summary, StringComparison.Ordinal);
        Assert.Contains(RequestedModel, summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary attempt: the route got what it asked for, <c>requestedModel</c> is absent, and the
    /// summary names one model only.
    ///
    /// <para>The inequality against the two-argument form is the load-bearing clause, not padding. The
    /// first two assertions are both satisfied by a formatter that ignores <c>requestedModel</c> and
    /// returns <see cref="ObservedModel"/> unconditionally — which would render every mismatch as an
    /// ordinary attempt and lose the signal entirely. Requiring the two forms to DIFFER is what makes
    /// the presence of the requested model observable at all.</para>
    /// </summary>
    [Fact]
    public void Summary_OmitsTheRequestedModel_WhenItIsAbsent()
    {
        string oneString = LiveRunObserver.AttemptModelSummary(ObservedModel, requestedModel: null);
        string twoString = LiveRunObserver.AttemptModelSummary(ObservedModel, RequestedModel);

        Assert.Contains(ObservedModel, oneString, StringComparison.Ordinal);
        Assert.DoesNotContain(RequestedModel, oneString, StringComparison.Ordinal);
        Assert.NotEqual(twoString, oneString, StringComparer.Ordinal);
    }

    /// <summary>
    /// The plain <c>--no-ui</c> surface writes the SHARED summary, not a second wording of its own.
    ///
    /// <para>Driven through the <see cref="IRunObserver"/> reference on purpose: that is how the harness
    /// raises it, and it is the call that resolves to the interface's empty default body when the class
    /// declares no member. A test that called a concrete method would not compile in the failure case it
    /// is meant to catch, which is no test at all.</para>
    ///
    /// <para>The mismatch fixture is used because it is the case both surfaces must always disclose;
    /// whether the plain surface also prints on the agreeing case is
    /// <c>06-render-attempt-model-in-live-and-console</c>'s call to make, and nothing here pins it.</para>
    /// </summary>
    [Fact]
    public void ConsoleObserver_WritesTheSharedSummary_ForAttemptModelResolved()
    {
        using var output = new StringWriter();
        IRunObserver observer = new ConsoleRunObserver(output);

        observer.AttemptModelResolved(FixtureTask(), attempt: 1, ObservedModel, RequestedModel);

        // Ordered on purpose: "it printed NOTHING" (the inherited empty default) and "it printed
        // something worded differently" (a second copy of the wording) are different bugs with
        // different fixes, and the second assertion cannot tell them apart on its own.
        string written = output.ToString();
        Assert.False(
            string.IsNullOrWhiteSpace(written),
            "ConsoleRunObserver wrote nothing for AttemptModelResolved — the raise resolved to the "
            + "empty default body on IRunObserver, so the disclosure is swallowed under --no-ui.");

        Assert.Contains(
            LiveRunObserver.AttemptModelSummary(ObservedModel, RequestedModel),
            written,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="LiveRunObserver"/> declares <c>AttemptModelResolved</c> itself rather than silently
    /// inheriting the interface's empty body.
    ///
    /// <para><c>DeclaredOnly</c> is the whole point: a default interface member produces NO member on an
    /// implementing type, so a <see cref="LiveRunObserver"/> that handles nothing returns null here while
    /// the solution still builds and every raise disappears without a trace. This is the "silently
    /// inherited the empty default" failure made observable — the same shape that let a whole surface
    /// ship dead behind a green build before.</para>
    /// </summary>
    [Fact]
    public void LiveObserver_DeclaresAttemptModelResolved_RatherThanInheritingTheEmptyDefault()
    {
        MethodInfo? declared = typeof(LiveRunObserver).GetMethod(
            nameof(IRunObserver.AttemptModelResolved),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null,
            types: AttemptModelResolvedSignature,
            modifiers: null);

        Assert.NotNull(declared);
    }

    /// <summary>
    /// The task an <c>AttemptModelResolved</c> raise describes. Hand-built and never loaded from disk:
    /// the renderers under test read the id, and no clause in this file touches the filesystem.
    /// </summary>
    private static TaskNode FixtureTask()
    {
        const string dir = "/fake/plan/tasks/01-render-fixture";
        return new TaskNode
        {
            Id = "01-render-fixture",
            Directory = dir,
            Description = "fixture — the attempt whose model is being disclosed",
            Action = new ActionDefinition { Path = $"{dir}/action.prompt.md", Kind = ActionKind.Prompt },
            Guardrails = [new GuardrailDefinition
            {
                Name = "01-check",
                Path = $"{dir}/guardrails/01-check.ps1",
                Kind = ActionKind.Script
            }]
        };
    }
}
