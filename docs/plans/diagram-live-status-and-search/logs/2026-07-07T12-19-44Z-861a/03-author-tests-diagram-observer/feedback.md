# Task '03-author-tests-diagram-observer' — union re-verify failed

Task: Author failing tests + minimal stubs for a new OnTheFlyDiagramObserver decorator

The non-FF union merge produced bytes that FAILED the integration-guardrail re-verify, so the harness rolled the merge back (state.json restored, integration worktree reset) and settled this task `needs-human`. The merged bytes were discarded, but each failing integration guardrail's output was persisted next to this file:

## 02-composition-root-wiring-verified

Determining projects to restore...

Full output persisted to `union-reverify-02-composition-root-wiring-verified.stdout.log`.

This is typically a MERGE COLLISION (two colliding contributions combined into something that no longer builds/passes) — inspect the persisted output, fix the offending task(s), and re-run.
