using System.Text;
using System.Text.RegularExpressions;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Bundle;

/// <summary>
/// What one artifact is redacted against (SSOT §17.6). The two-argument constructor is the seam the #799 blind canary
/// corpus codes against: the artifact's bundle-relative path and the bundling shell's environment. The bundle builder
/// adds the rest: the bundle-wide known values (so labels agree across files), the non-secret exemptions enumerated
/// from the plan and journal, and the path anonymizer (pass 3).
/// </summary>
public sealed class BundleRedactionContext
{
    /// <summary>
    /// A context that knows only the bundling shell: known values come from <paramref name="environment"/>
    /// (§17.6.1), the only exemption is the gateway placeholder, and paths are left alone.
    /// </summary>
    /// <param name="artifactPath">The file's bundle-relative path, forward slashes.</param>
    /// <param name="environment">The bundling shell's environment, name to value.</param>
    public BundleRedactionContext(string artifactPath, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(artifactPath);
        ArgumentNullException.ThrowIfNull(environment);
        ArtifactPath = artifactPath;
        Environment = environment;
        Secrets = BundleSecrets.FromEnvironment(environment);
    }

    /// <summary>The file's bundle-relative path.</summary>
    public string ArtifactPath { get; }

    /// <summary>The bundling shell's environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>The known values of pass 1. Defaults to the bundling shell's alone.</summary>
    public BundleSecrets Secrets { get; init; }

    /// <summary>
    /// Whole tokens the pattern and entropy passes never scrub (§17.6.2): task ids, wave names, the plan name, recorded
    /// branches, enumerated path segments. The gateway placeholder is always exempt, whatever this holds.
    /// </summary>
    public IReadOnlySet<string> ExemptTokens { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Pass 3. Null leaves paths as they are (<c>--keep-paths</c>, or a context built for one artifact).</summary>
    public BundlePathAnonymizer? Paths { get; init; }

    /// <summary>
    /// False for entry names and MANIFEST path cells: those get the known-value and pattern passes, not the entropy rule,
    /// which would scrub ordinary file names under a run-id path (§17.5).
    /// </summary>
    internal bool Entropy { get; init; } = true;
}

/// <summary>A redacted artifact and one label per redaction applied (SSOT §17.6).</summary>
/// <param name="Text">The redacted text.</param>
/// <param name="Labels">One entry per scrubbed span: a known-value label (<c>ANTHROPIC_AUTH_TOKEN#1</c>) or a pattern kind (<c>high-entropy</c>).</param>
public sealed record BundleRedactionResult(string Text, IReadOnlyList<string> Labels);

/// <summary>
/// The credential passes of SSOT §17.6: pass 1 (known values), pass 2 (patterns and entropy, minus the non-secret
/// exemptions), pass 4's delta join, and pass 3 (paths) when the context carries an anonymizer.
/// <para>
/// <b>How every spelling is covered.</b> Each pass scans decoded VIEWS of the text, not the text alone: the raw text,
/// its JSON-unescaped form (both the System.Text.Json and the Node spellings decode to the same characters), that
/// form unescaped once more (JSON quoted inside a JSON string, as stream logs carry tool output), each of those
/// percent-decoded, and for a stream log the joined delta text. A hit in any view is mapped back to the original
/// characters it came from, and those are replaced, so the scrub is escape-aligned and a JSON artifact stays
/// well-formed.
/// </para>
/// <para>
/// Over-redaction is the accepted cost; a false negative (a key on a public issue) is the failure that matters.
/// </para>
/// </summary>
public static partial class BundleRedactor
{
    /// <summary>
    /// The "Cannot catch" list of SSOT §17.6.7, verbatim, which REDACTIONS.md carries. The canary corpus asserts every
    /// limit it tags (<c>CC1</c>..<c>CC6</c>) is named here: a limit a reader is not told about is a silent leak.
    /// </summary>
    public const string CannotCatchText =
        "## Cannot catch\n" +
        "\n" +
        "- **`CC1`**: a secret that no shape rule matches and that the entropy rule misses: **hex-only**, **under 24\n" +
        "  characters**, **lacking one of upper case, lower case or digit**, or at or below the length-scaled entropy\n" +
        "  threshold (3.6 bits per character for 24-31 characters, 4.0 from 32). For a uniformly random base62 token the\n" +
        "  miss rate is about 1.5% at 24 characters, 0.7% at 28, 0.4% at 32 and under 0.1% from 40, almost all of it a\n" +
        "  token that happens to hold no digit.\n" +
        "- **`CC2`**: **space-separated** credentials (`password hunter2`, `login alice secret`) outside the netrc,\n" +
        "  header, `NAME=value` and JSON-pair shapes.\n" +
        "- **`CC3`**: a known value **transformed** before it was written: base64, reversed, or partly echoed.\n" +
        "- **`CC4`**: a secret that reached the run from a variable **no block names** and **the bundling shell does\n" +
        "  not have**.\n" +
        "- **`CC5`**: proprietary content, which a default (full) bundle includes. Redaction removes credentials, not\n" +
        "  intellectual property. `--lean` withholds it.\n" +
        "- **`CC6`**: the bundling shell holds a **different value** of a variable than the run used (a rotated key,\n" +
        "  another profile). D1 sees the variable as set, the known-value pass scrubs the wrong value, and the\n" +
        "  difference cannot be detected without a fingerprint of the run's value, which this contract refuses to\n" +
        "  emit.\n";

