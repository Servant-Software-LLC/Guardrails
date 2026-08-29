# catches: THREE failures a compile and a grep both miss.
#          (1) A SamplesCommand type that exists, compiles, and is never REGISTERED - `guardrails samples
#              verify` is an unrecognised command from the real entry point and the whole verb is dead
#              code reachable only from a unit test (#120). Registration lives in CommandFactory.cs, which
#              IS in this task's write scope - and nothing but driving the REAL entry point can tell a
#              registered verb from an unregistered one: the type compiles, its unit tests pass, and
#              `guardrails samples verify` still does not exist.
#          (2) A verb that reports FAILURE on a corpus that is provably sound - a false-red that would
#              halt every future run once task 05 wires this into the preflight phase.
#          (3) A verb that reports SUCCESS on a corpus that is provably broken - a `samples verify` that
#              can never fail. That is #510's own defect wearing the verb's clothes: the feature exists
#              because a two-sided claim recorded in a folder was never executed, and a verifier that
#              always exits 0 restores exactly that state while looking like progress.
#
# This is the RUNTIME proxy for reachability and for the whole verify contract, so no source-shape grep
# is spent on either (#468 demotion order). Guardrail 01 covers only the property no runtime observation
# at this boundary can see: that ONE shared implementation is driven rather than an inline duplicate.
#
# Two-sided BY CONSTRUCTION (#302): probe 1 is the valid half, probe 2 the invalid half, and probe 2's
# corpus is probe 1's corpus with exactly one sample pair's two halves SWAPPED - so the only difference
# between "must exit 0" and "must exit non-zero" is a reversed polarity, and the swap is asserted to have
# actually taken before its result is trusted.
#
# Corpus baseline - MEASURED 2026-08-29, not assumed. Both committed pairs under
# docs/plans/24-plan-source-provenance/tasks/05-wire-recorder-into-breakdown/ were executed by hand
# against their own guardrails: 01-wiring-test-drives-the-real-seam valid->0 invalid->1, and
# 02-breakdown-command-wires-the-gate valid->0 invalid->1. That corpus is frozen history, so probe 1 has
# a genuinely green subject.
$ErrorActionPreference = 'Continue'
$cli  = 'src/Guardrails.Cli'
$good = 'docs/plans/24-plan-source-provenance'
$pairBase = '01-wiring-test-drives-the-real-seam'

# PRECONDITION - the only early exit: every probe below is meaningless without the corpus.
if (-not (Test-Path (Join-Path $good 'guardrails.json'))) {
    Write-Output "the known-good sample corpus '$good' is not present - this guardrail cannot verify anything. This is NOT a finding about the new verb: restore the corpus or repoint this guardrail at another plan folder whose committed pairs are known two-sided green."
    exit 1
}

# ACCUMULATE (#478): every probe runs, one distinguishable message per failure, dumped once at the end,
# so ONE attempt learns every gap instead of discovering them one retry at a time.
$failures = @()

# ── PROBE 1 — the verb is REACHABLE and GREEN on a corpus whose pairs are known two-sided sound ──────
Write-Output "=== probe 1: samples verify '$good' (expect exit 0) ==="
$out1 = & dotnet run --project $cli -v quiet -- samples verify $good 2>&1
$exit1 = $LASTEXITCODE
$out1 | ForEach-Object { Write-Output $_ }
if ($exit1 -ne 0) {
    $failures += "probe 1: 'dotnet run --project $cli -- samples verify $good' exited $exit1, expected 0. Either the verb is not REGISTERED on the real root command (System.CommandLine reports an unrecognised command and exits non-zero - registration lives in CommandFactory.BuildRootCommand, beside the other rootCommand.Add lines), the project did not build, or the verifier FALSE-REDS a corpus whose two pairs were measured two-sided green on 2026-08-29. Read the output above: an unrecognised-command message and a findings report look nothing alike."
}

# ── PROBE 2 — the SAME corpus with one pair's halves SWAPPED must be REJECTED, by name ───────────────
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "gr510-badpair-$PID"
$copy    = Join-Path $sandbox 'plan'
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue   # never inherit a previous attempt's sandbox
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
Copy-Item -Path $good -Destination $copy -Recurse -Force
Remove-Item (Join-Path $copy 'logs') -Recurse -Force -ErrorAction SilentlyContinue
# The copy sits at <sandbox>/plan, so the default workspace ".." resolves to <sandbox>, which exists -
# ProcessRunner needs a real working directory. Sample paths reach each guardrail absolute, so a
# guardrail's own repo-relative default subject is never consulted and the copy's location is irrelevant.

$sdir = Join-Path $copy "tasks/05-wire-recorder-into-breakdown/samples"
$v    = Join-Path $sdir "$pairBase.valid.cs"
$i    = Join-Path $sdir "$pairBase.invalid.cs"

