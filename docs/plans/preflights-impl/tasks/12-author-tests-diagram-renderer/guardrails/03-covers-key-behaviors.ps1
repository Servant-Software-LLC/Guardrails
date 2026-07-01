# catches: a renderer golden file that satisfies build + tests-fail-on-current-code with one trivial
#          assertion while skipping the absence assertions or the plan-level-hash staleness. Lower-bound
#          presence grep, scoped to the one file this task owns.
$f = Get-Content "tests/Guardrails.Core.Tests/ContainerDiagramTests.cs" -Raw
if ($f -notmatch '(?i)subgraph') {
    Write-Output "ContainerDiagramTests.cs does not assert container subgraphs - the container model is untested"
    exit 1
}
if ($f -notmatch '(?i)anchor') {
    Write-Output "ContainerDiagramTests.cs does not assert invisible anchors / anchor->anchor edges"
    exit 1
}
if ($f -notmatch 'done_') {
    Write-Output "ContainerDiagramTests.cs does not assert the ABSENCE of done_ nodes (a required golden absence assertion)"
    exit 1
}
if ($f -notmatch '(?i)stale|source-sha256|sha256') {
    Write-Output "ContainerDiagramTests.cs does not cover the graph --check staleness on a plan-level check edit (source-sha256 must fold plan-level checks)"
    exit 1
}
exit 0
