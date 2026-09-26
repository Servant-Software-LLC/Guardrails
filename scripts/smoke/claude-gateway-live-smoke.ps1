<#
.SYNOPSIS
    Manual live smoke for the claude runner over a gateway (#782): seeds HOSTILE Claude Code configuration, runs a
    one-task Guardrails plan through a recording proxy in front of a real LiteLLM gateway, and asserts that no
    request escaped the gateway the plan names, carried a credential it did not grant, or named a Claude model.

.DESCRIPTION
    NOT a test, and never run by CI. It launches the real `claude` CLI against a real gateway (LiteLLM in front of
    a local model), so it needs both running. Re-run it on EVERY Claude Code upgrade: several of the facts the
    gateway design relies on are Claude Code behaviors the harness cannot pin (SSOT section 9.10).

    It NEVER writes into the operator's real ~/.claude. Claude Code reloads settings while running, so seeding
    hostile values there would redirect the operator's live interactive sessions, and a crashed smoke would leave
    the directory poisoned. Instead:
      - the hostile USER config lives in a throwaway directory, and the smoke exports CLAUDE_CONFIG_DIR pointing at
        it in the environment `guardrails` is launched from — which also tests that the harness scrubs an
        inherited CLAUDE_CONFIG_DIR. It holds a canary ANTHROPIC_API_KEY in settings `env`, a canary
        `apiKeyHelper`, a redirecting ANTHROPIC_BASE_URL (pointing at a trap listener), and a `fallbackModel`;
      - a canary ANTHROPIC_API_KEY and a redirecting ANTHROPIC_BASE_URL are also exported in the launch
        environment itself;
      - the target repository's PROJECT settings hold only keys the composed --settings overrides that the
        preflight does not refuse: a claude-* `model` and a `fallbackModel`;
      - a SEPARATE step plants, one at a time, a project ANTHROPIC_API_KEY, CLAUDE_CODE_USE_BEDROCK=1,
        `apiKeyHelper` and a redirecting ANTHROPIC_BASE_URL, and asserts the PREFLIGHT halts on each, naming the
        file and the key (no model is invoked by those runs).
    A SHA-256 of the real ~/.claude/settings.json (when present) is taken before and after as a tripwire.

    Then it runs a one-task plan (write hello.txt) through the harness BUILT FROM THIS SOURCE TREE
    (`dotnet run --project src/Guardrails.Cli`) with the block's baseUrl pointed at a recording proxy that forwards
    to -GatewayUrl. The run must be green. Over every recorded request (the preflight's own probes included:
    /v1/models, /model/info, /v1/messages; then Claude Code's /v1/models, HEAD /api/hello, count_tokens,
    /v1/messages, and subagent requests identified by x-claude-code-agent-id) it asserts:
      - no canary appears in any header;
      - nothing reached the redirect trap (every request hit the declared gateway);
      - no request body named a claude-* model;
      - every Claude Code request carried a Bearer token and no x-api-key.
    It separately RECORDS (does not assert) any TCP connection to api.anthropic.com seen while the run was in
    flight — machine-wide, so another process on this machine can appear there too.

    It also ASSERTS where Claude Code's state went: the session transcripts landed under
    logs/<runId>/claude-config/projects; nothing for the smoke's target appeared under the real ~/.claude/projects,
    and the real ~/.claude.json neither appeared nor names the target; the hostile user config directory gained no
    projects/ and no .claude.json (the inherited CLAUDE_CONFIG_DIR was really replaced).

    It prints what it observed for the design's unverified items: `claude --version`; the request paths, models
    and agent ids seen (whether the subagent/background aliases took effect). Whether Claude Code treats a blanked
    "" variable as unset is NOT exercised: the composed settings file's "" belt is only reached if a project settings
    file sets one of those names, and the preflight halts on exactly that before any child runs.

.PARAMETER GatewayUrl
    The LiteLLM gateway to forward to. Default http://127.0.0.1:4000. Must already be running, with its backend.

.PARAMETER Model
    The gateway model name the block declares. Default "Qwen".

.PARAMETER BackendModel
    Optional `backendModel` for the block (the preflight then checks it against the backend's /props).

.PARAMETER ContextTokens
    Optional `contextTokens` (the backend's PER-SLOT window) for the block.

.PARAMETER AuthTokenEnv
    Optional NAME of an environment variable holding the gateway key; it must be set in this shell.

.PARAMETER ClaudePath
    The `claude` executable. Default `claude`, resolved on PATH.

.PARAMETER ValidateOnly
    Build the plan and run `guardrails validate` on it; launches nothing and spends nothing.

.PARAMETER Keep
    Keep the temp folder afterward (it is printed either way) instead of deleting it on success.

.EXAMPLE
    pwsh scripts/smoke/claude-gateway-live-smoke.ps1 -Model Qwen -BackendModel qwen3.6-35b-a3b -ContextTokens 65536
#>
[CmdletBinding()]
param(
    [string] $GatewayUrl = 'http://127.0.0.1:4000',
    [string] $Model = 'Qwen',
    [string] $BackendModel,
    [int] $ContextTokens,
    [string] $AuthTokenEnv,
    [string] $ClaudePath = 'claude',
    [switch] $ValidateOnly,
    [switch] $Keep
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$root = Join-Path ([IO.Path]::GetTempPath()) ('guardrails-gateway-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$canary = 'gr782-canary-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$failures = [System.Collections.Generic.List[string]]::new()

# ─────────────────────────── the never-touch-~/.claude guard ───────────────────────────
$realClaudeDir = [IO.Path]::GetFullPath((Join-Path $HOME '.claude'))
$hostileUserDir = [IO.Path]::GetFullPath((Join-Path $root 'hostile-user-config'))
if ($hostileUserDir.StartsWith($realClaudeDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to run: the hostile config directory '$hostileUserDir' is inside the real '$realClaudeDir'."
}

function Get-FileHashOrNull([string] $path) {
    if (Test-Path $path) { (Get-FileHash -Algorithm SHA256 -Path $path).Hash } else { $null }
}
$realSettings = Join-Path $realClaudeDir 'settings.json'
$realSettingsHashBefore = Get-FileHashOrNull $realSettings
$realProjectsDir = Join-Path $realClaudeDir 'projects'
$realClaudeJson = Join-Path $HOME '.claude.json'
$realClaudeJsonExisted = Test-Path $realClaudeJson
# The smoke's folder name is unique, and Claude Code names a project's transcript folder after its path — so any
# entry naming it under the REAL ~/.claude could only have come from this smoke's child.
$smokeToken = Split-Path $root -Leaf

# ─────────────────────────── recording proxy (in-process, C#) ───────────────────────────
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public sealed class GatewaySmokeRecorder
{
    private readonly HttpListener _listener = new HttpListener();
    private readonly HttpClient _client;
    private readonly string _upstream;
    private readonly string _logPath;
    private readonly string _canary;
    private readonly object _gate = new object();

    public GatewaySmokeRecorder(string host, int port, string upstream, string logPath, string canary)
    {
        _upstream = string.IsNullOrEmpty(upstream) ? null : upstream.TrimEnd('/'); // PowerShell passes $null as ""
        _logPath = logPath;
        _canary = canary;
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        _listener.Prefixes.Add("http://" + host + ":" + port + "/");
        _listener.Start();
        Task.Run(Loop);
    }

    public void Stop() { try { _listener.Stop(); } catch { } }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        byte[] body;
        using (var ms = new MemoryStream()) { await req.InputStream.CopyToAsync(ms); body = ms.ToArray(); }

        // The record carries FACTS about the headers, never a credential's value.
        string auth = req.Headers["Authorization"] ?? "";
        bool canaryInHeaders = req.Headers.AllKeys.Any(k => (req.Headers[k] ?? "").Contains(_canary) || k.Contains(_canary));
        string model = null;
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(body.Length == 0 ? "{}" : Encoding.UTF8.GetString(body)))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("model", out JsonElement m)
                    && m.ValueKind == JsonValueKind.String) { model = m.GetString(); }
            }
        }
        catch { }

        var record = new Dictionary<string, object>
        {
            ["time"] = DateTimeOffset.UtcNow.ToString("o"),
            ["method"] = req.HttpMethod,
            ["path"] = req.RawUrl,
            ["authScheme"] = auth.Contains(" ") ? auth.Substring(0, auth.IndexOf(' ')) : (auth.Length == 0 ? null : "?"),
            ["hasXApiKey"] = req.Headers["x-api-key"] != null,
            ["canaryInHeaders"] = canaryInHeaders,
            ["agentId"] = req.Headers["x-claude-code-agent-id"],
            ["model"] = model,
            ["headerNames"] = req.Headers.AllKeys
        };
        lock (_gate) { File.AppendAllText(_logPath, JsonSerializer.Serialize(record) + "\n"); }

        try
        {
            if (_upstream == null)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.Close();
                return;
            }

            var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), _upstream + req.RawUrl);
            if (body.Length > 0) { msg.Content = new ByteArrayContent(body); }
            foreach (string key in req.Headers.AllKeys)
            {
                string lower = key.ToLowerInvariant();
                if (lower == "host" || lower == "content-length" || lower == "connection" || lower == "transfer-encoding") { continue; }
                if (lower.StartsWith("content-")) { if (msg.Content != null) { msg.Content.Headers.TryAddWithoutValidation(key, req.Headers[key]); } }
                else { msg.Headers.TryAddWithoutValidation(key, req.Headers[key]); }
            }

            using (HttpResponseMessage resp = await _client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead))
            {
                ctx.Response.StatusCode = (int)resp.StatusCode;
                foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                {
                    string lower = h.Key.ToLowerInvariant();
                    if (lower == "transfer-encoding" || lower == "content-length" || lower == "connection" || lower == "keep-alive") { continue; }
                    try { ctx.Response.Headers[h.Key] = string.Join(",", h.Value); } catch { }
                }

                ctx.Response.SendChunked = true;
                using (Stream upstream = await resp.Content.ReadAsStreamAsync())
                {
                    byte[] buffer = new byte[8192];
                    int n;
                    while ((n = await upstream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await ctx.Response.OutputStream.WriteAsync(buffer, 0, n);
                        await ctx.Response.OutputStream.FlushAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate) { File.AppendAllText(_logPath + ".errors", req.HttpMethod + " " + req.RawUrl + ": " + ex + Environment.NewLine); }
            try { ctx.Response.StatusCode = 502; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }
}
'@

function Get-FreePort {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop(); $port
}

# ─────────────────────────── the plan and the hostile configuration ───────────────────────────
$target = Join-Path $root 'target'
$planDir = Join-Path $target 'plan'
$taskDir = Join-Path $planDir 'tasks/01-write-hello'
New-Item -ItemType Directory -Force -Path (Join-Path $taskDir 'guardrails'), $hostileUserDir, (Join-Path $target '.claude') | Out-Null
& git -C $target init -q | Out-Null

$proxyPort = Get-FreePort
$trapPort = Get-FreePort
# http.sys (Windows) lets a non-admin listen on `localhost` only, and answers both loopback families there; the
# managed listener elsewhere binds the literal address, so the IPv4 literal is used to keep node's resolver off ::1.
$loopHost = if ($IsWindows) { 'localhost' } else { '127.0.0.1' }
$proxyUrl = "http://${loopHost}:$proxyPort"
$trapUrl = "http://${loopHost}:$trapPort"
$requestLog = Join-Path $root 'requests.jsonl'
$trapLog = Join-Path $root 'trap-requests.jsonl'

$block = [ordered]@{
    kind           = 'claude'
    command        = $ClaudePath
    baseUrl        = $proxyUrl
    model          = $Model
    permissionMode = 'acceptEdits'
    allowedTools   = @('Read', 'Write', 'Edit')
    maxTurns       = 20
}
if ($BackendModel) { $block.backendModel = $BackendModel }
if ($ContextTokens -gt 0) { $block.contextTokens = $ContextTokens }
if ($AuthTokenEnv) { $block.authTokenEnv = $AuthTokenEnv }

[ordered]@{
    version               = 1
    maxParallelism        = 1
    defaultRetries        = 0
    defaultTimeoutSeconds = 1200
    workspace             = '..'
    promptRunners         = [ordered]@{ default = 'gateway'; gateway = $block }
} | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8NoBOM (Join-Path $planDir 'guardrails.json')

@'
{ "description": "Write hello.txt in the workspace root containing exactly: hello from gateway", "dependsOn": [], "writeScope": ["hello.txt"] }
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $taskDir 'task.json')

@'
Create a file named `hello.txt` in the workspace root (your current working directory). Its entire content must be
exactly the text `hello from gateway` followed by a single newline. Do not create or modify any other file.
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $taskDir 'action.prompt.md')

