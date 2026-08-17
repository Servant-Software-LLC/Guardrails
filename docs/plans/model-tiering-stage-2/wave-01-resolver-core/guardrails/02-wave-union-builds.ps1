# catches: a UNION that merged cleanly per-file but does not hold as a whole. Wave 1 became a
#          PARALLEL topology when the tier-provenance pair (05/06) was added alongside the resolver
#          chain (01-04): two independent leaves, merged together for the first time at this gate.
#          The two branches touch disjoint files - TierResolver.cs/TierResolution.cs on one side,
#          PlanLoader.cs/ActionDefinition.cs on the other - and a per-file merge therefore CANNOT
#          conflict, which is exactly why it needs proving rather than assuming: a dropped
#          contribution or a cross-file type mismatch (the resolver reading a TierOrigin member the
#          loader branch renamed) survives the merge and shows up only as a compile error.
#
# This is the GR2028 union-soundness check (SSOT 3.3/14.3). The sibling 01-resolver-core-complete
# gate is FILTERED to this wave's three test classes and so does not satisfy GR2028 by itself - a
# filtered run cannot fail when a merge dropped something outside the filter. A whole-solution build
# can, and it subsumes a conflict-marker scan (markers do not compile).
#
# NOTE: deliberately NOT -c Release, matching the plan-level build gate - running this plan from a
# Release-built local binary locks Guardrails.Core.dll and would false-RED here.
# LOCAL - no scope key (#165): a whole-solution build is a wave TERMINAL postcondition. At an
# intermediate union inside the wave, a task that has not run yet leaves types unproduced, so an
# integration-scoped solution build would fail there and roll back a correct wave.
dotnet build Guardrails.sln --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the merged wave-1 HEAD does not build. The two parallel halves of this wave (the resolver chain 01-04, and the tier-provenance pair 05/06) each built in their own worktree and only meet here. MOST LIKELY CAUSE: a cross-file mismatch the per-file merge could not see - e.g. the TierOrigin enum member names the provenance pair settled on differ from what another file references. Read the compile errors above: they name the exact symbol."
    exit 1
}
exit 0