    /// <summary>The high-entropy rule's minimum run length (§17.6.2).</summary>
    public const int EntropyMinimumLength = 24;

    /// <summary>The high-entropy threshold for runs of 32 characters or more, bits per character, exclusive (§17.6.2).</summary>
    public const double EntropyThreshold = 4.0;

    /// <summary>The threshold for a 24-31 character run: a 24-character string cannot exceed log2(24) ≈ 4.58 bits.</summary>
    public const double ShortRunEntropyThreshold = 3.6;

    /// <summary>The length-scaled threshold of §17.6.2 (W2 of the #805 review).</summary>
    public static double EntropyThresholdFor(int length) => length < 32 ? ShortRunEntropyThreshold : EntropyThreshold;

    /// <summary>Redact <paramref name="content"/> against <paramref name="context"/>. Pure; never touches the disk.</summary>
    public static BundleRedactionResult Redact(string content, BundleRedactionContext context)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<RedactionView> views = Views(content);
        var hits = new List<Hit>();
        foreach (RedactionView view in views)
        {
            FindKnownValues(view, context.Secrets, hits);
            FindPatterns(view, context, hits);
        }

        (string text, List<string> labels) = Apply(content, hits);
        if (context.Paths is { } paths)
        {
            text = paths.Apply(text);
        }

        return new BundleRedactionResult(text, labels);
    }

    /// <summary>The replacement a label becomes in the text.</summary>
    public static string Render(string label) => $"[REDACTED:{label}]";

