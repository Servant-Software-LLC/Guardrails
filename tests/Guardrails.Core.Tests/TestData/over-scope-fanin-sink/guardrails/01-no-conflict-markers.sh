# catches: a fan-in union that left git conflict markers in the merged bytes — the
# deterministic verdict that the sink integrated cleanly, run on every union's bytes and
# on the final merged HEAD (never git's no-conflict signal, never an AI's say-so).
set -e
ws="${GUARDRAILS_WORKSPACE:-$(pwd)}"
# Line-anchored ours/theirs markers only (both write at column 0), false-positive-free (#187a).
if [ -d "$ws/src" ] && grep -rIlE '^<<<<<<<|^>>>>>>>' "$ws/src" >/dev/null 2>&1; then
  echo "merged bytes contain git conflict markers — the union did not cleanly integrate"
  exit 1
fi
exit 0