@'
# catches: the agent claimed success but hello.txt is missing or does not hold exactly "hello from gateway"
if (-not (Test-Path "hello.txt")) { Write-Output "hello.txt does not exist in the workspace"; exit 1 }
$content = (Get-Content "hello.txt" -Raw).Trim()
if ($content -ne "hello from gateway") { Write-Output "hello.txt holds '$content', expected 'hello from gateway'"; exit 1 }
exit 0
'@ | Set-Content -Encoding utf8NoBOM (Join-Path $taskDir 'guardrails/01-hello-exact.ps1')

# Hostile USER config (a throwaway CLAUDE_CONFIG_DIR — never ~/.claude).
[ordered]@{
    env           = [ordered]@{ ANTHROPIC_API_KEY = $canary; ANTHROPIC_BASE_URL = $trapUrl }
    apiKeyHelper  = "echo $canary"
    fallbackModel = 'claude-sonnet-4-5'
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $hostileUserDir 'settings.json')

# PROJECT settings: only keys the composed --settings overrides and the preflight does not refuse.
$projectSettings = Join-Path $target '.claude/settings.json'
[ordered]@{ model = 'claude-sonnet-4-5'; fallbackModel = 'claude-opus-4-1' } |
    ConvertTo-Json | Set-Content -Encoding utf8NoBOM $projectSettings

