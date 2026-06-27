#!/usr/bin/env bash
#
# End-to-end packaged-tool smoke test (issue #171).
#
# Exercises the REAL packaging path — `dotnet pack` of the PackAsTool project, then a
# `dotnet tool install` of that .nupkg into an ISOLATED tool-path — NOT the build output
# ($(OutDir)). This is the gap that let #169 ship broken across preview.27–30: the unit
# suite asserted against $(OutDir)skills/, but a PackAsTool package ships the `dotnet
# publish` output (repopulated fresh from the unstamped repo source), so every published
# .nupkg carried unstamped skills and `guardrails --version` always reported `unversioned`.
# Nothing caught it until a user installed the published tool.
#
# This script proves, against the ACTUAL artifact a user would install:
#   1. the bundled `skills/` payload made it into the package (skills install installs
#      the three expected skills — a missing payload would silently install zero);
#   2. each installed SKILL.md carries `metadata.guardrails-version` (the #169 regression);
#   3. `guardrails --version`, run from the install CWD, reports NO drift / no `unversioned`
#      warning for the freshly-installed skills — transitively proving the installed skill
#      version matches the harness version.
#
# Usage:
#   .github/scripts/smoke-packaged-tool.sh [VERSION]
#   GUARDRAILS_SMOKE_VERSION=<v> .github/scripts/smoke-packaged-tool.sh
#
# VERSION defaults to a test value. Both CI (ubuntu) and a local Git Bash run invoke this
# same script — it is the single source of truth for the smoke logic.
#
# Runs from the repo root (it resolves the repo root from its own location, so the caller's
# CWD does not matter). Cross-platform: the installed launcher is `guardrails` on Linux/macOS
# and `guardrails.exe` on Windows; the script picks whichever exists under --tool-path.

set -euo pipefail

# --- locate the repo root (two levels up from this script: .github/scripts/ -> repo) ------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# --- version to pack/install (arg > env > default test value) -----------------------------
VERSION="${1:-${GUARDRAILS_SMOKE_VERSION:-0.0.0-smoke}}"

# --- expected bundled skills (the three the CLI csproj globs into the package) -------------
EXPECTED_SKILLS=(plan-breakdown guardrails-review guardrails-domain-knowledge)

# --- isolated scratch space (cleaned on exit, success or failure) -------------------------
WORK_DIR="$(mktemp -d 2>/dev/null || mktemp -d -t guardrails-smoke)"
NUPKG_DIR="${WORK_DIR}/nupkg"
TOOL_DIR="${WORK_DIR}/tool"
CWD_DIR="${WORK_DIR}/cwd"        # isolated CWD; `skills install --project` writes ./.claude/skills here

cleanup() {
  # Best-effort uninstall, then nuke the whole scratch tree — nothing lingers on the box.
  local launcher
  launcher="$(find_launcher 2>/dev/null || true)"
  if [[ -d "${TOOL_DIR}" ]]; then
    dotnet tool uninstall ServantSoftware.Guardrails --tool-path "${TOOL_DIR}" >/dev/null 2>&1 || true
  fi
  rm -rf "${WORK_DIR}" 2>/dev/null || true
}
trap cleanup EXIT

fail() {
  echo "SMOKE FAIL: $*" >&2
  exit 1
}

# Resolve the installed launcher name (Windows ships guardrails.exe; *nix ships guardrails).
find_launcher() {
  if [[ -x "${TOOL_DIR}/guardrails" ]]; then
    echo "${TOOL_DIR}/guardrails"
  elif [[ -f "${TOOL_DIR}/guardrails.exe" ]]; then
    echo "${TOOL_DIR}/guardrails.exe"
  else
    return 1
  fi
}

echo "== Guardrails packaged-tool smoke =="
echo "repo:    ${REPO_ROOT}"
echo "version: ${VERSION}"
echo "work:    ${WORK_DIR}"
echo

# --- 1. pack the REAL tool package --------------------------------------------------------
echo "-- pack (dotnet pack src/Guardrails.Cli) --"
dotnet pack "${REPO_ROOT}/src/Guardrails.Cli" -c Release -o "${NUPKG_DIR}" -p:Version="${VERSION}"

