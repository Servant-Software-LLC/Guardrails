using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// The banned-guardrail-pattern registry (SSOT §4.6, issue #346, GR2037): the data-driven lint that
/// mechanically rejects a generated guardrail SCRIPT containing a known-bad regex construction so a
/// fixed-spelling catalogue lesson (a fresh LLM generation) cannot silently regress. Two halves:
/// <list type="bullet">
///   <item>the <b>meta-test</b> — the maintainer's quality bar: every seed entry's <c>badPattern</c> is a
///     valid regex, matches ALL its inline <c>mustMatch</c> fixtures, and matches NONE of its
///     <c>mustNotMatch</c> fixtures, so a malformed entry cannot ship; and</item>
///   <item>the <b>scan</b> — <c>PlanValidator</c> emits one GR2037 per (four-folder script guardrail,
///     matching entry), after comment-stripping (the #97 lesson), citing the entry id/reason/hint.</item>
/// </list>
/// </summary>
public sealed class BannedPatternRegistryTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("gr2037-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // ============================================================================================
    // Meta-test — the registry's own quality bar (a malformed entry cannot land).
    // ============================================================================================

    [Fact]
    public void EmbeddedRegistry_LoadsAndPreCompiles()
    {
        // Load() deserializes the embedded default AND pre-compiles every badPattern, so an invalid
        // regex or a missing required field is a loud fault here — not a silent mid-scan surprise.
        BannedPatternRegistry registry = BannedPatternRegistry.Load();
        Assert.NotEmpty(registry.Patterns);
    }

    [Fact]
    public void EverySeedEntry_BadPatternMatchesAllMustMatch_AndNoMustNotMatch()
    {
        BannedPatternRegistry registry = BannedPatternRegistry.Load();

        foreach (BannedPattern pattern in registry.Patterns)
        {
            Regex matcher = pattern.Matcher; // a valid regex (throws here if not — Load pre-compiles)

            Assert.NotEmpty(pattern.MustMatch);    // fixtures are the quality bar — they must exist
            Assert.NotEmpty(pattern.MustNotMatch);

            foreach (string fixture in pattern.MustMatch)
            {
                Assert.True(matcher.IsMatch(fixture),
                    $"entry '{pattern.Id}' badPattern must MATCH its mustMatch fixture but did not:\n{fixture}");
            }

            foreach (string fixture in pattern.MustNotMatch)
            {
                Assert.False(matcher.IsMatch(fixture),
                    $"entry '{pattern.Id}' badPattern must NOT match its mustNotMatch fixture but did:\n{fixture}");
            }
        }
    }

    [Fact]
    public void Registry_IsExactlyTheCuratedSet_NotWhateverAccumulated()
    {
        // EXACT membership, deliberately — and this strictness is the point, not an accident of the
        // registry once being small. GR2037 is an ERROR that BLOCKS `validate`, driven by regexes over
        // guardrail SOURCE TEXT, so every entry buys real rejection power with real false-positive
        // surface. Pinning the whole id set forces each addition to be a conscious, reviewed act: an
        // entry an agent adds in passing fails HERE, at a test whose only fix is to state the new id and
        // say why. Growth IS allowed — casual growth is not.
        //
        // The user-approved honest cut SEEDED it with two: #73 (hollow assertion) and #187a
        // (unanchored conflict marker; the bare '=======' variant #187b stayed out). The standing
        // exclusions still stand — #175 (wrong polarity: a MISSING check keyed on plan topology, which a
        // banned-regex-over-source cannot see), #97/#98 (structural: the defect is the ABSENCE of a
        // comment-strip step, a whole-script property, not a banned substring), #112 (accessor-order:
        // expressible but FP-prone, deferred until a real regression). Rationale of record:
        // docs/plans/15-guardrail-script-lint.md §B.6, whose "Net seed: #73 + #187a" describes the SEED
        // and is history — it is not a cap on the set.
        //
        // #462 is the reviewed third: a `dotnet test` carrying '-v q' in a script that then greps for the
        // failure-detail block, which '-v q' suppresses — the guardrail still fails correctly but its
        // #179 re-emit is dead, so the retry learns WHAT failed and never WHY. Added by request in issue
        // #462; text-local, meta-tested by its own fixtures, and pinned below by a fires/clean pair.
        //
        // #608a / #608b / #561 are the design-38 three (docs/plans/38-guardrail-scan-soundness.md §7):
        // #608a bans `ErrorActionPreference` values (`Continue`/`SilentlyContinue`) that let a terminating
        // error unwind a guardrail past its own verdict while `pwsh -File` still exits 0 (§3.1); #608b
        // requires a guardrail's own last statement to be an explicit `exit`, which is what keeps the
        // harness-owned abort shim's one accepted divergence (§3.4) enforced rather than merely assumed;
        // #561 bans a scan copy that strips comments BEFORE neutralizing string literals — the wrong
        // preprocessing order that lets a literal's own `/*` blank real script text (§4.1). There is
        // deliberately no `#449` entry — the shape it would need to fire on is also the shape of the
        // doctrine's own canonical union guardrail, so the fix belongs to a gate `validate` runs, not a
        // banned-text lint (§5).
        BannedPatternRegistry registry = BannedPatternRegistry.Load();

        string[] ids = registry.Patterns.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "#187a", "#462", "#561", "#608a", "#608b", "#73" }, ids);
    }

    [Fact]
    public void Entry462_Matcher_IsBounded_AndDoesNotBacktrackCatastrophically()
    {
        // #462's badPattern pairs an alternation LOOP — (?:[^\r\n]|(?<=`)\r?\n)* — with a lazy window,
        // [\s\S]{0,4000}?. That is the classic ReDoS silhouette, so the entry's "unambiguous per position,
        // therefore it cannot backtrack catastrophically" claim is VERIFIED here rather than trusted. The
        // claim rests on the two alternatives being disjoint at every position ([^\r\n] can never match the
        // \r or \n the lookbehind branch consumes), which makes the loop's match length unambiguous — so
        // its backtracking is linear in the run length, never exponential.
        //
        // MEASURED, not asserted (the #248 discipline). Doubling the probe doubles the time, at every size
        // from 4 KB to 260 KB; and the cost tracks the number of `-v <sep>q` CANDIDATES, not the script
        // length — a 450 KB script with ONE candidate matches in 0.3 ms, because the lazy window is hard-
        // bounded at 4000 chars. The shape here is the worst case that exists: ~300 candidates in 12 KB,
        // each forced to scan its full window and fail. The real committed victim (#462's own preflight,
        // 3.2 KB, one candidate) measures 0.014 ms.
        //
        // This drives the PRODUCTION construction path: the embedded registry's own cached Matcher, the
        // exact Regex instance PlanValidator scans with — not a hand-rolled `new Regex(...)` that could
        // differ in options or timeout and so prove nothing about what ships.
        BannedPattern entry = Assert.Single(BannedPatternRegistry.Load().Patterns, p => p.Id == "#462");
        Regex matcher = entry.Matcher;

        // The bound that gives "well inside the timeout" a meaning. It is also the production failure
        // MODE: PlanValidator does not catch RegexMatchTimeoutException, so a pattern that blows through
        // this budget does not degrade `validate` — it crashes it.
        Assert.Equal(TimeSpan.FromSeconds(2), matcher.MatchTimeout);

        string adversarial = BacktrackingProbeScript();

        bool matched;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            matched = matcher.IsMatch(adversarial);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"#462's badPattern TIMED OUT (>{matcher.MatchTimeout.TotalSeconds:F0}s) on a " +
                $"{adversarial.Length:N0}-char adversarial guardrail script — it now backtracks " +
                $"catastrophically. `guardrails validate` would CRASH on such a script, not merely slow " +
                $"down, because the GR2037 scan does not catch this exception. Underlying: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
        }

        // The probe carries NO failure-detail token, so every candidate must scan its full lazy window and
        // fail: the engine exhausts the search space rather than taking an early exit. A match here would
        // mean the probe is not the shape this test claims to be timing.
        Assert.False(matched,
            "the backtracking probe must contain no failure-detail token — otherwise the match " +
            "short-circuits and the timing proves nothing about exhausting the search space.");

        // #518 — ASSERT THE SHAPE OF THE COST CURVE, NOT A STOPWATCH READING.
        //
        // This was `elapsed < 1000ms`. Healthy is ~70 ms, so the bound carried ~14x headroom — which the
        // #518 rule classes as a RACE, not a backstop (a backstop's two sides are orders of magnitude
        // apart). It duly false-red under load while passing at 335 ms isolated on the same tree.
        //
        // The load-immune property is the one this test's own header already MEASURED: "doubling the
        // probe doubles the time, at every size from 4 KB to 260 KB". Linear. Catastrophic backtracking
        // is superlinear by definition — that is what makes it catastrophic — so the RATIO between two
        // sizes timed in the SAME run is the real signal, and a busy box inflates both measurements
        // together and cancels out. A stopwatch reading cannot separate "the pattern got ambiguous" from
        // "this runner is busy"; a ratio can.
        //
        // The hard catastrophe detector is unchanged and load-immune already: RegexMatchTimeoutException
        // against the production 2s MatchTimeout, asserted by the catch above. This adds the early
        // warning that the absolute bound was reaching for, without inheriting its fragility.
        //
        // ...and the FIRST version of it inherited a different fragility, which macOS CI found within
        // hours. It timed the ratio through `matcher` — the PRODUCTION instance, whose MatchTimeout is 2s
        // — on a probe with TWICE the candidates. That is twice the work against a fixed budget, so a slow
        // or contended runner turned a linear-but-slow measurement into a RegexMatchTimeoutException and
        // the test reported catastrophic backtracking that had not happened. Measured on macos-latest:
        // 3 s elapsed, timeout thrown, on an unchanged pattern.
        //
        // The two assertions want DIFFERENT timeouts, and conflating them was the error:
        //
        //   catastrophe   the ORIGINAL probe against the PRODUCTION 2s matcher — the timeout IS the
        //                 assertion, because that is exactly how `validate` fails in production
        //                 (PlanValidator does not catch it, so the tool crashes).
        //   linearity     both sizes against a matcher with a GENEROUS timeout — the question is the
        //                 SHAPE of the curve, and a wall-clock ceiling on a bigger input measures the
        //                 runner, which is the whole thing the ratio exists to stop measuring.
        //
        // Same pattern, same options, only the timeout differs — so this still times the shipped
        // expression, not a hand-rolled approximation of it.
        var unhurried = new Regex(matcher.ToString(), matcher.Options, RatioTimeout);

        string doubled = BacktrackingProbeScript(candidates: 300);

        // THE MEASUREMENT IS RETRIED, NOT THE ASSERTION (#697). The earlier revision of this block took
        // ONE reading per size and argued that a ratio is load-immune because "a loaded runner inflates
        // both readings and cancels out". That argument is wrong, and macOS CI falsified it a third time
        // (10.9x, 198 ms -> 2160 ms, on an unchanged pattern): a ratio cancels a STEADY multiplier, not a
        // TRANSIENT. A GC pause or a scheduler preemption lands in ONE of the two readings, and the longer
        // reading is exposed to it for proportionally longer — so a transient inflates the numerator
        // specifically, which is the direction that fails the test.
        //
        // A genuine ambiguous quantifier is superlinear EVERY time; a transient is not. So the discriminator
        // is reproducibility, and the cheapest honest form of it is to re-measure only when about to fail
        // and keep the BEST (smallest) ratio seen. Transients only ever inflate a reading, never deflate
        // one, so the minimum is the estimate closest to the uncontended cost. Costs nothing on the happy
        // path — the loop exits on the first passing reading.
        double ratio = double.NaN;
        TimeSpan singleCost = TimeSpan.Zero;
        TimeSpan doubleCost = TimeSpan.Zero;

        for (int attempt = 0; attempt < RatioAttempts; attempt++)
        {
            TimeSpan single = TimeMatch(unhurried, adversarial);
            TimeSpan doubled_ = TimeMatch(unhurried, doubled);

            // A floor, because a RATIO of two sub-millisecond readings is noise, not evidence. Below it the
            // cost is so far inside the timeout that only the exception above is meaningful.
            if (doubled_ <= RatioNoiseFloor)
            {
                return;
            }

            double thisRatio = doubled_.TotalMilliseconds / Math.Max(single.TotalMilliseconds, 0.001);
            if (double.IsNaN(ratio) || thisRatio < ratio)
            {
                (ratio, singleCost, doubleCost) = (thisRatio, single, doubled_);
            }

            if (ratio < LinearRatioCeiling)
            {
                return; // linear on this reading — a superlinear pattern would not have managed one
            }
        }

        Assert.Fail(
            $"#462's badPattern cost {ratio:F1}x when the candidate count doubled " +
            $"({singleCost.TotalMilliseconds:F0} ms -> {doubleCost.TotalMilliseconds:F0} ms), and stayed " +
            $"above the ceiling across {RatioAttempts} independent measurements. Linear is ~2x and the " +
            $"ceiling is {LinearRatioCeiling}x; a superlinear curve means someone has given the pattern an " +
            "ambiguous quantifier — two alternatives that can both match the same position, or an unbounded " +
            "window where {0,4000} was. This is the BEST of several readings, so a one-off GC pause or a " +
            "preempted scheduler slice cannot be what you are looking at.");
    }

    /// <summary>
    /// How many times the linearity measurement is repeated BEFORE failing. Only paid when a reading is
    /// already over the ceiling, so a healthy run measures exactly once. Three is enough that a transient
    /// would have to land in the doubled reading three times running — while a genuinely ambiguous
    /// quantifier exceeds the ceiling on every attempt by construction.
    /// </summary>
    private const int RatioAttempts = 3;

    /// <summary>
    /// Below this, the ratio check is skipped: dividing two sub-millisecond readings measures scheduler
    /// noise, not the cost curve. At that speed the pattern is so far inside the 2s production timeout
    /// that the <see cref="RegexMatchTimeoutException"/> path is the only meaningful signal (#518).
    /// </summary>
    private static readonly TimeSpan RatioNoiseFloor = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The timeout the LINEARITY measurement runs under — generous on purpose, and deliberately not the
    /// production 2s. The ratio asks about the shape of the cost curve; a wall-clock ceiling on the
    /// doubled input measures the RUNNER, which is precisely what the ratio exists to stop measuring.
    /// The production timeout still gates the original probe above, where it is the assertion (#518).
    /// </summary>
    private static readonly TimeSpan RatioTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The ceiling on cost growth when the candidate count DOUBLES. Linear is ~2x — the measured property
    /// this probe's header records at every size from 4 KB to 260 KB. Six leaves 3x of slack for JIT,
    /// GC and a contended scheduler while still being far below the growth an ambiguous quantifier
    /// produces, which is superlinear and runs away rather than drifting (#518).
    /// </summary>
    private const double LinearRatioCeiling = 6.0;

    /// <summary>
    /// Times one match on the PRODUCTION matcher, letting a genuine catastrophe surface as the timeout
    /// exception rather than as a number (#518).
    /// </summary>
    private static TimeSpan TimeMatch(Regex matcher, string subject)
    {
        var sw = Stopwatch.StartNew();
        matcher.IsMatch(subject);
        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>
    /// The adversarial input for the backtracking check — aimed squarely at #462's measured worst case,
    /// which is CANDIDATE DENSITY, not length. ONE <c>dotnet test</c> invocation is spread over many
    /// physical lines by PowerShell backtick continuations, so the alternation loop must traverse every
    /// newline through its lookbehind branch instead of stopping at the first one. Every line carries two
    /// REAL <c>-v q</c> candidates (<c>-v quiet</c>, <c>-v q</c>), each forcing a full 4000-char lazy-window
    /// scan, plus three near-miss <c>-v</c> fragments (<c>-v normal</c>, <c>-verbosity</c>, <c>-vq</c>) that
    /// reach the flag test and fail it. Nothing anywhere contains a failure-detail token, so the overall
    /// match can only fail after the whole space is exhausted — no early exit inflates the result.
    /// </summary>
    private static string BacktrackingProbeScript(int candidates = 150)
    {
        var script = new StringBuilder();
        script.Append("$log = & dotnet test tests/Big.Tests/Big.Tests.csproj `\n");
        for (int i = 0; i < candidates; i++)
        {
            script.Append("    --filter \"Category=Slice").Append(i)
                  .Append("\" -v quiet -v normal -verbosity -vq -v q --nologo `\n");
        }

        script.Append("    --no-restore 2>&1\n");
        script.Append("if ($LASTEXITCODE -ne 0) { exit 1 }\n");
        return script.ToString();
    }

    // ============================================================================================
    // Scan — GR2037 fires on the shipped seed patterns, in each of the four folders.
    // ============================================================================================

    [Fact]
    public void UnanchoredConflictMarker_FiresGr2037_Citing187a()
    {
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            """
            $content = Get-Content -Raw $file
            if ($content -match '<<<<<<<' -or $content -match '>>>>>>>') { Write-Output 'conflict'; exit 1 }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#187a", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongParenAnchorForm_FiresGr2037_Citing187a()
    {
        // The exact #346-incident spelling: (^|[[:space:]]) accepts a whitespace-preceded (non-start)
        // match, so a mid-line illustrative marker false-fails. Banned.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            """
            grep -Eq '(^|[[:space:]])(<<<<<<<|>>>>>>>)' "$rel" && exit 1
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#187a", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetextUnderlineOrBannerCheck_IsClean_NoGr2037()
    {
        // The deferred '={7}' term is correctly ABSENT from #187a (review BLOCKER): a legitimate
        // markdown-setext-underline / banner check that greps for a bare '=======' must NOT be
        // rejected — banning it added no coverage of the actual #346 incident and was pure FP surface.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-setext",
            """
            $doc = Get-Content -Raw $file
            if ($doc -notmatch '(?m)^=======') { Write-Output 'missing setext underline'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void AnchoredConflictMarker_IsClean_NoGr2037()
    {
        // The GOOD line-anchored form (the #187 doctrine, matching examples/parallel-hello) — the
        // ours/theirs tokens are immediately preceded by '^', so the unanchored ban does not fire.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            """
            $content = Get-Content -Raw $file
            if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') { exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void HollowAssertion_FiresGr2037_Citing73()
    {
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-moved-count",
            """
            $src = Get-Content -Raw $test
            if ($src -notmatch 'Assert.*\([^)]*(Moved|Written|Count|Entities)') { Write-Output 'no assertion'; exit 1 }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#73", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveValueAssertion_IsClean_NoGr2037()
    {
        // The GOOD form: require a STRICTLY-POSITIVE value — a legitimate Count>0 / NotEmpty check must
        // not be mistaken for the hollow keyword-presence construction.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-moved-count",
            """
            $src = Get-Content -Raw $test
            if ($src -notmatch '(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)') { exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void HollowShapeButRequiresPositivity_IsClean_NoGr2037()
    {
        // The review's #73 WEAK FP: an Assert-on-quantity construct that ALSO requires positivity
        // ('.*>\s*0' inside the SAME quoted regex) IS sufficient, not hollow — the trailing negative
        // lookahead keeps it clean.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-moved-positive",
            """
            $src = Get-Content -Raw $test
            if ($src -notmatch 'Assert.*(Moved|Written|Count|Entities).*>\s*0') { exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ---- #462: a `dotnet test -v q` whose #179 failure-detail re-emit is therefore dead -------------

    [Fact]
    public void QuietVerbosityOnDotnetTestWithFailureDetailReEmit_FiresGr2037_Citing462()
    {
        // The shipped defect, in the shape it was actually committed in (issue #462 names four live
        // victims under docs/plans/model-tiering-stage-1/). `-v q` deletes the entire failure-detail
        // block, so the Select-String below re-emits NOTHING and the ~60-line retry-feedback tail carries
        // '[FAIL] <name>' with no WHY — the exact blindness #179 exists to prevent. The guardrail still
        // fails correctly and still reads as right on the page, which is why a human reviewer misses it
        // and a deterministic lint must not.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-tests-pass",
            """
            $ErrorActionPreference = 'Stop'
            $filter = 'Category=Stage1&FullyQualifiedName~PromptRunnerSchemaTests'
            $log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo -v q 2>&1
            $code = $LASTEXITCODE
            $log | ForEach-Object { Write-Output $_ }
            if ($code -ne 0) {
                Write-Output "--- failure detail ---"
                $log | Select-String -Pattern '^\s*(Failed|Error Message|Assert\.|Expected|Actual)' | ForEach-Object { Write-Output $_.Line }
                exit 1
            }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#462", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DotnetTestWithLiveReEmitAndNoQuietVerbosity_IsClean_NoGr2037()
    {
        // The GOOD form — and deliberately the firing script above with ONE TOKEN REMOVED, nothing else
        // touched, so this pair isolates the lint's discriminator to '-v q' itself. Widen the entry and
        // this test goes red, which is the point: the re-emit is not what is banned, only its pairing
        // with the flag that empties it. (The full doctrinal good form also pins
        // $env:DOTNET_CLI_UI_LANGUAGE so the summary the zero-match guard reads is not localized; that
        // belongs to the hint, not to this lint's trigger, and the registry's own mustNotMatch fixture
        // covers it.)
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-tests-pass",
            """
            $ErrorActionPreference = 'Stop'
            $filter = 'Category=Stage1&FullyQualifiedName~PromptRunnerSchemaTests'
            $log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo 2>&1
            $code = $LASTEXITCODE
            $log | ForEach-Object { Write-Output $_ }
            if ($code -ne 0) {
                Write-Output "--- failure detail ---"
                $log | Select-String -Pattern '^\s*(Failed|Error Message|Assert\.|Expected|Actual)' | ForEach-Object { Write-Output $_.Line }
                exit 1
            }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void QuietVerbosityOnInverseStubCheckWithNoReEmit_IsClean_NoGr2037()
    {
        // DELIBERATE EXCLUSION 1 of 2 — the half of the doctrine this entry does NOT enforce, and the
        // scope decision is the contract, not an oversight. On an INVERSE 'tests-fail-on-stubs' check a
        // NON-ZERO exit is the SUCCESS condition, so there is no failure detail to lose and '-v q' costs
        // nothing. Doctrine still discourages it (dotnet.md §4.3 point 3, so the two halves of a TDD pair
        // stay copy-pasteable and the flag is not propagated onto a forward check by cloning a sibling) —
        // but GR2037 is an ERROR that BLOCKS `validate`, and firing here would REJECT a guardrail that
        // certifies exactly what it claims. That half stays doctrine enforced by review.
        //
        // The entry earns its keep by being self-contradictory FROM THE SCRIPT TEXT ALONE. No re-emit
        // grep, no contradiction, no fire.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "02-tests-fail-on-stubs",
            """
            $ErrorActionPreference = 'Stop'
            $filter = 'Category=Stage1&FullyQualifiedName~PromptRunnerSchemaTests'
            dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo -v q
            if ($LASTEXITCODE -eq 0) {
                Write-Output 'the authored tests PASS against the stubs - they are tautological, not a forward check'
                exit 1
            }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void QuietVerbosityOnDotnetBuildBesideACleanTestAndReEmit_IsClean_NoGr2037()
    {
        // DELIBERATE EXCLUSION 2 of 2 — '-v q' is genuinely RIGHT for `dotnet build` (it strips restore
        // chatter and leaves the errors), so a build line carrying it must not poison a correct
        // `dotnet test` + re-emit sitting below it. This is what confines the flag search to the test
        // command's own logical line: a PLAIN newline stops the scan, so the two commands cannot be
        // conflated. Widen that and this script — the recommended shape — starts failing `validate`.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-tests-pass",
            """
            $ErrorActionPreference = 'Stop'
            $filter = 'Category=Stage1&FullyQualifiedName~PromptRunnerSchemaTests'
            dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --nologo -v q
            if ($LASTEXITCODE -ne 0) { Write-Output 'the test project does not compile'; exit 1 }
            $log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --no-build --nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Output "--- failure detail ---"
                $log | Select-String -Pattern 'Error Message:|Assert\.|Stack Trace:|Expected:|Actual:' | ForEach-Object { Write-Output $_.Line }
                exit 1
            }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BacktickContinuationHidingQuietVerbosity_FiresGr2037_Citing462(string newline)
    {
        // The flag hidden on the NEXT PHYSICAL LINE — not a hypothetical, this is verbatim the shape of
        // the committed §21 baseline preflight (model-tiering-stage-1/preflights/01-baseline-core-tests-
        // green.ps1), where a PowerShell backtick continuation put '-v q' one line below `dotnet test`.
        // The entry deliberately crosses a BACKTICK-terminated newline for exactly this reason, and just
        // as deliberately stops at a plain one (pinned by the sibling `dotnet build` test above) — so
        // this pair, not either half alone, is what makes the line-crossing rule non-arbitrary.
        //
        // Both line endings are exercised on EVERY OS. The .cs file is not eol-pinned, so a raw string
        // literal would carry CRLF on a Windows checkout and LF on Linux/macOS: each leg of the 3-OS
        // matrix would prove only its own half. Built explicitly here, both legs are proved everywhere.
        string body = string.Join(newline,
        [
            "$ErrorActionPreference = 'Stop'",
            "$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `",
            "    --filter \"Category!=ModelTieringStage1\" --nologo -v q 2>&1",
            "$code = $LASTEXITCODE",
            "if ($code -ne 0) {",
            "    Write-Output \"--- baseline failure detail ---\"",
            "    $log | Select-String -Pattern '^\\s*(Failed|Error Message|Assert\\.)' | ForEach-Object { Write-Output $_.Line }",
            "    exit 1",
            "}",
            "exit 0",
        ]);

        GuardrailDefinition guardrail = WriteScript("tasks/01-a/preflights", "01-baseline-green", body);
        PlanDefinition plan = BasePlan() with { Tasks = [SimpleTask("01-a", preflights: [guardrail])] };

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(plan));
        Assert.Contains("#462", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BannedConstructionOnlyInComment_IsClean_NoGr2037()
    {
        // Comment-strip discipline (the #97 lesson, itself the reason to strip first): a `catches:`
        // header that merely DESCRIBES the banned constructions must not false-fire.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            """
            # catches: a hollow assertion Assert.*\([^)]*(Moved|Written|Count|Entities) that passes on
            #          zero, and an unanchored <<<<<<< / ======= conflict-marker scan (#73 / #187a).
            $count = 5
            if ($count -le 0) { Write-Output 'nothing produced'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void OneGuardrailMatchingBothSeeds_EmitsTwoGr2037_OnePerEntry()
    {
        // "One GR2037 per match" — a body carrying BOTH banned constructions yields one diagnostic per
        // matching entry (#73 and #187a).
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-both",
            """
            $src = Get-Content -Raw $test
            if ($src -match 'Assert.*\([^)]*(Moved|Written|Count|Entities)') { exit 0 }
            if ($src -match '<<<<<<<') { exit 1 }
            exit 0
            """);

        IReadOnlyList<Diagnostic> diagnostics = ValidateEmbedded(PlanWithTaskGuardrail(guardrail));

        List<Diagnostic> gr2037 = diagnostics.Where(d => d.Code == DiagnosticCodes.BannedGuardrailPattern).ToList();
        Assert.Equal(2, gr2037.Count);
        Assert.Contains(gr2037, d => d.Message.Contains("#73", StringComparison.Ordinal));
        Assert.Contains(gr2037, d => d.Message.Contains("#187a", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptGuardrail_IsNotScanned_NoGr2037()
    {
        // Prompt guardrails are prose, not a regex construction — out of scope for the scan even if the
        // prose happens to contain a banned token.
        GuardrailDefinition prompt = WriteScript("tasks/01-a/guardrails", "01-judge",
            "The output must contain no <<<<<<< conflict markers.");
        prompt = prompt with { Kind = ActionKind.Prompt };

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(prompt)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ---- #608a: ErrorActionPreference must not let a mid-script terminating error unwind silently ----

    [Fact]
    public void Entry608a_ContinuePreference_FiresGr2037()
    {
        // The exact #608 fail-open shape (design 38 §3.1): 'Continue' lets a terminating error unwind the
        // script past its own verdict while pwsh -File still exits 0 — the abort shim (§3.2) closes the
        // harness side, but the source itself is banned so the next generation does not recreate it.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-census",
            """
            $ErrorActionPreference = 'Continue'
            $content = Get-Content -Raw $file
            if ($content -match 'TODO') { Write-Output 'todo found'; exit 1 }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#608a", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry608a_StopPreference_IsClean_NoGr2037()
    {
        AssertEntryPresent("#608a");

        // The doctrinal GOOD form — same guardrail, 'Stop' in place of 'Continue'.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-census",
            """
            $ErrorActionPreference = 'Stop'
            $content = Get-Content -Raw $file
            if ($content -match 'TODO') { Write-Output 'todo found'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ---- #608b: a guardrail must end on an explicit exit -- the abort shim's own assumption (§3.4) ----

    [Fact]
    public void Entry608b_GuardrailNotEndingOnExit_FiresGr2037()
    {
        // Falls off the end after a real check instead of stating its verdict explicitly — the divergent
        // shape design 38 §3.4 measured at zero in the corpus, and this entry is what keeps it at zero.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            """
            $ErrorActionPreference = 'Stop'
            $content = Get-Content -Raw $file
            if ($content -match 'TODO') { Write-Output 'todo found'; exit 1 }
            Write-Output 'done'
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#608b", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry608b_GuardrailEndingOnExit_IsClean_NoGr2037()
    {
        AssertEntryPresent("#608b");

        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            """
            $ErrorActionPreference = 'Stop'
            $content = Get-Content -Raw $file
            if ($content -match 'TODO') { Write-Output 'todo found'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    [Fact]
    public void Entry608b_TryFinallyCleanupIdiom_IsClean_NoGr2037()
    {
        // The trap this entry must NOT fall into: a guardrail that creates a temp directory and cleans it
        // up on BOTH the exit-1 and the exit-0 path via 'finally' has its last physical line as a bare
        // '}', not the literal token 'exit' — but its verdict is still stated explicitly on every path
        // that reaches it. A lint keyed on "does the last non-blank line start with exit" would false-fire
        // on this doctrinal idiom, which is exactly why it is pinned here rather than assumed clean.
        AssertEntryPresent("#608b");

        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            """
            $ErrorActionPreference = 'Stop'
            $tmp = Join-Path $env:TEMP ([Guid]::NewGuid())
            New-Item -ItemType Directory -Path $tmp | Out-Null
            try {
                $content = Get-Content -Raw $file
                if ($content -match 'TODO') { Write-Output 'todo found'; exit 1 }
                exit 0
            } finally {
                Remove-Item $tmp -Recurse -Force
            }
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ---- #561: neutralize string literals BEFORE stripping comments, never the reverse (§4.1) --------

    [Fact]
    public void Entry561_CommentStripBeforeLiteralNeutralize_FiresGr2037()
    {
        // The corrected-order violation, in its own words: block comments are blanked FIRST, so a literal
        // that spells a comment delimiter (a glob pattern's '/*' is the repo's routine case) has already
        // been mis-parsed as a real comment by the time the literal-neutralization pass ever runs.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-scan-copy",
            """
            $raw = Get-Content -Raw $file
            $scan = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')
            $scan = [regex]::Replace($scan, '(?m)//[^\r\n]*', ' ')
            $scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')
            if ($scan -match 'BANNED_TOKEN') { Write-Output 'found'; exit 1 }
            exit 0
            """);

        Diagnostic diagnostic = AssertSingleGr2037(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)));
        Assert.Contains("#561", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry561_LiteralNeutralizeFirst_IsClean_NoGr2037()
    {
        AssertEntryPresent("#561");

        // The doctrinal GOOD form (§4.1) — the exact script above with the two passes swapped, nothing
        // else touched, so this pair isolates the lint's discriminator to ORDER alone.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-scan-copy",
            """
            $raw = Get-Content -Raw $file
            $scan = [regex]::Replace($raw, '"(\\.|[^"\\])*"', '""')
            $scan = [regex]::Replace($scan, '/\*[\s\S]*?\*/', ' ')
            $scan = [regex]::Replace($scan, '(?m)//[^\r\n]*', ' ')
            if ($scan -match 'BANNED_TOKEN') { Write-Output 'found'; exit 1 }
            exit 0
            """);

        Assert.DoesNotContain(ValidateEmbedded(PlanWithTaskGuardrail(guardrail)),
            d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ---- per-folder coverage: the scan reaches every four-folder script slot -------------------

    [Fact]
    public void TaskPreflight_IsScanned_FiresGr2037()
    {
        GuardrailDefinition preflight = WriteScript("tasks/01-a/preflights", "01-dep",
            "if ($content -match '<<<<<<<') { exit 1 }");
        PlanDefinition plan = BasePlan() with
        {
            Tasks = [SimpleTask("01-a", preflights: [preflight])],
        };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    [Fact]
    public void PlanLevelPreflight_IsScanned_FiresGr2037()
    {
        GuardrailDefinition preflight = WriteScript("preflights", "01-baseline",
            "if ($content -match '<<<<<<<') { exit 1 }");
        PlanDefinition plan = BasePlan() with
        {
            Tasks = [SimpleTask("01-a")],
            PlanPreflights = [preflight],
        };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    [Fact]
    public void PlanLevelGuardrail_IsScanned_FiresGr2037()
    {
        GuardrailDefinition planGuardrail = WriteScript("guardrails", "01-terminal",
            "if ($content -match '<<<<<<<') { exit 1 }");
        PlanDefinition plan = BasePlan() with
        {
            Tasks = [SimpleTask("01-a")],
            PlanGuardrails = [planGuardrail],
        };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    [Fact]
    public void WaveLevelGuardrail_IsScanned_FiresGr2037()
    {
        GuardrailDefinition waveGuardrail = WriteScript("wave-01-x/guardrails", "01-exit",
            "if ($content -match '<<<<<<<') { exit 1 }");
        TaskNode waveTask = SimpleTask("wave-01-x/01-a") with { WaveDir = "wave-01-x" };
        var wave = new WaveNode
        {
            Dir = "wave-01-x",
            Number = 1,
            Slug = "x",
            Directory = Path.Combine(_tempRoot, "wave-01-x"),
            Tasks = [waveTask],
            Guardrails = [waveGuardrail],
        };
        PlanDefinition plan = BasePlan() with { Tasks = [waveTask], Waves = [wave] };

        AssertSingleGr2037(ValidateEmbedded(plan));
    }

    // ============================================================================================
    // Injection seam (DIP) — a synthetic registry drives the scan; an empty one disables it.
    // ============================================================================================

    [Fact]
    public void InjectedSyntheticRegistry_DrivesTheScan()
    {
        var synthetic = new BannedPatternRegistry(
        [
            new BannedPattern
            {
                Id = "#synthetic",
                BadPattern = "FORBIDDEN_TOKEN",
                Reason = "a synthetic banned construction for the injection test.",
                GoodPatternHint = "use ALLOWED_TOKEN.",
            },
        ]);

        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-check",
            "if ($x -match 'FORBIDDEN_TOKEN') { exit 1 }");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All, synthetic).Validate(PlanWithTaskGuardrail(guardrail));

        Diagnostic diagnostic = AssertSingleGr2037(diagnostics);
        Assert.Contains("#synthetic", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRegistry_EmitsNoGr2037()
    {
        var empty = new BannedPatternRegistry([]);

        // A body that WOULD trip the seed patterns is clean under an empty registry.
        GuardrailDefinition guardrail = WriteScript("tasks/01-a/guardrails", "01-union",
            "if ($content -match '<<<<<<<') { exit 1 }");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All, empty).Validate(PlanWithTaskGuardrail(guardrail));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    private GuardrailDefinition WriteScript(string relFolder, string name, string body)
    {
        string dir = Path.Combine(_tempRoot, relFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".ps1");
        File.WriteAllText(path, body);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    /// <summary>A task with one clean, always-passing script guardrail (so GR2003 never fires on it).</summary>
    private GuardrailDefinition CleanGuardrail(string taskId) =>
        WriteScript($"tasks/{taskId}/guardrails", "00-ok", "exit 0");

    private TaskNode SimpleTask(string id, IReadOnlyList<GuardrailDefinition>? preflights = null) => new()
    {
        Id = id,
        Directory = Path.Combine(_tempRoot, "tasks", id),
        Description = $"task {id}",
        Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", id, "action.ps1"), Kind = ActionKind.Script },
        Guardrails = [CleanGuardrail(id)],
        Preflights = preflights ?? [],
    };

    private PlanDefinition BasePlan() => new()
    {
        PlanDirectory = _tempRoot,
        Workspace = _tempRoot,
        // Serial (maxParallelism 1) so the git-root (GR2015) / terminal-gate (GR2028) worktree-mode
        // checks stay silent — the GR2037 scan is the only rule under test.
        Config = new RunConfig { Version = 1, MaxParallelism = 1 },
        Tasks = [],
        PlanPreflights = [],
        PlanGuardrails = [],
    };

    /// <summary>A single-task plan whose one task carries <paramref name="guardrail"/> as its only guardrail.</summary>
    private PlanDefinition PlanWithTaskGuardrail(GuardrailDefinition guardrail)
    {
        TaskNode task = new()
        {
            Id = "01-a",
            Directory = Path.Combine(_tempRoot, "tasks", "01-a"),
            Description = "task 01-a",
            Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", "01-a", "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [guardrail],
            Preflights = [],
        };
        return BasePlan() with { Tasks = [task] };
    }

    private static IReadOnlyList<Diagnostic> ValidateEmbedded(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    private static Diagnostic AssertSingleGr2037(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        return diagnostic;
    }

    /// <summary>
    /// Absence is not the same as an entry that correctly stays silent — an "IsClean" test against an
    /// entry that does not exist yet passes for the wrong reason. Call this before asserting no
    /// diagnostic fires, so the clean half is coupled to the entry it claims to pin.
    /// </summary>
    private static void AssertEntryPresent(string id) =>
        Assert.Contains(BannedPatternRegistry.Load().Patterns, p => p.Id == id);
}
