# catches: TWO defects in the same subject - this task's registration surface.
#          (1) A verb that does not COMPILE - a SamplesCommand.cs that does not type-check against the
#              System.CommandLine surface, or a CommandFactory registration line that does not bind. It
#              runs BEFORE the reachability smoke (03) so a compile failure reports as a compile failure,
#              rather than surfacing there as a `dotnet run` non-zero exit that is indistinguishable from
#              "the verb ran and rejected the corpus" (#155).
#          (2) A registration that reaches for the process-global console instead of the injected seam -
#              `SystemConsoleIo.Instance` written inside CommandFactory.cs or SamplesCommand.cs. The
#              action prompt forbids this in prose ("do NOT reach for SystemConsoleIo.Instance inside the
#              factory"), and a prose-only prohibition is free to ignore (#221). `BuildRootCommand` and
#              every sibling `Create` already RECEIVE an `IConsoleIo io`, so the token has no legitimate
#              use in either file. The prompt names the consequence as INVISIBLE - "silently, and only for
#              tests written later" - which is exactly the class of defect that needs a gate rather than a
#              sentence: the CLI works by hand, the build is green, guardrail 03's `dotnet run` smoke is
#              green, and the damage lands on a future test that cannot capture this verb's output.
#
# WHY THIS CLAUSE LIVES HERE and not in guardrail 01 or 03. Not 01: it carries a committed
# .valid/.invalid sample pair and binds its subject through $f/GR_SUBJECT, so a clause reading a
# DIFFERENT real path would be evaluated against the real tree even while SampleVerifier is driving that
# guardrail against a sample - the verdict would stop being a property of the sample, which is the
# "guardrail ignored the sample" hazard this very plan exists to detect. Not 03: it is the last and by far
# the most expensive guardrail (two `dotnet run`s), so a static clause there is paid for at the end. 02 is
# cheapest-first, has no sample pair to contaminate, and CommandFactory.cs is already its named subject.
#
# BASELINE - MEASURED 2026-08-29, not assumed. A forbidden-present clause is EXPECTED to be green before
# the task runs (#478 exempts bans from the zero-count rule), but it must not be RED on arrival, and the
# RAW form of this one IS:
#   src/Guardrails.Cli/CommandFactory.cs  RAW `SystemConsoleIo\s*\.\s*Instance`            = 1  <- #97/#98
#     ...it is line 9, inside an XML doc comment: `/// <see cref="SystemConsoleIo.Instance"/>`, an
#     accurate sentence about what Program.cs does. A comment-blind ban would false-red a CORRECT
#     CommandFactory.cs on EVERY attempt and whack-a-mole the agent into deleting true documentation.
#   src/Guardrails.Cli/CommandFactory.cs  comment-stripped                                  = 0
#   src/Guardrails.Cli/CommandFactory.cs  comment- AND string-stripped                      = 0
#   src/Guardrails.Cli/Commands/*.cs      RAW, every sibling command                        = 0
#     ...so banning it in SamplesCommand.cs too asks only for the form all 14 siblings already use.
#   The token's ONE legitimate production use is src/Guardrails.Cli/Program.cs:4 - the composition root,
#   deliberately outside this task's write scope and never scanned here.
#   Positive control for those zeroes (#500): the same invocation over CommandFactory.cs for a literal
#   known to be present, `BuildRootCommand`, returns 1 and `IConsoleIo io` returns 2 - so the search
#   reached the file rather than silently skipping it.
# ANCHORED ON THE USE, NOT THE MENTION (#76/#470): the ban is the dotted static access
# `SystemConsoleIo.Instance`, over comment- and string-stripped source. Naming the type in a comment (as
# CommandFactory.cs legitimately does today), in an operator-facing message, or in a `using` is not a use.
# No required-present clause in this file carries the banned literal, so the pair cannot collide (#470).
$ErrorActionPreference = 'Continue'

# ACCUMULATE (#478): one distinguishable message per clause, dumped once, so ONE attempt learns every gap.
$failures = @()

# ── CLAUSE 1 (cheap, structural) — neither of this task's files reaches for the process-global console ──
foreach ($src in @('src/Guardrails.Cli/CommandFactory.cs', 'src/Guardrails.Cli/Commands/SamplesCommand.cs')) {
    if (-not (Test-Path $src)) {
        # SamplesCommand.cs is CREATED by this task; its absence is guardrail 01's finding, not this one's.
        continue
    }
    $raw  = Get-Content $src -Raw                                # NEVER matched against
    $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
    $code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments (covers /// doc comments)
    $scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
    $scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
    $scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

    if ($scan -cmatch 'SystemConsoleIo\s*\.\s*Instance') {
        $failures += "$src reaches for SystemConsoleIo.Instance - the process-global console - in live code. It must not: BuildRootCommand already RECEIVES an 'IConsoleIo io' parameter (and every sibling command's Create(IConsoleIo io) does too), and that parameter exists precisely so a test can build a root command with a StringWriter-backed double and capture output per-invocation. Hard-coding the global defeats that for this verb alone, silently, and only for tests written later - so nothing else in this plan can catch it. Pass the 'io' you were given: 'rootCommand.Add(SamplesCommand.Create(io));'. (Naming the type in a comment or a message string is fine and is not what this checked - the scan strips comments and string literals first.)"
    }
}

# ── CLAUSE 2 (the cost stage, deliberately AFTER the dump) — the project compiles ───────────────────────
# Placed last so a clause-1 failure is reported without first paying for a build, and so a build failure
# is never reported alongside a finding the build itself would have hidden.
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

# src/Guardrails.Cli is the smallest scope that covers this task's whole diff (SamplesCommand.cs +
# CommandFactory.cs) and it builds Guardrails.Core transitively, so the SampleVerifier surface the verb binds
# against is compiled too (#176).
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet run in 03 - there the app's own report IS the evidence.
dotnet build src/Guardrails.Cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Cli does not build - SamplesCommand.cs or the CommandFactory registration is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
