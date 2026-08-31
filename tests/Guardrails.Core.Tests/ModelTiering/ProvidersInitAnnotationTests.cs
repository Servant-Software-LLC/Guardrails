using System.Text.Json;
using Guardrails.Core.Model;
using Guardrails.Core.Providers;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The engine behind <c>guardrails providers init</c> (SSOT §9.7, model-tiering Stage 1 charter §B,
/// DoR §4.3): the surgical, comment-preserving annotation of a real <c>guardrails.json</c>.
///
/// <para><b>The idempotency cases are the centre of this file, not a corner of it.</b> The charter's
/// acceptance criterion is blunt about why — <i>"a generator that clobbers the annotation it exists to
/// solicit is worse than no generator"</i> — so the assertion is BYTE-IDENTICAL output after a human has
/// edited the file, not "produces an equivalent config". A round-trip through
/// <see cref="JsonSerializer"/> would pass a semantic-equality test and fail every one of these, which is
/// exactly why the implementation may not use one.</para>
/// </summary>
public sealed class ProvidersInitAnnotationTests
{
    /// <summary>
    /// A config in the shape real ones take: a <c>default</c> pointer that is NOT a block, one block
    /// carrying no axes at all, and one that already states two of them WITHOUT comments — so both
    /// insertion paths (append a missing key; comment a present one) are exercised by one fixture.
    /// </summary>
    private const string Fixture =
        """
        {
          // Run configuration — a comment a human wrote, and a generator must never eat.
          "version": 1,
          "promptRunners": {
            "default": "claude",
            "claude": {
              "command": "claude",
              "maxTurns": 25
            },
            "cheap": {
              "command": "claude",
              "kind": "claude",
              "costly": false,
              "strength": 2
            }
          }
        }
        """;

    // ── criterion 3: the legal values become discoverable in the file being edited ───────────

    /// <summary>
    /// Every axis is introduced by a <c>//</c> comment carrying its LEGAL VALUES, and those values are
    /// the ones validation actually enforces — the assertion reads
    /// <see cref="PromptRunnerSpecializations.TokenList"/> and <see cref="ActionTiers.TokenList"/> rather
    /// than repeating their spellings, so a future enum change fails here instead of shipping a comment
    /// that lies to the user about what their own config may contain.
    /// </summary>
    [Fact]
    public void Annotate_CommentsEveryAxisWithTheLegalValuesValidationEnforces()
    {
        RegistryAnnotationResult result = RegistryAnnotation.Annotate(Fixture);

        Assert.True(result.Succeeded, result.Failure);
        string annotated = result.AnnotatedText;

        Assert.Contains("// costly: true | false | null", annotated, StringComparison.Ordinal);
        Assert.Contains("// strength: an integer >= 1 | null", annotated, StringComparison.Ordinal);
        Assert.Contains("// specialization:", annotated, StringComparison.Ordinal);
        Assert.Contains("// routing:", annotated, StringComparison.Ordinal);

        // The enums themselves, spelled by their single source of truth.
        Assert.Contains(PromptRunnerSpecializations.TokenList, annotated, StringComparison.Ordinal);
        Assert.Contains(ActionTiers.TokenList, annotated, StringComparison.Ordinal);

        // And every one of those legal-value lines really is a `//` comment, and really landed.
        foreach (RegistryAxisSpec axis in RegistryAxes.All)
        {
            Assert.All(axis.CommentLines, line => Assert.StartsWith("//", line, StringComparison.Ordinal));
            Assert.All(axis.CommentLines, line => Assert.Contains(line, annotated, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A block that already STATES an axis is left ENTIRELY alone — value and commentary both. Only the
    /// axes it does not carry are appended (each with its legal-value comment). Maintainer ruling
    /// 2026-08-15: the skip is keyed on "the key exists", not "a comment is near it", which is what makes
    /// a human's deletion of a generated comment stick — see
    /// <see cref="Annotate_DoesNotResurrectACommentAHumanDeleted"/>.
    /// </summary>
    [Fact]
    public void Annotate_LeavesAPresentAxisEntirelyAlone()
    {
        RegistryAnnotationResult result = RegistryAnnotation.Annotate(Fixture);

        RegistryBlockReport cheap = result.Blocks.Single(b => b.Name == "cheap");
        Assert.Equal(0, cheap.AddedComments);   // `costly` and `strength` are stated: nothing to solicit
        Assert.Equal([RegistryAxes.Specialization, RegistryAxes.Routing], cheap.AddedKeys);

        Assert.Contains("\"costly\": false", result.AnnotatedText, StringComparison.Ordinal);
        Assert.Contains("\"strength\": 2", result.AnnotatedText, StringComparison.Ordinal);

        // Exactly one `"costly": null` — the one appended to the block that had none. The block that
        // stated `false` keeps its answer; a second placeholder there would be a rewrite.
        Assert.Equal(1, Occurrences(result.AnnotatedText, "\"costly\": null"));
        Assert.Equal(1, Occurrences(result.AnnotatedText, "\"strength\": null"));
    }

    /// <summary>
    /// A comment a human DELETED must not come back (maintainer ruling, 2026-08-15). Deleting the
    /// solicitation is a decision — re-inserting it would re-ask a question they closed, every run,
    /// forever. Stickiness is structural here: the skip is keyed on the key's existence, so no marker is
    /// written into the user's file to remember the deletion by.
    /// <para>
    /// What is NOT lost is the asking itself: the block stays on the run report's unstated list while its
    /// value is still <c>null</c>. The comment asks once in the file; the report keeps asking.
    /// </para>
    /// </summary>
    [Fact]
    public void Annotate_DoesNotResurrectACommentAHumanDeleted()
    {
        // Run 1: the verb appends every axis with its legal-value comment.
        string annotated = RegistryAnnotation.Annotate(Fixture).AnnotatedText;
        Assert.Contains("// specialization:", annotated, StringComparison.Ordinal);

        // The human deletes ONE generated comment block, keeping the key it introduced.
        string[] lines = annotated.Split('\n');
        string withoutComment = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("// specialization:", StringComparison.Ordinal)
                                                               && !l.TrimStart().StartsWith("//   A preference used", StringComparison.Ordinal)
                                                               && !l.TrimStart().StartsWith("//   'coding'", StringComparison.Ordinal)
                                                               && !l.TrimStart().StartsWith("//   'unspecified' explicitly", StringComparison.Ordinal)));
        Assert.DoesNotContain("// specialization:", withoutComment, StringComparison.Ordinal);

        // Run 2: the key is present, so the axis is skipped whole — the deletion sticks.
        RegistryAnnotationResult second = RegistryAnnotation.Annotate(withoutComment);

        Assert.True(second.Succeeded);
        Assert.DoesNotContain("// specialization:", second.AnnotatedText, StringComparison.Ordinal);
        Assert.Equal(withoutComment, second.AnnotatedText);

        // …and the question is still being asked where asking belongs: the report.
        Assert.Contains(second.Blocks, b => b.UnstatedAxes.Contains(RegistryAxes.Specialization));
    }

    // ── criterion 1: idempotent, and byte-identical over a human's annotation ────────────────

    /// <summary>
    /// THE acceptance criterion. Annotate; let a human answer the axes, add their own note, and reflow a
    /// generated comment into their own words; re-run. The result must be BYTE-IDENTICAL — not
    /// "equivalent", not "the same values re-emitted". A second run must not touch those bytes at all.
    /// </summary>
    [Fact]
    public void Annotate_IsByteIdenticalOverAHumanAnnotation()
    {
        string generated = RegistryAnnotation.Annotate(Fixture).AnnotatedText;

        // The human does what the file is asking them to do: answers the axes, replaces one of the
        // generator's comments with their own shorter wording, and adds a note of their own.
        string humanEdited = generated
            .Replace("\"costly\": null", "\"costly\": true, // Fable: humans only", StringComparison.Ordinal)
            .Replace("\"strength\": null", "\"strength\": 9", StringComparison.Ordinal)
            .Replace(
                "// strength: an integer >= 1 | null — higher = stronger, and the only total order.",
                "// strength — ours is the strongest thing we have.",
                StringComparison.Ordinal)
            .Replace(
                "\"maxTurns\": 25",
                "\"maxTurns\": 25, // raised for the long refactor task",
                StringComparison.Ordinal);

        RegistryAnnotationResult rerun = RegistryAnnotation.Annotate(humanEdited);

        Assert.True(rerun.Succeeded, rerun.Failure);
        Assert.False(rerun.HasChanges, "a re-run proposed a change to an already-annotated config");
        Assert.Empty(rerun.Hunks);
        Assert.Equal(humanEdited, rerun.AnnotatedText);
    }

    /// <summary>
    /// The plain idempotency case: annotate twice with no human in between. The second pass is a genuine
    /// no-op — zero hunks — rather than a rewrite that happens to land on the same bytes.
    /// </summary>
    [Fact]
    public void Annotate_TwiceInARowIsAGenuineNoOp()
    {
        string once = RegistryAnnotation.Annotate(Fixture).AnnotatedText;
        RegistryAnnotationResult twice = RegistryAnnotation.Annotate(once);

        Assert.False(twice.HasChanges);
        Assert.Empty(twice.Hunks);
        Assert.Equal(once, twice.AnnotatedText);
    }

    /// <summary>
    /// A comment a human wrote is carried through verbatim, and exactly once. Round-tripping the file
    /// through <see cref="JsonSerializer"/> would delete every one of these, which is the whole reason
    /// the implementation is a text edit.
    /// </summary>
    [Fact]
    public void Annotate_KeepsExistingUserCommentsVerbatim()
    {
        const string userComment = "// Run configuration — a comment a human wrote, and a generator must never eat.";

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(Fixture);

        // Guard the guard: an implementation that annotated NOTHING would trivially preserve the
        // comment, so the test has to insist the file really was rewritten before it means anything.
        Assert.True(result.Succeeded, result.Failure);
        Assert.True(result.HasChanges);

        Assert.Contains(userComment, result.AnnotatedText, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(result.AnnotatedText, userComment));
    }

    /// <summary>
    /// Never reorders, never deletes: every key the original carried is still there, with the same value,
    /// and in the same relative order — the original property sequence of every block is a PREFIX of the
    /// annotated one, because the only structural change permitted is an append.
    /// </summary>
    [Fact]
    public void Annotate_NeverReordersAndNeverDeletes()
    {
        RegistryAnnotationResult result = RegistryAnnotation.Annotate(Fixture);

        // Guard the guard: without these, an implementation that REFUSED to annotate at all would pass
        // every assertion below by handing back the original text. (A mutation that moved the appended
        // keys to the TOP of the block did exactly that — it produced invalid JSON, the post-condition
        // refused it, and this test went green on the unchanged original.)
        Assert.True(result.Succeeded, result.Failure);
        Assert.True(result.HasChanges, "the fixture has unstated axes, so there was something to add");

        string annotated = result.AnnotatedText;

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        using JsonDocument before = JsonDocument.Parse(Fixture, options);
        using JsonDocument after = JsonDocument.Parse(annotated, options);

        JsonElement beforeRunners = before.RootElement.GetProperty("promptRunners");
        JsonElement afterRunners = after.RootElement.GetProperty("promptRunners");

        Assert.Equal(
            beforeRunners.EnumerateObject().Select(p => p.Name),
            afterRunners.EnumerateObject().Select(p => p.Name));

        foreach (JsonProperty block in beforeRunners.EnumerateObject()
                     .Where(p => p.Value.ValueKind == JsonValueKind.Object))
        {
            string[] originalKeys = [.. block.Value.EnumerateObject().Select(p => p.Name)];
            string[] annotatedKeys =
                [.. afterRunners.GetProperty(block.Name).EnumerateObject().Select(p => p.Name)];

            Assert.Equal(originalKeys, annotatedKeys[..originalKeys.Length]);

            foreach (JsonProperty original in block.Value.EnumerateObject())
            {
                Assert.Equal(
                    original.Value.GetRawText(),
                    afterRunners.GetProperty(block.Name).GetProperty(original.Name).GetRawText());
            }
        }
    }

    // ── criterion 2: it never invents a model (settled OD-E) ─────────────────────────────────

    /// <summary>
    /// <c>claude</c> — the only kind this fixture declares — has no model-enumeration surface, so the
    /// command adds NO block and writes NO model identifier: it annotates what is there, says out loud that
    /// it could not enumerate, and SUCCEEDS. The assertion is deliberately structural: the only keys that
    /// appear anywhere in the annotated config that were not in the original are the four solicited ones,
    /// so a <c>model</c> key (or a whole invented block) cannot slip in unnoticed.
    ///
    /// <para><b>Re-baselined when <c>openai-compat</c> joined <see cref="PromptRunnerKinds.ModelEnumerable"/></b>
    /// (plan 28 §7, issue #223): that kind's blocks declare an <c>endpoint</c> and the pre-DAG preflight
    /// really does <c>GET {endpoint}/models</c>, so "no kind in this build can be enumerated" stopped being
    /// true. What this test is FOR did not move an inch — the premise below now pins the two facts the
    /// fixture actually rests on (the new member really carries <c>openai-compat</c>; <c>claude</c> really
    /// is still unenumerable, so the honest-degradation path below is genuinely taken rather than
    /// vacuously satisfied), and every never-invents-a-model assertion is unchanged.</para>
    /// </summary>
    [Fact]
    public void Annotate_NeverInventsAModelIdAndStillSucceeds()
    {
        Assert.Contains(PromptRunnerKind.OpenAiCompat, PromptRunnerKinds.ModelEnumerable);
        Assert.False(
            PromptRunnerKinds.HasModelEnumeration(PromptRunnerKind.Claude),
            "the fixture declares only `claude` blocks, so this test only means anything while the Claude CLI "
            + "still exposes no model list — otherwise 'adds no block' would be vacuous rather than a decision");

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(Fixture);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(["claude"], result.UnenumerableKinds);
        Assert.Contains(
            RegistryAxes.CouldNotEnumerateMarker("claude"), result.AnnotatedText, StringComparison.Ordinal);

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        using JsonDocument before = JsonDocument.Parse(Fixture, options);
        using JsonDocument after = JsonDocument.Parse(result.AnnotatedText, options);

        JsonElement beforeRunners = before.RootElement.GetProperty("promptRunners");
        JsonElement afterRunners = after.RootElement.GetProperty("promptRunners");

        // No block was invented.
        Assert.Equal(beforeRunners.EnumerateObject().Count(), afterRunners.EnumerateObject().Count());

        // And no key beyond the four solicited ones was added to any block.
        string[] solicited = [.. RegistryAxes.All.Select(a => a.Name)];
        foreach (JsonProperty block in afterRunners.EnumerateObject()
                     .Where(p => p.Value.ValueKind == JsonValueKind.Object))
        {
            JsonElement original = beforeRunners.GetProperty(block.Name);
            IEnumerable<string> added = block.Value.EnumerateObject().Select(p => p.Name)
                .Except(original.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);

            Assert.All(added, key => Assert.Contains(key, solicited, StringComparer.Ordinal));
        }

        Assert.DoesNotContain("\"model\"", result.AnnotatedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNRECOGNISED <c>kind</c> takes the same honest path and is named VERBATIM in the note — the
    /// generator does not quietly re-label it <c>claude</c> to have something to say. (Such a config also
    /// fails <c>guardrails validate</c> with GR2044; this verb is not the gate for that, and must not
    /// pretend to be.)
    /// </summary>
    [Fact]
    public void Annotate_NamesAnUnrecognisedKindVerbatim()
    {
        const string config =
            """
            {
              "promptRunners": {
                "weird": { "command": "x", "kind": "not-a-kind" }
              }
            }
            """;

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(config);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(["not-a-kind"], result.UnenumerableKinds);
        Assert.Contains(
            RegistryAxes.CouldNotEnumerateMarker("not-a-kind"),
            result.AnnotatedText,
            StringComparison.Ordinal);
    }

    // ── criterion 5: the tri-state payoff ───────────────────────────────────────────────────

    /// <summary>
    /// The concrete reason <c>costly</c> kept its third state. The block that stated <c>false</c> is
    /// ANSWERED and drops off the list; the block the generator just wrote <c>null</c> into is STILL
    /// unstated and stays on it. If <c>null</c> collapsed into <c>false</c>, the generator's own
    /// placeholder would read as an answer and the question could never be asked again.
    /// </summary>
    [Fact]
    public void Annotate_KeepsAskingAboutCostlyUntilAHumanAnswers()
    {
        RegistryAnnotationResult first = RegistryAnnotation.Annotate(Fixture);

        Assert.Equal(["claude"], first.Unstated(RegistryAxes.Costly).Select(b => b.Name));

        // Re-running does not treat its own `null` as an answer.
        RegistryAnnotationResult second = RegistryAnnotation.Annotate(first.AnnotatedText);
        Assert.Equal(["claude"], second.Unstated(RegistryAxes.Costly).Select(b => b.Name));

        // A human answering it — with either value — is what retires the question.
        string answered = first.AnnotatedText
            .Replace("\"costly\": null", "\"costly\": false", StringComparison.Ordinal);
        Assert.Empty(RegistryAnnotation.Annotate(answered).Unstated(RegistryAxes.Costly));
    }

    // ── byte-level fidelity ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A CRLF file gets CRLF annotation lines, and a UTF-8 BOM survives — two ways a "harmless"
    /// rewrite silently changes bytes nothing asked it to touch.
    /// </summary>
    [Fact]
    public void Annotate_PreservesTheFilesNewlineConventionAndItsByteOrderMark()
    {
        string crlf = "﻿" + Fixture.Replace("\n", "\r\n", StringComparison.Ordinal);

        string annotated = RegistryAnnotation.Annotate(crlf).AnnotatedText;

        Assert.StartsWith("﻿", annotated, StringComparison.Ordinal);
        Assert.Contains("\"costly\": null", annotated, StringComparison.Ordinal);

        // Every newline in the result is a CRLF: a lone LF anywhere means an inserted line was spelled
        // in the wrong convention and the file now has mixed endings it did not have before.
        Assert.Equal(annotated.Count(c => c == '\n'), Occurrences(annotated, "\r\n"));
    }

    /// <summary>
    /// A non-ASCII value shifts byte offsets away from character offsets. The edit must still land in the
    /// right place — this is the case a naive "treat byte index as string index" implementation corrupts.
    /// </summary>
    [Fact]
    public void Annotate_PlacesEditsCorrectlyAroundNonAsciiValues()
    {
        const string config =
            """
            {
              "promptRunners": {
                "claude": {
                  "command": "claude",
                  "routing": { "tiers": ["easy"], "notes": "naïve — ünicode… 🙂 shifts byte offsets" }
                }
              }
            }
            """;

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(config);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Contains("naïve — ünicode… 🙂 shifts byte offsets", result.AnnotatedText, StringComparison.Ordinal);

        // The appended keys land AFTER the multi-byte value, so their insertion offset is exactly the one
        // a byte-index-as-char-index bug corrupts. Treating the byte offset as a string index throws
        // inside StringBuilder.Append rather than misplacing quietly.
        Assert.Contains("\"costly\": null", result.AnnotatedText, StringComparison.Ordinal);
        Assert.Contains("// costly:", result.AnnotatedText, StringComparison.Ordinal);

        // `routing` is PRESENT here, so it is left entirely alone — no comment, value verbatim.
        Assert.DoesNotContain("// routing:", result.AnnotatedText, StringComparison.Ordinal);
    }

    /// <summary>A trailing comma (legal here — the loader allows them) does not produce a double comma.</summary>
    [Fact]
    public void Annotate_HandlesATrailingCommaAndAnEmptyBlock()
    {
        const string config =
            """
            {
              "promptRunners": {
                "trailing": { "command": "claude", },
                "empty": {}
              }
            }
            """;

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(config);

        Assert.True(result.Succeeded, result.Failure);
        Assert.DoesNotContain(",,", result.AnnotatedText, StringComparison.Ordinal);
        Assert.Equal(2, result.Blocks.Count);
        Assert.All(result.Blocks, b => Assert.Equal(4, b.AddedKeys.Count));
    }

    // ── refusals: a broken edit never reaches the caller ─────────────────────────────────────

    /// <summary>Unparseable input is refused, and the returned text is the original, untouched.</summary>
    [Fact]
    public void Annotate_RefusesUnparseableJsonAndChangesNothing()
    {
        const string broken = "{ \"promptRunners\": { \"claude\": { ";

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(broken);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.False(result.HasChanges);
        Assert.Equal(broken, result.AnnotatedText);
    }

    /// <summary>
    /// A config with no <c>promptRunners</c> at all is left alone and reports no blocks. The command
    /// annotates blocks; it never invents a registry for a plan that declared none.
    /// </summary>
    [Fact]
    public void Annotate_LeavesAConfigWithNoRegistryAlone()
    {
        const string config = """{ "version": 1, "workspace": "." }""";

        RegistryAnnotationResult result = RegistryAnnotation.Annotate(config);

        Assert.True(result.Succeeded, result.Failure);
        Assert.False(result.HasChanges);
        Assert.Empty(result.Blocks);
        Assert.Equal(config, result.AnnotatedText);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int from = 0;
        while ((from = haystack.IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += needle.Length;
        }

        return count;
    }
}
