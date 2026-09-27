using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Guardrails.Core.Tests;

/// <summary>
/// <b>The #799 blind canary corpus</b>: independent evidence that <c>guardrails bundle</c> redaction
/// removes secrets from a run's artifacts before the zip is attached to a PUBLIC GitHub issue.
///
/// <para><b>Why it is blind.</b> The corpus in <c>TestData/bundle-canaries/canaries.json</c> was authored
/// from the threat list and the CC1-CC6 limit ids only, before the redactor existed and without reading the
/// design doc or any pattern list. Canaries written after the patterns tend to echo them; these test them.
/// Do not rewrite an entry's <c>expect</c> to match what the redactor happens to do. If an entry is wrong,
/// say why in its <c>note</c> and change it deliberately.</para>
///
/// <h3>The seam row 3 must implement exactly</h3>
///
/// <code>
/// namespace Guardrails.Core.Bundle;
///
/// public sealed class BundleRedactionContext
/// {
///     // artifactPath: the file's bundle-relative path, forward slashes (e.g. "logs/03-impl/attempt-1/claude-stream.jsonl").
///     // environment:  the BUNDLING shell's environment, name -> value.
///     public BundleRedactionContext(string artifactPath, IReadOnlyDictionary&lt;string, string&gt; environment);
/// }
///
/// public sealed class BundleRedactionResult          // record is fine
/// {
///     public string Text { get; }                    // the redacted artifact
///     public IReadOnlyList&lt;string&gt; Labels { get; }  // one label per redaction applied (non-empty when anything was scrubbed)
/// }
///
/// public static class BundleRedactor
/// {
///     public static BundleRedactionResult Redact(string content, BundleRedactionContext context);
///     public static string CannotCatchText { get; }  // the REDACTIONS.md "cannot catch" section; names every CCn it admits
/// }
/// </code>
///
/// <para>A <c>const string CannotCatchText</c> field is accepted too.</para>
///
/// <h3>How the not-yet-implemented tests stay green, and why they cannot stay skipped by accident</h3>
///
/// <para>The seam is reached by reflection, so this file compiles before row 3 exists. While
/// <c>Guardrails.Core.Bundle.BundleRedactor</c> is absent, the three per-entry theories and
/// <see cref="TheRedactorSeamMatchesTheDocumentedContract"/> call <c>Assert.Skip</c> with
/// <see cref="RedactorSeam.AwaitingReason"/>. Once the type exists they RUN, and a seam that does not match
/// the contract above FAILS rather than skips. <see cref="NoBundleTypeShipsWithoutTheRedactorSeam"/> never
/// skips: if anything lands in <c>Guardrails.Core.Bundle</c> without <c>BundleRedactor</c> (a rename, say),
/// it fails, so the corpus cannot go silently dark. When row 3 lands, prefer replacing the reflection with
/// direct calls.</para>
///
/// <h3>What each expectation asserts</h3>
///
/// <list type="bullet">
///   <item><c>caught</c>: no window of the secret (its <c>secretCore</c> when set, else its <c>value</c>)
///   survives in the redacted output in any form: raw, stj-escaped, node-escaped, or percent-encoded (upper
///   and lower hex). The window is <c>min(12, max(8, len/2))</c> characters, so a redactor that keeps a short
///   prefix hint (<c>ghp_…</c>) passes and one that keeps most of the secret fails. Checking windows rather
///   than the whole value is deliberate: partial redaction also removes the whole value, and would pass a
///   whole-value check. The result must also carry at least one label.</item>
///   <item><c>CC1</c>..<c>CC6</c>: the entry may survive, but <c>CannotCatchText</c> must name that id.</item>
///   <item><c>survives</c> (a third state, beyond the brief's two): a diagnostic value (the gateway placeholder
///   <c>guardrails-gateway-no-auth</c>, SHAs, GUIDs, branch names, test names, the shell's <c>PWD</c>) that must
///   still be present, in its rendered form, after redaction. A redactor that scrubs everything would pass the
///   <c>caught</c> rows; these rows are what make that fail.</item>
/// </list>
///
/// <para>The corpus self-checks at the bottom run now, with or without a redactor: they are what keep the
/// per-entry rows from passing vacuously (every canary is present before redaction; no context template
/// already contains a canary window; every JSON artifact parses).</para>
/// </summary>
public sealed class BundleCanaryCorpusTests
{
    // ------------------------------------------------------------------ per-entry rows (need row 3)

