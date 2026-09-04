# Context registry — Guardrails repo

Each line: `- [Topic-Path](relative/path/) — one-line description`

## Guardrails

- [Guardrails/Autonomous-Mode](Guardrails/Autonomous-Mode/) — #360/#361 autonomous-mode arc: waved dogfood plan (dial → classify-and-escalate → review-gate), auto-breakdown-by-default (#376), preview.44, and the dogfood findings (#253/#371/#374/#375)
- [Guardrails/Build](Guardrails/Build/) — Full M1–M7 harness build, skill authoring, dogfood plan, and NuGet release pipeline
- [Guardrails/Distribution](Guardrails/Distribution/) — Native self-contained binary distribution: GitHub Release binaries, Homebrew tap, SDK-free installers, and hands-off release automation (auto-formula-bump PR, gated macOS codesign/notarize)
- [Guardrails/Event-Stream-Webhooks](Guardrails/Event-Stream-Webhooks/) — #585/#560/#595 run event stream arc (events.jsonl, GET /events, --on-event webhooks, attach) + the v1.17.0 stabilization pass (#564/#566/#596/#597/#603)
- [Guardrails/Model-Tiering-Epic](Guardrails/Model-Tiering-Epic/) — Model-tiering & provider-management epic (#201): sub-issue breakdown, sequential wave plans, diagram.html live-status/search features, and OSS-extraction verdict
- [Guardrails/Preflights-Two-Scope](Guardrails/Preflights-Two-Scope/) — Two-scope preflights/guardrails feature plan-breakdown and execution on feat/preflights-two-scope
- [Guardrails/Verifier-Infrastructure](Guardrails/Verifier-Infrastructure/) — Guardrail-strength / verifier arc: the #374 salvage-advice-provisioning plan (COMPLETE 2026-08-12, 14/14 green, merged — the #193 orphaned-golden halt, the re-homed golden coverage, and harness issues #447/#448/#449 it exposed); pilot-seat model-provenance dogfood (#349), specialized-guardrail-library (#350), verifier-store handoff enabler (#351)

## Uber-Report

- [Uber-Report/Spine-Standardization](Uber-Report/Spine-Standardization/) — bootstrap-uber-report skill, .tasks.json→.findings.json rename, and cross-repo honesty-gate re-base
