#!/usr/bin/env bash
# =============================================================================
# install.sh — macOS/Linux bootstrap for Guardrails (NO .NET required).
#
# Downloads a prebuilt self-contained binary + its bundled skills from the
# GitHub Release, installs them under ~/.guardrails, drops a wrapper on PATH,
# and deploys the agent skills. Prefer Homebrew if you have it:
#     brew install servant-software-llc/tap/guardrails
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.sh | bash
#   ./install.sh                 # install the latest release
#   ./install.sh 1.1.0           # install a specific version
#
# Env overrides:
#   GUARDRAILS_HOME  install dir for binary + skills   (default: ~/.guardrails)
#   GUARDRAILS_BIN   dir for the `guardrails` wrapper   (default: ~/.local/bin)
# =============================================================================
set -euo pipefail

REPO="Servant-Software-LLC/Guardrails"
HOME_DIR="${GUARDRAILS_HOME:-$HOME/.guardrails}"
BIN_DIR="${GUARDRAILS_BIN:-$HOME/.local/bin}"

# --- 1. detect platform -> .NET RID -----------------------------------------
os="$(uname -s)"; arch="$(uname -m)"
case "$os" in
  Darwin) os_rid="osx" ;;
  Linux)  os_rid="linux" ;;
  *) echo "ERROR: unsupported OS '$os' (this installer covers macOS and Linux)." >&2; exit 1 ;;
esac
case "$arch" in
  arm64|aarch64) arch_rid="arm64" ;;
  x86_64|amd64)  arch_rid="x64" ;;
  *) echo "ERROR: unsupported architecture '$arch'." >&2; exit 1 ;;
esac
RID="$os_rid-$arch_rid"

# --- 2. resolve version/tag (latest STABLE release) -------------------------
if [ "${1:-}" != "" ]; then
  VER="${1#v}"
else
  # /releases/latest returns the newest NON-prerelease release, which is exactly what we
  # want now that releases are stable vX.Y.0 (#421/#423).
  #
  # It must NOT be swapped back to plain /releases (newest-first, prereleases included):
  # `release.yml`'s create-release job is NOT gated on `-ci.`, so a throwaway dry-run tag
  # like v0.0.0-ci.1 DOES publish a GitHub release (flagged prerelease). Under newest-first
  # that CI junk would be the newest entry and this installer would hand a user a dry-run
  # build. /releases/latest skips prereleases, so it ignores those entirely.
  #
  # (Before v1.1.0 every release was a prerelease and this endpoint 404'd, which is why the
  # old newest-first form existed. That transition is over.) No jq dependency.
  #
  # The response is captured to a variable BEFORE grepping rather than piped straight into
  # `grep -m1`. Piping is what the old code did and it is unsafe under `set -o pipefail`:
  # grep exits at the first match, closing the pipe, and curl then dies with exit 23
  # ("Failure writing output") — which pipefail propagates, aborting the whole install.
  # Verified failing that way; assigning first removes the pipe from curl entirely.
  RELEASE_JSON="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")"
  VER="$(printf '%s' "$RELEASE_JSON" \
        | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name" *: *"v?([^"]+)".*/\1/')"
fi
[ -n "$VER" ] || { echo "ERROR: could not resolve a release version." >&2; exit 1; }
TAG="v$VER"
ASSET="guardrails-$VER-$RID.tar.gz"
URL="https://github.com/$REPO/releases/download/$TAG/$ASSET"

echo "Installing Guardrails $VER ($RID)"
echo "  from $URL"

# --- 3. download, verify, extract -------------------------------------------
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$URL" -o "$tmp/$ASSET"

# checksum (best-effort: skip if the .sha256 asset is unavailable)
if curl -fsSL "$URL.sha256" -o "$tmp/$ASSET.sha256" 2>/dev/null; then
  expected="$(awk '{print $1}' "$tmp/$ASSET.sha256")"
  if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$tmp/$ASSET" | awk '{print $1}')"
  else actual="$(shasum -a 256 "$tmp/$ASSET" | awk '{print $1}')"; fi
  [ "$expected" = "$actual" ] || { echo "ERROR: checksum mismatch for $ASSET." >&2; exit 1; }
  echo "  checksum OK"
fi

rm -rf "$HOME_DIR"; mkdir -p "$HOME_DIR"
tar -C "$HOME_DIR" -xzf "$tmp/$ASSET"
chmod +x "$HOME_DIR/guardrails"

# --- 4. wrapper on PATH ------------------------------------------------------
# A wrapper (not a symlink) guarantees the binary runs from $HOME_DIR, where the
# skills/ folder sits, so `guardrails skills install` always finds its payload.
mkdir -p "$BIN_DIR"
cat > "$BIN_DIR/guardrails" <<SH
#!/bin/sh
exec "$HOME_DIR/guardrails" "\$@"
SH
chmod +x "$BIN_DIR/guardrails"

# --- 5. deploy the bundled skills -------------------------------------------
echo ""
echo "Installing bundled skills..."
"$BIN_DIR/guardrails" skills install --force

# --- 6. next steps -----------------------------------------------------------
echo ""
echo "Guardrails $VER is installed at $HOME_DIR"
case ":$PATH:" in
  *":$BIN_DIR:"*) : ;;
  *) echo "NOTE: add $BIN_DIR to your PATH, e.g.:"
     echo "      echo 'export PATH=\"$BIN_DIR:\$PATH\"' >> ~/.zshrc && source ~/.zshrc" ;;
esac
echo "Next steps:"
echo "  1. Restart Claude Code (or start a new session) so it picks up the new skills."
echo "  2. In your work repo:  /plan-breakdown path/to/your-plan.md"
echo "  3. Then:               guardrails run path/to/your-plan"