    public static TheoryData<string> CaughtIds => IdsWhere(e => e.Expect == Caught);
    public static TheoryData<string> LimitIds => IdsWhere(e => LimitIdSet.Contains(e.Expect));
    public static TheoryData<string> SurvivesIds => IdsWhere(e => e.Expect == Survives);

    [Theory]
    [MemberData(nameof(CaughtIds))]
    public void CaughtCanaryLeavesNoWindowInTheRedactedOutput(string id)
    {
        var entry = Corpus.Entry(id);
        var result = RedactOrSkip(entry);

        foreach (var (form, window) in LeakWindows(entry))
        {
            var at = result.Text.IndexOf(window, StringComparison.Ordinal);
            Assert.True(at < 0,
                $"{id} ({entry.Threat}, {entry.ArtifactKind}, {entry.Serializer}): the {form} form of the secret survived " +
                $"redaction. Surviving window '{window}' at offset {at}; around it: '{Around(result.Text, at, window.Length)}'.");
        }

        Assert.True(result.Labels.Count > 0, $"{id}: the canary was scrubbed but the result carries no label.");
    }

    [Theory]
    [MemberData(nameof(LimitIds))]
    public void LimitCanaryIsNamedInTheCannotCatchText(string id)
    {
        var entry = Corpus.Entry(id);
        _ = RedactOrSkip(entry); // must not throw on the input, whatever it leaves
        var cannotCatch = RedactorSeam.CannotCatchText();
        Assert.True(cannotCatch.Contains(entry.Expect, StringComparison.Ordinal),
            $"{id} ({entry.Threat}) is a declared {entry.Expect} limit, but REDACTIONS.md's cannot-catch text does not name " +
            $"{entry.Expect}. A limit a reader is not told about is a silent leak. Text was: {cannotCatch}");
    }

    [Theory]
    [MemberData(nameof(SurvivesIds))]
    public void DiagnosticValueSurvivesRedaction(string id)
    {
        var entry = Corpus.Entry(id);
        var result = RedactOrSkip(entry);
        var expected = Slot(entry, entry.Value);
        Assert.True(result.Text.Contains(expected, StringComparison.Ordinal),
            $"{id} ({entry.ArtifactKind}): the diagnostic value '{expected}' was scrubbed. It is not a secret, and losing it " +
            $"makes the bundle useless for triage. Redacted output: {result.Text}");
    }

    [Fact]
    public void TheRedactorSeamMatchesTheDocumentedContract()
    {
        Assert.SkipWhen(RedactorSeam.RedactorType is null, RedactorSeam.AwaitingReason);
        var result = RedactorSeam.Redact("nothing secret here\n", "validate.txt", new Dictionary<string, string>());
        Assert.Equal("nothing secret here\n", result.Text);
        Assert.Empty(result.Labels);
        Assert.False(string.IsNullOrWhiteSpace(RedactorSeam.CannotCatchText()));
    }

    [Fact]
    public void NoBundleTypeShipsWithoutTheRedactorSeam()
    {
        var bundleTypes = RedactorSeam.CoreAssembly.GetTypes()
            .Where(t => t.Namespace == RedactorSeam.BundleNamespace)
            .Select(t => t.FullName)
            .ToList();
        if (bundleTypes.Count == 0)
            return; // row 3 has not landed; the per-entry rows skip with AwaitingReason
        Assert.True(RedactorSeam.RedactorType is not null,
            $"{RedactorSeam.BundleNamespace} exists ({string.Join(", ", bundleTypes)}) but {RedactorSeam.RedactorTypeName} does not. " +
            "The canary corpus reaches the redactor through that exact name; without it every canary row skips and the corpus goes dark. " +
            "Implement the seam documented at the top of BundleCanaryCorpusTests.cs, or update the seam here deliberately.");
    }

