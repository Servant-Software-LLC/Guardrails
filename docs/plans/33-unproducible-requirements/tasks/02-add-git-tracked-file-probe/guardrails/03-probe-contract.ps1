# catches: a probe that answers "untracked" when it merely could not ask - the one semantic error that
#          turns GR2060 (an ERROR-severity check) into a false red on a correct plan. IScriptSyntaxProbe
#          documents this as "absence of an entry never means valid"; the git probe needs the same
#          contract, and a Null implementation to be the no-git default. A build cannot see any of it.
#
# SOURCE-SHAPE, and why no test carries it HERE: the not-known BEHAVIOUR is a runtime property and it IS
#          carried by a test - in task 3, whose suite includes GR2060's git-unavailable conservatism
#          case. That test cannot exist yet: GR2060 does not exist until task 4. What this guardrail
#          asserts is the two STRUCTURAL facts a downstream test cannot retroactively supply - that a
#          Null implementation exists to be the default, and that the contract is written down where the
#          next implementer reads it. The demotion order (#468) is satisfied by the pair, not by this
#          file alone.
#
# TWO SUBJECTS, deliberately: declarations are matched against COMMENT-BLANKED source, so a doc comment
#          naming the type cannot satisfy them; the documented contract is matched against RAW text,
#          because a doc comment is exactly where that clause must live (#97/#98 runs the other way for
#          a documentation requirement).
#
# Required-present baseline (#478), measured at author time: all three clauses occur 0 times in
#          IGitTrackedFileProbe.cs - the file does not exist yet. Expected 0.
$ErrorActionPreference = 'Continue'

# GR_SUBJECT arrives ABSOLUTE from the sample verifier (#559) - use it as given, never Join-Path it.
$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs' }

if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist. Task 2 must declare IGitTrackedFileProbe and its Null implementation there; every clause below would crash without it.')
    exit 1
}

$raw  = Get-Content -LiteralPath $subject -Raw
$scan = [regex]::Replace($raw, '(?m)^\s*///?.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$failures = New-Object System.Collections.Generic.List[string]

# Structural DECLARATION regexes, not bare name greps (stacks/dotnet.md section 3).
if ($scan -notmatch '(?m)public\s+interface\s+IGitTrackedFileProbe\b') {
    $failures.Add('NO INTERFACE DECLARATION: ' + $subject + ' does not declare `public interface IGitTrackedFileProbe`. GR2060 takes this as a constructor dependency in task 4; a concrete class alone is not the seam.')
}
if ($scan -notmatch '(?m)class\s+NullGitTrackedFileProbe\s*:\s*IGitTrackedFileProbe\b') {
    $failures.Add('NO NULL IMPLEMENTATION: ' + $subject + ' does not declare `class NullGitTrackedFileProbe : IGitTrackedFileProbe`. It is the no-git default, and it lives beside its interface exactly as NullScriptSyntaxProbe does. Without it, a validator constructed off a machine with git has nothing safe to fall back to.')
}

# The documented contract, matched on RAW text because a doc comment is where it belongs.
if ($raw -notmatch '(?i)never\s+(?:be\s+)?(?:read|taken|treated)\s+as|not[- ]known|silence\s+is\s+not') {
    $failures.Add('THE SILENCE-IS-NOT-PROOF CONTRACT IS NOT DOCUMENTED in ' + $subject + '. State plainly that a not-known answer must NEVER be read as "untracked". GR2060 is ERROR severity: a probe that reports untracked when git was simply unavailable makes it fire on a correct plan, and an ERROR blocks the run and the resume.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The git-tracked-file probe contract is incomplete (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output ('IGitTrackedFileProbe declares its interface, its Null default, and the not-known contract in ' + $subject + '.')
exit 0
