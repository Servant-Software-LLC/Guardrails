#!/usr/bin/env bash
# =============================================================================
# install.sh — macOS/Linux bootstrap for Guardrails.
#
# !! NOT TESTED ON THIS WINDOWS DEV BOX. !!
# This is a minimal twin of install.ps1 (the tested, canonical bootstrap) and
# mirrors its logic line-for-line: verify dotnet, install-or-update the tool,
# run `guardrails skills install`, print next steps. Treat install.ps1 as the
# source of truth; if the two ever diverge, install.ps1 is correct.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.sh | bash
#   ./install.sh                 # install from NuGet.org
#   ./install.sh ./nupkg         # install from a local packed folder (pre-publish testing)
# =============================================================================
set -euo pipefail

PACKAGE_ID="ServantSoftware.Guardrails"
SOURCE="${1:-}"   # optional: a local folder (or feed) to install from

# --- 1. dotnet must be on PATH -----------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: the .NET SDK (dotnet) was not found on PATH." >&2
  echo "Install the .NET 8 SDK, then re-run this script:" >&2
  echo "    brew install dotnet            # macOS" >&2
  echo "  or see https://dotnet.microsoft.com/download/dotnet/8.0" >&2
  exit 1
fi

# --- 2. install vs update (idempotent) ---------------------------------------
if dotnet tool list --global 2>/dev/null | grep -qi "$PACKAGE_ID"; then
  VERB="update"
else
  VERB="install"
fi

ARGS=(tool "$VERB" --global "$PACKAGE_ID" --prerelease)
if [ -n "$SOURCE" ]; then
  RESOLVED="$(cd "$SOURCE" && pwd)"
  ARGS+=(--add-source "$RESOLVED")
  echo "Installing $PACKAGE_ID from source: $RESOLVED"
else
  echo "Installing $PACKAGE_ID from NuGet.org"
fi

echo "  dotnet ${ARGS[*]}"
dotnet "${ARGS[@]}"

# --- 3. install the bundled skills -------------------------------------------
# --force so re-running the bootstrap to UPGRADE actually refreshes the deployed skills
# (skills install skips existing folders by default — that would leave stale skills on upgrade).
echo ""
echo "Installing bundled skills..."
if ! guardrails skills install --force; then
  echo "ERROR: 'guardrails skills install' failed." >&2
  echo "If 'guardrails' was not found, ensure ~/.dotnet/tools is on PATH, then run:" >&2
  echo "    guardrails skills install --force" >&2
  exit 1
fi

# --- 4. next steps -----------------------------------------------------------
echo ""
echo "Guardrails is installed."
echo "Next steps:"
echo "  1. Restart Claude Code (or start a new session) so it picks up the new skills."
echo "  2. In your work repo, run  /plan-breakdown path/to/your-plan.md  to generate a task folder."
echo "  3. Then  guardrails run path/to/your-plan  to execute it to green."
