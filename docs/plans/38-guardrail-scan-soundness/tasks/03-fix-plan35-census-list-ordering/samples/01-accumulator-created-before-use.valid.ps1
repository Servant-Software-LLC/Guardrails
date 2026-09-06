# VALID: the accumulator is created before its first use, so the failing branch reports its finding
# instead of aborting the script.
$problems = New-Object System.Collections.Generic.List[string]
foreach ($name in $mustExecute) {
    $problems.Add("[$name] NOT EXECUTED")
}
exit 0
