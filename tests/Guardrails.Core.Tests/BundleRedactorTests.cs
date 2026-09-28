using System.Text.Json;
using Guardrails.Core.Bundle;

namespace Guardrails.Core.Tests;

/// <summary>
/// #799 row 3: the redaction passes of SSOT §17.6, by rule. The blind canary corpus (<see cref="BundleCanaryCorpusTests"/>)
/// is the independent evidence; these pin each rule's shape, the spellings pass 1 covers, the exemptions' narrowness,
/// the delta join, and the no-false-scrub set. Every value here is synthetic.
/// </summary>
public sealed class BundleRedactorTests
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    private static BundleRedactionResult Redact(string text, IReadOnlyDictionary<string, string>? environment = null,
        IEnumerable<string>? exempt = null) =>
        BundleRedactor.Redact(text, new BundleRedactionContext("test.txt", environment ?? NoEnvironment)
        {
            ExemptTokens = new HashSet<string>(exempt ?? [], StringComparer.Ordinal),
        });

    // ------------------------------------------------------------------ pass 2: every shape

    [Theory]
    [InlineData("key sk-proj-AbCdEfGhIjKlMnOpQrSt12 end", "sk-proj-AbCdEfGhIjKlMnOpQrSt12", "sk-key")]
    [InlineData("stripe sk_live_AbCdEfGhIjKlMnOp12 end", "sk_live_AbCdEfGhIjKlMnOp12", "stripe-key")]
    [InlineData("restricted rk_live_AbCdEfGhIjKlMnOp12 end", "rk_live_AbCdEfGhIjKlMnOp12", "stripe-key")]
    [InlineData("gh ghp_AbCdEfGhIjKlMnOpQrStUv1234 end", "ghp_AbCdEfGhIjKlMnOpQrStUv1234", "github-token")]
    [InlineData("pat github_pat_AbCdEfGhIjKlMnOp_1234 end", "github_pat_AbCdEfGhIjKlMnOp_1234", "github-token")]
    [InlineData("gl glpat-AbCdEfGhIjKlMnOp12 end", "glpat-AbCdEfGhIjKlMnOp12", "gitlab-token")]
    [InlineData("npm npm_AbCdEfGhIjKlMnOpQrStUv12 end", "npm_AbCdEfGhIjKlMnOpQrStUv12", "npm-token")]
    [InlineData("g AIzaSyAbCdEfGhIjKlMnOpQrStUv12 end", "AIzaSyAbCdEfGhIjKlMnOpQrStUv12", "google-api-key")]
    [InlineData("slack xoxb-1234567890-AbCdEfGh end", "xoxb-1234567890-AbCdEfGh", "slack-token")]
    [InlineData("slack xapp-1-A0123-4567-abcdef end", "xapp-1-A0123-4567-abcdef", "slack-token")]
    [InlineData("jwt eyJhbGciOi.eyJzdWIiOi.SflKxwRJSM end", "eyJhbGciOi.eyJzdWIiOi.SflKxwRJSM", "jwt")]
    [InlineData("curl -H 'Authorization: Bearer tokvalue-for-test' x", "tokvalue-for-test", "auth-header")]
    [InlineData("x-api-key: apikeyvalue-for-test\n", "apikeyvalue-for-test", "auth-header")]
    [InlineData("api-key: apikeyvalue-azure\n", "apikeyvalue-azure", "auth-header")]
    [InlineData("Ocp-Apim-Subscription-Key: subkeyvalue-01\n", "subkeyvalue-01", "auth-header")]
    [InlineData("Cookie: sid=cookievalue-01\n", "cookievalue-01", "auth-header")]
    [InlineData("Set-Cookie: sid=cookievalue-02; Path=/\n", "cookievalue-02", "auth-header")]
    [InlineData("sent bearer tokvalue-in-prose now", "tokvalue-in-prose", "bearer")]
    [InlineData("git clone https://bot:urlpassword-01@example.test/x.git", "urlpassword-01", "url-credential")]
    [InlineData("machine example.test login bot password netrcpass-01\n", "netrcpass-01", "netrc")]
    [InlineData("DB_PASSWORD=pairvalue-equals\n", "pairvalue-equals", "named-secret")]
    [InlineData("api_key: pairvalue-yaml\n", "pairvalue-yaml", "named-secret")]
    [InlineData("SMTP_PASS=pairvalue-pass-suffix\n", "pairvalue-pass-suffix", "named-secret")]
    [InlineData("{\"clientSecret\": \"pairvalue-json\"}", "pairvalue-json", "named-secret")]
    [InlineData("generated Qx8QZ3kf9LmNpR2sT4vW6yB1dF5hJ7 ok", "Qx8QZ3kf9LmNpR2sT4vW6yB1dF5hJ7", "high-entropy")]
    public void EachShapeIsScrubbedAndLabelledByKind(string text, string secret, string kind)
    {
        BundleRedactionResult result = Redact(text);

        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains(kind, result.Labels);
        Assert.Contains($"[REDACTED:{kind}]", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAwsAccessKeyIdIsScrubbed()
    {
        // Assembled at run time: a literal of this shape in source trips GitHub push protection.
        string key = "AK" + "IA" + "CANARYFIXTURE799";
        BundleRedactionResult result = Redact($"aws {key} end");

        Assert.DoesNotContain(key, result.Text, StringComparison.Ordinal);
        Assert.Contains("aws-access-key", result.Labels);
    }

    [Fact]
    public void APemPrivateKeyBlockIsScrubbedWhole()
    {
        string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAabc123\nXYZdef456==\n-----END RSA PRIVATE KEY-----";
        BundleRedactionResult result = Redact($"$ cat key.pem\n{pem}\nnext line\n");

        Assert.DoesNotContain("MIIEowIBAAKCAQEAabc123", result.Text, StringComparison.Ordinal);
        Assert.Contains("private-key", result.Labels);
        Assert.EndsWith("next line\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAuthSchemeWordStaysReadable()
    {
        BundleRedactionResult result = Redact("Authorization: Bearer tokvalue-for-test\n");
        Assert.Equal("Authorization: Bearer [REDACTED:auth-header]\n", result.Text);
    }

    [Fact]
    public void AShapeIsNotMatchedInsideALongerWord()
    {
        // `sk-` inside a task id is not an OpenAI key.
        BundleRedactionResult result = Redact("task 03-mask-something-long-name-here ran");
        Assert.Empty(result.Labels);
    }

    // ------------------------------------------------------------------ pass 2 on the percent-decoded form

    [Fact]
    public void APercentEncodedTokenIsScrubbedInItsEncodedBytes()
    {
        string encoded = Uri.EscapeDataString("ghp_AbCdEfGhIjKlMnOpQrStUv1234");
        BundleRedactionResult result = Redact($"GET /cb?t={encoded}&x=1\n");

        Assert.DoesNotContain(encoded, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AbCdEfGhIjKlMnOp", result.Text, StringComparison.Ordinal);
        Assert.Contains("github-token", result.Labels);
        Assert.EndsWith("&x=1\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APercentEncodedHighEntropyValueIsScrubbedWithEveryEscape()
    {
        string secret = "Zq7Kf9LmNpR2sT4vW6yB1dF5hJ/+Qx8=";
        string encoded = Uri.EscapeDataString(secret);
        BundleRedactionResult result = Redact($"azcopy https://store.example.test/c?sp=r&sig={encoded} ./x\n");

        Assert.DoesNotContain("%2F", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("%2B", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Zq7Kf9LmNp", result.Text, StringComparison.Ordinal);
        Assert.Contains("high-entropy", result.Labels);
    }

    // ------------------------------------------------------------------ pass 1: every spelling of a known value

    [Fact]
    public void AKnownValueIsScrubbedInEverySpellingWithOneStableLabel()
    {
        const string value = "known+value/with=chars&more";
        var environment = new Dictionary<string, string> { ["QWEN_TOKEN"] = value };
        string stj = JsonSerializer.Serialize(new { a = value });
        string node = "{\"a\":\"" + value + "\"}";
        string percentUpper = Uri.EscapeDataString(value);
        string percentLower = percentUpper.ToLowerInvariant(); // the value has no upper case: only the hex changes
        string text = string.Join("\n", value, stj, node, percentUpper, percentLower) + "\n";

        BundleRedactionResult result = Redact(text, environment);

        Assert.Equal(5, result.Labels.Count);
        Assert.All(result.Labels, label => Assert.Equal("QWEN_TOKEN#1", label));
        Assert.DoesNotContain("known", result.Text, StringComparison.Ordinal);
        Assert.Contains("\\u002B", stj, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownValueLabelsAreNumberedPerVariableInCollectionOrder()
    {
        var environment = new Dictionary<string, string>
        {
            ["ZED_TOKEN"] = "value-of-zed-token",
            ["ALPHA_SECRET"] = "value-of-alpha-secret",
            ["PATH"] = "not-collected-path-value",
            ["PWD"] = "/not/collected/cwd",
        };
        BundleSecrets secrets = BundleSecrets.Collect(environment, [],
        [
            new("ALPHA_SECRET", "second-alpha-value"),
            new("OTHER_KEY", "value-of-zed-token"),
            new("PLAN_LITERAL", "plan-literal-ignored"),
            new("LITELLM_MASTER_KEY", "short"),
        ]);

        Assert.Equal(["ALPHA_SECRET#1", "ZED_TOKEN#1", "ALPHA_SECRET#2"], secrets.Values.Select(v => v.Label));
    }

    [Fact]
    public void ABlockNamedVariableIsCollectedWhateverItsName()
    {
        var environment = new Dictionary<string, string> { ["QWEN_LOCAL"] = "value-under-an-odd-name" };
        BundleSecrets secrets = BundleSecrets.Collect(environment, ["QWEN_LOCAL"], []);
        Assert.Equal("QWEN_LOCAL#1", Assert.Single(secrets.Values).Label);
    }

    [Fact]
    public void TheGatewayPlaceholderIsNeverCollectedAsAKnownValue()
    {
        var environment = new Dictionary<string, string> { ["ANTHROPIC_AUTH_TOKEN"] = "guardrails-gateway-no-auth" };
        Assert.Empty(BundleSecrets.FromEnvironment(environment).Values);
    }

    [Fact]
    public void ExemptionsNeverApplyToKnownValues()
    {
        // A known secret is scrubbed even when it equals a task id.
        var environment = new Dictionary<string, string> { ["DEPLOY_TOKEN"] = "03-impl-task-id" };
        BundleRedactionResult result = Redact("task 03-impl-task-id failed\n", environment, exempt: ["03-impl-task-id"]);
        Assert.Equal("task [REDACTED:DEPLOY_TOKEN#1] failed\n", result.Text);
    }

    [Theory]
    [InlineData("PWD", false)]
    [InlineData("PIN", true)]
    [InlineData("CARD_PIN", true)]
    [InlineData("SPINNER", false)]
    [InlineData("OLDPWD", false)]
    [InlineData("SQL_PWD", true)]
    [InlineData("PWD_FILE", true)]
    [InlineData("SMTP_PASS", true)]
    [InlineData("GIT_ASKPASS", false)]
    [InlineData("x-api-key", true)]
    [InlineData("apiKey", true)]
    [InlineData("HOME", false)]
    public void TheSecretNameRule(string name, bool secret) => Assert.Equal(secret, SecretNameRule.IsSecretName(name));

    // ------------------------------------------------------------------ pass 4: the delta join

    [Fact]
    public void AKnownValueSplitAcrossDeltaEventsIsScrubbedInEachEventAndTheJsonSurvives()
    {
        var environment = new Dictionary<string, string> { ["QWEN_TOKEN"] = "split-known-value-0123456789" };
        string text =
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"key: split-known-\"}}\n" +
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"value-0123456789 ok\"}}\n";

        BundleRedactionResult result = Redact(text, environment);

        Assert.DoesNotContain("split-known-", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("value-0123456789", result.Text, StringComparison.Ordinal);
        foreach (string line in result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument _ = JsonDocument.Parse(line);
        }
    }

    [Fact]
    public void AShapeSplitAcrossDeltaEventsIsScrubbed()
    {
        string text =
            "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"ghp_AbCdEf\"}}\n" +
            "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"GhIjKlMnOp1234\"}}\n";

        BundleRedactionResult result = Redact(text);

        Assert.DoesNotContain("AbCdEf", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("GhIjKlMnOp1234", result.Text, StringComparison.Ordinal);
        Assert.Contains("github-token", result.Labels);
    }

    [Fact]
    public void DeltasOfDifferentBlocksAreNotJoined()
    {
        // Block 0 says "sk-" and block 1 says the rest: they are two content blocks, not one value.
        string text =
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"a sk-\"}}\n" +
            "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"abcdefghijklmnopqrst\"}}\n";
        Assert.DoesNotContain("sk-key", Redact(text).Labels);
    }

    // ------------------------------------------------------------------ no false scrub

    [Theory]
    [InlineData("commit e46f75a26a692180f0ff3d0b27d63a706aef939c\n")]
    [InlineData("definitionHash sha256:69d0b6b20cb4c9fc88a949a689342a2e518fe0ae4229ed9ff417db20e91aa43e\n")]
    [InlineData("run 3d58336b-0799-4c1a-9e2f-6b1d2c3e4f50 done\n")]
    [InlineData("On branch guardrails/39-incremental-delivery-post-40-adjust\n")]
    [InlineData("  Failed Guardrails.Core.Tests.WaveTests.LoadAndValidateTargetResolvesWaveThroughParent [12 ms]\n")]
    [InlineData("  Passed Guardrails.Core.Tests.ResumeTests.Plan39ResumeRetriesWave2TaskAfterHalt [3 ms]\n")]
    [InlineData("> Authorization: Bearer guardrails-gateway-no-auth\n")]
    [InlineData("{\"headers\":{\"authorization\":\"Bearer guardrails-gateway-no-auth\"}}")]
    [InlineData("ANTHROPIC_AUTH_TOKEN=guardrails-gateway-no-auth\n")]
    [InlineData("{\"authTokenEnv\": \"LITELLM_MASTER_KEY\", \"apiKeyEnv\": \"OPENROUTER_API_KEY\"}")]
    [InlineData("GR2060 tasks/04-wire-bundle/guardrails/02-tests.ps1: script references no produced file\n")]
    [InlineData("{\"exitCode\":0,\"timedOut\":false}")]
    public void DiagnosticValuesSurviveUnchanged(string text)
    {
        BundleRedactionResult result = Redact(text);
        Assert.Equal(text, result.Text);
        Assert.Empty(result.Labels);
    }

    [Fact]
    public void AnEnumeratedTaskIdOrPathThatLooksRandomSurvivesButOnlyWhole()
    {
        const string taskId = "07-Ab3dEfGhIjKlMnOpQrStUv9";
        const string runId = "2026-06-10T16-22-31Z-Ab9q";
        string[] exempt = [taskId, runId, "logs", "attempt-2", "claude-stream.jsonl"];

        Assert.True(BundleRedactor.IsHighEntropy(taskId), "fixture: the task id must qualify for the entropy rule");
        Assert.Equal($"task {taskId} ok\n", Redact($"task {taskId} ok\n", exempt: exempt).Text);

        string path = $"logs/{runId}/{taskId}/attempt-2/claude-stream.jsonl";
        Assert.Equal(path + "\n", Redact(path + "\n", exempt: exempt).Text);

        // A path with one segment that is not enumerated stays in scope: exemptions are exact whole tokens.
        string mixed = $"logs/{runId}/Zq7Kf9LmNpR2sT4vW6yB/attempt-2";
        Assert.Contains("high-entropy", Redact(mixed + "\n", exempt: exempt).Labels);
    }

    [Theory]
    [InlineData("Plan39ResumeRetriesWave2TaskAfterHalt", true)]
    [InlineData("Guardrails.Core.Tests.ResumeTests.Plan39ResumeRetriesWave2TaskAfterHalt", true)]
    [InlineData("Qx8QZ3kf9LmNpR2sT4vW6yB1dF5hJ7", false)]
    [InlineData("CANARY7a024KWqDwkV4JfYDbYIFbXctSqEaUDlMQSyh", false)]
    [InlineData("Plan39", false)]
    public void TheIdentifierExemptionIsNarrow(string run, bool exempt) => Assert.Equal(exempt, BundleRedactor.IsIdentifier(run));

    [Fact]
    public void TheIdentifierExemptionNeverSparesAKnownValue()
    {
        var environment = new Dictionary<string, string> { ["TEST_TOKEN"] = "Plan39ResumeRetriesWave2TaskAfterHalt" };
        Assert.Contains("TEST_TOKEN#1", Redact("Plan39ResumeRetriesWave2TaskAfterHalt\n", environment).Labels);
    }

    [Fact]
    public void HexTopsOutAtFourBitsSoItIsNotHighEntropy()
    {
        Assert.False(BundleRedactor.IsHighEntropy("0123456789abcdef0123456789abcdef"));
        Assert.True(BundleRedactor.ShannonEntropy("0123456789abcdef") <= BundleRedactor.EntropyThreshold);
    }

    // ------------------------------------------------------------------ JSON stays JSON

    [Fact]
    public void RedactingSystemTextJsonAndNodeOutputLeavesValidJson()
    {
        var environment = new Dictionary<string, string> { ["API_TOKEN"] = "canary<tag>'q'+plus\"quote\\back" };
        string stj = JsonSerializer.Serialize(new { detail = "token canary<tag>'q'+plus\"quote\\back rejected", clientSecret = "abc-secret-value" });
        string node = "{\"detail\":\"token canary<tag>'q'+plus\\\"quote\\\\back rejected\"}";

        foreach (string json in new[] { stj, node })
        {
            BundleRedactionResult result = Redact(json, environment);
            using JsonDocument document = JsonDocument.Parse(result.Text);
            Assert.Contains("[REDACTED:API_TOKEN#1]", document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NothingToRedactReturnsTheTextUnchangedWithNoLabels()
    {
        BundleRedactionResult result = Redact("plain text\n");
        Assert.Equal("plain text\n", result.Text);
        Assert.Empty(result.Labels);
    }

    // ------------------------------------------------------------------ pass 3: paths

    [Fact]
    public void PathAnonymizationCoversEverySpellingAndNeverRewritesALabel()
    {
        var anonymizer = new BundlePathAnonymizer(
            home: @"C:\Users\dev", workspace: @"C:\Users\dev\src\app", worktreeRoot: @"C:\wt", userName: "dev",
            hosts: ["build-box-7"], caseInsensitive: true);

        string text = string.Join("\n",
            @"C:\Users\dev\src\app\plan\run.json",
            "C:/Users/dev/.config/x",
            @"{""p"":""C:\\Users\\dev\\src\\app\\x""}",
            @"C:\wt\abcd1234\run\03\attempt-1",
            @"D:\other\dev\file",
            "host build-box-7 ran it",
            "[REDACTED:dev#1]");

        string result = anonymizer.Apply(text);

        Assert.Equal(string.Join("\n",
            @"<workspace>\plan\run.json",
            "~/.config/x",
            @"{""p"":""<workspace>\\x""}",
            @"<worktrees>\abcd1234\run\03\attempt-1",
            @"D:\other\<user>\file",
            "host <host> ran it",
            "[REDACTED:dev#1]"), result);
    }

    [Fact]
    public void TheContextAppliesPathsAfterTheSecretPasses()
    {
        var context = new BundleRedactionContext("x", new Dictionary<string, string> { ["DEV_TOKEN"] = "/home/dev/secret-path-token" })
        {
            Paths = new BundlePathAnonymizer("/home/dev", null, null, "dev", [], caseInsensitive: false),
        };

        BundleRedactionResult result = BundleRedactor.Redact("a /home/dev/secret-path-token b /home/dev/x\n", context);
        Assert.Equal("a [REDACTED:DEV_TOKEN#1] b ~/x\n", result.Text);
    }

    // ------------------------------------------------------------------ #805 review: redaction blockers and weak spots

    [Theory]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"{\\\"password\\\":\\\"Tr0ub4dor&3x\\\",\\\"user\\\":\\\"sa\\\"}\"}]}}", "Tr0ub4dor&3x")]
    [InlineData("{\"out\":\"{\\\"api_key\\\":\\\"abcDEF123ghiJKL\\\"}\"}", "abcDEF123ghiJKL")]
    public void B2_ASecretInTheFirstKeyOfJsonQuotedInAStringIsCaught(string line, string secret)
    {
        BundleRedactionResult result = Redact(line);

        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains("named-secret", result.Labels);
        using JsonDocument _ = JsonDocument.Parse(result.Text);
    }

    [Theory]
    [InlineData(@"C:\Users\José\src\app", "José")]
    [InlineData(@"C:\Users\O'Brien\src\app", "O'Brien")]
    public void B1_AnEscapedOrEncodedHomeWithANonAsciiOrApostropheUserIsAnonymized(string workspace, string user)
    {
        string home = workspace[..workspace.IndexOf(@"\src", StringComparison.Ordinal)];
        var anonymizer = new BundlePathAnonymizer(home, workspace, null, user, [], caseInsensitive: true);
        // A bare `who` field is replaced only for a distinctive name (#805 N6), so only then is it in the fixture.
        string stj = BundlePathAnonymizer.IsDistinctiveName(user)
            ? JsonSerializer.Serialize(new { path = workspace + @"\plan", other = home + @"\notes.txt", who = user })
            : JsonSerializer.Serialize(new { path = workspace + @"\plan", other = home + @"\notes.txt" });
        string percent = "file:///" + Uri.EscapeDataString(workspace.Replace('\\', '/')) + "/x " + Uri.EscapeDataString(home + @"\y");

        Assert.True(stj.Contains("\\u", StringComparison.Ordinal), "fixture: System.Text.Json must escape the user name");
        foreach (string text in new[] { stj, percent })
        {
            string result = anonymizer.Apply(text);
            IEnumerable<string> leaks = [JsonSerializer.Serialize(user)[1..^1], Uri.EscapeDataString(user), "Users"];
            if (BundlePathAnonymizer.IsDistinctiveName(user))
            {
                leaks = leaks.Append(user);
            }

            foreach (string leak in leaks.Distinct())
            {
                Assert.DoesNotContain(leak, result, StringComparison.Ordinal);
            }
        }

        using JsonDocument document = JsonDocument.Parse(anonymizer.Apply(stj));
        Assert.Equal(@"<workspace>\plan", document.RootElement.GetProperty("path").GetString());
        Assert.Equal(@"~\notes.txt", document.RootElement.GetProperty("other").GetString());
        if (BundlePathAnonymizer.IsDistinctiveName(user))
        {
            Assert.Equal("<user>", document.RootElement.GetProperty("who").GetString());
        }
    }

    [Fact]
    public void W1_APwdInAConnectionStringIsASecretButAShellCwdIsNot()
    {
        BundleRedactionResult result = Redact("Server=db;Database=app;Uid=sa;Pwd=S3cr3tP4ss;\n");
        Assert.DoesNotContain("S3cr3tP4ss", result.Text, StringComparison.Ordinal);

        Assert.Equal("PWD=/home/runner/work/app\n", Redact("PWD=/home/runner/work/app\n").Text);
        Assert.Equal("pwd: C:\\src\\app\n", Redact("pwd: C:\\src\\app\n").Text);
    }

    [Fact]
    public void W2_TheEntropyThresholdScalesWithLength()
    {
        Assert.Equal(3.6, BundleRedactor.EntropyThresholdFor(24));
        Assert.Equal(3.6, BundleRedactor.EntropyThresholdFor(31));
        Assert.Equal(4.0, BundleRedactor.EntropyThresholdFor(32));

        // A 24-character random-looking token with repeats (entropy between 3.6 and 4.0) is now caught.
        const string token = "aB3xY9kQ7mN5pRaB3xY9kQ7m"; // 14 distinct characters, 10 of them twice: 3.75 bits
        double entropy = BundleRedactor.ShannonEntropy(token);
        Assert.InRange(entropy, 3.6, 4.0);
        Assert.Contains("high-entropy", Redact($"key {token} end\n").Labels);
    }

    [Theory]
    [InlineData("hf token hf_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 end", "hf_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "huggingface-token")]
    [InlineData("git clone https://tokvalue4a8f2b@github.test/x.git", "tokvalue4a8f2b", "url-credential")]
    [InlineData("curl https://user:pa/ss9Xz@host.test/x", "pa/ss9Xz", "url-credential")]
    public void W3_MoreShapesAreCaught(string text, string secret, string kind)
    {
        BundleRedactionResult result = Redact(text);
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains(kind, result.Labels);
    }

    [Fact]
    public void W4_IdentityValuesAreAnonymizedWhereverTheyAppear()
    {
        var anonymizer = new BundlePathAnonymizer(null, null, null,
            new BundleIdentity("david.maltby", "David Maltby", "dev@example.test"), [], caseInsensitive: false);

        string text = "USER=david.maltby\nUSERNAME=david.maltby\nAuthor: David Maltby <dev@example.test>\n"
                      + "cwd C--Users-david-maltby-src\n";
        string result = anonymizer.Apply(text);

        Assert.Equal("USER=<user>\nUSERNAME=<user>\nAuthor: <git-user> <<git-email>>\ncwd C--Users-<user>-src\n", result);
    }

    /// <summary>
    /// #805 N1: adversarial minified shapes must scan in linear time. .NET exposes no regex step counter, and the
    /// per-match timeout cannot catch a scan that is quadratic across MANY cheap matches, so this is a SMOKE GUARD with
    /// a generous absolute cap: 2 MiB of each shape must redact within 60 s. Before the fix, `a=`×2 MiB was projected at
    /// about 25 minutes and the minified shapes at about 13, so the cap separates linear from quadratic by two orders of
    /// magnitude. The anonymizer's identity patterns are driven over the same input.
    /// <para>
    /// It also caught a linear-but-costly shape on a loaded macOS CI runner: the encoded-user LOOKBEHIND walked back up to
    /// 256 characters at every `danaher` in one long `-danaher-…` run that never matched, so all ~67M steps landed in ONE
    /// regex operation and tripped its 10 s timeout. That rule is now a consuming token scan plus an index search, and the
    /// shape runs in about 0.5 s locally.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("a=")]
    [InlineData("a=b,c=d;e=f(g);")]
    [InlineData("a:b,c:d,")]
    [InlineData("?k=v&t=1&x=2")]
    [InlineData("a-")]
    [InlineData("a.")]
    [InlineData("-danaher")]
    [InlineData("--api-token x ")]
    [InlineData("mysql -p")]
    public void N1_TwoMebibytesOfAnAdversarialShapeRedactWithinTheSmokeCap(string shape)
    {
        var text = new System.Text.StringBuilder(2 * 1024 * 1024 + shape.Length);
        while (text.Length < 2 * 1024 * 1024)
        {
            text.Append(shape);
        }

        text.Append('\n');
        var anonymizer = new BundlePathAnonymizer("/home/danaher", null, null,
            new BundleIdentity("danaher", "Dana Hersh", "dana@example.test"), [], caseInsensitive: false);
        var context = new BundleRedactionContext("adversarial.txt", NoEnvironment) { Paths = anonymizer };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Exception? thrown = Record.Exception(() => BundleRedactor.Redact(text.ToString(), context));

        clock.Stop();
        Assert.Null(thrown);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"2 MiB of '{shape}' took {clock.Elapsed.TotalSeconds:0.0} s: the scan is not linear");
    }

    // ------------------------------------------------------------------ #805 final batch

    [Theory]
    [InlineData("tool --api-token tokvalue4a8f2b9 --verbose", "tokvalue4a8f2b9")]
    [InlineData("deploy --db-password hunter2hunter --host db", "hunter2hunter")]
    [InlineData("svc --client-secret s3cretvalue01", "s3cretvalue01")]
    [InlineData("mysql -h db -u app -p s3cretvalue02 app", "s3cretvalue02")]
    [InlineData("mysqldump -u app -ps3cretvalue03 app", "s3cretvalue03")]
    [InlineData("sshpass -p s3cretvalue04 ssh deploy@host", "s3cretvalue04")]
    [InlineData("curl -sS -u bot:s3cretvalue05 https://api.example.test", "bot:s3cretvalue05")]
    public void N2_SpaceSeparatedSecretFlagsAreCaught(string commandLine, string secret)
    {
        BundleRedactionResult result = Redact(commandLine + "\n");
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains("named-secret", result.Labels);
    }

    [Theory]
    [InlineData("mysql -uroot -p'S3cret Pw' db", "S3cret Pw")]
    [InlineData("mysql -u root -p\"S3cretPw\" db", "S3cretPw")]
    [InlineData("tool --password \"Hunter2 xyz\" --verbose", "Hunter2 xyz")]
    [InlineData("tool --client-secret 'Abc123secret'", "Abc123secret")]
    [InlineData("curl -u \"admin:S3cretPw\" https://x.example.test", "admin:S3cretPw")]
    [InlineData("curl -uadmin:S3cretPw https://x.example.test", "admin:S3cretPw")]
    [InlineData("curl --user=admin:S3cretPw https://x.example.test", "admin:S3cretPw")]
    public void N2_QuotedAndAttachedFlagValuesAreCaught(string commandLine, string secret)
    {
        BundleRedactionResult result = Redact(commandLine + "\n");
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cret", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mysql -P 3306 -h db app\n")]
    [InlineData("sshpass -f pw.txt ssh deploy@host\n")]
    [InlineData("ssh -p 22 deploy@host\n")]
    [InlineData("docker run -p 8080:80 image\n")]
    [InlineData("ssh -p 2222 deploy@host\n")]
    [InlineData("tool --api-token --verbose\n")]
    [InlineData("python -m http.server -p 9000\n")]
    public void N2_APortOrAFlagWithNoValueIsNotAPassword(string commandLine) =>
        Assert.Equal(commandLine, Redact(commandLine).Text);

    [Theory]
    [InlineData("llama-server --max-tokens 4096 --ctx-size 262144 --api-key-timeout 30s -np 4\n")]
    [InlineData("MAX_TOKENS=4096\nSESSION_TTL: 0.5h\n")]
    [InlineData("{\"max_tokens\": \"32k\", \"auth_retry\": \"500ms\"}")]
    public void ANumericSettingIsNeverASecret(string text) => Assert.Equal(text, Redact(text).Text);

    [Theory]
    [InlineData("tool --api-token abc123XYZpqr", "abc123XYZpqr")]
    [InlineData("tool --max-tokens-secret x9Kq2mLp7", "x9Kq2mLp7")]
    [InlineData("MAX_TOKENS=4096abc\n", "4096abc")]
    [InlineData("PASSWORD=12345678\n", "12345678")] // a numeric value under a password-like name is a PIN, not a setting
    [InlineData("deploy --db-pass 1234 --host db\n", "1234")]
    [InlineData("PIN=0000\n", "0000")]
    [InlineData("tool --api-secret 424242\n", "424242")]
    [InlineData("mysql -u app -p 1234 db\n", "1234")]
    public void ANonNumericValueOrANumericPinIsStillScrubbed(string text, string secret)
    {
        BundleRedactionResult result = Redact(text);
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains("named-secret", result.Labels);
    }

    [Fact]
    public void N6_ACommonAccountNameIsReplacedOnlyInPathForms()
    {
        var anonymizer = new BundlePathAnonymizer("/home/runner", null, null, new BundleIdentity("runner", "guardrails", null), [],
            caseInsensitive: false);
        string journal = "{\"runner\":\"claude\",\"attempt\":1}";
        string route = "runner: claude\nrunner block: default\n";
        string names = "plan/guardrails.json and guardrails run\n";
        string paths = "/home/runner/work/app and C--home-runner-src and /opt/runner/cache\n";

        Assert.Equal(journal, anonymizer.Apply(journal));
        Assert.Equal(route, anonymizer.Apply(route));
        Assert.Equal(names, anonymizer.Apply(names));
        // `C--home-runner-src` holds the '-'-encoded home itself, so the root rule takes it first.
        Assert.Equal("~/work/app and C-~-src and /opt/<user>/cache\n", anonymizer.Apply(paths));
    }

    [Theory]
    [InlineData("runner", false)]
    [InlineData("root", false)]
    [InlineData("ubuntu", false)]
    [InlineData("ec2-user", false)]
    [InlineData("dana", false)]
    [InlineData("guardrails", false)]
    [InlineData("danaher", true)]
    [InlineData("david.maltby", true)]
    public void N6_OnlyADistinctiveNameIsReplacedAsABareWord(string name, bool distinctive) =>
        Assert.Equal(distinctive, BundlePathAnonymizer.IsDistinctiveName(name));

    [Fact]
    public void N6_ADistinctiveNameIsNeverRewrittenAsAJsonKeyOrAFileName()
    {
        var anonymizer = new BundlePathAnonymizer(null, null, null, new BundleIdentity("danaher"), [], caseInsensitive: false);
        Assert.Equal("{\"danaher\": 1} danaher.json by <user>\n", anonymizer.Apply("{\"danaher\": 1} danaher.json by danaher\n"));
    }

    // ------------------------------------------------------------------ #812: identifiers are pseudonymized, not erased

    private const string SessionId = "5649d0ff-7dee-435f-b28e-452624770dcb";
    private const string ToolA = "call_Xq7Lk9Zp2Mw8Rt4VbN3c";
    private const string ToolB = "toolu_01AbCdEfGhIjKlMnOpQrSt";

    [Fact]
    public void AToolCallStillPairsWithItsResultAndTheSessionReadsTheSameEverywhere()
    {
        // Two parallel tool calls and their results, shaped like a gateway stream.
        string stream =
            $"{{\"type\":\"assistant\",\"message\":{{\"id\":\"msg_26daf271-f996-4687-b049-460beb36e182\",\"content\":[" +
            $"{{\"type\":\"tool_use\",\"id\":\"{ToolA}\",\"name\":\"Read\"}},{{\"type\":\"tool_use\",\"id\":\"{ToolB}\",\"name\":\"Grep\"}}]}}," +
            $"\"parent_tool_use_id\":null,\"session_id\":\"{SessionId}\",\"uuid\":\"6d75676f-4366-4fc7-9b6f-d733771646b2\"}}\n" +
            $"{{\"type\":\"user\",\"message\":{{\"content\":[{{\"tool_use_id\":\"{ToolB}\",\"type\":\"tool_result\"}}," +
            $"{{\"tool_use_id\":\"{ToolA}\",\"type\":\"tool_result\"}}]}},\"session_id\":\"{SessionId}\"," +
            $"\"parentUuid\":\"6d75676f-4366-4fc7-9b6f-d733771646b2\",\"request_id\":\"req_011CVxYzAbCdEfGh\"}}\n";

        BundleRedactionResult result = Redact(stream);
        string[] lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument call = JsonDocument.Parse(lines[0]);
        using JsonDocument reply = JsonDocument.Parse(lines[1]);

        JsonElement uses = call.RootElement.GetProperty("message").GetProperty("content");
        JsonElement results = reply.RootElement.GetProperty("message").GetProperty("content");
        string a = uses[0].GetProperty("id").GetString()!;
        string b = uses[1].GetProperty("id").GetString()!;
        Assert.Matches(@"^\[id-\d+\]$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(b, results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal(a, results[1].GetProperty("tool_use_id").GetString());
        Assert.Equal(call.RootElement.GetProperty("session_id").GetString(), reply.RootElement.GetProperty("session_id").GetString());
        Assert.Equal(call.RootElement.GetProperty("uuid").GetString(), reply.RootElement.GetProperty("parentUuid").GetString());
        Assert.Matches(@"^\[id-\d+\]$", call.RootElement.GetProperty("message").GetProperty("id").GetString()!);
        Assert.Matches(@"^\[id-\d+\]$", reply.RootElement.GetProperty("request_id").GetString()!);
        foreach (string raw in new[] { SessionId, ToolA, ToolB, "req_011CVxYzAbCdEfGh", "msg_26daf271" })
        {
            Assert.DoesNotContain(raw, result.Text, StringComparison.Ordinal);
        }

        Assert.Contains(BundleRedactor.PseudonymKind, result.Labels);
        Assert.DoesNotContain("named-secret", result.Labels);
        Assert.DoesNotContain("high-entropy", result.Labels);
    }

    [Theory]
    [InlineData("{\"id\":\"sk-proj-AbCdEfGhIjKlMnOpQrSt12\"}", "sk-proj-AbCdEfGhIjKlMnOpQrSt12", "sk-key")]
    [InlineData("{\"session_id\":\"ghp_AbCdEfGhIjKlMnOpQrStUv1234\"}", "ghp_AbCdEfGhIjKlMnOpQrStUv1234", "github-token")]
    [InlineData("{\"tool_use_id\":\"Qx8QZ3kf9LmNpR2sT4vW6yB1dF5hJ7\"}", "Qx8QZ3kf9LmNpR2sT4vW6yB1dF5hJ7", "high-entropy")]
    [InlineData("{\"model\":\"sk-proj-AbCdEfGhIjKlMnOpQrSt34\"}", "sk-proj-AbCdEfGhIjKlMnOpQrSt34", "sk-key")]
    public void ASecretUnderAnIdentifierOrModelKeyIsStillScrubbed(string json, string secret, string kind)
    {
        BundleRedactionResult result = Redact(json);
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains(kind, result.Labels);
        Assert.DoesNotContain(BundleRedactor.PseudonymKind, result.Labels);
    }

    [Fact]
    public void AKnownValueWinsOverAPseudonymAndOverAModelAllow()
    {
        var environment = new Dictionary<string, string>
        {
            ["QWEN_TOKEN"] = "toolu_01KnownSecretValue99",
            ["MODEL_API_KEY"] = "Qwen3.6-KnownModelKey-7",
        };
        BundleRedactionResult result = Redact("{\"id\":\"toolu_01KnownSecretValue99\",\"model\":\"Qwen3.6-KnownModelKey-7\"}", environment);

        Assert.Equal("{\"id\":\"[REDACTED:QWEN_TOKEN#1]\",\"model\":\"[REDACTED:MODEL_API_KEY#1]\"}", result.Text);
    }

    [Theory]
    [InlineData("{\"model\":\"Qwen3.6-35B-A3B-MXFP4_MOE\"}")]
    [InlineData("{\"model\":\"Qwen3.6-35B-A3B-MXFP4_MOE.gguf\"}")]
    [InlineData("{\"model\":\"claude-sonnet-4-5-20250929\",\"requestedModel\":\"qwen-3.6-35b-mtp\"}")]
    [InlineData("{\"model\":\"Qwen/Qwen3-Coder-30B-A3B-Instruct\"}")]
    [InlineData("model: Qwen3.6-35B-A3B-MXFP4_MOE\nrequested model: qwen-3.6-35b-mtp\n")]
    public void AModelNameSurvives(string text) => Assert.Equal(text, Redact(text).Text);

    [Fact]
    public void AModelFileBasenameSurvivesWhileItsPathIsAnonymizedOrJudged()
    {
        var context = new BundleRedactionContext("attempt-route.log", NoEnvironment)
        {
            Paths = new BundlePathAnonymizer("/Users/dana", null, null, "dana", [], caseInsensitive: false),
        };
        string route = "backend model: http://127.0.0.1:8080 /Users/dana/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf\n";
        Assert.Equal("backend model: http://127.0.0.1:8080 ~/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf\n",
            BundleRedactor.Redact(route, context).Text);

        // A random directory before the basename is still judged, and the basename still survives.
        string provenance = "{\"backendModel\":\"http://127.0.0.1:8080 /opt/Xq7Lk9Zp2Mw8Rt4VbN3cQ8/Qwen3.6-35B-A3B-MXFP4_MOE.gguf\"}";
        BundleRedactionResult judged = Redact(provenance);
        Assert.DoesNotContain("Xq7Lk9Zp2Mw8Rt4VbN3cQ8", judged.Text, StringComparison.Ordinal);
        Assert.Contains("/Qwen3.6-35B-A3B-MXFP4_MOE.gguf\"}", judged.Text, StringComparison.Ordinal);
        using JsonDocument _ = JsonDocument.Parse(judged.Text);
    }

    [Fact]
    public void PseudonymsAreStableWithinOneTableAndNumberedInFirstSeenOrder()
    {
        var table = new BundlePseudonyms();
        var context = new BundleRedactionContext("x", NoEnvironment) { Pseudonyms = table };
        string first = BundleRedactor.Redact($"{{\"id\":\"{ToolA}\"}}\n{{\"id\":\"{ToolB}\"}}\n", context).Text;
        string second = BundleRedactor.Redact($"{{\"tool_use_id\":\"{ToolB}\"}}\n", context).Text;

        Assert.Equal("{\"id\":\"[id-1]\"}\n{\"id\":\"[id-2]\"}\n", first);
        Assert.Equal("{\"tool_use_id\":\"[id-2]\"}\n", second);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void TheCannotCatchTextNamesEveryLimit()
    {
        foreach (string id in new[] { "CC1", "CC2", "CC3", "CC4", "CC5", "CC6" })
        {
            Assert.Contains(id, BundleRedactor.CannotCatchText, StringComparison.Ordinal);
        }
    }
}
