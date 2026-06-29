# scope: precondition  (SIMULATED — see the example/README.md and guardrails.json header)
#
# catches: the touched HTTP service's health route having SILENTLY regressed (un-mapped,
# renamed, or removed) before this plan starts. Acme.Payments.Api already exposes GET /health
# returning 200; this plan MODIFIES the API (adds correlation middleware). If the route is
# already broken before we start, the later "health still 200 after the middleware change"
# guardrail (05) would FALSE-ATTRIBUTE a pre-existing break to our middleware. The preflight
# fails fast and attributes correctly. This is the NON-TEST generalization of the baseline
# archetype (docs/plans/09-preflight-first-class.md §"Positive vs negative modeling": "an endpoint
# already responding") — the case that, if it could not be expressed by the doctrine
# no-op-ROOT-task pattern, would be the very Trigger criterion 1 for building Phase-2.
#
# POLARITY: positive — exit 0 when the route IS present/wired, exit 1 when it is missing.
#
# IMPORTANT — BLOCKER (e) (flaky-SPOF / "no process start"): the volume-control gate FORBIDS a
# pre-DAG preflight from STARTING A SERVER or hitting a live network endpoint — a flaky probe
# in a one-shot pre-DAG phase fails the WHOLE plan intermittently (maximal blast radius). So
# this "endpoint up" baseline is expressed as a CHEAP DETERMINISTIC BYTE CHECK on the already
# built/wired source (the /health route is mapped), NOT a live `Invoke-WebRequest`. A genuinely
# live endpoint probe belongs in a TASK's own guardrail, where a flake costs one task's retry
# budget — never in the pre-DAG phase. (docs/plans/09-preflight-first-class.md, BLOCKER (e) and the
# devil's-advocate counter on the "plane on the runway" endpoint case.)
$ErrorActionPreference = 'Stop'

# In a real plan this would deterministically assert the route is mapped in the (already built)
# API — e.g. grep the wired endpoint table / a generated route manifest for "/health":
#   Select-String -Path 'Acme.Payments.Api/Endpoints/*.cs' -Pattern 'MapGet\("/health"' -Quiet
# NOT a live HTTP call. SIMULATED here as a fixed pass.
Write-Output "Acme.Payments.Api GET /health route baseline: already wired (simulated, byte-level)"
exit 0
