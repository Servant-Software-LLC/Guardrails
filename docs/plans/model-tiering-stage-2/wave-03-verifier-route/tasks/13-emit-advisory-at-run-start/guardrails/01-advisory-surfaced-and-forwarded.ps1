# catches: the advisory being raised into a void. Two ways, and the second is invisible to any test
#          that talks to ConsoleRunObserver directly:
#
#   (a) the event is declared but the Scheduler never raises it, or VerifierAdvisory is never called
#       (a second, divergent "is this judge weak" rule - what D22a forbids);
#   (b) THE DECORATORS DO NOT FORWARD IT. The new IRunObserver method needs a defaulted empty body to
#       keep the addition non-breaking, and OnTheFlyDiagramObserver / OnTheFlyLogSiteObserver are
#       decorators that forward each call explicitly to an _inner observer. A method they do not
#       forward resolves to the INTERFACE DEFAULT - the empty body - so the advisory is silently
#       swallowed in exactly the UI mode most operators run, while everything else stays green.
#
# The check DERIVES the event name from the interface rather than hard-coding one, so the author is
# free to name it well and the guardrail still knows what to look for in the other four files.
$ErrorActionPreference = 'Continue'
$iface      = 'src/Guardrails.Core/Execution/IRunObserver.cs'
$scheduler  = 'src/Guardrails.Core/Execution/Scheduler.cs'
$console    = 'src/Guardrails.Cli/ConsoleRunObserver.cs'
$decorators = @('src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs', 'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs')
$failures   = @()

foreach ($f in (@($iface, $scheduler, $console) + $decorators)) {
    if (-not (Test-Path $f)) { Write-Output "$f does not exist"; exit 1 }
}

function Get-Code([string]$Path) {
    $r = Get-Content -Raw $Path
    $c = [regex]::Replace($r, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($c, '(?m)//.*$', '')
}

$schedCode = Get-Code $scheduler
if ($schedCode -cnotmatch 'VerifierAdvisory') {
    $failures += 'Scheduler never names VerifierAdvisory in real code - the run-start walk must ask the ONE owner of the rule (tasks 09/10) what is and is not an advisory condition. A second implementation of it here is the divergence D22a forbids.'
}

$ifaceCode = Get-Code $iface
$ifaceMethods = [regex]::Matches($ifaceCode, '(?m)^\s{4}(?:void|Task)\s+([A-Z]\w*)\s*\(') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$advisoryEvents = @($ifaceMethods | Where-Object { $_ -cmatch 'Advisor|Verifier' })

if ($advisoryEvents.Count -lt 1) {
    $failures += "IRunObserver declares no advisory event. Its declared methods are: $($ifaceMethods -join ', '). Add one whose name contains Advisory (or Verifier), following ParallelismClampedNoProvider - the existing precedent for a one-off run-level diagnostic, defaulted empty body included."
} else {
    foreach ($evt in $advisoryEvents) {
        $esc = [regex]::Escape($evt)
        if ($schedCode -cnotmatch ('_observer\s*\.\s*' + $esc + '\s*\(')) {
            $failures += "IRunObserver declares $evt but Scheduler never RAISES it (no _observer.$evt( in real code). A declared event nothing raises is the run-start surface existing on paper only."
        }
        foreach ($d in $decorators) {
            $dName = [System.IO.Path]::GetFileName($d)
            if ((Get-Code $d) -cnotmatch ('\b' + $esc + '\s*\(')) {
                $failures += "$dName does not forward $evt. It is a DECORATOR: an IRunObserver method it does not explicitly forward falls through to the interface DEFAULT (an empty body), so the advisory vanishes in this UI mode while every other check here stays green. Add the one-line forward beside its ParallelismClampedNoProvider forward."
            }
        }
    }
    $consoleCode = Get-Code $console
    $renders = @($advisoryEvents | Where-Object { $consoleCode -cmatch ('\b' + [regex]::Escape($_) + '\s*\(') }).Count
    if ($renders -lt 1) {
        $failures += "ConsoleRunObserver implements none of the advisory event(s) ($($advisoryEvents -join ', ')) - the event is raised and forwarded, but nothing prints it, so the operator this task exists for still learns nothing before the DAG runs."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== run-start verifier advisory: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "All four pieces exist already, and ParallelismClampedNoProvider is the worked example of every one: declared with a defaulted body on IRunObserver, raised by Scheduler near the top of the run, rendered by ConsoleRunObserver, and forwarded one line each by BOTH Ui/OnTheFly*Observer decorators. Grep that name and follow it."
    exit 1
}
exit 0
