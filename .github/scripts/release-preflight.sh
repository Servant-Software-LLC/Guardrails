#!/usr/bin/env bash
#
# Release mechanics for ServantSoftware.Guardrails — the parts prose cannot make reliable.
#
# The cut-release SKILL.md owns the JUDGEMENT (which version, is the work actually done, what
# goes in the notes). This script owns the MECHANICS, because the avoidable errors in the
# v1.21.0 cut were all in hand-written shell, not in the procedure:
#
#   * `git show "origin/master:.github/..."` silently returned NOTHING — MSYS rewrote the
#     `rev:path` colon into a Windows path, and a `2>/dev/null` hid the error. Twice.
#   * `dotnet test ... | tail -40` reported "exit code 0" for a run with a FAILING test,
#     because the exit code observed was tail's, not the test run's.
#   * a `--jq` filter yielded an empty run-id, which built a malformed URL and 404'd.
#   * the 12-combination skip-count check was re-typed by hand five times.
#   * the expected test-count baseline lived only in one agent's memory across five PRs.
#
# TRAP — never pipe `dotnet test` (or any gate) into head/tail/grep and then read `$?`. That
# is the LAST command's status, not the gate's. Use a temp file, or `${PIPESTATUS[0]}`:
#     dotnet test Guardrails.sln -c Release > "$log" 2>&1; rc=$?      # correct
#     dotnet test Guardrails.sln -c Release | tail -40; rc=$?          # WRONG — that is tail's
#
# Usage:
#   .github/scripts/release-preflight.sh preflight <sha>      # safe-to-tag checks; non-zero if not
#   .github/scripts/release-preflight.sh ci <sha>             # poll the CI run for <sha> to completion
#   .github/scripts/release-preflight.sh counts <run-id>      # per-assembly test counts, every test job
#   .github/scripts/release-preflight.sh baseline <run-id>    # record those counts as the baseline
#   .github/scripts/release-preflight.sh verify <run-id>      # diff a run's counts against the baseline
#   .github/scripts/release-preflight.sh published <version>  # confirm the version is live on NuGet.org
#
# Env: REPO_SLUG (default Servant-Software-LLC/Guardrails), REPO_DIR (default: the repo root).
# Requires: git, gh (authenticated), curl. Does NOT require a `jq` binary — only gh's built-in
# `--jq`, because a standalone jq is not present on the maintainer's Git Bash box.

set -Eeuo pipefail

# TRAP 1: MSYS path conversion mangles `rev:path` arguments on Git Bash for Windows, turning
# `origin/master:file` into a Windows path and yielding EMPTY output. Exported here so every
# git invocation below is covered, and so a caller that sources this file inherits it.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

REPO_SLUG="${REPO_SLUG:-Servant-Software-LLC/Guardrails}"
REPO_DIR="${REPO_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
BASELINE_FILE="${REPO_DIR}/.github/release-baseline.txt"

# Name the line that failed instead of dying mutely. `-E` above makes the ERR trap survive
# into functions and subshells, so a failure inside a helper is still reported.
trap 's=$?; printf "\n  ABORT: %s failed at line %s (exit %s)\n" "${BASH_SOURCE[0]##*/}" "$LINENO" "$s" >&2' ERR

die()  { printf '\n  FAIL: %s\n\n' "$*" >&2; exit 1; }
ok()   { printf '  ok: %s\n' "$*"; }
warn() { printf '  WARNING: %s\n' "$*" >&2; }

usage() {
  # Range to the first NON-comment line, so the block cannot truncate mid-sentence as the header
  # grows. A hand-counted line range is the same brittleness this script exists to remove.
  sed -n '/^# Usage:/,/^[^#]/p' "${BASH_SOURCE[0]}" | sed '/^[^#]/d; s/^# \{0,1\}//'
  exit 2
}