    /// <summary>The label kinds of the scrubbed spans in an already-redacted text, for pass 4's comparison.</summary>
    public static IReadOnlySet<string> LabelsIn(string redacted) =>
        LabelToken().Matches(redacted).Select(m => m.Groups["label"].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>The decoded views every pass scans, pass 3 included (§17.6.2).</summary>
    internal static IReadOnlyList<RedactionView> Views(string content)
    {
        var views = new List<RedactionView>();
        void AddWithPercent(RedactionView? view)
        {
            if (view is null)
            {
                return;
            }

            views.Add(view);
            if (view.PercentDecoded() is { } decoded)
            {
                views.Add(decoded);
            }
        }

        RedactionView raw = RedactionView.Identity(content);
        AddWithPercent(raw);
        RedactionView? unescaped = raw.JsonUnescaped();
        AddWithPercent(unescaped);
        AddWithPercent(unescaped?.JsonUnescaped());
        AddWithPercent(StreamDeltaJoin.Build(content));
        return views;
    }

    // ------------------------------------------------------------------ pass 1

    private static void FindKnownValues(RedactionView view, BundleSecrets secrets, List<Hit> hits)
    {
        foreach (KnownSecret secret in secrets.Values)
        {
            int at = view.Text.IndexOf(secret.Value, StringComparison.Ordinal);
            while (at >= 0)
            {
                hits.Add(new Hit(view.OriginalRanges(at, at + secret.Value.Length), secret.Label, Priority.KnownValue));
                at = view.Text.IndexOf(secret.Value, at + secret.Value.Length, StringComparison.Ordinal);
            }
        }
    }

    // ------------------------------------------------------------------ pass 2

    private static void FindPatterns(RedactionView view, BundleRedactionContext context, List<Hit> hits)
    {
        string text = view.Text;

        void Add(Group group, string kind, Priority priority)
        {
            if (group.Success && group.Length > 0)
            {
                hits.Add(new Hit(view.OriginalRanges(group.Index, group.Index + group.Length), kind, priority));
            }
        }

        foreach ((Regex regex, string kind) in Shapes)
        {
            foreach (Match match in regex.Matches(text))
            {
                Add(match.Groups["v"].Success ? match.Groups["v"] : match.Groups[0], kind, Priority.Shape);
            }
        }

        foreach (Match match in HeaderPattern().Matches(text))
        {
            Group value = match.Groups["v"];
            string trimmed = value.Value.TrimEnd();
            if (trimmed.Length == 0 || IsExempt(trimmed, context))
            {
                continue;
            }

            hits.Add(new Hit(view.OriginalRanges(value.Index, value.Index + trimmed.Length), "auth-header", Priority.Header));
        }

        foreach (Match match in BearerPattern().Matches(text))
        {
            if (!IsExempt(match.Groups["v"].Value, context))
            {
                Add(match.Groups["v"], "bearer", Priority.Header);
            }
        }

        foreach (Match match in UrlCredentialPattern().Matches(text))
        {
            Add(match.Groups["v"], "url-credential", Priority.Header);
        }

        foreach (Match match in NetrcPattern().Matches(text))
        {
            Add(match.Groups["v"], "netrc", Priority.Header);
        }

        foreach (Regex pair in new[] { AssignmentPairPattern(), JsonPairPattern() })
        {
            foreach (Match match in pair.Matches(text))
            {
                string name = match.Groups["name"].Value;
                Group value = match.Groups["qv"].Success ? match.Groups["qv"] : match.Groups["v"];
                if (!value.Success || value.Length == 0 || !IsSecretPairName(name, value.Value) || IsHeaderName(name)
                    || NameIsAVariableName(name) || IsInertValue(value.Value) || IsExemptPairValue(value.Value, context))
                {
                    continue;
                }

                Add(value, "named-secret", Priority.Pair);
            }
        }

        foreach (Match match in context.Entropy ? EntropyRun().Matches(text) : Enumerable.Empty<Match>())
        {
            string run = match.Value;
            if (IsHighEntropy(run) && !IsExempt(run, context) && !IsIdentifier(run) && !IsExemptPath(run, context))
            {
                Add(match.Groups[0], "high-entropy", Priority.Entropy);
            }
        }
    }

    /// <summary>
    /// The high-entropy rule of §17.6.2: upper case, lower case AND a digit, with Shannon entropy above 4.0 bits per
    /// character. The run's length (at least 24, of the broad class) is the caller's regex.
    /// </summary>
    public static bool IsHighEntropy(string run)
    {
        if (run.Length < EntropyMinimumLength || !run.Any(char.IsAsciiLetterUpper) || !run.Any(char.IsAsciiLetterLower)
            || !run.Any(char.IsAsciiDigit))
        {
            return false;
        }

        return ShannonEntropy(run) > EntropyThresholdFor(run.Length);
    }

    /// <summary>Shannon entropy of <paramref name="text"/>'s characters, in bits per character.</summary>
    public static double ShannonEntropy(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var counts = new Dictionary<char, int>();
        foreach (char c in text)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        double entropy = 0;
        foreach (int count in counts.Values)
        {
            double p = (double)count / text.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>
    /// The identifier exemption (§17.6.2, entropy pass ONLY): a run made entirely of dot-separated identifier segments,
    /// each built of capitalized words (a capital and two or more lower-case letters) and digit runs, with at least
    /// three such parts in all, e.g. <c>Guardrails.Core.Tests.ResumeTests.Plan39ResumeRetriesWave2TaskAfterHalt</c>.
    /// A random token essentially never has that shape. Never applied to known values or shape patterns.
    /// </summary>
    public static bool IsIdentifier(string run)
    {
        string trimmed = run.TrimEnd('.');
        if (!IdentifierRun().IsMatch(trimmed))
        {
            return false;
        }

        return IdentifierPart().Matches(trimmed).Count >= 3;
    }

    private static bool IsExempt(string token, BundleRedactionContext context) =>
        string.Equals(token, ClaudeGatewayEnvironment.PlaceholderToken, StringComparison.Ordinal)
        || context.ExemptTokens.Contains(token)
        || context.ExemptTokens.Contains(token.TrimEnd('.'));

    // A `/`-joined run is exempt only when EVERY segment is itself an exact exempt token (an enumerated path, e.g.
    // logs/<runId>/<task>/attempt-2/claude-stream.jsonl). A segment that is not enumerated keeps the whole run in scope,
    // so a base64 secret that happens to contain `/` is never split into innocent-looking pieces.
    private static bool IsExemptPath(string run, BundleRedactionContext context)
    {
        if (!run.Contains('/', StringComparison.Ordinal) || context.ExemptTokens.Count == 0)
        {
            return false;
        }

        return run.TrimEnd('.').Split('/').All(segment => segment.Length == 0 || context.ExemptTokens.Contains(segment));
    }

    // A pair's value may carry an auth scheme before the exempt token (`"authorization": "Bearer guardrails-gateway-no-auth"`).
    private static bool IsExemptPairValue(string value, BundleRedactionContext context)
    {
        string trimmed = value.Trim();
        if (IsExempt(trimmed, context))
        {
            return true;
        }

        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 && AuthSchemes.Contains(trimmed[..space]) && IsExempt(trimmed[(space + 1)..].Trim(), context);
    }

    // The pair pass also treats `pwd` / `Pwd` / `PWD` as a secret name (`…;Uid=sa;Pwd=S3cr3tP4ss;`). The exact-name
    // PWD / OLDPWD exclusion stays for known-value COLLECTION, and a PWD whose value is a path is the shell's cwd.
    private static bool IsSecretPairName(string name, string value) =>
        SecretNameRule.IsSecretName(name)
        || (string.Equals(name, "pwd", StringComparison.OrdinalIgnoreCase) && !LooksLikePath(value));

    private static bool LooksLikePath(string value) =>
        value.StartsWith('/') || value.StartsWith('~') || value.StartsWith('\\')
        // A drive: the unquoted value stops at the backslash, so `C:\src` arrives as `C:`.
        || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value.Length == 2 || value[2] is '\\' or '/'));

    private static readonly HashSet<string> AuthSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bearer", "Basic", "Token", "Digest", "Negotiate", "NTLM",
    };

