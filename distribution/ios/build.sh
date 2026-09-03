#!/usr/bin/env bash
set -eo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPOSITORY_ROOT"

if [ "${CI:-false}" != true ]; then
  [ -f "$HOME/.zshrc" ] && source "$HOME/.zshrc" || true
  [ -f "$HOME/.zprofile" ] && source "$HOME/.zprofile" || true
  [ -f "$HOME/.bash_profile" ] && source "$HOME/.bash_profile" || true
  [ -f "$HOME/.bashrc" ] && source "$HOME/.bashrc" || true
fi

export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"

if [ -z "${DOTNET:-}" ]; then
  if [ -n "${DOTNET_ROOT:-}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
    DOTNET="$DOTNET_ROOT/dotnet"
  else
    DOTNET=$(command -v dotnet || true)
  fi
fi

if [ -z "$DOTNET" ]; then
  for candidate in \
    "/opt/homebrew/bin/dotnet" \
    "/usr/local/bin/dotnet" \
    "/usr/local/share/dotnet/dotnet"
  do
    if [ -x "$candidate" ]; then
      DOTNET="$candidate"
      break
    fi
  done
fi

if [ -z "$DOTNET" ]; then
  echo "dotnet not found"
  exit 1
fi

PUBLISH_ARGUMENTS=(publish -c Release -r ios-arm64 -p:ExtraDefineConstants=DISABLE_UPDATER src/Ryujinx.Library --self-contained true)
if [ -n "${DEVELOPER_DIR:-}" ]; then
  PUBLISH_ARGUMENTS+=("-p:XCodePath=${DEVELOPER_DIR%/}/")
fi
if [ -n "${MELO_NX_NATIVE_BINLOG:-}" ]; then
  PUBLISH_ARGUMENTS+=("-bl:$MELO_NX_NATIVE_BINLOG")
fi
"$DOTNET" "${PUBLISH_ARGUMENTS[@]}"
