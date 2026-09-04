namespace Guardrails.Core.Telemetry;

/// <summary>
/// Why a corpus row's <see cref="TelemetryRow.Model"/> reads the way it does (SSOT §15.2b, issue #577) —
/// the column that makes a row SELF-DESCRIBING about its own attribution.
///
/// <para><b>The defect this closes.</b> Before this column, <c>model: null</c> was overloaded across three
/// completely different facts, and nothing in the row told them apart:</para>
/// <list type="bullet">
///   <item>the once-per-task <c>attempt == 0</c> sentinel, which carries no model because a summary of N
///   attempts has no single route — correct by construction;</item>
///   <item>a SCRIPT action, which invokes no model at all — correct by construction;</item>
///   <item>a PROMPT attempt whose route was never journalled — a genuine recording gap.</item>
/// </list>
///
/// <para>A census over the 806-row operator corpus measured the split as 319 / 2 / 93: <b>77% of the
/// no-model rows were correct by construction</b>, and the headline "76% of rows name no usable model"
/// was therefore mostly a reporting artifact of an ambiguous column rather than a recording failure. That
/// number could only be produced by joining the corpus back to the plan folders on disk — an external
/// join that is impossible for any row whose plan folder has since been deleted (41 such rows), and that
/// every future analysis would otherwise have to re-derive. Writing the reason ON the row at ingest time,
/// where both facts are present together, makes the answer permanent.</para>
///
/// <para><b>Why a token and not a bool.</b> "Has a model" and "should have a model" are different
/// questions, and a comparison against a local model needs the second one: the denominator for
/// "attribution coverage" is the rows that COULD name a model (<see cref="Recorded"/> +
/// <see cref="CliDefault"/> + <see cref="NotRecorded"/>), not every row in the file. A boolean would
/// answer only the first question and leave the second to a join nobody can perform.</para>
///
/// <para><b>The invariant a regression test pins</b> (SSOT §15.2b): a PROMPT action's attempt row is
/// <see cref="Recorded"/>, <see cref="CliDefault"/>, or — as a defect — <see cref="NotRecorded"/>; it is
/// NEVER <see cref="ScriptAction"/>. A script action's attempt row is <see cref="ScriptAction"/> and never
/// <see cref="NotRecorded"/>. The two cases that both render as an empty model column are thus
/// distinguishable in the data, which is the whole point.</para>
/// </summary>
public static class ModelAttribution
{
    /// <summary>
    /// The row names a real, fully resolved model. The only value for which
    /// <see cref="TelemetryRow.Model"/> is a usable comparison key.
    /// </summary>
    public const string Recorded = "recorded";

    /// <summary>
    /// The attempt ran a model, but no NAMED route was ever resolved — <see cref="TelemetryRow.Model"/>
    /// holds the <c>(cli default)</c> display sentinel
    /// (<c>PromptExecutionSupport.CliDefaultModelDisplay</c>), meaning "whatever the runner CLI's own
    /// default was that day".
    ///
    /// <para>Honest, and NOT a defect — it is what every pre-tiering run truthfully knew about itself.
    /// It is still not comparable: the sentinel is not a model identity, so pooling these rows with a
    /// named model's would attribute their cost and outcomes to a model nobody recorded. Separated from
    /// <see cref="Recorded"/> precisely so an analysis must decide what to do with them rather than
    /// inheriting them by accident.</para>
    /// </summary>
    public const string CliDefault = "cli-default";

    /// <summary>
    /// CORRECT BY CONSTRUCTION — the task's action is a SCRIPT, which invokes no model, so there is no
    /// attribution to record. Excluded from any attribution-coverage denominator: a script row is not a
    /// missing measurement, it is a measurement that does not apply.
    /// </summary>
    public const string ScriptAction = "script-action";

    /// <summary>
    /// CORRECT BY CONSTRUCTION — the once-per-task <c>attempt == 0</c> sentinel row. It summarizes a
    /// task's identity, declared tier and terminal outcome across every attempt, so it cannot carry one
    /// attempt's route without inventing a number nobody measured. Excluded from any
    /// attribution-coverage denominator for the same reason <see cref="ScriptAction"/> is.
    /// </summary>
    public const string TaskGrain = "task-grain";

    /// <summary>
    /// <b>THE DEFECT TOKEN.</b> A PROMPT attempt — which certainly ran some model — journalled with no
    /// provenance, or with provenance naming no model. The row cannot say what it ran on and no later
    /// reader can recover it.
    ///
    /// <para>Its presence in NEW data is a bug, and that is exactly why it is written rather than left as
    /// a bare null: a defect that names itself can be counted, alerted on, and regression-tested, whereas
    /// the bare null it replaces was indistinguishable from two correct outcomes and so could sit in the
    /// corpus indefinitely looking like design intent. Silence is the failure mode this vocabulary
    /// exists to break.</para>
    /// </summary>
    public const string NotRecorded = "not-recorded";

    /// <summary>
    /// The row names no model AND the action kind could not be decided — the task definition was
    /// unreadable, absent, or ambiguous (zero or several <c>action.*</c> files, both of which SSOT §3
    /// makes a validation error), or the ETL was handed a bare journal with no plan folder to resolve
    /// against.
    ///
    /// <para>Counted as NEITHER correct-by-construction nor a defect. This is SSOT §15.4's standing rule
    /// for an unrecognised guardrail failure applied here — recorded, never guessed at: booking these as
    /// <see cref="NotRecorded"/> would inflate the defect with rows nobody classified, and booking them as
    /// <see cref="ScriptAction"/> would hide real gaps behind an unearned excuse.</para>
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The tokens whose rows COULD have named a model — the honest denominator for an attribution-coverage
    /// figure. <see cref="ScriptAction"/>, <see cref="TaskGrain"/> and <see cref="Unknown"/> are absent by
    /// design: including rows that were never going to name a model would understate coverage, which is
    /// the flattering-number failure #577 exists to prevent.
    /// </summary>
    public static IReadOnlySet<string> AttributableTokens { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Recorded, CliDefault, NotRecorded };

    /// <summary>Every token this vocabulary defines — the closed set a reader may expect.</summary>
    public static IReadOnlySet<string> AllTokens { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Recorded, CliDefault, ScriptAction, TaskGrain, NotRecorded, Unknown
        };
}
