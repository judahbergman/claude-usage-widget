# Claude Usage

A small app that shows how much of your Claude plan you've used, at a glance:

- **Session**: your 5-hour usage, and when it resets
- **Weekly**: your weekly usage, and when it resets

It shows the same numbers as claude.ai → Settings → Usage. Colors tell you where you stand: **green** while there's plenty left, **orange** from 50%, **red** from 80%.

- **Mac**: a small widget on your desktop
- **Windows**: a small colored number in the taskbar, next to the clock

Both start on their own when you turn on your computer, and **update themselves automatically**. You install once and never need to reinstall.

Jump to: [Install on Mac](#install-on-mac) · [Install on Windows](#install-on-windows) · [Signing in](#signing-in) · [Help](#if-something-doesnt-look-right)

---

## Install on Mac

Takes about a minute. Works on macOS 12 or later, on any Mac.

**1. Open Terminal.**
Press **Command (⌘) + Space**, type **Terminal**, and press **Return**. A window with a text prompt opens. That's normal.

**2. Copy this line, paste it into Terminal, and press Return:**

```
curl -fsSL https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.sh | bash
```

You'll see a few lines appear as it downloads and installs. It ends with **"Done!"**

> If it asks for your **password**: type your Mac login password and press Return. **The password won't appear as you type.** That's normal on Mac.

**3. Sign in.**
A **Sign in to Claude** window opens. See [Signing in](#signing-in) below.

**4. Done.** The widget appears in the top-right corner of your screen. You can close Terminal.

**Using it:**
- **Drag** the widget wherever you like. It remembers its spot, even if you have more than one screen.
- **Click** it (or right-click it) for its menu: Refresh, Sign In, Keep On Top of Windows, Check for Updates, Quit.

---

## Install on Windows

Takes about a minute. Works on Windows 10 and 11.

**1. Open PowerShell.**
Click **Start**, type **PowerShell**, and click **Windows PowerShell**. A blue or black window with a text prompt opens. That's normal.

**2. Copy this line, paste it into PowerShell (right-click to paste), and press Enter:**

```
irm https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.ps1 | iex
```

It downloads the app (about 70 MB) and ends with **"Done! Claude Usage is running."**

**3. Sign in.**
A **Sign in to Claude** window opens. See [Signing in](#signing-in) below.

**4. Find it in the taskbar.**
Your usage appears as a small **colored number** in the taskbar, by the clock. The number is your session usage in percent.

> **Don't see it?** Windows sometimes hides new icons. Click the small **up-arrow (^)** next to the clock. You'll find the colored number there. **Drag it onto the taskbar** so it's always visible.

**Using it:**
- **Click** the number for details: both usage bars and when each one resets.
- **Hover** over it for a quick summary.
- **Right-click** it for options: Refresh, Sign In, Start with Windows, Check for Updates, Uninstall, Quit.

> If a blue **"Windows protected your PC"** box ever appears, click **More info → Run anyway**. It appears because the app isn't from a big-name publisher; it's safe.

---

## Signing in

The first time, a **Sign in to Claude** window opens. Sign in with **your own** Claude account, the one you use at work. You only do this once; the app stays signed in.

**Option A: Continue with Google.** Click it and sign in as usual.

**Option B: Sign in with email.**
1. Type your email and click **Continue with email**.
2. Open the email from Claude. **Don't click the link in it.** Clicking opens it in your web browser instead of the app, and the sign-in won't work.
3. Instead, **right-click** the sign-in button (or link) in the email and choose **Copy Link** (Mac) or **Copy link** (Windows).
4. Go back to the **Sign in to Claude** window and click **Paste Link** at the top.

The window closes on its own when you're signed in, and your usage appears.

> Already clicked the link by mistake and got **"There was an error verifying your code"**? Close the window, open the app's menu, choose **Sign In to Claude…**, and follow Option B again with a fresh email.

---

## If something doesn't look right

| What you see | What to do |
|---|---|
| **"Click to sign in"** | Click it (Windows: click the number) and sign in. See [Signing in](#signing-in). |
| **"Offline"** | Your computer can't reach claude.ai right now. It tries again by itself. |
| The widget or number is missing after restarting | **Mac:** System Settings → General → Login Items → make sure **ClaudeUsage** is on. **Windows:** right-click the number → make sure **Start with Windows** is checked. Or just run the install line again. |
| Want the newest version right away | Open the menu → **Check for Updates…** |
| Anything else | Run the install line again. It's safe: it simply reinstalls the newest version and keeps your settings. |

---

## Uninstall

- **Mac:** paste this into Terminal and press Return:
  ```
  curl -fsSL https://raw.githubusercontent.com/JoolBaby/claude-usage-widget/main/install.sh | bash -s -- --uninstall
  ```
- **Windows:** right-click the number by the clock → **Uninstall Claude Usage…**

---

## Privacy

- The app talks only to **claude.ai** (to read your usage) and to **GitHub** (to check for new versions).
- Your Claude sign-in is stored only on your own computer. On Windows it's encrypted with your Windows account. Nothing is sent anywhere else.
- On Mac, as a backup, it can try to borrow your claude.ai login from Google Chrome. macOS asks your permission first, and you can click **Deny**. Newer versions of macOS may block this; the app's own sign-in works either way.
- This project contains only the app's code. There are no passwords, keys or account data in it.

---

## For maintainers: publishing an update

- Mac code: `mac/Sources/ClaudeUsage.swift`. Windows code: `windows/ClaudeUsage/` (C#, .NET 8).
- To publish: change the code, raise the number in the `VERSION` file (for example `1.5.0` → `1.5.1`), and push to `main`.

GitHub then builds both apps on a real Mac and a real Windows PC and checks that they start. If everything passes, it publishes one release with both apps, and every installed copy picks it up within about 12 hours (or right away with **Check for Updates…**). If anything fails, nothing is released. Pushes to other branches are built and tested, but never released.
