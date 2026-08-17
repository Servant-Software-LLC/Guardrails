# catches: a UNION that merged cleanly per-file but does not hold as a whole. This wave is a
#          multi-branch topology - the journal-schema pair, the unavailability-classification pair,
#          the wiring chain (07-09), the per-tier-spend pair and the usage-tokens pair each run in
#          their own segment and meet for the first time at this gate. Several of them touch the
#          SAME types from DIFFERENT files (AttemptProvenance's new members are written by 02 and
#          consumed by 07, 11 and 13; AttemptJournaler is edited by 08 and 13), so a per-file merge
#          CANNOT conflict and a cross-file type mismatch survives it - visible only as a compile
#          error.
#
# This is the GR2028 union-soundness check (SSOT 3.3/14.3), and it is the clause that CREDITS this
# folder: its siblings 02 and 03 are FILTERED test runs, and a filtered run cannot fail when a merge
# dropped something outside its filter. A whole-solution build can, and it subsumes a
# conflict-marker scan (markers do not compile).
#
# NOTE: deliberately NOT -c Release, matching the plan-level build gate - running this plan from a
# Release-built local binary locks Guardrails.Core.dll and would false-RED here.
# LOCAL - no scope key (#165): a whole-solution build is a wave TERMINAL postcondition. At an
# intermediate union inside this wave, a task that has not run yet leaves types unproduced (the
# conformance suite compiles against AttemptProvenance.TierSource; the per-tier renderer against
# JournalTierSpend), so an integration-scoped solution build would fail there and roll back a
# correct wave.
dotnet build Guardrails.sln --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the merged wave-2 HEAD does not build. The branches of this wave each built in their own worktree and only meet here. MOST LIKELY CAUSE: a cross-file mismatch the per-file merge could not see - e.g. the AttemptProvenance/TierSource member names task 02 settled on differ from what the wiring (07), the per-tier renderer (11) or the usage-token journaling (13) reference. Read the compile errors above: they name the exact symbol."
    exit 1
}
exit 0