PKG="${NUPKG_DIR}/ServantSoftware.Guardrails.${VERSION}.nupkg"
[[ -f "${PKG}" ]] || fail "expected package not produced: ${PKG}"
echo "packed: ${PKG}"
echo

# --- 2. install the packed tool to an ISOLATED tool-path (no global pollution) ------------
echo "-- install (dotnet tool install --tool-path) --"
dotnet tool install ServantSoftware.Guardrails \
  --tool-path "${TOOL_DIR}" \
  --add-source "${NUPKG_DIR}" \
  --version "${VERSION}"

LAUNCHER="$(find_launcher)" || fail "installed launcher not found under ${TOOL_DIR} (looked for guardrails / guardrails.exe)"
echo "launcher: ${LAUNCHER}"
echo

# --- 3. run skills install --project in an ISOLATED CWD -----------------------------------
# `--project` installs into ./.claude/skills under the CWD, which is exactly the project-level
# scan root `guardrails --version` later inspects. Running from a temp CWD keeps it isolated.
mkdir -p "${CWD_DIR}"
echo "-- skills install --project (cwd=${CWD_DIR}) --"
( cd "${CWD_DIR}" && "${LAUNCHER}" skills install --project )
SKILLS_ROOT="${CWD_DIR}/.claude/skills"
echo

# --- 4. ASSERT --------------------------------------------------------------------------
# 4a. the bundled skills/ payload made it into the package: each expected skill's SKILL.md
#     now exists under the install target. A missing package payload would silently install
#     ZERO skills, leaving these files absent.
echo "-- assert: bundled skills payload installed --"
[[ -d "${SKILLS_ROOT}" ]] || fail "no skills were installed — ${SKILLS_ROOT} does not exist (package likely missing its skills/ payload)"
for skill in "${EXPECTED_SKILLS[@]}"; do
  skill_md="${SKILLS_ROOT}/${skill}/SKILL.md"
  [[ -f "${skill_md}" ]] || fail "expected skill not installed: ${skill_md} (bundled skills/ payload missing from the package?)"
  echo "  ok: ${skill}/SKILL.md installed"
done
echo

# 4b. each installed SKILL.md carries metadata.guardrails-version (the direct #169 guard).
echo "-- assert: each installed SKILL.md is version-stamped --"
for skill in "${EXPECTED_SKILLS[@]}"; do
  skill_md="${SKILLS_ROOT}/${skill}/SKILL.md"
  if ! grep -q "guardrails-version:" "${skill_md}"; then
    fail "installed ${skill}/SKILL.md has no 'guardrails-version:' frontmatter key (the #169 regression — install-time stamp missing)"
  fi
  echo "  ok: ${skill}/SKILL.md has guardrails-version:"
done
echo

# 4c. `guardrails --version` from the install CWD reports NO drift / no `unversioned` warning
#     for the freshly-installed skills. stdout must be the version; stderr must NOT contain
#     `unversioned` or the drift WARNING block. This transitively proves the installed skill
#     version matches the harness version (no drift).
echo "-- assert: --version reports no drift for freshly-installed skills --"
VER_STDOUT="${WORK_DIR}/version.out"
VER_STDERR="${WORK_DIR}/version.err"
( cd "${CWD_DIR}" && "${LAUNCHER}" --version ) >"${VER_STDOUT}" 2>"${VER_STDERR}"

echo "  --version stdout: $(cat "${VER_STDOUT}")"
if grep -qi "unversioned" "${VER_STDERR}"; then
  echo "  --- stderr ---" >&2
  cat "${VER_STDERR}" >&2
  fail "'--version' reported an 'unversioned' skill — the #169 drift regression"
fi
if grep -q "WARNING:" "${VER_STDERR}"; then
  echo "  --- stderr ---" >&2
  cat "${VER_STDERR}" >&2
  fail "'--version' emitted a drift WARNING for the freshly-installed skills"
fi
# stdout must echo the version we packed (the harness reports its own AssemblyInformationalVersion).
if ! grep -q "${VERSION}" "${VER_STDOUT}"; then
  fail "'--version' stdout '$(cat "${VER_STDOUT}")' does not contain the packed version '${VERSION}'"
fi
echo "  ok: no drift, no 'unversioned', version line matches ${VERSION}"
echo

echo "SMOKE PASS: packaged tool carries its skills/, install-time stamping works, and --version reports no drift."
