using System.Net;
using System.Net.Sockets;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Webhooks;

/// <summary>
/// Pins the two PURE functions the webhook dispatcher's policy rests on (#585 layer 3 —
/// docs/plans/585-layer3-webhooks-contract.md): the retry classifier (§5.1) and the redacted-URL
/// renderer (§6.6).
///
/// <para>Authored RED against <see cref="WebhookEventSink"/>'s throwing stubs (task 04); task 05
/// implements <see cref="WebhookEventSink.IsRetryable"/> and <see cref="WebhookEventSink.RedactUrl"/>.
/// </para>
/// </summary>
public sealed class WebhookPolicyTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Retry classification (§5.1). Between them these six cover every row of that table.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void IsRetryableIsTrueFor408And429()
    {
        // The server explicitly said "later".
        Assert.True(WebhookEventSink.IsRetryable(HttpStatusCode.RequestTimeout, null));
        Assert.True(WebhookEventSink.IsRetryable(HttpStatusCode.TooManyRequests, null));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void IsRetryableIsTrueForEvery5xx()
    {
        // The whole band, not a handful of favourites — walked as raw integers so a
        // `== 500 || == 503` implementation cannot pass.
        for (int code = 500; code <= 599; code++)
        {
            Assert.True(WebhookEventSink.IsRetryable((HttpStatusCode)code, null), $"status {code} should be retryable (5xx, server-side, transient by definition)");
        }

        // Table row 1 — 2xx is delivered, not retried. The band's lower control: a success that
        // classified as retryable would re-POST a row the receiver already accepted.
        Assert.False(WebhookEventSink.IsRetryable(HttpStatusCode.OK, null));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void IsRetryableIsFalseFor3xx()
    {
        // Across the whole 3xx band, not one member. Redirects are never followed
        // (AllowAutoRedirect = false, §6.5), so a retry reproduces the redirect forever and the
        // payload plus its Authorization header never reaches anywhere the operator named.
        for (int code = 300; code <= 399; code++)
        {
            Assert.False(WebhookEventSink.IsRetryable((HttpStatusCode)code, null), $"status {code} is a hard failure (3xx)");
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void IsRetryableIsFalseForOtherFourXx()
    {
        // A byte-identical retry of a malformed, unauthorized or misaimed request fails
        // identically and hides the real problem.
        HttpStatusCode[] otherFourXx =
        [
            HttpStatusCode.BadRequest,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed,
            HttpStatusCode.RequestEntityTooLarge
        ];

        foreach (HttpStatusCode status in otherFourXx)
        {
            Assert.False(WebhookEventSink.IsRetryable(status, null), $"status {(int)status} should not be retried");
        }

        // Discriminating control, inside this same test: 408 and 429 are still retryable, so an
        // implementation that blankets all of 4xx as false cannot pass it.
        Assert.True(WebhookEventSink.IsRetryable(HttpStatusCode.RequestTimeout, null));
        Assert.True(WebhookEventSink.IsRetryable(HttpStatusCode.TooManyRequests, null));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void IsRetryableIsTrueForTransportExceptions()
    {
        // An endpoint may still be starting up — the common case for a sidecar.
        Assert.True(WebhookEventSink.IsRetryable(null, new HttpRequestException("connection refused")));
        Assert.True(WebhookEventSink.IsRetryable(null, new SocketException((int)SocketError.ConnectionRefused)));
        Assert.True(WebhookEventSink.IsRetryable(null, new IOException("TLS handshake failed")));

        // Table's last row: "any other exception from the client -> yes, treated as transient."
        // InvalidOperationException is a type the classifier cannot have a specific rule for — the
        // policy is deliberately conservative; §5.2's bounds are what cap the cost of being wrong.
        Assert.True(WebhookEventSink.IsRetryable(null, new InvalidOperationException("unexpected")));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void IsRetryableIsTrueForPerAttemptTimeout()
    {
        // A per-attempt (10 s) timeout surfaces in .NET as a TaskCanceledException. This is NOT a
        // bug: telling an attempt timeout from a RUN cancellation is the CALLER's job (the pump
        // owns both tokens and checks its own), and §3.3 is explicit that the drain never observes
        // the run's token. A pure classifier that special-cased cancellation would make the
        // per-attempt timeout non-retryable, which is the opposite of the §5.1 table.
        Assert.True(WebhookEventSink.IsRetryable(null, new TaskCanceledException()));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Redacted-URL rendering (§6.6): <scheme>://<host>[:<port>]/… — never the path, never the query.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void RedactedUrlKeepsSchemeHostAndPort()
    {
        Assert.Equal(
            "https://hooks.example.com/…",
            WebhookEventSink.RedactUrl(new Uri("https://hooks.example.com/services/T00/B11/XyZ?token=abc")));

        // Loopback and private addresses are explicitly allowed (§6.5) — the whole point of the
        // feature is an agent monitor on 127.0.0.1 — and the port is shown because 9000 is not
        // the scheme's default port.
        Assert.Equal(
            "http://127.0.0.1:9000/…",
            WebhookEventSink.RedactUrl(new Uri("http://127.0.0.1:9000/hook")));

        // No path, no query — nothing to elide, so no "/…" is appended, and https's default port
        // (443) is not shown either.
        Assert.Equal(
            "https://example.com",
            WebhookEventSink.RedactUrl(new Uri("https://example.com")));

        // The userinfo is a credential and it never appears — this is the assertion that forces
        // the renderer to be BUILT from Uri.Scheme / Uri.Host / Uri.Port rather than produced by
        // trimming url.ToString().
        Assert.Equal(
            "https://example.com/…",
            WebhookEventSink.RedactUrl(new Uri("https://user:s3cr3t@example.com/hook")));
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void RedactedUrlNeverContainsThePath()
    {
        // Path segments built to look like credentials, the way a real Slack incoming webhook or
        // webhook.site URL does — for those services, the URL PATH IS THE CREDENTIAL.
        var url = new Uri("https://hooks.example.com/services/T00000000/B11111111/XyZ0123456789abcdefGHIJ");

        string redacted = WebhookEventSink.RedactUrl(url);

        // Positive control: proves the renderer actually produced something, so the negative
        // assertions below cannot pass by rendering an empty string.
        Assert.Contains("hooks.example.com", redacted);

        Assert.DoesNotContain("services", redacted);
        Assert.DoesNotContain("T00000000", redacted);
        Assert.DoesNotContain("B11111111", redacted);
        Assert.DoesNotContain("XyZ0123456789abcdefGHIJ", redacted);
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void RedactedUrlNeverContainsTheQuery()
    {
        // Query, no path.
        var queryOnly = new Uri("https://hooks.example.com?webhook_token=s3cr3t-value");
        string redactedQueryOnly = WebhookEventSink.RedactUrl(queryOnly);

        // Positive control, same reason as the path test above.
        Assert.Contains("hooks.example.com", redactedQueryOnly);
        Assert.DoesNotContain("webhook_token", redactedQueryOnly);
        Assert.DoesNotContain("s3cr3t-value", redactedQueryOnly);

        // Both path and query.
        var pathAndQuery = new Uri("https://hooks.example.com/services/T00/B11?webhook_token=s3cr3t-value");
        string redactedBoth = WebhookEventSink.RedactUrl(pathAndQuery);

        Assert.Contains("hooks.example.com", redactedBoth);
        Assert.DoesNotContain("webhook_token", redactedBoth);
        Assert.DoesNotContain("s3cr3t-value", redactedBoth);
        Assert.DoesNotContain("services", redactedBoth);
        Assert.DoesNotContain("T00", redactedBoth);
        Assert.DoesNotContain("B11", redactedBoth);
    }
}
