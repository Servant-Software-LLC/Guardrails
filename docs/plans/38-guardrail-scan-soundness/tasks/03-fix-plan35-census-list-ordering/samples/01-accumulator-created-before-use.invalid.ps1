# INVALID: the exact shipped defect - the loop that uses $problems runs BEFORE the line that creates it,
# so on the branch that fires, $null.Add(...) throws and the script exits 0 without running the census.
foreach ($name in $mustExecute) {
    $problems.Add("[$name] NOT EXECUTED")
}
$problems = New-Object System.Collections.Generic.List[string]
exit 0
