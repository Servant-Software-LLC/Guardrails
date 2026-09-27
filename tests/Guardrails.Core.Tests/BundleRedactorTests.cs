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

    [Fact]
    public void TheCannotCatchTextNamesEveryLimit()
    {
        foreach (string id in new[] { "CC1", "CC2", "CC3", "CC4", "CC5", "CC6" })
        {
            Assert.Contains(id, BundleRedactor.CannotCatchText, StringComparison.Ordinal);
        }
    }
}
