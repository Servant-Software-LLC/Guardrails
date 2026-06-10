# Hello Guardrails — a tiny demonstration plan

Produce a greeting for a configurable recipient, validated end to end.

This plan is deliberately trivial. It exists so that:
- `guardrails run hello-guardrails` is the repo's end-to-end smoke test,
- the generated folder next to this file is the **golden reference** for what
  the `plan-breakdown` skill should produce, and
- newcomers can read one small, complete example of every moving part:
  a deterministic action, two prompt actions, state passing, deterministic
  guardrails, and one (deliberate) prompt-judge guardrail.

## The plan

1. **Create a greeting script.** Write a PowerShell script `out/greet.ps1` that
   takes a name parameter and prints `Hello, <name>!`.
   *Done when:* the script exists and runs cleanly for a sample name.

2. **Generate the greeting.** Using the recipient name configured in the shared
   state (`recipientName`), run the greeting script and save its output to
   `out/greeting.txt`. Record where the greeting was written for later steps.
   *Done when:* `out/greeting.txt` exists and contains a well-formed greeting.

3. **Quality-check the greeting.** Write a short report `out/report.md` that
   quotes the greeting and assesses whether it is friendly in tone.
   *Done when:* the report exists and its tone assessment is positive.