Write-Host "Smoke folder: $root"
$cli = Join-Path $repoRoot 'src/Guardrails.Cli'

if ($ValidateOnly) {
    & dotnet run --project $cli -- validate $planDir
    $validateExit = $LASTEXITCODE
    if (-not $Keep) { Remove-Item -Recurse -Force $root }
    exit $validateExit
}

$claudeVersion = (& $ClaudePath --version 2>&1 | Out-String).Trim()
Write-Host "claude --version: $claudeVersion"

# The hostile LAUNCH environment, process-scoped and restored in `finally`.
$saved = @{}
foreach ($name in 'CLAUDE_CONFIG_DIR', 'ANTHROPIC_API_KEY', 'ANTHROPIC_BASE_URL') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }

function Invoke-Guardrails([string[]] $arguments) {
    $output = & dotnet run --project $cli -- @arguments 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output }
}

$proxy = $null
$trap = $null
$anthropicConnections = [System.Collections.Generic.HashSet[string]]::new()
try {
    $env:CLAUDE_CONFIG_DIR = $hostileUserDir
    $env:ANTHROPIC_API_KEY = $canary
    $env:ANTHROPIC_BASE_URL = $trapUrl

    $proxy = [GatewaySmokeRecorder]::new($loopHost, $proxyPort, $GatewayUrl, $requestLog, $canary)
    $trap = [GatewaySmokeRecorder]::new($loopHost, $trapPort, $null, $trapLog, $canary)

    # ── Step A: the preflight must halt on each project-settings authority, naming the file and the key. ──
    $localSettings = Join-Path $target '.claude/settings.local.json'
    $planted = [ordered]@{
        'env.ANTHROPIC_API_KEY'       = @{ env = @{ ANTHROPIC_API_KEY = $canary } }
        'env.CLAUDE_CODE_USE_BEDROCK' = @{ env = @{ CLAUDE_CODE_USE_BEDROCK = '1' } }
        "'apiKeyHelper'"              = @{ apiKeyHelper = "echo $canary" }
        'env.ANTHROPIC_BASE_URL'      = @{ env = @{ ANTHROPIC_BASE_URL = $trapUrl } }
    }
    foreach ($key in $planted.Keys) {
        $planted[$key] | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM $localSettings
        $halt = Invoke-Guardrails @('run', $planDir, '--no-ui', '--fresh')
        if ($halt.Exit -eq 0) { $failures.Add("the preflight did NOT halt on a project $key") }
        elseif ($halt.Output -notmatch [regex]::Escape('settings.local.json') -or $halt.Output -notmatch [regex]::Escape($key)) {
            $failures.Add("the preflight halted on a project $key but did not name the file and the key")
        }
        else { Write-Host "  preflight halted on project $key, naming the file and key: ok" }
        Remove-Item -Force $localSettings
    }
    if (Test-Path (Join-Path $target 'hello.txt')) { $failures.Add('a halted preflight run still produced hello.txt') }
    Remove-Item -Force -ErrorAction SilentlyContinue $requestLog

    # ── Step B: the green run through the recording proxy. ──
    $ips = @()
    try { $ips = [System.Net.Dns]::GetHostAddresses('api.anthropic.com') | ForEach-Object { $_.ToString() } } catch { }
    $runJob = Start-ThreadJob -ScriptBlock {
        param($cli, $planDir)
        & dotnet run --project $cli -- run $planDir --no-ui --fresh 2>&1 | Out-String
        $LASTEXITCODE
    } -ArgumentList $cli, $planDir
    while ($runJob.State -eq 'Running') {
        if ($ips.Count -gt 0) {
            try {
                if ($IsWindows) {
                    Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $ips -contains $_.RemoteAddress } |
                        ForEach-Object { [void]$anthropicConnections.Add("$($_.RemoteAddress):$($_.RemotePort) pid $($_.OwningProcess)") }
                }
                else {
                    (& lsof -nP -iTCP 2>$null) | Where-Object { $line = $_; $ips | Where-Object { $line -match [regex]::Escape($_) } } |
                        ForEach-Object { [void]$anthropicConnections.Add($_.Trim()) }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    $runOutput = Receive-Job $runJob
    $runExit = [int]($runOutput | Select-Object -Last 1)
    Write-Host ($runOutput | Select-Object -SkipLast 1 | Out-String)
    if ($runExit -ne 0) { $failures.Add("guardrails run exited $runExit (expected 0)") }

    $hello = Join-Path $target 'hello.txt'
    if (-not (Test-Path $hello)) { $failures.Add('hello.txt was not written') }
    elseif ((Get-Content $hello -Raw).Trim() -ne 'hello from gateway') { $failures.Add("hello.txt holds '$((Get-Content $hello -Raw).Trim())'") }
}
finally {
    if ($proxy) { $proxy.Stop() }
    if ($trap) { $trap.Stop() }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}

# ─────────────────────────── assertions over every recorded request ───────────────────────────
$requests = @()
if (Test-Path $requestLog) { $requests = Get-Content $requestLog | ForEach-Object { $_ | ConvertFrom-Json } }
$trapped = @()
if (Test-Path $trapLog) { $trapped = Get-Content $trapLog | ForEach-Object { $_ | ConvertFrom-Json } }

if ($requests.Count -eq 0) { $failures.Add('the recording proxy saw no request at all') }
if ($trapped.Count -gt 0) { $failures.Add("$($trapped.Count) request(s) reached the redirect trap: $(($trapped | ForEach-Object { "$($_.method) $($_.path)" }) -join ', ')") }
foreach ($r in $requests) {
    if ($r.canaryInHeaders) { $failures.Add("a canary appeared in the headers of $($r.method) $($r.path)") }
    if ($r.hasXApiKey) { $failures.Add("$($r.method) $($r.path) carried an x-api-key header") }
    if ($r.authScheme -ne 'Bearer') { $failures.Add("$($r.method) $($r.path) carried no Bearer token (scheme '$($r.authScheme)')") }
    if ($r.model -and $r.model -match 'claude') { $failures.Add("$($r.method) $($r.path) named the Claude model '$($r.model)'") }
}

$tripwire = Get-FileHashOrNull $realSettings
if ($tripwire -ne $realSettingsHashBefore) { $failures.Add("the REAL $realSettings changed during the smoke") }

# ── where Claude Code's own state went (never the operator's ~/.claude, never the hostile directory) ──
$configDirs = @(Get-ChildItem -Directory -Recurse -Filter 'claude-config' -Path (Join-Path $planDir 'logs') -ErrorAction SilentlyContinue)
if (@($configDirs | Where-Object { Test-Path (Join-Path $_.FullName 'projects') }).Count -eq 0) {
    $failures.Add('no session transcripts landed under logs/<runId>/claude-config/projects')
}
if ((Test-Path $realProjectsDir) -and @(Get-ChildItem -Directory $realProjectsDir -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*$smokeToken*" }).Count -gt 0) {
    $failures.Add("a transcript folder for the smoke's target appeared under the REAL $realProjectsDir")
}
if (-not $realClaudeJsonExisted -and (Test-Path $realClaudeJson)) {
    $failures.Add("the REAL $realClaudeJson did not exist before the smoke and does now")
}
elseif ((Test-Path $realClaudeJson) -and (Select-String -Path $realClaudeJson -SimpleMatch -Quiet -Pattern $smokeToken)) {
    $failures.Add("the REAL $realClaudeJson now names the smoke's target ($smokeToken)")
}
foreach ($leak in 'projects', '.claude.json') {
    if (Test-Path (Join-Path $hostileUserDir $leak)) {
        $failures.Add("the hostile user config directory gained '$leak' — the inherited CLAUDE_CONFIG_DIR was used, not replaced")
    }
}

# ─────────────────────────── what the smoke observed (the design's unverified items) ───────────────────────────
Write-Host ''
Write-Host "Observed (claude $claudeVersion):"
Write-Host "  requests by path: $((($requests | Group-Object { "$($_.method) $($_.path -replace '\?.*$','')" } | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join '; '))"
Write-Host "  models named: $((($requests | Where-Object model | Select-Object -ExpandProperty model -Unique) -join ', '))"
Write-Host "  subagent requests (x-claude-code-agent-id): $(@($requests | Where-Object agentId).Count)"
Write-Host "  HEAD /api/hello seen: $(@($requests | Where-Object { $_.method -eq 'HEAD' -and $_.path -like '/api/hello*' }).Count -gt 0)"
Write-Host "  count_tokens seen: $(@($requests | Where-Object { $_.path -like '*count_tokens*' }).Count -gt 0)"
if (Test-Path "$requestLog.errors") {
    Write-Host "  recording-proxy forwarding errors (a client that hung up mid-stream lands here too): $requestLog.errors"
}
Write-Host "  connections to api.anthropic.com while the run was in flight (machine-wide; RECORDED, not asserted):"
if ($anthropicConnections.Count -eq 0) { Write-Host '    none observed' } else { $anthropicConnections | ForEach-Object { Write-Host "    $_" } }

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'CLAUDE GATEWAY LIVE SMOKE: FAILED' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Smoke folder kept for inspection: $root"
    exit 1
}

Write-Host ''
Write-Host 'CLAUDE GATEWAY LIVE SMOKE: GREEN' -ForegroundColor Green
if ($Keep) { Write-Host "Smoke folder kept: $root" }
else { Remove-Item -Recurse -Force $root }
exit 0