    // ------------------------------------------------------------------ corpus self-checks (run now)

    [Fact]
    public void CorpusIdsAreUniqueAndEveryFieldIsPopulated()
    {
        var entries = Corpus.Entries;
        Assert.True(entries.Count >= 80, $"corpus shrank to {entries.Count} entries");
        var dupes = entries.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "duplicate ids: " + string.Join(", ", dupes));
        foreach (var e in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrEmpty(e.Value), $"{e.Id}: empty value");
            Assert.True(e.Context.Contains("{{value}}", StringComparison.Ordinal) || e.Chunks is { Count: > 0 },
                $"{e.Id}: context has no {{{{value}}}} placeholder and no chunks");
            Assert.True(e.ValueForm is null or "percent", $"{e.Id}: unknown valueForm '{e.ValueForm}'");
            if (e.SecretCore is not null)
                Assert.True(e.Value.Contains(e.SecretCore, StringComparison.Ordinal), $"{e.Id}: secretCore is not inside value");
        }
    }

    [Fact]
    public void EveryThreatCategoryIsCoveredWithSeveralShapes()
    {
        var byThreat = Corpus.Entries.GroupBy(e => e.Threat).ToDictionary(g => g.Key, g => g.Count());
        var unknown = byThreat.Keys.Except(ThreatCategories).ToList();
        Assert.True(unknown.Count == 0, "unknown threat categories: " + string.Join(", ", unknown));
        foreach (var threat in ThreatCategories)
            Assert.True(byThreat.GetValueOrDefault(threat) >= 2, $"threat '{threat}' has {byThreat.GetValueOrDefault(threat)} entries; want several shapes");
    }

    [Fact]
    public void EveryArtifactKindIsCoveredAndMappedToAPath()
    {
        Assert.Equal(ArtifactKinds.OrderBy(k => k, StringComparer.Ordinal), Corpus.ArtifactPaths.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var used = Corpus.Entries.Select(e => e.ArtifactKind).ToHashSet(StringComparer.Ordinal);
        var unknown = used.Except(ArtifactKinds).ToList();
        Assert.True(unknown.Count == 0, "unknown artifactKind: " + string.Join(", ", unknown));
        var missing = ArtifactKinds.Except(used).ToList();
        Assert.True(missing.Count == 0, "artifactKind with no entry: " + string.Join(", ", missing));
    }

    [Fact]
    public void SerializersMatchTheArtifactAndBothJsonFlavorsExerciseTheirEscaping()
    {
        foreach (var e in Corpus.Entries)
        {
            Assert.Contains(e.Serializer, new[] { Stj, Node, Raw });
            var isJson = e.ArtifactKind.Contains("json", StringComparison.Ordinal);
            Assert.True(isJson == (e.Serializer != Raw), $"{e.Id}: serializer '{e.Serializer}' does not fit artifact kind '{e.ArtifactKind}'");
        }

        // stj writes + as +: at least two caught canaries must actually hit that escape.
        var stjPlus = Corpus.Entries.Count(e => e.Expect == Caught && e.Serializer == Stj && Slot(e, e.Value).Contains("\\u002B", StringComparison.Ordinal));
        Assert.True(stjPlus >= 2, $"only {stjPlus} caught stj entries render a \\u002B escape");
        // node leaves + alone but escapes " and \.
        var nodePlus = Corpus.Entries.Count(e => e.Expect == Caught && e.Serializer == Node && e.Value.Contains('+', StringComparison.Ordinal));
        Assert.True(nodePlus >= 2, $"only {nodePlus} caught node entries carry a '+'");
        var nodeEscaped = Corpus.Entries.Count(e => e.Expect == Caught && e.Serializer == Node && Slot(e, e.Value) != e.Value);
        Assert.True(nodeEscaped >= 1, "no caught node entry exercises node's own escaping of \" or \\");
    }

    [Fact]
    public void ReservedCharactersAppearInASubsetOfSecrets()
    {
        var secrets = Corpus.Entries.Where(e => e.Expect != Survives).ToList();
        foreach (var (ch, min) in new[] { ('+', 3), ('/', 3), ('=', 3), ('&', 2) })
        {
            var count = secrets.Count(e => e.Value.Contains(ch, StringComparison.Ordinal));
            Assert.True(count >= min, $"only {count} secret values contain '{ch}' (want >= {min})");
        }
        Assert.Contains(secrets, e => e.ValueForm == "percent" && e.Serializer == Raw);
        Assert.Contains(secrets, e => e.ValueForm == "percent" && e.Serializer == Stj);
    }

    [Fact]
    public void EveryExpectationAndLimitIdIsUsed()
    {
        var used = Corpus.Entries.Select(e => e.Expect).ToHashSet(StringComparer.Ordinal);
        var unknown = used.Except(LimitIdSet).Except(new[] { Caught, Survives }).ToList();
        Assert.True(unknown.Count == 0, "unknown expect values: " + string.Join(", ", unknown));
        foreach (var id in LimitIdSet.Append(Caught).Append(Survives))
            Assert.Contains(id, used);
    }

    [Fact]
    public void RenderedJsonArtifactsAreWellFormed()
    {
        foreach (var e in Corpus.Entries.Where(e => e.Serializer != Raw))
        {
            var rendered = Render(e);
            var docs = e.ArtifactKind.Contains("jsonl", StringComparison.Ordinal)
                ? rendered.Split('\n').Where(l => l.Length > 0)
                : new[] { rendered };
            foreach (var doc in docs)
            {
                try { using var _ = JsonDocument.Parse(doc); }
                catch (JsonException ex) { Assert.Fail($"{e.Id}: rendered {e.ArtifactKind} is not valid JSON ({ex.Message}): {doc}"); }
            }
        }
    }

    [Fact]
    public void EveryCanaryIsPresentBeforeRedaction()
    {
        foreach (var e in Corpus.Entries)
        {
            var rendered = Render(e);
            Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
            if (e.Expect == Survives)
            {
                Assert.True(rendered.Contains(Slot(e, e.Value), StringComparison.Ordinal), $"{e.Id}: survives value not rendered");
                continue;
            }

            Assert.True(LeakWindows(e).Any(w => rendered.Contains(w.Window, StringComparison.Ordinal)),
                $"{e.Id}: no window of the secret is in the rendered artifact, so its row would pass vacuously");
            if (e.Chunks is { Count: >= 2 } chunks)
            {
                Assert.Equal(e.Value, string.Concat(chunks));
                Assert.False(rendered.Contains(Slot(e, e.Value), StringComparison.Ordinal), $"{e.Id}: split value is contiguous in the artifact");
            }
            else
            {
                Assert.True(rendered.Contains(Slot(e, e.SecretCore ?? e.Value), StringComparison.Ordinal), $"{e.Id}: secret not rendered whole");
            }
        }
    }

    [Fact]
    public void ContextTemplatesDoNotAlreadyContainTheCanary()
    {
        // Otherwise a surviving window could be innocent context and a caught row would fail for the wrong reason.
        foreach (var e in Corpus.Entries.Where(e => e.Expect != Survives))
        {
            var bare = System.Text.RegularExpressions.Regex.Replace(e.Context, @"\{\{(value|chunk\d+)\}\}", "\u0001");
            var hit = LeakWindows(e).FirstOrDefault(w => bare.Contains(w.Window, StringComparison.Ordinal));
            Assert.True(hit.Window is null, $"{e.Id}: context template already contains secret window '{hit.Window}'");
        }
    }

    [Fact]
    public void ValuesAreSyntheticAndGreppable()
    {
        var secrets = Corpus.Entries.Where(e => e.Expect != Survives).ToList();
        foreach (var e in secrets)
        {
            var lower = e.Value.ToLowerInvariant();
            Assert.True(SyntheticMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)),
                $"{e.Id}: value carries none of the synthetic markers ({string.Join(", ", SyntheticMarkers)}); a marker-less value could be mistaken for a real credential");
        }
        var dupes = secrets.GroupBy(e => e.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "secret values must be unique (so a leak names its entry): " + string.Join(", ", dupes));

        var raw = File.ReadAllText(CorpusPath);
        foreach (var realMailHost in new[] { "@gmail.", "@hotmail.", "@outlook.", "@yahoo." })
            Assert.DoesNotContain(realMailHost, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShellEnvironmentMatchesEachExpectation()
    {
        foreach (var e in Corpus.Entries)
        {
            var known = e.ShellEnv.Values.Contains(e.Value, StringComparer.Ordinal);
            switch (e.Expect)
            {
                case "CC3" or "CC4" or "CC6":
                    Assert.False(known, $"{e.Id}: a {e.Expect} value must not be verbatim in the bundling shell");
                    break;
                case Caught when e.Threat is "bare-named-variable-value" or "serializer-escaping":
                    Assert.True(known, $"{e.Id}: a caught {e.Threat} entry is caught BY the shell value, so the shell must hold it");
                    break;
            }
            if (e.Threat == "rotated-key")
                Assert.True(e.ShellEnv.Keys.Except(BaseShellNames).Any(), $"{e.Id}: a rotated-key entry needs the stale value in the shell");
        }
    }

    [Fact]
    public void TheRendererEscapesLikeEachSerializer()
    {
        Assert.Equal("a\\u002Bb\\u0026c\\u003Cd\\u0027", Escape(Stj, "a+b&c<d'"));
        Assert.Equal(JsonSerializer.Serialize("x+/=&<>'\"\\\n")[1..^1], Escape(Stj, "x+/=&<>'\"\\\n"));
        Assert.Equal("a+b&c<d'\\\"e\\\\f\\ng\\u0001", Escape(Node, "a+b&c<d'\"e\\f\ng\u0001"));
        Assert.Equal("a+b", Escape(Raw, "a+b"));
    }

    // ------------------------------------------------------------------ vocabulary

    private const string Caught = "caught";
    private const string Survives = "survives";
    private const string Stj = "stj";
    private const string Node = "node";
    private const string Raw = "raw";

    private static readonly string[] LimitIdSet = ["CC1", "CC2", "CC3", "CC4", "CC5", "CC6"];

    private static readonly string[] ThreatCategories =
    [
        "bare-named-variable-value", "secret-named-pair", "provider-token-shape", "http-credential-header",
        "url-embedded-credential", "netrc-and-space-separated", "high-entropy-random", "transformed-known-value",
        "rotated-key", "serializer-escaping", "split-stream-chunks", "diagnostic-must-survive", "proprietary-content",
    ];

    private static readonly string[] ArtifactKinds =
    [
        "transcript.md", "stream-log jsonl", "gateway-session jsonl", "feedback.md", "guardrail-stdout", "guardrail-stderr",
        "composed-prompt.md", "action-result.json", "attempt-provenance.json", "run.json reason field", "events.jsonl",
        "observer.jsonl", "guardrails.json env map", "triage.json", "overwatch.jsonl", "escalation json",
        "git log/status text", "validate.txt",
    ];

    // canary; its reverse; the hex marker for hex-only values; base64("canary").
    private static readonly string[] SyntheticMarkers = ["canary", "yranac", "c0ffee", "y2fuyxj5"];

    private static readonly string[] BaseShellNames = ["PATH", "HOME", "PWD"];

    // ------------------------------------------------------------------ rendering

    private static string Render(CanaryEntry e)
    {
        var text = e.Context;
        if (e.Chunks is { } chunks)
        {
            for (var i = 0; i < chunks.Count; i++)
                text = text.Replace("{{chunk" + i + "}}", Slot(e, chunks[i]), StringComparison.Ordinal);
            return text;
        }
        return text.Replace("{{value}}", Slot(e, e.Value), StringComparison.Ordinal);
    }

    /// <summary>What a value looks like in the artifact: percent-encoded if the entry says so, then serializer-escaped.</summary>
    private static string Slot(CanaryEntry e, string s) =>
        Escape(e.Serializer, e.ValueForm == "percent" ? Uri.EscapeDataString(s) : s);

    private static readonly JsonSerializerOptions StjDefault = new() { Encoder = JavaScriptEncoder.Default };

    private static string Escape(string serializer, string s) => serializer switch
    {
        Raw => s,
        Stj => JsonSerializer.Serialize(s, StjDefault)[1..^1],
        Node => NodeEscape(s),
        _ => throw new ArgumentOutOfRangeException(nameof(serializer), serializer, "unknown serializer"),
    };

    /// <summary>JSON.stringify's minimal escaping: quote, backslash and control characters only.</summary>
    private static string NodeEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Every window of every form of the secret that must be gone from a caught entry's output.</summary>
    private static IEnumerable<(string Form, string Window)> LeakWindows(CanaryEntry e)
    {
        var core = e.SecretCore ?? e.Value;
        var percent = Uri.EscapeDataString(core);
        var forms = new (string Name, string Text)[]
        {
            ("raw", core),
            ("stj-escaped", Escape(Stj, core)),
            ("node-escaped", Escape(Node, core)),
            ("percent-encoded", percent),
            ("percent-encoded (lower hex)", System.Text.RegularExpressions.Regex.Replace(percent, "%[0-9A-F]{2}", m => m.Value.ToLowerInvariant())),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, text) in forms)
        {
            var size = Math.Min(12, Math.Max(8, text.Length / 2));
            if (text.Length <= size)
            {
                if (seen.Add(text)) yield return (name, text);
                continue;
            }
            for (var i = 0; i + size <= text.Length; i++)
            {
                var w = text.Substring(i, size);
                if (seen.Add(w)) yield return (name, w);
            }
        }
    }

    private static string Around(string text, int at, int length)
    {
        var start = Math.Max(0, at - 20);
        var end = Math.Min(text.Length, at + length + 20);
        return text[start..end];
    }

    private static RedactionOutcome RedactOrSkip(CanaryEntry e)
    {
        Assert.SkipWhen(RedactorSeam.RedactorType is null, RedactorSeam.AwaitingReason);
        return RedactorSeam.Redact(Render(e), Corpus.ArtifactPaths[e.ArtifactKind], e.ShellEnv);
    }

    private static TheoryData<string> IdsWhere(Func<CanaryEntry, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (var e in Corpus.Entries.Where(predicate))
            data.Add(e.Id);
        return data;
    }

    // ------------------------------------------------------------------ corpus model

    private static string CorpusPath => TestPaths.Fixture(Path.Combine("bundle-canaries", "canaries.json"));

    public sealed record CanaryEntry
    {
        public required string Id { get; init; }
        public required string Threat { get; init; }
        public required string Value { get; init; }
        public required string Context { get; init; }
        public required string ArtifactKind { get; init; }
        public required string Serializer { get; init; }
        public Dictionary<string, string> ShellEnv { get; init; } = new(StringComparer.Ordinal);
        public required string Expect { get; init; }
        public string? ValueForm { get; init; }
        public List<string>? Chunks { get; init; }
        public string? SecretCore { get; init; }
        public string? Note { get; init; }
    }

    private sealed record CorpusFile
    {
        public int SchemaVersion { get; init; }
        public Dictionary<string, string> ArtifactPaths { get; init; } = new(StringComparer.Ordinal);
        public List<CanaryEntry> Entries { get; init; } = [];
    }

    private static class Corpus
    {
        private static readonly Lazy<CorpusFile> Loaded = new(() =>
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var file = JsonSerializer.Deserialize<CorpusFile>(System.IO.File.ReadAllText(CorpusPath), options)
                ?? throw new InvalidOperationException("canaries.json deserialized to null");
            if (file.SchemaVersion != 1)
                throw new InvalidOperationException($"canaries.json schemaVersion {file.SchemaVersion}; this reader knows 1");
            return file;
        });

        public static IReadOnlyList<CanaryEntry> Entries => Loaded.Value.Entries;
        public static IReadOnlyDictionary<string, string> ArtifactPaths => Loaded.Value.ArtifactPaths;
        public static CanaryEntry Entry(string id) => Entries.Single(e => e.Id == id);
    }

    // ------------------------------------------------------------------ the reflection seam

    public readonly record struct RedactionOutcome(string Text, IReadOnlyList<string> Labels);

    /// <summary>Reaches the row-3 redactor by name so this file compiles before it exists. See the class remarks.</summary>
    internal static class RedactorSeam
    {
        public const string BundleNamespace = "Guardrails.Core.Bundle";
        public const string RedactorTypeName = BundleNamespace + ".BundleRedactor";
        public const string ContextTypeName = BundleNamespace + ".BundleRedactionContext";
        public const string AwaitingReason =
            "awaiting #799 row 3: " + RedactorTypeName + " is not in Guardrails.Core yet (the corpus self-checks still run)";

        public static Assembly CoreAssembly { get; } = typeof(Guardrails.Core.Io.RealPath).Assembly;
        public static Type? RedactorType => CoreAssembly.GetType(RedactorTypeName);

        public static RedactionOutcome Redact(string content, string artifactPath, IReadOnlyDictionary<string, string> environment)
        {
            var redactor = Require(RedactorType, RedactorTypeName);
            var contextType = Require(CoreAssembly.GetType(ContextTypeName), ContextTypeName);
            var ctor = Require(contextType.GetConstructor([typeof(string), typeof(IReadOnlyDictionary<string, string>)]),
                ContextTypeName + "(string artifactPath, IReadOnlyDictionary<string, string> environment)");
            var redact = Require(redactor.GetMethod("Redact", BindingFlags.Public | BindingFlags.Static, [typeof(string), contextType]),
                "public static " + RedactorTypeName + ".Redact(string, BundleRedactionContext)");

            var env = new Dictionary<string, string>(environment, StringComparer.Ordinal);
            var result = redact.Invoke(null, [content, ctor.Invoke([artifactPath, env])]);
            Assert.NotNull(result);
            var resultType = result.GetType();
            var text = Require(resultType.GetProperty("Text"), "BundleRedactionResult.Text").GetValue(result);
            var labels = Require(resultType.GetProperty("Labels"), "BundleRedactionResult.Labels").GetValue(result);
            var typedText = Assert.IsType<string>(text);
            var typedLabels = Assert.IsAssignableFrom<IReadOnlyList<string>>(labels);
            return new RedactionOutcome(typedText, typedLabels);
        }

        public static string CannotCatchText()
        {
            var redactor = Require(RedactorType, RedactorTypeName);
            var value = redactor.GetProperty("CannotCatchText", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? redactor.GetField("CannotCatchText", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return Assert.IsType<string>(Require(value, "public static string " + RedactorTypeName + ".CannotCatchText"));
        }

        private static T Require<T>(T? member, string what) where T : class =>
            member ?? throw new InvalidOperationException(
                $"#799 redactor seam: {what} is missing. The canary corpus codes against the contract documented at the top of " +
                "BundleCanaryCorpusTests.cs; implement it exactly or change the seam deliberately.");
    }
}
