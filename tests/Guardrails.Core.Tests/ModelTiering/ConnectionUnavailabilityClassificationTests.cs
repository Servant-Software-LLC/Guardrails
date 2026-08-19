using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The executable answer to DoR §6.3's open question: <i>does the shipped
/// <see cref="PromptFailureKind"/> quarantine already catch a bare DNS / connection-refused / TLS
/// shape, or does it need an additive classification?</i> One test per real-world shape, so the
/// answer is a passing or failing assertion rather than a paragraph.
///
/// <para><b>The ruling these tests encode (DoR §6.3).</b> A <i>connection-level</i> failure at
/// launch — DNS failure, connection refused/reset, TLS timeout, a missing CLI — is
/// <see cref="PromptFailureKind.Transient"/> and rides the SHIPPED #115 transient-pause machinery:
/// bounded exponential backoff (2s→60s, honoring any parsed reset hint), bounded by
/// <c>transientPauseBudgetSeconds</c>, and <b>no retry-budget consumption</b>. The provider is
/// down; a human cannot fix it and an immediate re-run just re-fails.</para>
///
/// <para><b>No new enum member.</b> §6.3 is explicit that no new probe enum is introduced in v1
/// (the DA's <c>unreachable</c> probe state belongs with the v2 probes, #227). There is therefore
/// no new member of <see cref="PromptFailureKind"/> and nothing here asks for one — the expected
/// classification for every shape below is the existing
/// <see cref="PromptFailureKind.Transient"/>. This is an ADDITIVE widening of what the shipped
/// quarantine recognizes, not a new kind.</para>
///
/// <para><b>Why some of these pass today and some fail.</b> That split IS the answer. The shipped
/// <c>TransientPhrases</c> list already carries the spelled-out
/// <c>"connection refused"</c>/<c>"connection reset"</c> wordings, so those rows are green — and a
/// green row is not waste here: it is the half of the answer that says "already covered", and it
/// pins that coverage against a future edit that removes a phrase. The errno spellings
/// (<c>ECONNREFUSED</c>), every DNS shape, every TLS shape and every missing-CLI shape are red —
/// which is what says the quarantine needs the additive extension.</para>
///
/// <para><b>Realistic verbatim text, not the grep target.</b> Every string below is the message a
/// real failure actually emits (libuv/Node errno lines, curl, .NET <c>SocketException</c> /
/// <c>Win32Exception</c>, Go's <c>net/http</c>, OpenSSL, cmd.exe, bash) — not the phrase the
/// classifier greps for. The point is that a REAL message classifies, not that a keyword does.</para>
///
/// <para><b>The load-bearing test is <see cref="OrdinaryFailureMentioningConnection_StaysError"/>.</b>
/// Without it, every other row can be made green by classifying anything containing "connect" or
/// "resolve" as Transient — and a genuine logic failure would then loop on the pause machinery
/// instead of consuming its retry budget and surfacing to a human. The classifier's own rule
/// governs: <b>a miss is conservative — an unrecognised error yields
/// <see cref="PromptFailureKind.Error"/>, never a false <see cref="PromptFailureKind.Transient"/>
/// that could loop.</b> Widening the recogniser must not widen it past that.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class ConnectionUnavailabilityClassificationTests
{
    // ---------------------------------------------------------------------------------------
    // Shape 1 — DNS resolution failure. The "internet is down" case review comment 5 asked about,
    // and the one the DA pass specifically flagged: there is no HTTP status at all here, because
    // no HTTP request was ever made. Every spelling below is currently UNrecognized.
    // ---------------------------------------------------------------------------------------

    [Theory]
    // libuv/Node (the shape the Claude CLI's own fetch surfaces).
    [InlineData("getaddrinfo ENOTFOUND api.anthropic.com")]
    // libuv/Node, the temporary-failure variant (resolver reachable, answer not).
    [InlineData("getaddrinfo EAI_AGAIN api.anthropic.com")]
    // curl.
    [InlineData("curl: (6) Could not resolve host: api.anthropic.com")]
    // .NET on Linux (glibc's message, surfaced through HttpRequestException).
    [InlineData("System.Net.Http.HttpRequestException: Name or service not known (api.anthropic.com:443)")]
    // .NET on Windows (the Winsock wording for the same condition).
    [InlineData("System.Net.Http.HttpRequestException: No such host is known. (api.anthropic.com:443)")]
    public void DnsResolutionFailure_IsTransient(string text) =>
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));

    // ---------------------------------------------------------------------------------------
    // Shape 2 — connection refused / reset. Split deliberately into the two halves of the answer:
    // the spelled-out wordings the shipped TransientPhrases list already carries, and the errno
    // spellings it does not. Same condition, different vendor prose.
    // ---------------------------------------------------------------------------------------

    [Theory]
    // A local inference endpoint that is not listening (the "local is up, frontier is down"
    // config §6.3 discusses, in its inverse).
    [InlineData("System.Net.Sockets.SocketException (111): Connection refused")]
    [InlineData("System.Net.Sockets.SocketException (104): Connection reset by peer")]
    public void ConnectionRefusedOrReset_SpelledOut_IsTransient_AlreadyCovered(string text) =>
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));

    [Theory]
    // libuv/Node errno spellings — the SAME condition as the rows above, and the spelling a
    // JavaScript-hosted CLI actually prints. "ECONNREFUSED" does not contain "connection refused".
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:11434")]
    [InlineData("Error: read ECONNRESET")]
    // Winsock's prose for a refused connect, which likewise never says "connection refused".
    [InlineData("System.Net.Http.HttpRequestException: No connection could be made because the "
        + "target machine actively refused it. (127.0.0.1:11434)")]
    public void ConnectionRefusedOrReset_ErrnoSpelling_IsTransient(string text) =>
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));

    // ---------------------------------------------------------------------------------------
    // Shape 3 — TLS / handshake failure. Named explicitly by §6.3 ("TLS timeout"). A corporate
    // proxy, a captive portal or an expired trust store produces these; the provider is
    // unreachable for the same operational purpose, and the same bounded pause is the answer.
    // ---------------------------------------------------------------------------------------

    [Theory]
    // Go's net/http (proxies and sidecars in front of a provider endpoint).
    [InlineData("Post \"https://api.anthropic.com/v1/messages\": net/http: TLS handshake timeout")]
    // curl behind a TLS-intercepting proxy without its root installed.
    [InlineData("curl: (60) SSL certificate problem: unable to get local issuer certificate")]
    // OpenSSL 3.x.
    [InlineData("error:0A000126:SSL routines::unexpected eof while reading")]
    // .NET's wording for a handshake torn down mid-negotiation.
    [InlineData("The SSL connection could not be established, see inner exception. Authentication "
        + "failed because the remote party has closed the transport stream.")]
    public void TlsHandshakeFailure_IsTransient(string text) =>
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));

    // ---------------------------------------------------------------------------------------
    // Shape 4 — a missing CLI at launch. §6.3 lists this alongside the network shapes: the runner
    // binary for the resolved route is not on PATH, so the process never starts and there is no
    // agent output at all — only the OS's message from the failed start. Operationally it is the
    // same fact ("this provider cannot be reached right now"), and it must not burn the retry
    // budget by re-launching a binary that is still absent.
    //
    // Note for the implementer: these phrases are broad in a way the network ones are not — "no
    // such file or directory" is ordinary text inside a normal agent failure. Anchor the match to
    // the launch channel (the process-start failure text) rather than to any error text the agent
    // itself produced, or the negative control below becomes hard to keep green for real inputs.
    // ---------------------------------------------------------------------------------------

    [Theory]
    // .NET Process.Start on Windows when the executable is not on PATH.
    [InlineData("System.ComponentModel.Win32Exception (2): The system cannot find the file specified.")]
    // cmd.exe.
    [InlineData("'claude' is not recognized as an internal or external command, operable program "
        + "or batch file.")]
    // .NET Process.Start on POSIX.
    [InlineData("System.ComponentModel.Win32Exception (2): An error occurred trying to start "
        + "process 'claude' with working directory '/repo'. No such file or directory")]
    // bash.
    [InlineData("bash: claude: command not found")]
    public void MissingCliAtLaunch_IsTransient(string text) =>
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));

    // ---------------------------------------------------------------------------------------
    // Shape 5 — the negative control, and the most important test in the file.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An ordinary agent failure that merely MENTIONS a network word must stay
    /// <see cref="PromptFailureKind.Error"/> — consuming the retry budget and surfacing — not
    /// Transient. Each row is aimed at a specific cheap widening that would make every other test
    /// in this file pass: a bare "connect" / "resolve" / "host" / "not recognized" substring.
    ///
    /// <para>The rule, restated from the classifier's own contract: <b>a miss is conservative — an
    /// unrecognised error yields <c>Error</c>, never a false <c>Transient</c> that could loop.</b>
    /// A false Transient is the expensive direction of the trade: it does not consume the retry
    /// budget, so a deterministic logic failure would ride the pause machinery until
    /// <c>transientPauseBudgetSeconds</c> is spent and then surface as a connectivity problem that
    /// was never one.</para>
    /// </summary>
    [Theory]
    // "connection" + "refused" present, but not adjacent — a parser assertion, not a socket.
    [InlineData("The test asserted the connection string was refused by the parser")]
    // "resolve" in the compiler's sense.
    [InlineData("Compilation failed: cannot resolve symbol 'HostName'")]
    // "resolve" AND "host" — kills a bare "resolve host" substring without touching the full
    // "could not resolve host" curl phrase asserted above.
    [InlineData("Expected the parser to resolve the host header, but it returned null")]
    // "is not recognized" in the compiler's sense — kills the bare substring without touching the
    // full "is not recognized as an internal or external command" cmd.exe phrase above.
    [InlineData("The build failed: the identifier 'GuardrailsHost' is not recognized in this scope")]
    // "connect" as a substring of "disconnected".
    [InlineData("Assert.Equal() Failure: expected 2 open sockets, but the client had already "
        + "disconnected")]
    public void OrdinaryFailureMentioningConnection_StaysError(string text)
    {
        Assert.Equal(PromptFailureKind.Error, ClaudeSignalClassifier.Classify(text));
        Assert.False(ClaudeSignalClassifier.IsTransient(text));
    }

    // ---------------------------------------------------------------------------------------
    // The two predicate surfaces must not disagree.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ClaudeSignalClassifier.IsTransient"/> is the documented "carries a transient
    /// signal" predicate and <see cref="ClaudeSignalClassifier.Classify"/> is what the pause
    /// machinery routes on (<c>ClaudePromptRunner</c> → <c>TaskExecutor</c>). Widening one and not
    /// the other leaves a split brain: the attempt pauses but the reset-hint path (guarded on
    /// <c>Transient</c>) and any other <c>IsTransient</c> caller see a non-transient failure.
    /// Whatever recognizes the shapes above must sit BELOW both entry points.
    /// </summary>
    [Theory]
    [InlineData("getaddrinfo ENOTFOUND api.anthropic.com")]
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:11434")]
    [InlineData("Post \"https://api.anthropic.com/v1/messages\": net/http: TLS handshake timeout")]
    [InlineData("bash: claude: command not found")]
    public void IsTransient_AgreesWithClassify_ForConnectionFailureShapes(string text)
    {
        Assert.True(ClaudeSignalClassifier.IsTransient(text));
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));
    }

    // ---------------------------------------------------------------------------------------
    // Shape 6 — the reset hint stays intact.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The rate-limit half of the pause machinery is untouched by the widening: a message that
    /// carries a reset time still yields it, so the bounded backoff keeps honoring the hint.
    /// </summary>
    [Theory]
    [InlineData("You've hit your session limit · resets 11:20am (America/Chicago)", "11:20")]
    [InlineData("usage limit reached · resets 3pm", "3pm")]
    public void ExtractResetHint_FromRateLimitMessage_StillReturnsTheHint(string text, string expected)
    {
        string? hint = ClaudeSignalClassifier.ExtractResetHint(text);
        Assert.NotNull(hint);
        Assert.Contains(expected, hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection failure has no reset time to report — nothing told us when the provider comes
    /// back — so the hint stays null and the pause falls to plain bounded exponential backoff.
    /// The <c>ECONNRESET</c> row is the trap: "RESET" is right there in the text, and the hint
    /// regex must keep requiring an actual time after it.
    /// </summary>
    [Theory]
    [InlineData("getaddrinfo ENOTFOUND api.anthropic.com")]
    [InlineData("Error: read ECONNRESET")]
    [InlineData("System.Net.Sockets.SocketException (104): Connection reset by peer")]
    [InlineData("Post \"https://api.anthropic.com/v1/messages\": net/http: TLS handshake timeout")]
    [InlineData("bash: claude: command not found")]
    public void ExtractResetHint_ForConnectionFailure_IsNull(string text) =>
        Assert.Null(ClaudeSignalClassifier.ExtractResetHint(text));
}
