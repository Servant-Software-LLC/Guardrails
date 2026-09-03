# catches: an extraction that declared the seam but left one of the two construction branches inline -
#          the half-fix this task exists to prevent. RunCommand builds the observer chain in TWO places
#          (the live-UI branch and the --no-ui branch); a later task inserting the projections into the
#          extracted method would then wire ONE branch and leave the other silently unwired.
# Measured baselines (#478), against src/Guardrails.Cli/Commands/RunCommand.cs BEFORE this task:
#   'BuildObserverChain' declaration : 0  (the method does not exist yet)
#   'BuildObserverChain(' call sites : 0
# Both are required-present clauses and both correctly measure 0 on the starting tree.
# SAMPLE CONTRACT (#559): SampleVerifier runs this guardrail against each committed samples/ half,
# supplying the sample BOTH as the first positional argument AND as GR_SUBJECT (SampleVerifier.cs:67,336),
# with cwd = the workspace. A guardrail that hardcodes its subject scans the REAL tree in both halves, so
# both exit 1, the pair certifies nothing, and the pre-DAG samples gate halts the run at exit 2 before
# task 01 launches (PlanPreflightPhase.cs:220). Accept either binding; fall back to the real subject.
param([string]$Subject)
$ErrorActionPreference = 'Stop'
$file = if ($Subject) { $Subject } elseif ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Cli/Commands/RunCommand.cs' }
$failures = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath $file)) {
    Write-Output "PRECONDITION: $file not found - every clause below would be meaningless."
    exit 1
}
$raw = Get-Content -LiteralPath $file -Raw
# Strip comments AND string literals before scanning: a mention in either is not a use (#470/#97/#98).
$code = [regex]::Replace($raw, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$code = [regex]::Replace($code, '"(?:[^"\\]|\\.)*"', '""')

# The DECLARATION - a structural C# method declaration, not a bare name grep (dotnet.md 3).
if ($code -notmatch '(?m)^\s*public\b[^\r\n]*\bBuildObserverChain\s*\(') {
    $failures.Add("no PUBLIC BuildObserverChain method DECLARATION in $file - the composition seam was not extracted, or was declared internal/private. Task 14's tests live in Guardrails.Integration.Tests, a DIFFERENT assembly, and Guardrails.Cli ships no InternalsVisibleTo - so a non-public seam is uncompilable from the tests that must drive it (a bare name mention does not count; the scan ignores comments and string literals)")
}

# BOTH branches must CALL it. Count invocations that are not the declaration itself.
$calls = @([regex]::Matches($code, '\bBuildObserverChain\s*\(') | Where-Object { $_ })
$declMatches = @([regex]::Matches($code, '(?m)^\s*public\b[^\r\n]*\bBuildObserverChain\s*\(') | Where-Object { $_ })
$callCount = $calls.Count - $declMatches.Count
if ($callCount -lt 2) {
    $failures.Add("BuildObserverChain is called $callCount time(s) in $file - both the live-UI branch and the --no-ui branch must call it, or a later wiring task will wire one branch and silently leave the other unwired")
}

# The inline construction must be GONE from the branches - otherwise the seam exists beside the
# duplicate it was meant to replace, and the wiring task can still land in only one of them.
$inline = @([regex]::Matches($code, '\bnew\s+OnTheFlyDiagramObserver\s*\(') | Where-Object { $_ })
if ($inline.Count -gt 1) {
    $failures.Add("found $($inline.Count) 'new OnTheFlyDiagramObserver(' construction sites in $file - the extraction must leave exactly ONE, inside BuildObserverChain; more than one means a branch was left inline")
}

if ($failures.Count -gt 0) {
    Write-Output "=== Observer-composition seam not extracted ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
