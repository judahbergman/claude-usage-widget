# Claude Usage

A small widget for your Mac desktop that shows how much of your Claude plan you've used:

- **Session**: your 5-hour usage, and when it resets
- **Weekly**: your weekly usage, and when it resets

It shows the same numbers as claude.ai → Settings → Usage, and updates every couple of minutes.

## Install

1. Open **Terminal**: press Cmd+Space, type `Terminal`, and press Enter.
2. Paste this line and press Enter:

   ```
   curl -fsSL https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.sh | bash
   ```

3. A **Sign in to Claude** window opens. Sign in with your own Claude account. You only need to do this once.
   - **Signing in by email?** Don't click the link in the email, because that opens it in your web browser. Instead, right-click the sign-in button in the email, choose **Copy Link**, and click **Paste Link** at the top of the sign-in window.

The widget then appears in the top-right corner of your screen. It starts on its own when you log in, and it **updates itself automatically**, so you never need to reinstall it.

Requires macOS 12 or later. Works on Apple Silicon and Intel Macs.

## Using it

- **Click** (or right-click) the widget to open its menu: Refresh, Sign In, Keep On Top of Windows, Check for Updates, Quit.
- **Drag** it wherever you like. It remembers its spot, even with more than one display.
- Usage bars turn orange at 75% and red at 90%.

## Uninstall

```
curl -fsSL https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.sh | bash -s -- --uninstall
```

## Privacy

- The widget talks only to **claude.ai** (to read your usage) and to **GitHub** (to check for new versions).
- Your Claude login is stored only on your own Mac, inside the widget's built-in sign-in window storage. Nothing is sent anywhere else.
- As a backup, it can try to borrow your claude.ai login from Google Chrome on your Mac. macOS asks your permission first, and you can click **Deny**. Newer versions of macOS may block this entirely; the widget's own sign-in works either way.
- This project contains only the widget's code. There are no passwords, keys or account data in it.

## For maintainers: publishing an update

1. Change the code in `Sources/ClaudeUsage.swift`.
2. Raise the number in the `VERSION` file (for example `1.4.0` → `1.4.1`).
3. Push to `main`.

GitHub then builds the app on a real Mac and checks that it starts. If both pass, it publishes the new version as a release. Every installed widget picks it up within about 12 hours, or right away with **Check for Updates…**. If the build or the check fails, nothing is released.
