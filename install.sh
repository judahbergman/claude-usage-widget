#!/bin/bash
# Installs (or reinstalls) the Claude Usage desktop widget from the latest GitHub release.
#
#   Install:    curl -fsSL https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.sh | bash
#   Uninstall:  curl -fsSL https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.sh | bash -s -- --uninstall
#
# After this, the widget keeps itself up to date.
set -e

REPO="JoolBaby/claude-usage-widget"
APP="/Applications/Claude Usage.app"
OLD_APP="$HOME/Applications/Claude Usage.app"
BUNDLE_ID="com.joolbaby.claudeusage"
AGENT="$HOME/Library/LaunchAgents/$BUNDLE_ID.plist"

stop_widget() {
  launchctl bootout "gui/$(id -u)/$BUNDLE_ID" 2>/dev/null || true
  pkill -x ClaudeUsage 2>/dev/null || true
  sleep 1
}

if [ "${1:-}" = "--uninstall" ]; then
  stop_widget
  rm -rf "$OLD_APP" "$AGENT"
  if [ -e "$APP" ]; then
    rm -rf "$APP" 2>/dev/null || sudo rm -rf "$APP"
  fi
  echo "Claude Usage widget removed."
  exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "==> Downloading the latest Claude Usage…"
curl -fsSL "https://github.com/$REPO/releases/latest/download/ClaudeUsage.zip" -o "$WORK/ClaudeUsage.zip"
ditto -x -k "$WORK/ClaudeUsage.zip" "$WORK"
if [ ! -x "$WORK/Claude Usage.app/Contents/MacOS/ClaudeUsage" ]; then
  echo "The download didn't look right. Please try again in a few minutes."
  exit 1
fi

echo "==> Installing to your Applications folder"
stop_widget
SUDO=""
if [ ! -w /Applications ]; then
  echo "    (Your Mac may ask for your password to write to Applications.)"
  SUDO="sudo"
fi
$SUDO rm -rf "$APP"
$SUDO ditto "$WORK/Claude Usage.app" "$APP"
# Owned by you, so the widget can update itself without asking for a password.
$SUDO chown -R "$(id -un)" "$APP" 2>/dev/null || true
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
touch "$APP"

if [ -e "$OLD_APP" ]; then
  echo "==> Removing an older copy from your personal Applications folder"
  rm -rf "$OLD_APP"
  rmdir "$HOME/Applications" 2>/dev/null || true
fi

echo "==> Setting it to start when you log in"
mkdir -p "$HOME/Library/LaunchAgents"
cat > "$AGENT" <<AGENT_EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$BUNDLE_ID</string>
  <key>ProgramArguments</key>
  <array>
    <string>$APP/Contents/MacOS/ClaudeUsage</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key>
  <dict>
    <key>SuccessfulExit</key><false/>
  </dict>
  <key>ThrottleInterval</key><integer>5</integer>
  <key>AbandonProcessGroup</key><true/>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
  <key>ProcessType</key><string>Interactive</string>
</dict>
</plist>
AGENT_EOF

echo "==> Starting Claude Usage"
if ! launchctl bootstrap "gui/$(id -u)" "$AGENT" 2>/dev/null; then
  open "$APP"
fi

echo ""
echo "Done! Claude Usage is on your desktop."
echo "  • A \"Sign in to Claude\" window opens the first time. Sign in once and it stays signed in."
echo "  • Click (or right-click) the widget for its menu. Drag it to move it; it remembers the spot."
echo "  • It updates itself automatically from now on."