if (-not (Test-Path $v) -or -not (Test-Path $i)) {
    $failures += "probe 2: could not stage the reversed-polarity corpus - '$pairBase.valid.cs'/'.invalid.cs' are not both under $sdir. This is NOT a finding about the new verb: the corpus moved, so repoint this guardrail."
}
else {
    $vc = Get-Content $v -Raw
    $ic = Get-Content $i -Raw
    Set-Content -Path $v -Value $ic -NoNewline    # THE MUTATION: swap the two halves, and nothing else
    Set-Content -Path $i -Value $vc -NoNewline

    # MUTATION PROOF - assert the swap actually took before trusting a negative result (#302). A swap
    # that silently no-ops turns probe 2 into a check that can never fail, which is the exact defect
    # class this whole task exists to detect. Refusing to trust an unverified mutation is the point.
    if (((Get-Content $v -Raw) -ceq $vc) -or ((Get-Content $i -Raw) -ceq $ic)) {
        $failures += "probe 2: the sandbox half-swap did not change the files, so the 'bad pair' corpus is identical to the good one and probe 2 would certify nothing. This is NOT a finding about the new verb."
    }
    else {
        Write-Output ""
        Write-Output "=== probe 2: samples verify a copy with '$pairBase' halves SWAPPED (expect NON-zero) ==="
        $out2 = & dotnet run --project $cli -v quiet -- samples verify $copy 2>&1
        $exit2 = $LASTEXITCODE
        $out2 | ForEach-Object { Write-Output $_ }
        $text2 = ($out2 | Out-String)

        if ($exit2 -eq 0) {
            $failures += "probe 2: 'samples verify' exited 0 on a corpus whose '$pairBase' pair has its two halves SWAPPED - the .valid half now fails its guardrail and the .invalid half now passes it. A verifier that greens a reversed pair CAN NEVER FAIL, which is the same 'a claim recorded in a folder, never executed' state #510 exists to end. Exit non-zero whenever any finding is reported."
        }
        # The three clauses below assert the SHAPE of a findings report, and a report only exists once
        # the verb is reachable. Gate them on probe 1: an UNREGISTERED verb prints a System.CommandLine
        # "Unrecognized command" block for probe 2 as well, which names none of these tokens - so
        # ungated they turn one registration failure into four findings, three of them aimed at the
        # report format, which is not the thing that is broken. That is the #455 misdiagnosis trap in
        # miniature: a confident wrong message pointing at the artifact the retry agent CAN edit.
        if ($exit1 -eq 0) {
            if ($text2 -cnotmatch [regex]::Escape("$pairBase.invalid.cs")) {
                $failures += "probe 2: the report never names the SAMPLE file '$pairBase.invalid.cs'. A finding that says a pair is wrong without saying WHICH half of WHICH pair is unactionable, and an operator who cannot act on a check deletes it. Print the sample path on the finding line."
            }
            if ($text2 -cnotmatch [regex]::Escape("$pairBase.ps1")) {
                $failures += "probe 2: the report never names the GUARDRAIL file '$pairBase.ps1'. The plan of record requires every mismatch to carry the guardrail path, the sample path and the observed exit code - the guardrail path is the one an operator has to open to fix the polarity."
            }
            # A DIGIT IS REQUIRED, not the word (measured 2026-08-29). The clause this replaces was a
            # bare `(?i)exit`, which the sentence "the guardrail exited unexpectedly" satisfies with no
            # number anywhere - and the plan of record asks for the OBSERVED EXIT CODE, which is the
            # single datum that tells a reversed pair apart from a guardrail that ignored the sample.
            # The connector list keeps the digit anchored to the word `exit` while admitting every house
            # phrasing. MEASURED against the repo's own operator-facing strings - `$"action exited {n}"`
            # (StatusCommand.cs:113, ActionRunner.cs:493), `$"exited {n}"` (AttemptArtifacts.cs:137),
            # `$"exit code {n}"` (GuardrailReVerifier.cs:84), `$"... exited {n}: {stderr}"`
            # (AiMergeResolver.cs:252) - plus `exited with 1`, `exit code was 0`, `exit status 1`,
            # `exited with code 1`, `exit code of 0`, `exit code is 0`: all ACCEPTED. And measured
            # REJECTED on the no-code reports it exists to catch, including path-laden ones whose text is
            # full of digits: "the guardrail exited", "the pair exited unexpectedly", "sample mismatch",
            # "...01-wiring-test-drives-the-real-seam.invalid.cs did not behave as expected",
            # "01-...ps1 / 01-...invalid.cs : reversed polarity", "2 findings across 2 pairs verified".
            if ($text2 -cnotmatch '(?i)\bexit(ed)?\b[ \t]*(?:(?:with|code|status|was|of|is|[:=])[ \t]*)*\d') {
                $failures += "probe 2: the report never carries an observed exit CODE - a number. Naming the word 'exit' is not enough: the plan of record requires every mismatch to report the code the guardrail actually returned, because that is the one datum distinguishing a REVERSED pair (the .invalid half exited 0 where non-zero was required) from a guardrail that IGNORED the sample entirely (both halves exited identically because neither reached the script). Those two are repaired differently. Print it the way the harness already does elsewhere - 'exited 1' or 'exit code 1'."
            }
        }
    }
}

Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== 'guardrails samples verify' reachability + polarity smoke: $($failures.Count) failure(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