# ── The #747 guard ──────────────────────────────────────────────────────────────────────────
# `PromptRunnerReliabilityTests.Transient_PausesAndResumes_WithoutConsumingRetryBudget` feeds a
# transient carrying `resetHint: "11:20am"` and asserts the waited total EQUALS
# `DefaultProbeInterval + BaseDelay*2` (30m + 4s = 30:04).
#
# TransientBackoff.NextDelay takes `min(resetInstant, now + probeInterval)`. While 11:20 is more
# than the 30-minute probe interval away, the interval wins the min() and the equality holds.
# Once wall-clock reaches 11:20 − 30m = 10:50, `untilReset` becomes the smaller value and wins,
# so the assert fails with an `Actual` slightly BELOW 30:04 — deterministically, for the rest of
# the window. Issue #747 recorded exactly that: Expected 00:30:04, Actual 00:29:03.37, which
# back-solves to a wall-clock of 10:51. Not load, not flake: arithmetic.
#
# WHICH CLOCK. ProviderResetHint.Resolve builds the instant with `now.Offset` — the LOCAL zone of
# whatever machine runs the test — and rolls to the next day if it is not strictly ahead. This
# guard reads `date -u` because the release's tests run on GitHub runners, which are UTC. A LOCAL
# `dotnet test` on a non-UTC box fails in that box's own local window instead; the guard reports
# both clocks so neither case is a surprise. Do not "fix" this to local time.
#
# The window is DERIVED from the two constants below, not hand-typed — re-typing it is how the
# draft of this script ended up with 10:40 (a 40-minute probe interval that does not exist).
# Overridable ONLY so the firing path can be exercised on demand — a guard whose trigger has never
# actually run is indistinguishable from one that is broken, which is the same invisible-evidence-loss
# shape as a silently-skipped test. To watch it fire:
#     RESET_HHMM=$(date -u -d '+10 minutes' +%H%M) .github/scripts/release-preflight.sh preflight <sha>
RESET_HHMM="${RESET_HHMM:-1120}"                  # the resetHint hardcoded in the test fixture
PROBE_INTERVAL_MIN="${PROBE_INTERVAL_MIN:-30}"    # TransientBackoff.DefaultProbeInterval