    private static readonly HashSet<string> HeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "ocp-apim-subscription-key", "cookie", "set-cookie",
    };

    // The header rule owns these names, and keeps the scheme word (`Bearer`) readable.
    private static bool IsHeaderName(string name) => HeaderNames.Contains(name);

    // `authTokenEnv` / `apiKeyEnv` hold the NAME of a variable, by schema — the diagnostic #791 turned on.
    private static bool NameIsAVariableName(string name) => name is "authTokenEnv" or "apiKeyEnv";

    private static bool IsInertValue(string value) =>
        value.Trim() is "true" or "false" or "null" or "True" or "False" or "None" or "undefined";

    // ------------------------------------------------------------------ apply

    private static (string Text, List<string> Labels) Apply(string content, List<Hit> hits)
    {
        var spans = new List<(int Start, int End, string Label, Priority Priority)>();
        foreach (Hit hit in hits)
        {
            foreach ((int start, int end) in hit.Ranges)
            {
                if (end > start)
                {
                    spans.Add((start, end, hit.Label, hit.Priority));
                }
            }
        }

        if (spans.Count == 0)
        {
            return (content, []);
        }

        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        var merged = new List<(int Start, int End, string Label, Priority Priority, int Length)>();
        foreach ((int start, int end, string label, Priority priority) in spans)
        {
            if (merged.Count > 0 && start < merged[^1].End)
            {
                var last = merged[^1];
                bool better = priority < last.Priority || (priority == last.Priority && end - start > last.Length);
                merged[^1] = (last.Start, Math.Max(last.End, end), better ? label : last.Label, better ? priority : last.Priority,
                    better ? end - start : last.Length);
            }
            else
            {
                merged.Add((start, end, label, priority, end - start));
            }
        }

        var output = new StringBuilder(content.Length);
        var labels = new List<string>(merged.Count);
        int at = 0;
        foreach ((int start, int end, string label, _, _) in merged)
        {
            output.Append(content, at, start - at).Append(Render(label));
            labels.Add(label);
            at = end;
        }

        output.Append(content, at, content.Length - at);
        return (output.ToString(), labels);
    }

    private enum Priority
    {
        KnownValue = 0,
        Shape = 1,
        Header = 2,
        Pair = 3,
        Entropy = 4,
    }

    private sealed record Hit(IReadOnlyList<(int Start, int End)> Ranges, string Label, Priority Priority);

    // ------------------------------------------------------------------ the patterns (§17.6.2)

    private static readonly (Regex Regex, string Kind)[] Shapes =
    [
        (PrivateKeyPattern(), "private-key"),
        (JwtPattern(), "jwt"),
        (StripePattern(), "stripe-key"),
        (SkKeyPattern(), "sk-key"),
        (GitHubPattern(), "github-token"),
        (GitLabPattern(), "gitlab-token"),
        (NpmPattern(), "npm-token"),
        (GooglePattern(), "google-api-key"),
        (AwsPattern(), "aws-access-key"),
        (SlackPattern(), "slack-token"),
        (HuggingFacePattern(), "huggingface-token"),
    ];

    private const int Timeout = 10_000;

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:sk|rk)_live_[A-Za-z0-9]{10,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex StripePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex SkKeyPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9_]{16,}|github_pat_[A-Za-z0-9_]{16,})", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex GitHubPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex GitLabPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])npm_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex NpmPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])AIza[0-9A-Za-z_-]{20,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex GooglePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])AKIA[0-9A-Z]{16}(?![0-9A-Z])", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex AwsPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:xox[abprs]-[A-Za-z0-9-]{10,}|xapp-[A-Za-z0-9-]{10,})", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex SlackPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])hf_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex HuggingFacePattern();

    // The value of a credential-carrying header, raw (`Authorization: Bearer x`) or as a JSON pair
    // (`"x-api-key":"x"`). An auth scheme word is kept; the value runs to the end of the line or a quote.
    [GeneratedRegex(
        @"(?i)(?<![A-Za-z0-9_-])(?<name>proxy-authorization|authorization|x-api-key|api-key|ocp-apim-subscription-key|set-cookie|cookie)[""']?[ \t]*:[ \t]*[""']?(?:(?:bearer|basic|token|digest|negotiate|ntlm)[ \t]+)?(?<v>[^\r\n""'\\]+)",
        RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])bearer[ \t]+(?<v>[A-Za-z0-9._~+/=-]+)", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"://(?<v>[^/\s:@""'\\]+:[^\s@""'\\]+|[^/\s:@""'\\]{8,})@", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex UrlCredentialPattern();

    [GeneratedRegex(@"(?i)\bmachine\s+\S+\s+(?:login\s+\S+\s+)?password\s+(?<v>[^\s""'\\]+)", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex NetrcPattern();

    // NAME=value, NAME: value, NAME = "value" (shell, .env, YAML, code). The name is an identifier; a value is quoted,
    // or runs to whitespace, a quote, a backtick or a backslash. The value is captured in a LOOKAHEAD, so a pair inside
    // another pair's value (`Server=db;Uid=sa;Pwd=…`, `?a=1&token=…`) is still scanned (#805 W1).
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.-])-{0,2}(?<name>(?>[A-Za-z_][A-Za-z0-9_.-]*))[ \t]*[:=][ \t]*(?=""(?<qv>[^""\r\n]*)""|'(?<qv>[^'\r\n]*)'|(?<v>[^\s""'`\\]+))",
        RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex AssignmentPairPattern();

    // "name": "value" (JSON, including JSON quoted inside another string once a view has unescaped it).
    [GeneratedRegex(@"""(?<name>(?>[A-Za-z_][A-Za-z0-9_.-]*))""\s*:\s*""(?<v>(?:[^""\\\r\n]|\\.)*)(?="")", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex JsonPairPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/=_~.-]{24,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex EntropyRun();

    [GeneratedRegex(@"^(?:(?:[A-Z][a-z]{2,}|[0-9]+)+)(?:\.(?:(?:[A-Z][a-z]{2,}|[0-9]+)+))*$", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex IdentifierRun();

    [GeneratedRegex(@"[A-Z][a-z]{2,}|[0-9]+", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex IdentifierPart();

    [GeneratedRegex(@"\[REDACTED:(?<label>[^\]\s]+)\]", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex LabelToken();
}
