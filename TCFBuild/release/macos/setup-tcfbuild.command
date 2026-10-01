#!/bin/sh

SCRIPT_DIR=$(dirname "$0")
if ! APP_DIR=$(CDPATH= cd "$SCRIPT_DIR" && pwd); then
  echo "ERROR: Could not resolve the TCFBuild folder." >&2
  exit 1
fi

ARCH=$(uname -m)
case "$ARCH" in
  arm64)
    NODE="$APP_DIR/runtime/darwin-arm64/node"
    ;;
  x86_64)
    NODE="$APP_DIR/runtime/darwin-x64/node"
    ;;
  *)
    echo "ERROR: Unsupported macOS architecture \"$ARCH\". TCFBuild supports arm64 and x86_64." >&2
    exit 1
    ;;
esac

if [ ! -f "$NODE" ]; then
  echo "ERROR: Embedded Node.js runtime is missing: $NODE" >&2
  exit 1
fi

if ! chmod +x "$NODE"; then
  echo "ERROR: Could not make the embedded Node.js runtime executable: $NODE" >&2
  exit 1
fi

"$NODE" -e "const major=Number(process.versions.node.split('.')[0]); if (major < 18) { console.error('ERROR: Embedded Node.js 18 or newer is required. Found ' + process.versions.node); process.exit(1); }" || exit 1

exec "$NODE" "$APP_DIR/updater.mjs" "$@"