guard_747_window() {
  local state reset_min open_min now_hm now_min
  # No `2>/dev/null`: a guard that goes quiet when it cannot check is the silent-failure shape
  # this whole script exists to remove.
  if ! state="$(gh issue view 747 --repo "$REPO_SLUG" --json state --jq '.state' 2>&1)"; then
    warn "could not read issue #747 (${state}) — the timing guard did NOT run. Check it by hand."
    return 0
  fi
  if [ "$state" != "OPEN" ]; then
    ok "#747 is ${state} — timing guard no longer applies"
    return 0
  fi

  # Minutes since midnight, NOT raw HHMM: HHMM is not a uniform number line (1059 → 1100 skips
  # 40), so range tests on it are only accidentally right. `10#` forces base 10 — without it a
  # leading-zero hour like 08 is an invalid octal literal and the arithmetic aborts.
  reset_min=$(( 10#${RESET_HHMM:0:2} * 60 + 10#${RESET_HHMM:2:2} ))
  open_min=$(( reset_min - PROBE_INTERVAL_MIN ))
  now_hm="$(date -u +%H%M)"
  now_min=$(( 10#${now_hm:0:2} * 60 + 10#${now_hm:2:2} ))

  if [ "$now_min" -ge "$open_min" ] && [ "$now_min" -lt "$reset_min" ]; then
    die "#747 is OPEN and it is $(date -u +%H:%M)Z — inside the \
[$(printf '%02d:%02d' $((open_min/60)) $((open_min%60))), $(printf '%02d:%02d' $((reset_min/60)) $((reset_min%60)))) UTC window in which
        PromptRunnerReliabilityTests.Transient_PausesAndResumes_WithoutConsumingRetryBudget fails
        DETERMINISTICALLY. A release tagged now goes red for a reason unrelated to the code, and
        burns a version number that cannot be reused. Wait until after \
$(printf '%02d:%02d' $((reset_min/60)) $((reset_min%60)))Z, or fix #747."
  fi
  ok "outside #747's failure window (now $(date -u +%H:%M)Z, local $(date +%H:%M\ %Z))"
}

# ── preflight ───────────────────────────────────────────────────────────────────────────────
cmd_preflight() {
  local sha="${1:-}" want have
  [ -n "$sha" ] || usage
  git -C "$REPO_DIR" fetch origin --tags --quiet

  # Resolve BOTH sides through rev-parse before comparing. Comparing the caller's argument
  # verbatim against a 40-char rev-parse makes every short sha a false failure.
  want="$(git -C "$REPO_DIR" rev-parse --verify --quiet "${sha}^{commit}")" \
    || die "'$sha' is not a commit in this repository."
  have="$(git -C "$REPO_DIR" rev-parse --verify "origin/master^{commit}")"

  # The check is on the TAG TARGET, not on the working tree. You can be standing anywhere —
  # a stale checkout, a worktree, a detached HEAD — and still tag the right commit, provided
  # the commit you are about to tag IS origin/master. That is the case this exists for.
  [ "$want" = "$have" ] \
    || die "the commit you are about to tag is NOT origin/master.
        tagging : $(git -C "$REPO_DIR" rev-parse --short "$want")  $(git -C "$REPO_DIR" log -1 --format=%s "$want")
        master  : $(git -C "$REPO_DIR" rev-parse --short "$have")  $(git -C "$REPO_DIR" log -1 --format=%s "$have")
        Tag the tip you verified, or fetch/merge first."
  ok "tag target $(git -C "$REPO_DIR" rev-parse --short "$want") IS origin/master"

  # Advisory: a dirty tree does not invalidate the tag (the tag names a commit, not your files),
  # so this warns rather than dies — the tag-target check above is the load-bearing one.
  local dirty
  dirty="$(git -C "$REPO_DIR" status --porcelain | grep -v '^?? docs/plans/' || true)"
  if [ -n "$dirty" ]; then
    warn "working tree is not clean (does not block the tag, but check nothing here was meant to ship):"
    printf '%s\n' "$dirty" | sed 's/^/      /' >&2
  else
    ok "working tree clean (untracked docs/plans drafts ignored)"
  fi

  guard_747_window
  printf '\n  preflight passed — safe to tag %s\n\n' "$(git -C "$REPO_DIR" rev-parse --short "$want")"
}

# ── ci ──────────────────────────────────────────────────────────────────────────────────────
# TRAP 2: poll the RUN's `.status`, never a nested job — `--json jobs` shows packaged-tool-smoke
# "completed" while the run as a whole is still in_progress.
# Diagnostics go to stderr and the run id alone to stdout, so `rid=$(... ci "$sha")` composes.
cmd_ci() {
  local sha="${1:-}" rid status conclusion
  [ -n "$sha" ] || usage

  rid="$(gh run list --repo "$REPO_SLUG" --limit 30 \
         --json headSha,databaseId,workflowName \
         --jq "map(select(.headSha==\"$sha\" and .workflowName==\"Release\"))[0].databaseId // empty")"
  # TRAP 3: an empty id silently builds a malformed URL that 404s. Fail loudly instead.
  [ -n "$rid" ] || die "no Release run found for $sha — do NOT build a run URL from an empty id."
  printf '  run: https://github.com/%s/actions/runs/%s\n' "$REPO_SLUG" "$rid" >&2

  for _ in $(seq 1 90); do
    status="$(gh run view "$rid" --repo "$REPO_SLUG" --json status --jq '.status')"
    [ -n "$status" ] || die "empty status for run $rid — refusing to treat that as 'completed'."
    if [ "$status" = "completed" ]; then
      break
    fi
    printf '  %s … %s\n' "$(date -u +%H:%M:%S)Z" "$status" >&2
    sleep 60
  done
  [ "$status" = "completed" ] || die "run $rid still '$status' after 90 minutes — check it by hand."

  gh run view "$rid" --repo "$REPO_SLUG" --json jobs \
    --jq '.jobs[] | "  \(.conclusion)\t\(.name)"' >&2
  conclusion="$(gh run view "$rid" --repo "$REPO_SLUG" --json conclusion --jq '.conclusion')"
  [ "$conclusion" = "success" ] || die "Release run $rid concluded '$conclusion' — NOT published."
  printf '  all jobs green\n' >&2
  printf '%s\n' "$rid"
}

# ── counts ──────────────────────────────────────────────────────────────────────────────────
# Every `test (<os>)` job runs Core and Integration SEQUENTIALLY and then the whole solution
# CONCURRENTLY (#566), so each job emits four summary lines: 3 OS x 4 = the 12 combinations.
#
# Duration is deliberately STRIPPED. It differs every run, so a baseline that kept it could never
# match — which would make `verify` useless exactly when it matters.
#
# A moving SKIP count under a static total means a test stopped RUNNING on some platform. That is
# invisible in a green tick, and it is the shape that cost v1.15.0.
job_counts() {
  local jid="$1" jname="$2" log lines
  # No `2>/dev/null` here either: an unreadable log must be loud, not an empty section.
  log="$(gh api "repos/$REPO_SLUG/actions/jobs/${jid}/logs")" \
    || die "could not read logs for job ${jname} (${jid})."
  lines="$(printf '%s' "$log" | tr -d '\r' |
    sed -nE 's/.*(Failed:[[:space:]]*[0-9]+,[[:space:]]*Passed:[[:space:]]*[0-9]+,[[:space:]]*Skipped:[[:space:]]*[0-9]+,[[:space:]]*Total:[[:space:]]*[0-9]+).*-[[:space:]]+([A-Za-z0-9.]+\.dll).*/\2 | \1/p' |
    sed -E 's/:[[:space:]]+/: /g')"
  # grep/sed matching nothing is not "no news": a test job that emitted no summary line at all
  # is precisely the evidence loss this subcommand exists to detect.
  [ -n "$lines" ] || die "job ${jname} (${jid}) emitted NO test-summary line — it did not run tests, or the log format moved."
  printf '%s\n' "$lines" | sed -E "s/^/${jname} | /"
}

cmd_counts() {
  local rid="${1:-}" jobs jid jname out=""
  [ -n "$rid" ] || usage
  jobs="$(gh run view "$rid" --repo "$REPO_SLUG" --json jobs \
          --jq '.jobs[] | select(.name|startswith("test ")) | "\(.databaseId) \(.name)"')"
  [ -n "$jobs" ] || die "run $rid has no 'test (<os>)' jobs — wrong run id, or the job names moved."
  # A here-string, NOT a pipe: `while read` on the right of a pipe runs in a SUBSHELL, so any
  # variable it set would be lost when the loop ended.
  #
  # The loop then accumulates and sorts afterwards rather than piping itself into `sort`. To be
  # precise about why, because the tempting claim is wrong: piping into `sort` would NOT swallow a
  # `die` inside job_counts — `pipefail` propagates the loop subshell's exit status out of the
  # pipeline, and an A/B probe of both shapes confirmed each exits 1. The accumulate form is chosen
  # so that this stays true without DEPENDING on `pipefail` still being set by whoever edits the
  # `set` line above — not because the pipe was losing failures.
  while read -r jid jname; do
    [ -n "$jid" ] || continue
    out+="$(job_counts "$jid" "$jname")"$'\n'
  done <<< "$jobs"
  printf '%s' "$out" | sort
}

cmd_baseline() {
  local rid="${1:-}" tag counts
  [ -n "$rid" ] || usage
  tag="$(gh run view "$rid" --repo "$REPO_SLUG" --json headBranch --jq '.headBranch')"
  counts="$(cmd_counts "$rid")"
  # Provenance in the file itself. A baseline nobody can date or trace back to a run is barely
  # better than the one that lived in an agent's memory — the problem this file exists to fix.
  {
    printf '# Test-count baseline for the release gate. Comment lines are ignored by `verify`.\n'
    printf '#\n'
    printf '# Recorded from : run %s (%s)\n' "$rid" "$tag"
    printf '#                 https://github.com/%s/actions/runs/%s\n' "$REPO_SLUG" "$rid"
    printf '# Recorded at   : %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '#\n'
    printf '# Totals must match across all three OS; SKIPS legitimately differ per platform, which is\n'
    printf '# exactly why they are pinned here. Re-record deliberately, never to make red go away:\n'
    printf '#     .github/scripts/release-preflight.sh baseline <run-id>\n'
    printf '#\n'
    printf '%s\n' "$counts"
  } > "$BASELINE_FILE"
  ok "baseline written to ${BASELINE_FILE} (COMMIT IT — a baseline in one agent's memory is not a baseline)"
  cat "$BASELINE_FILE"
}

cmd_verify() {
  local rid="${1:-}" actual
  [ -n "$rid" ] || usage
  [ -f "$BASELINE_FILE" ] || die "no baseline at ${BASELINE_FILE} — record one with: $0 baseline <run-id>"
  actual="$(cmd_counts "$rid")"
  # `tr -d '\r'` belt-and-braces alongside the .gitattributes eol=lf pin: a checkout predating that
  # pin still has a CRLF baseline, and a diff failing on invisible ^M reads as drifted test counts.
  if diff -u <(grep -v '^#' "$BASELINE_FILE" | tr -d '\r') <(printf '%s\n' "$actual"); then
    ok "counts match the recorded baseline exactly"
  else
    die "test counts DIFFER from ${BASELINE_FILE} (above). A changed TOTAL is expected when tests were
        added; a changed SKIPPED with an unchanged TOTAL means a test stopped running somewhere.
        Explain it, then re-record with: $0 baseline $rid"
  fi
}

# ── published ───────────────────────────────────────────────────────────────────────────────
cmd_published() {
  local ver="${1:-}" index
  [ -n "$ver" ] || usage
  index="$(curl -fsSL "https://api.nuget.org/v3-flatcontainer/servantsoftware.guardrails/index.json")" \
    || die "could not reach the NuGet flat-container index (network, or the package id moved)."
  # -F: a dotted version is a REGEX otherwise, where `1.20.0` would also match `1x20x0`.
  if printf '%s' "$index" | grep -qF "\"${ver}\""; then
    ok "${ver} is live on NuGet.org"
  else
    die "${ver} is NOT on the NuGet feed. Indexing lags a couple of minutes after the publish job
        goes green, so re-check before concluding anything — but do NOT announce it as shipped yet."
  fi
}

case "${1:-}" in
  preflight) shift; cmd_preflight "$@" ;;
  ci)        shift; cmd_ci        "$@" ;;
  counts)    shift; cmd_counts    "$@" ;;
  baseline)  shift; cmd_baseline  "$@" ;;
  verify)    shift; cmd_verify    "$@" ;;
  published) shift; cmd_published "$@" ;;
  *)         usage ;;
esac
