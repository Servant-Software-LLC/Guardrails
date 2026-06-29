# Bucket A — shared positive baseline (the NON-TEST generalization). A no-op-root doctrine
# TASK with an ordinary scope:"local" guardrail; doctrine that SHIPS and VALIDATES today. Its
# guardrail runs in the first wave like any task's; every modifier in the Acme.Payments.Api
# area `dependsOn` this baseline (deduped one-per-area).
#
# catches: the touched HTTP service's health route having SILENTLY regressed (un-mapped,
# renamed, or removed) before this plan starts. Acme.Payments.Api already exposes GET /health
# returning 200; this plan MODIFIES the API (adds correlation middleware). If the route is
# already broken before we start, the later "health still 200 after the middleware change"
# guardrail (05) would FALSE-ATTRIBUTE a pre-existing break to our middleware. The baseline
# fails fast and attributes correctly. This is the NON-TEST generalization of the baseline
# archetype (docs/plans/09-preflight-first-class.md: "an endpoint already responding").
#
# POLARITY: positive — exit 0 when the route IS present/wired, exit 1 when it is missing.
#
# IMPORTANT — BLOCKER (e) (flaky-SPOF / "no process start"): the volume-control gate FORBIDS a
# baseline from STARTING A SERVER or hitting a live network endpoint — a flaky probe in a
# baseline would be an intermittent SPOF. So this "endpoint up" baseline is a DETERMINISTIC
# BYTE-CHECK on the already-built/wired source (the /health route is mapped), NOT a live
# `Invoke-WebRequest`. A genuinely live endpoint probe belongs in a TASK's own guardrail, where
# a flake costs one task's retry budget (e.g. 05's `01-health-still-200`). (See BLOCKER (e) and
# the devil's-advocate counter on the "plane on the runway" endpoint case.)
$ErrorActionPreference = 'Stop'

# In a real plan this would deterministically assert the route is mapped in the (already built)
# API — e.g. grep the wired endpoint table / a generated route manifest for "/health":
#   Select-String -Path 'Acme.Payments.Api/Endpoints/*.cs' -Pattern 'MapGet\("/health"' -Quiet
# NOT a live HTTP call. SIMULATED here as a fixed pass.
Write-Output "Acme.Payments.Api GET /health route baseline: already wired (simulated, byte-level)"
exit 0
