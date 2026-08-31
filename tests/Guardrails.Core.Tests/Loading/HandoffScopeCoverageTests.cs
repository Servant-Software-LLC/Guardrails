using System.Text;
using System.Text.Json;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD-red pins for GR2068 / GR2069 (issue #553, plan 31 §4): a plan whose tasks cannot write the files
/// its own handoff table names. Every fixture is a real plan folder in a temp dir — a
/// <c>guardrails.json</c>, task folders carrying real <c>writeScope</c> arrays, and a SIBLING
/// <c>&lt;plan-folder&gt;.md</c> carrying a markdown table with a <c>filesTouched</c> column — run through
/// <see cref="PlanValidator.Validate"/>, asserting on the diagnostic list it returns. <c>validate</c> stays
/// STATIC and OFFLINE: nothing here touches the repo tree, spawns an interpreter, or depends on a file the
/// plan is about to create.
///
/// <para><b>Authored RED, before the check exists.</b> <c>src/Guardrails.Core/Loading/HandoffScopeCoverage.cs</c>
/// does not exist and <see cref="PlanValidator"/> runs no such check, so nothing can emit either code yet.
/// Seven of the nine pins therefore fail today. The two SILENCE pins are the declared exception —
/// see their own doc comments — because they assert the diagnostic list is UNCHANGED, which is true today
/// and must STAY true after the check ships. Demanding red of those two would demand that a correct
/// implementation fail.</para>
///
/// <para><b>Both codes are asserted as string LITERALS</b>, never through a <c>DiagnosticCodes</c>
/// constant: <c>HandoffPathUnreachable</c> / <c>HandoffRowSplitAcrossTasks</c> are the implementation
/// stage's deliverable and do not compile today (plan 31 §7). That stage carries its own pin asserting each
/// constant equals its literal.</para>
///
/// <para><b>The acceptance criterion, and the mis-keying it exists to catch.</b> Pins 1 and 2 are the two
/// REAL plan-28 failures in their broken state — run 1 ($13.33, 19 blocked) and run 3 ($3.84, 21 blocked) —
/// and BOTH assert <b>GR2069</b>, not GR2068. Neither failure was ever an unreachable path: in run 1
/// <c>tests/**</c> was reachable by the test-authoring tasks, and in run 3 <c>PlanLoader.cs</c> was reachable
/// by task 21. In both cases the row was reachable ACROSS the plan and unreachable by the ONE task that
/// owned it — the split condition, exactly (plan 31 §4.6). GR2068 is the code that merely SOUNDS like "the
/// broken one"; it fires exactly once in plan 28's ten rows, on row 3's genuinely stale path, which is
/// pin 3 here.</para>
///
/// <para><b>Where the fixtures come from.</b> Pins 1, 2 and 3 reproduce plan 28 §13 rows 7, 1 and 3 against
/// the real <c>writeScope</c> arrays in <c>docs/plans/28-local-inference-runner/tasks/&lt;id&gt;/task.json</c>.
/// Plan 28's own §13 table has FOUR columns (<c>| # | Agent | filesTouched | Deliverable |</c>) and no
/// <c>writeScope</c> column, so coverage resolves against the task manifests, never against a column in that
/// document; <see cref="HandoffTable"/> reproduces that four-column shape.</para>
/// </summary>
public sealed class HandoffScopeCoverageTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-handoff-scope-" + Guid.NewGuid().ToString("N"));

    public HandoffScopeCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    // --- Pin 1: the REAL row-7 catch (plan 28, run 3) — and it is GR2069 ---------------------------

    /// <summary>
    /// <b>Plan 28 §13 row 7, in its run-3 BROKEN state</b> — the row #553 was written about. The row names
    /// four <c>Loading/…</c> files; every one of them IS writable by some task, but no SINGLE task holds all
    /// four. In the real folder the nearest task, <c>21-implement-reachability-gate</c>, holds THREE and
    /// lacks <c>RawManifests.cs</c>, which only <c>09-add-openai-block-config-surface</c> writes. That is a
    /// split, so the code is <b>GR2069</b>.
    ///
    /// <para><b>Asserting GR2068 here would be the mis-keying this pin exists to catch.</b> Not one of the
    /// four paths is unreachable — the row was never undeliverable, it was undeliverable BY ONE TASK. The
    /// method name's "OnlyTwoOfFour" is a label carried over from the plan's shorthand, not a specification
    /// of the split: what the pin requires is SOME shortfall, and the fixture reproduces the real one.</para>
    ///
    /// <para>The message must name each path and the task(s) covering it — that is the fact the author needs
    /// in order to answer the confirm, and the check has already computed it.</para>
    /// </summary>
    [Fact]
    public void Row7WhoseOwningTaskHoldsOnlyTwoOfFourPaths_EmitsGR2069NamingTheCoveringTask()
    {
        string plan = PlanFolder("28-row-7-run-3",
            ("09-add-openai-block-config-surface", new[]
            {
                "src/Guardrails.Core/Model/PromptRunnerConfig.cs",
                "src/Guardrails.Core/Loading/RawManifests.cs",
            }),
            ("19-implement-block-diagnostics", new[]
            {
                "src/Guardrails.Core/Loading/PlanValidator.cs",
                "src/Guardrails.Core/Loading/DiagnosticCodes.cs",
            }),
            ("21-implement-reachability-gate", new[]
            {
                "src/Guardrails.Core/Loading/PlanValidator.cs",
                "src/Guardrails.Core/Loading/DiagnosticCodes.cs",
                "src/Guardrails.Core/Loading/PlanLoader.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("`Loading/PlanLoader.cs` (frontmatter helper **extracted**, not copied), " +
             "`Loading/RawManifests.cs`, `Loading/PlanValidator.cs`, `Loading/DiagnosticCodes.cs`",
             "The block schema, the frontmatter fold, kind-aware validation")));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic split = Assert.Single(diagnostics, d => d.Code == "GR2069");
        Assert.Equal(DiagnosticSeverity.Warning, split.Severity);

        // Every path the row names, and the two tasks whose split delivers it.
        Assert.Contains("Loading/PlanLoader.cs", split.Message, StringComparison.Ordinal);
        Assert.Contains("Loading/RawManifests.cs", split.Message, StringComparison.Ordinal);
        Assert.Contains("Loading/PlanValidator.cs", split.Message, StringComparison.Ordinal);
        Assert.Contains("Loading/DiagnosticCodes.cs", split.Message, StringComparison.Ordinal);
        Assert.Contains("21-implement-reachability-gate", split.Message, StringComparison.Ordinal);
        Assert.Contains("09-add-openai-block-config-surface", split.Message, StringComparison.Ordinal);

        // Reachable-by-someone is precisely what makes this NOT the unreachable code.
        Assert.DoesNotContain(diagnostics, d => d.Code == "GR2068");
    }

    // --- Pin 2: the REAL row-1 catch (plan 28, run 1), both directions — and it is GR2069 -----------

    /// <summary>
    /// <b>Plan 28 §13 row 1, in its run-1 BROKEN state.</b> The row names a concrete producer and the glob
    /// <c>tests/**</c>; the task that owns it (<c>00-land-the-required-role-seam</c>) held the producer but
    /// no test path at all, while the test-authoring task held one. Both candidates reachable, no single
    /// task holding both ⇒ <b>GR2069</b>. The prose fragment "all seven producers" carries no backticks and
    /// is therefore not a candidate — it must never be guessed at.
    ///
    /// <para><b>Both directions in one test.</b> Adding <c>tests/**</c> to that task's <c>writeScope</c> —
    /// the actual fix that unblocked run 1 — must make the row go SILENT. That half is what proves the check
    /// measures COVERAGE rather than counting paths: an implementation that merely counts, or that resolves
    /// against the union of every task's scope, passes the first half and fails here. The final assertion is
    /// stronger than "no split code": the whole list must be the first list MINUS that one finding, so
    /// widening the scope is proved to have silenced this row and disturbed nothing else.</para>
    /// </summary>
    [Fact]
    public void Row1WithoutTheTestGlobEmitsGR2069_AndIsSilentOnceTheGlobIsAdded()
    {
        string plan = PlanFolder("28-row-1-run-1",
            ("00-land-the-required-role-seam", new[]
            {
                "src/Guardrails.Core/Prompts/PromptInvocation.cs",
                "src/Guardrails.Core/Execution/ActionRunner.cs",
            }),
            ("06-author-openai-compat-tests", new[]
            {
                "tests/Guardrails.Integration.Tests/OpenAiCompat/OpenAiCompatServerTests.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("`Prompts/PromptInvocation.cs`, all **seven** §3.4 producers, **and `tests/**`**",
             "The Role seam and the empty-path doc")));

        IReadOnlyList<Diagnostic> before = Validate(plan);

        Diagnostic split = Assert.Single(before, d => d.Code == "GR2069");
        Assert.Equal(DiagnosticSeverity.Warning, split.Severity);
        Assert.Contains("Prompts/PromptInvocation.cs", split.Message, StringComparison.Ordinal);
        Assert.Contains("tests/**", split.Message, StringComparison.Ordinal);
        Assert.Contains("00-land-the-required-role-seam", split.Message, StringComparison.Ordinal);
        Assert.Contains("06-author-openai-compat-tests", split.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(before, d => d.Code == "GR2068");

        // The fix that unblocked run 1: the owning task gains the test glob it was always going to write.
        WriteTask(plan, "00-land-the-required-role-seam",
            "src/Guardrails.Core/Prompts/PromptInvocation.cs",
            "src/Guardrails.Core/Execution/ActionRunner.cs",
            "tests/**");

        IReadOnlyList<Diagnostic> after = Validate(plan);

        Assert.DoesNotContain(after, d => d.Code is "GR2068" or "GR2069");
        Assert.Equal(before.Where(d => d.Code != "GR2069"), after);
    }

    // --- Pin 3: the unreachable case — GR2068, with no suggested correction -------------------------

    /// <summary>
    /// <b>Plan 28 §13 row 3, real and shipped.</b> The cell names
    /// <c>tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs</c>; the file actually shipped one directory
    /// deeper, under <c>OpenAiCompat/</c>, so no <c>writeScope</c> entry in the plan matches the cell. Nothing
    /// can write that path under any implementation, so the row cannot be delivered at all ⇒ <b>GR2068</b>.
    /// The candidate stays checkable because its first segment, <c>tests</c>, IS a whole segment of a real
    /// scope entry — this is a stale path, not an unresolvable fragment.
    ///
    /// <para><b>No suggested correction.</b> The same-named file one directory deeper is sitting right there
    /// in a task's scope and is exactly the near-miss a helpful implementation would offer. It must not: a
    /// suggested correction that is wrong is worse than none, and this check cannot know whether the path is
    /// stale or whether nobody owns the deliverable. Naming no covering task is also what keeps the two
    /// message forms distinct — that is GR2069's job, and only GR2069's.</para>
    /// </summary>
    [Fact]
    public void ConcretePathNoTaskCanWrite_EmitsGR2068WithNoSuggestedCorrection()
    {
        string plan = PlanFolder("28-row-3-stale-path",
            ("03-author-the-fake-openai-server", new[]
            {
                "tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServer.cs",
            }),
            ("04-implement-the-runner", new[]
            {
                "src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("`tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs`",
             "The adversarial loopback server")));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic unreachable = Assert.Single(diagnostics, d => d.Code == "GR2068");
        Assert.Equal(DiagnosticSeverity.Warning, unreachable.Severity);
        Assert.Contains(
            "tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs", unreachable.Message, StringComparison.Ordinal);

        // The near-miss path, and the task that writes it, are BOTH withheld.
        Assert.DoesNotContain("OpenAiCompat/FakeOpenAiServer.cs", unreachable.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("03-author-the-fake-openai-server", unreachable.Message, StringComparison.Ordinal);
    }

    // --- Pin 3a: the codes are mutually exclusive per row -------------------------------------------

    /// <summary>
    /// <b>The two codes are mutually exclusive PER ROW</b>, asserted on the same diagnostic list. This
    /// fixture is the tempting shape: a row with one unreachable path AND two reachable paths that no single
    /// task holds together — so an implementation that evaluates "is anything unmatched?" and "does one task
    /// cover everything?" independently emits both codes for it.
    ///
    /// <para>That must not happen, and the reason is operational rather than aesthetic. GR2069 fires on
    /// legitimate splits often enough that an operator may decide to silence it; if every broken row also
    /// carried a GR2069, silencing it would take the provable half with it. GR2068 has to mean "provably
    /// broken" on its own, forever.</para>
    /// </summary>
    [Fact]
    public void AnUnreachableRowEmitsGR2068AndNoGR2069()
    {
        string plan = PlanFolder("unreachable-beats-split",
            ("12-implement-the-loader-fold", new[]
            {
                "src/Guardrails.Core/Loading/PlanLoader.cs",
            }),
            ("13-implement-the-validator-rule", new[]
            {
                "src/Guardrails.Core/Loading/PlanValidator.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("`Loading/PlanLoader.cs`, `Loading/PlanValidator.cs`, `Loading/NeverOwned.cs`",
             "A row that is split across tasks AND names a path nobody can write")));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic unreachable = Assert.Single(diagnostics, d => d.Code == "GR2068");
        Assert.Contains("Loading/NeverOwned.cs", unreachable.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Code == "GR2069");
    }

    // --- Pin 4: the anchor discriminator, both halves in ONE fixture --------------------------------

    /// <summary>
    /// <b>The whole-segment anchor, positive and negative in a single cell.</b> The cell names two things
    /// that match nothing in the plan's scopes and are entirely different animals:
    /// <list type="bullet">
    /// <item><c>tests/Guardrails.Core.Tests/Wrong.cs</c> — ANCHORED: <c>tests</c> is a whole segment of a real
    /// scope entry, so the candidate is written in the plan's own path vocabulary and its absence is a fact
    /// worth reporting ⇒ GR2068.</item>
    /// <item><c>Cli/Commands/</c> — UNANCHORED: the real segment is <c>Guardrails.Cli</c>, so <c>Cli</c> is a
    /// FRAGMENT of a segment, not a segment. The cell is too vague to judge; firing on it teaches nothing and
    /// is exactly the noise (plan 28's row 8) that gets a check muted.</item>
    /// </list>
    ///
    /// <para><b>The negative half is load-bearing.</b> Without it, a later "improvement" that drops the anchor
    /// test passes every other pin in this file and re-introduces row 8's noise — which is why the assertion is
    /// EXACTLY ONE finding across both codes, not merely "a GR2068 appears". Counting alone is not enough
    /// either: both candidates sit in ONE cell, so an implementation that drops the anchor test reports one
    /// row-level finding naming BOTH paths and still satisfies the count. Hence the last assertion, which is
    /// the "for the first" half of this pin and the only place it can be made.</para>
    /// </summary>
    [Fact]
    public void AnchoredUnmatchedAndUnanchoredFragmentInOneCell_EmitExactlyOneFinding()
    {
        string plan = PlanFolder("anchor-discriminator",
            ("05-wire-the-cli-command", new[]
            {
                "src/Guardrails.Cli/Commands/ProvidersCheckCommand.cs",
            }),
            ("06-author-the-tests", new[]
            {
                "tests/Guardrails.Core.Tests/ModelTiering/OpenAiCompatConfigShapeTests.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("the run preflight, `tests/Guardrails.Core.Tests/Wrong.cs`, `Cli/Commands/`",
             "The reachability preflight and the providers command")));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic only = Assert.Single(diagnostics, d => d.Code is "GR2068" or "GR2069");
        Assert.Equal("GR2068", only.Code);
        Assert.Contains("tests/Guardrails.Core.Tests/Wrong.cs", only.Message, StringComparison.Ordinal);

        // The fragment was DROPPED, not merely folded into the same row-level finding: it never became a
        // candidate, so nothing the check reports can name it.
        Assert.DoesNotContain("Cli/Commands", only.Message, StringComparison.Ordinal);
    }

    // --- Pin 5a: the glob arm's argument direction --------------------------------------------------

    /// <summary>
    /// <b>The argument-direction pin.</b> <c>WriteScope.IsInScope(path, scope)</c> globs the SCOPE side and
    /// splits the PATH side literally (<c>WriteScope.cs:74-98</c>), so for a GLOB candidate the only direction
    /// that can ever match is <c>IsInScope(entry, [candidate])</c> — arguments swapped. Row 1 of this fixture
    /// is the directory glob <c>tests/Guardrails.Integration.Tests/</c> + <c>**</c>, covered by one task's
    /// concrete test file two segments below it. It must be SILENT.
    ///
    /// <para><b>This pin must FAIL under the un-swapped form <c>IsInScope(candidate, scope)</c>.</b> That form
    /// splits the candidate LITERALLY, so its trailing <c>**</c> becomes a literal segment that has to equal a
    /// real directory name — it can never match — and EVERY glob row would report as unreachable. A pin that
    /// passed both ways would prove nothing. The row-2 discriminator is what makes that failure visible: a glob
    /// candidate no task covers, which MUST fire. Asserting exactly one finding across both codes fails at two
    /// (the un-swapped form, where row 1 fires too) and at zero (no check at all). Do not weaken this to a bare
    /// "row 1 emits nothing" — that assertion is green under the broken direction as soon as the discriminator
    /// is removed.</para>
    ///
    /// <para>Both candidates are ANCHORED (pin 4's rule): <c>tests</c> is a whole segment of a real scope
    /// entry, so both are checkable and row 2's silence could only ever mean the check is dead. The two globs
    /// differ in their SECOND segment, which is what keeps row 1's coverage from spilling onto row 2.</para>
    /// </summary>
    [Fact]
    public void GlobCandidateCoveredByAConcreteScopeEntry_IsSilent()
    {
        string plan = PlanFolder("glob-arm-direction",
            ("09-add-openai-block-config-surface", new[]
            {
                "tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServerTests.cs",
                "src/Guardrails.Core/Model/PromptRunnerConfig.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("`tests/Guardrails.Integration.Tests/**`", "The config-shape acceptances"),
            ("`tests/Guardrails.NeverAuthored.Tests/**`", "A suite this plan forgot to give anyone")));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic only = Assert.Single(diagnostics, d => d.Code is "GR2068" or "GR2069");
        Assert.Equal("GR2068", only.Code);
        Assert.Contains("NeverAuthored", only.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Guardrails.Integration.Tests", only.Message, StringComparison.Ordinal);
    }

    // --- Pin 5b: a segment-aligned suffix, never a substring ----------------------------------------

    /// <summary>
    /// <b>The concrete arm matches on a segment-aligned suffix, not a substring.</b> A handoff cell names
    /// files RELATIVELY (<c>Loading/PlanLoader.cs</c>) while a <c>writeScope</c> names them from the repo root,
    /// so the concrete arm has to accept a suffix — otherwise no relative cell resolves at all. The suffix must
    /// be SEGMENT-ALIGNED.
    ///
    /// <para>Row 1 is the positive half: <c>src/Guardrails.Core/Loading/PlanLoader.cs</c> ends with
    /// <c>/Loading/PlanLoader.cs</c>, so the row is covered and silent. Row 2 is the negative:
    /// <c>src/Guardrails.Core/PreLoading/PlanValidator.cs</c> ends with the raw text
    /// <c>Loading/PlanValidator.cs</c> but NOT with <c>/Loading/PlanValidator.cs</c> — <c>Loading</c> is a
    /// substring of the segment <c>PreLoading</c>, not a segment — so nothing covers row 2 and it must fire
    /// GR2068. A substring implementation reports zero findings here and fails this pin, which is the point of
    /// pairing the halves in one fixture.</para>
    /// </summary>
    [Fact]
    public void SegmentAlignedSuffixMatches_ButASubstringOfASegmentDoesNot()
    {
        string plan = PlanFolder("suffix-arm-alignment",
            ("21-implement-reachability-gate", new[]
            {
                "src/Guardrails.Core/Loading/PlanLoader.cs",
            }),
            ("22-implement-the-legacy-shim", new[]
            {
                "src/Guardrails.Core/PreLoading/PlanValidator.cs",
            }));

        WritePlanDocument(plan, HandoffTable(
            ("`Loading/PlanLoader.cs`", "The loader fold"),
            ("`Loading/PlanValidator.cs`", "The validator rule nobody was given")));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic only = Assert.Single(diagnostics, d => d.Code is "GR2068" or "GR2069");
        Assert.Equal("GR2068", only.Code);
        Assert.Contains("Loading/PlanValidator.cs", only.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading/PlanLoader.cs", only.Message, StringComparison.Ordinal);
    }

    // --- Pin 6: the SILENCE pin — no handoff table means the list is UNCHANGED ----------------------

    /// <summary>
    /// <b>A plan with no handoff table emits nothing at all.</b> Most plans predate the convention; a check
    /// that fires on every legacy plan gets turned off within a week, so adopting it is opt-in BY WRITING THE
    /// COLUMN. The document here carries a table and a path no task can write — everything except a header
    /// that normalises to <c>filestouched</c>.
    ///
    /// <para><b>Why this asserts the FULL list rather than the absence of a code.</b> A pin written as
    /// "assert GR2068 does not appear" passes trivially when GR2068 is broken and never fires at all — a
    /// negative pin over an unreachable state, which is the archetype plan 31 deletes elsewhere. So the
    /// control list is captured from the same plan folder BEFORE the document exists and the two lists are
    /// compared whole: same count, same codes, same messages, in order. Nothing may be added under any code,
    /// including one invented later. The control is deliberately NON-EMPTY — one task carries a six-path
    /// writeScope, which trips the unrelated structural over-scope lint (GR2042) — so this is provably a
    /// comparison of real content and not of two empty lists.</para>
    ///
    /// <para><b>This pin is GREEN on today's code, by declaration.</b> It asserts the list is unchanged, which
    /// is true while no check exists and must stay true once one does. The red census that gates this task
    /// exempts it by name for exactly that reason.</para>
    /// </summary>
    [Fact]
    public void APlanWithNoHandoffTable_LeavesTheDiagnosticListUNCHANGED()
    {
        string plan = PlanFolder("no-handoff-table",
            ("01-does-everything", new[]
            {
                "src/Guardrails.Core/Loading/PlanLoader.cs",
                "src/Guardrails.Core/Loading/PlanValidator.cs",
                "src/Guardrails.Core/Loading/RawManifests.cs",
                "src/Guardrails.Core/Loading/DiagnosticCodes.cs",
                "src/Guardrails.Core/Model/PromptRunnerConfig.cs",
                "src/Guardrails.Core/Prompts/PromptInvocation.cs",
            }));

        IReadOnlyList<Diagnostic> control = Validate(plan);
        Assert.NotEmpty(control);

        WritePlanDocument(plan, TableWithoutAFilesTouchedColumn(
            ("`Loading/NeverOwned.cs`, `tests/Guardrails.Core.Tests/AlsoNeverOwned.cs`",
             "A table this check must never read")));

        Assert.Equal(control, Validate(plan));
    }

    // --- Pin 7: a cell of backticked non-paths leaves the list UNCHANGED ----------------------------

    /// <summary>
    /// <b>A backticked code span that is not a path is not a candidate.</b> A <c>filesTouched</c> cell is
    /// prose with paths in it, and plan documents habitually backtick field names. A span with no <c>/</c> and
    /// no file extension — <c>required</c>, <c>writeScope</c> — is not a path, and there is no extension
    /// allow-list, no case heuristic and no C#-member-access special case behind that rule.
    ///
    /// <para>Asserted on the FULL list for the same reason as the pin above: "no GR2068 appeared" is satisfied
    /// by a check that fires on nothing ever. Here the table IS present and IS read — the row simply yields no
    /// candidates — so the comparison is against the same plan folder before the document existed. GREEN on
    /// today's code by declaration, and it must stay green afterwards.</para>
    /// </summary>
    [Fact]
    public void ACellOfBacktickedNonPaths_LeavesTheDiagnosticListUNCHANGED()
    {
        string plan = PlanFolder("prose-cells-only",
            ("01-does-everything", new[]
            {
                "src/Guardrails.Core/Loading/PlanLoader.cs",
                "src/Guardrails.Core/Loading/PlanValidator.cs",
                "src/Guardrails.Core/Loading/RawManifests.cs",
                "src/Guardrails.Core/Loading/DiagnosticCodes.cs",
                "src/Guardrails.Core/Model/PromptRunnerConfig.cs",
                "src/Guardrails.Core/Prompts/PromptInvocation.cs",
            }));

        IReadOnlyList<Diagnostic> control = Validate(plan);
        Assert.NotEmpty(control);

        WritePlanDocument(plan, HandoffTable(
            ("`required` is a source break at every construction site, and `writeScope` is the gate",
             "The seam, described in field names rather than paths")));

        Assert.Equal(control, Validate(plan));
    }

    // --- fixtures -----------------------------------------------------------------------------------

    /// <summary>
    /// A plan folder at <c>&lt;root&gt;/&lt;name&gt;/</c>, one task folder per entry, each carrying the given
    /// <c>writeScope</c> — the on-disk shape the check reads scopes from. Returns the plan directory; its
    /// sibling document goes to <c>&lt;root&gt;/&lt;name&gt;.md</c>, which is the layout the breakdown command
    /// itself creates and the only one this check relies on.
    /// </summary>
    private string PlanFolder(string name, params (string Id, string[] WriteScope)[] tasks)
    {
        string planDirectory = Path.Combine(_root, name);
        Directory.CreateDirectory(planDirectory);

        // maxParallelism 1 is not incidental: worktree mode makes the temp dir's git-ness (GR2015) and the
        // terminal-gate obligation (GR2028) part of every fixture's diagnostic list, and the first of those
        // varies with where TMP happens to live. Serial keeps every list below a function of the fixture alone.
        File.WriteAllText(Path.Combine(planDirectory, "guardrails.json"), """
            { "version": 1, "maxParallelism": 1 }
            """);

        foreach ((string id, string[] writeScope) in tasks)
        {
            WriteTask(planDirectory, id, writeScope);
        }

        return planDirectory;
    }

    /// <summary>
    /// Write (or overwrite) one task folder. Overwriting is how the second half of the row-1 pin widens a
    /// task's scope between two validations of the same plan folder.
    /// </summary>
    private static void WriteTask(string planDirectory, string id, params string[] writeScope)
    {
        string taskDirectory = Path.Combine(planDirectory, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDirectory, "guardrails"));

        File.WriteAllText(Path.Combine(taskDirectory, "task.json"), $$"""
            {
              "description": "fixture task",
              "dependsOn": [],
              "writeScope": {{JsonSerializer.Serialize(writeScope)}}
            }
            """);

        File.WriteAllText(Path.Combine(taskDirectory, "action.sh"), "exit 0\n");
        File.WriteAllText(Path.Combine(taskDirectory, "guardrails", "01-verifies.sh"),
            "# catches: a change that was never verified\nexit 0\n");
    }

    /// <summary>Write the plan's SIBLING markdown document — <c>&lt;plan-folder&gt;.md</c>.</summary>
    private static void WritePlanDocument(string planDirectory, string markdown) =>
        File.WriteAllText(planDirectory + ".md", markdown);

    /// <summary>
    /// A handoff table in plan 28 §13's own FOUR-column shape — <c>| # | Agent | filesTouched | Deliverable |</c>.
    /// There is no <c>writeScope</c> column, deliberately: coverage resolves against the task manifests on
    /// disk, and a fixture carrying a scope column would be testing a document shape no plan of record has.
    /// </summary>
    private static string HandoffTable(params (string FilesTouched, string Deliverable)[] rows)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# A fixture plan");
        markdown.AppendLine();
        markdown.AppendLine("## 13. Implementation handoff");
        markdown.AppendLine();
        markdown.AppendLine("| # | Agent | filesTouched | Deliverable |");
        markdown.AppendLine("|---|---|---|---|");
        for (int i = 0; i < rows.Length; i++)
        {
            markdown.AppendLine(
                $"| {i + 1} | `guardrails-harness-developer` | {rows[i].FilesTouched} | {rows[i].Deliverable} |");
        }

        return markdown.ToString();
    }

    /// <summary>
    /// The same document with the <c>filesTouched</c> column REMOVED — a real markdown table, real paths in
    /// its cells, and no header that normalises to <c>filestouched</c>. This is what most plans look like.
    /// </summary>
    private static string TableWithoutAFilesTouchedColumn(params (string Files, string Deliverable)[] rows)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# A fixture plan that predates the convention");
        markdown.AppendLine();
        markdown.AppendLine("## 13. Implementation handoff");
        markdown.AppendLine();
        markdown.AppendLine("| # | Agent | Deliverable |");
        markdown.AppendLine("|---|---|---|");
        for (int i = 0; i < rows.Length; i++)
        {
            markdown.AppendLine($"| {i + 1} | `guardrails-harness-developer` | {rows[i].Files} — {rows[i].Deliverable} |");
        }

        return markdown.ToString();
    }

    /// <summary>
    /// Load the fixture plan and return every diagnostic <see cref="PlanValidator.Validate"/> produces. The
    /// probes are fakes so the run is offline and deterministic: the PATH probe resolves every interpreter,
    /// and the syntax probe parses nothing, so no assertion below can depend on which shells the machine has.
    /// A loader ERROR means the FIXTURE is broken rather than the check, and failing loudly here keeps that
    /// from reading as a finding about GR2068 or GR2069.
    /// </summary>
    private static IReadOnlyList<Diagnostic> Validate(string planDirectory)
    {
        PlanLoadResult result = new PlanLoader().Load(planDirectory);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(result.Plan);

        return new PlanValidator(
                FakeExecutableProbe.All,
                BannedPatternRegistry.Load(),
                NullScriptSyntaxProbe.Instance)
            .Validate(result.Plan);
    }
}
