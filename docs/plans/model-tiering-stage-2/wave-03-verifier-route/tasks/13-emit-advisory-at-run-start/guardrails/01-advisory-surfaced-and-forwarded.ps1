# catches: the advisory being raised into a void. The observer chain has FIVE participants, and a
#          declaration-shaped stub in any one of them looks identical to a working wire.
#
#   (a) the Scheduler never CONSULTS VerifierAdvisory (a second, divergent "is this judge weak" rule -
#       what D22a forbids), or never raises the event;
#   (b) A DECORATOR DECLARES THE METHOD BUT DOES NOT FORWARD IT. OnTheFlyDiagramObserver and
#       OnTheFlyLogSiteObserver are transparent decorators over an _inner observer. A method they do
#       not forward resolves to the interface DEFAULT (an empty body) and the advisory is swallowed.
#       An earlier form of this clause keyed on the method NAME - which a declaration matches just as
#       well as a forward, so an empty-body override PASSED the very check written to catch it
#       (measured, exit 0). It now keys on the forward itself.
#   (c) THE LEAF NEVER RENDERS IT. There are TWO leaves and the chain differs by mode (see
#       RunCommand.cs's composition root): live/default is Diagram -> LogSite -> LiveRunObserver;
#       --no-ui is Diagram -> LogSite -> ConsoleRunObserver. ConsoleRunObserver is NOT in the live
#       chain at all, so rendering only there prints nothing in the mode most operators run.
#
# DO NOT copy ParallelismClampedNoProvider as the worked example for the LEAF. It is declared, raised,
# and forwarded by both decorators - and rendered by NEITHER leaf. It is a dead event, and an earlier
# draft of this guardrail told the author to follow it. For the leaf render copy LiveRunObserver's
# PlanHashMismatch / DecisionRecorded instead: a run-level message under the _gate lock.
$ErrorActionPreference = 'Continue'
$iface      = 'src/Guardrails.Core/Execution/IRunObserver.cs'
$scheduler  = 'src/Guardrails.Core/Execution/Scheduler.cs'
$decorators = @('src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs', 'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs')
$leaves     = @('src/Guardrails.Cli/ConsoleRunObserver.cs', 'src/Guardrails.Cli/Ui/LiveRunObserver.cs')
$failures   = @()

foreach ($f in (@($iface, $scheduler) + $decorators + $leaves)) {
    if (-not (Test-Path $f)) { Write-Output "$f does not exist"; exit 1 }
}

function Get-Code([string]$Path) {
    $r = Get-Content -Raw $Path
    $c = [regex]::Replace($r, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($c, '(?m)//.*$', '')
}

# --- (a) the rule owner is CONSULTED, not name-dropped -------------------------------------------
# A DOTTED CALL (#76), not a bare token. The bare-token form was satisfied by the EVENT NAME itself:
# an event called VerifierAdvisoryFound contains the string VerifierAdvisory, so declaring the event
# silently self-satisfied the clause meant to force a call into the rule owner.
$schedCode = Get-Code $scheduler
if ($schedCode -cnotmatch 'VerifierAdvisory\s*\.') {
    $failures += 'Scheduler never CALLS into VerifierAdvisory (no dotted VerifierAdvisory. call in real code) - the run-start walk must ask the ONE owner of the rule (tasks 09/10) what is and is not an advisory condition. Naming an EVENT VerifierAdvisorySomething does not count. A second implementation of the rule here is the divergence D22a forbids.'
}

# --- derive the event name from the interface ----------------------------------------------------
# Tolerant of an explicit access modifier, any return shape, and any indentation: a default interface
# member written `public void X(...)` is legal and common, and a stricter form false-RED with the
# message "IRunObserver declares no advisory event" against an author who had just added one.
$ifaceCode = Get-Code $iface
$ifaceMethods = [regex]::Matches($ifaceCode, '(?m)^\s+(?:public\s+)?(?:void|Task|ValueTask)(?:<[^>]+>)?\s+([A-Z]\w*)\s*\(') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$advisoryEvents = @($ifaceMethods | Where-Object { $_ -cmatch 'Advisor|Verifier' })

if ($advisoryEvents.Count -lt 1) {
    $failures += "IRunObserver declares no advisory event. Its declared methods are: $($ifaceMethods -join ', '). Add one whose name contains Advisory or Verifier. Declare its parameters with PUBLIC or primitive types - IRunObserver is public and Guardrails.Cli has no InternalsVisibleTo into Guardrails.Core, so an internal finding type on this signature is CS0051 inconsistent accessibility, and that type's own file is out of your scope."
} else {
    foreach ($evt in $advisoryEvents) {
        $esc = [regex]::Escape($evt)
        if ($schedCode -cnotmatch ('_observer\s*\.\s*' + $esc + '\s*\(')) {
            $failures += "IRunObserver declares $evt but Scheduler never RAISES it (no _observer.$evt( in real code). A declared event nothing raises is the run-start surface existing on paper only."
        }
        foreach ($d in $decorators) {
            $dName = [System.IO.Path]::GetFileName($d)
            if ((Get-Code $d) -cnotmatch ('_inner\s*\.\s*' + $esc + '\s*\(')) {
                $failures += "$dName never FORWARDS $evt to _inner. It is a transparent decorator: declaring the method with an empty body overrides the interface default and swallows the event just as silently as omitting it, while looking correct in review. Add the one-line forward beside its other forwards."
            }
        }
        foreach ($leaf in $leaves) {
            $lName = [System.IO.Path]::GetFileName($leaf)
            $lCode = Get-Code $leaf
            if ($lCode -cnotmatch ('\b' + $esc + '\s*\(')) {
                $failures += "$lName does not implement $evt. It is a LEAF of the observer chain, and the two leaves serve DIFFERENT MODES: LiveRunObserver is the default live UI, ConsoleRunObserver is --no-ui. Implementing only one means the advisory prints in one mode and vanishes in the other."
            } elseif ($lCode -cnotmatch ('(?s)' + $esc + '\s*\([^)]*\)[^{;]*[{=][^}]{0,600}?(MarkupLine|WriteLine|Write\s*\(|_output|AnsiConsole)')) {
                $failures += "$lName declares $evt but EMITS NOTHING in it - a body with no write is the same silence as no method at all. In LiveRunObserver follow PlanHashMismatch/DecisionRecorded (AnsiConsole.MarkupLine under the _gate lock); in ConsoleRunObserver follow its _output.WriteLine calls."
            }
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== run-start verifier advisory: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The chain, from RunCommand.cs's composition root:  live (default): OnTheFlyDiagramObserver -> OnTheFlyLogSiteObserver -> LiveRunObserver ;  --no-ui: OnTheFlyDiagramObserver -> OnTheFlyLogSiteObserver -> ConsoleRunObserver. The event must be DECLARED on IRunObserver, RAISED by Scheduler, FORWARDED by both decorators, and RENDERED by both leaves. Five files, and a gap in any one of them is silent."
    exit 1
}
exit 0
