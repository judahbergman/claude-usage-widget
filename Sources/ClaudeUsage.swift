// Claude Usage — a small native desktop widget for macOS.
// Shows the claude.ai 5-hour session and weekly usage with reset times.
// Signs in with its own built-in claude.ai window (and stays signed in), and can also
// borrow your claude.ai login from Google Chrome as a backup.

import Cocoa
import SwiftUI
import WebKit
import Security
import SQLite3
import CommonCrypto

// MARK: - Models

struct Bucket {
    let utilization: Double
    let resetsAt: Date?

    init?(json: Any?) {
        guard let d = json as? [String: Any] else { return nil }
        utilization = (d["utilization"] as? NSNumber)?.doubleValue ?? 0
        resetsAt = Formatting.parseDate(d["resets_at"] as? String)
    }
}

struct Org {
    let id: String
    let name: String
    let score: Int

    init?(json: [String: Any]) {
        guard let uuid = json["uuid"] as? String else { return nil }
        id = uuid
        if let d = json["display_name"] as? String, !d.isEmpty {
            name = d
        } else {
            name = (json["name"] as? String) ?? "Account"
        }
        let caps = (json["capabilities"] as? [String]) ?? []
        if json["raven_type"] is String {
            score = 3            // Team / Enterprise
        } else if caps.contains("claude_max") {
            score = 2
        } else if caps.contains("claude_pro") {
            score = 1
        } else {
            score = 0
        }
    }
}

// MARK: - Formatting

enum Formatting {
    static func parseDate(_ raw: String?) -> Date? {
        guard var s = raw else { return nil }
        if let r = s.range(of: "\\.[0-9]+", options: .regularExpression) {
            s.removeSubrange(r)
        }
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: s)
    }

    static func percent(_ b: Bucket?) -> String {
        guard let b = b else { return "—" }
        return "\(Int(b.utilization.rounded()))%"
    }

    static func reset(_ date: Date?, countdown: Bool, now: Date) -> String {
        guard let date = date else { return "Not started" }
        let cal = Calendar.current
        let time = DateFormatter.localizedString(from: date, dateStyle: .none, timeStyle: .short)
        var text: String
        if cal.isDateInToday(date) {
            text = "Resets \(time)"
        } else if cal.isDateInTomorrow(date) {
            text = "Resets tomorrow \(time)"
        } else {
            let f = DateFormatter()
            f.dateFormat = "EEE"
            text = "Resets \(f.string(from: date)) \(time)"
        }
        if countdown {
            let secs = max(0, Int(date.timeIntervalSince(now)))
            let h = secs / 3600
            let m = (secs % 3600) / 60
            text += h > 0 ? " · in \(h)h \(m)m" : " · in \(m)m"
        }
        return text
    }
}

// MARK: - The widget's own claude.ai login (kept in its built-in browser's cookie storage)

@MainActor
enum WebSession {
    private static var cookieStore: WKHTTPCookieStore {
        WKWebsiteDataStore.default().httpCookieStore
    }

    static func sessionKey() async -> String? {
        let store = cookieStore
        return await withCheckedContinuation { (cont: CheckedContinuation<String?, Never>) in
            store.getAllCookies { cookies in
                let now = Date()
                let match = cookies.first { c in
                    c.name == "sessionKey" && c.domain.hasSuffix("claude.ai")
                        && (c.expiresDate.map { $0 > now } ?? true)
                }
                cont.resume(returning: match?.value)
            }
        }
    }

    /// Stores a session key as the widget's own login (replacing any old one).
    static func save(_ key: String) async {
        await clear()
        let props: [HTTPCookiePropertyKey: Any] = [
            .domain: ".claude.ai",
            .path: "/",
            .name: "sessionKey",
            .value: key,
            .secure: "TRUE",
            .expires: Date().addingTimeInterval(60 * 60 * 24 * 365),
        ]
        guard let cookie = HTTPCookie(properties: props) else { return }
        let store = cookieStore
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            store.setCookie(cookie) { cont.resume() }
        }
    }

    static func clear() async {
        let store = cookieStore
        let cookies: [HTTPCookie] = await withCheckedContinuation { (cont: CheckedContinuation<[HTTPCookie], Never>) in
            store.getAllCookies { cont.resume(returning: $0) }
        }
        for c in cookies where c.name == "sessionKey" && c.domain.hasSuffix("claude.ai") {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                store.delete(c) { cont.resume() }
            }
        }
    }
}

// MARK: - Chrome login reader (backup)

struct ChromeResult: Sendable {
    var keys: [String]
    var problem: String?
}

enum ChromeCookies {
    /// All claude.ai session keys found in Chrome profiles, plus a plain-English reason if none.
    static func read() -> ChromeResult {
        let dbs = cookieDatabases()
        if dbs.isEmpty {
            return ChromeResult(keys: [], problem: "Couldn't find Google Chrome's data.")
        }
        var status: OSStatus = 0
        guard let password = safeStoragePassword(&status) else {
            return ChromeResult(keys: [], problem: "macOS didn't let the widget read your Chrome login (code \(status)).")
        }
        guard let key = deriveKey(password) else {
            return ChromeResult(keys: [], problem: "Couldn't unlock Chrome's login.")
        }
        var rows = 0
        var found: [String] = []
        for db in dbs {
            let values = readEncryptedValues(dbPath: db)
            rows += values.count
            for enc in values {
                if let v = decrypt(enc, key: key), !found.contains(v) {
                    found.append(v)
                }
            }
        }
        if rows == 0 {
            return ChromeResult(keys: [], problem: "You're not logged in to claude.ai in Chrome.")
        }
        if found.isEmpty {
            return ChromeResult(keys: [], problem: "Chrome saves its login in a format the widget can't read.")
        }
        return ChromeResult(keys: found, problem: nil)
    }

    private static func cookieDatabases() -> [String] {
        let fm = FileManager.default
        let base = (NSHomeDirectory() as NSString)
            .appendingPathComponent("Library/Application Support/Google/Chrome")
        guard let items = try? fm.contentsOfDirectory(atPath: base) else { return [] }
        let profiles = items.filter { $0 == "Default" || $0.hasPrefix("Profile ") }.sorted()
        var paths: [String] = []
        for p in profiles {
            for rel in ["Network/Cookies", "Cookies"] {
                let path = (base as NSString).appendingPathComponent("\(p)/\(rel)")
                if fm.fileExists(atPath: path) {
                    paths.append(path)
                    break
                }
            }
        }
        return paths
    }

    private static func safeStoragePassword(_ status: inout OSStatus) -> Data? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "Chrome Safe Storage",
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: CFTypeRef?
        status = SecItemCopyMatching(q as CFDictionary, &out)
        guard status == errSecSuccess, let data = out as? Data else { return nil }
        return data
    }

    private static func deriveKey(_ password: Data) -> Data? {
        var key = Data(count: 16)
        let salt = Data("saltysalt".utf8)
        let status: Int32 = key.withUnsafeMutableBytes { (kp: UnsafeMutableRawBufferPointer) -> Int32 in
            salt.withUnsafeBytes { (sp: UnsafeRawBufferPointer) -> Int32 in
                password.withUnsafeBytes { (pp: UnsafeRawBufferPointer) -> Int32 in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        pp.baseAddress?.assumingMemoryBound(to: Int8.self), password.count,
                        sp.baseAddress?.assumingMemoryBound(to: UInt8.self), salt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), 1003,
                        kp.baseAddress?.assumingMemoryBound(to: UInt8.self), 16)
                }
            }
        }
        return status == Int32(kCCSuccess) ? key : nil
    }

    private static func readEncryptedValues(dbPath: String) -> [Data] {
        let fm = FileManager.default
        let tmp = (NSTemporaryDirectory() as NSString)
            .appendingPathComponent("claude-usage-\(UUID().uuidString).sqlite")
        do { try fm.copyItem(atPath: dbPath, toPath: tmp) } catch { return [] }
        if fm.fileExists(atPath: dbPath + "-wal") {
            try? fm.copyItem(atPath: dbPath + "-wal", toPath: tmp + "-wal")
        }
        defer {
            try? fm.removeItem(atPath: tmp)
            try? fm.removeItem(atPath: tmp + "-wal")
            try? fm.removeItem(atPath: tmp + "-shm")
        }

        var db: OpaquePointer?
        guard sqlite3_open_v2(tmp, &db, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK else {
            sqlite3_close(db)
            return []
        }
        defer { sqlite3_close(db) }

        let sql = "SELECT encrypted_value FROM cookies WHERE name = 'sessionKey' AND host_key LIKE '%claude.ai' ORDER BY last_access_utc DESC"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(stmt) }

        var out: [Data] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let n = Int(sqlite3_column_bytes(stmt, 0))
            if n > 0, let blob = sqlite3_column_blob(stmt, 0) {
                out.append(Data(bytes: blob, count: n))
            }
        }
        return out
    }

    private static func decrypt(_ enc: Data, key: Data) -> String? {
        guard enc.count > 3 else { return nil }
        let prefix = String(data: enc.prefix(3), encoding: .ascii)
        guard prefix == "v10" || prefix == "v11" else { return nil }
        let payload = Data(enc.dropFirst(3))
        let iv = Data(repeating: 0x20, count: 16)
        let outCapacity = payload.count + kCCBlockSizeAES128
        var output = Data(count: outCapacity)
        var moved = 0
        let status: CCCryptorStatus = output.withUnsafeMutableBytes { (op: UnsafeMutableRawBufferPointer) -> CCCryptorStatus in
            payload.withUnsafeBytes { (ip: UnsafeRawBufferPointer) -> CCCryptorStatus in
                key.withUnsafeBytes { (kp: UnsafeRawBufferPointer) -> CCCryptorStatus in
                    iv.withUnsafeBytes { (vp: UnsafeRawBufferPointer) -> CCCryptorStatus in
                        CCCrypt(
                            CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES),
                            CCOptions(kCCOptionPKCS7Padding),
                            kp.baseAddress, key.count, vp.baseAddress,
                            ip.baseAddress, payload.count,
                            op.baseAddress, outCapacity, &moved)
                    }
                }
            }
        }
        guard status == CCCryptorStatus(kCCSuccess) else { return nil }
        let plain = output.prefix(moved)
        // Newer Chrome versions put a 32-byte hash before the value; find the key itself.
        guard let r = plain.range(of: Data("sk-ant-".utf8)) else { return nil }
        return String(data: plain[r.lowerBound...], encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

// MARK: - claude.ai API

enum APIError: Error {
    case unauthorized
    case blocked
    case network(String)
    case badData
}

enum API {
    private static let session = URLSession(configuration: .ephemeral)
    private static let userAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36"

    /// Returns the JSON, plus a renewed session key if claude.ai handed one back.
    static func get(_ path: String, key: String) async throws -> (json: Any, renewedKey: String?) {
        guard let url = URL(string: "https://claude.ai" + path) else { throw APIError.badData }
        var req = URLRequest(url: url)
        req.timeoutInterval = 20
        req.httpShouldHandleCookies = false
        req.setValue("sessionKey=\(key)", forHTTPHeaderField: "Cookie")
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        req.setValue(userAgent, forHTTPHeaderField: "User-Agent")

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: req)
        } catch {
            throw APIError.network(error.localizedDescription)
        }
        let http = response as? HTTPURLResponse
        let code = http?.statusCode ?? 0
        let type = (http?.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased()
        if code == 429 || ((code == 403 || code == 503) && type.contains("html")) {
            throw APIError.blocked
        }
        if code == 401 || code == 403 { throw APIError.unauthorized }
        guard code == 200, let json = try? JSONSerialization.jsonObject(with: data) else {
            throw APIError.badData
        }
        var renewed: String? = nil
        if let http = http {
            var headers: [String: String] = [:]
            for (k, v) in http.allHeaderFields {
                if let ks = k as? String, let vs = v as? String { headers[ks] = vs }
            }
            let cookies = HTTPCookie.cookies(withResponseHeaderFields: headers, for: url)
            if let fresh = cookies.first(where: { $0.name == "sessionKey" })?.value,
               !fresh.isEmpty, fresh != key {
                renewed = fresh
            }
        }
        return (json, renewed)
    }
}

// MARK: - Self-updater (new versions are published on GitHub)

enum UpdateError: Error {
    case download
    case unpack
    case invalid
    case install(String)
}

enum Updater {
    static let repo = "JoolBaby/claude-usage-widget"
    static let launchLabel = "com.joolbaby.claudeusage"

    static var currentVersion: String {
        (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "0"
    }

    /// True when version string `a` is newer than `b` (e.g. "1.4.1" > "1.4.0").
    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    /// The newest published version, or nil if GitHub couldn't be reached.
    static func latestVersion() async -> String? {
        guard let url = URL(string: "https://api.github.com/repos/\(repo)/releases/latest") else { return nil }
        var req = URLRequest(url: url)
        req.timeoutInterval = 20
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("ClaudeUsageWidget/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        guard let result = try? await URLSession.shared.data(for: req),
              (result.1 as? HTTPURLResponse)?.statusCode == 200,
              let obj = (try? JSONSerialization.jsonObject(with: result.0)) as? [String: Any],
              let tag = obj["tag_name"] as? String else { return nil }
        return tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
    }

    private static func run(_ tool: String, _ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        do { try p.run() } catch { return -1 }
        p.waitUntilExit()
        return p.terminationStatus
    }

    /// Downloads the newest version and swaps it into this app, in place.
    static func downloadAndInstall() async throws {
        guard let zipURL = URL(string: "https://github.com/\(repo)/releases/latest/download/ClaudeUsage.zip") else {
            throw UpdateError.download
        }
        let downloaded: URL
        let response: URLResponse
        do {
            (downloaded, response) = try await URLSession.shared.download(from: zipURL)
        } catch {
            throw UpdateError.download
        }
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError.download }

        let fm = FileManager.default
        let work = fm.temporaryDirectory.appendingPathComponent("claude-usage-update-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: true)
        let zip = work.appendingPathComponent("ClaudeUsage.zip")
        try fm.moveItem(at: downloaded, to: zip)

        guard run("/usr/bin/ditto", ["-x", "-k", zip.path, work.path]) == 0 else { throw UpdateError.unpack }

        let newApp = work.appendingPathComponent("Claude Usage.app")
        let newContents = newApp.appendingPathComponent("Contents")
        guard let newBundle = Bundle(url: newApp),
              newBundle.bundleIdentifier == Bundle.main.bundleIdentifier,
              fm.isExecutableFile(atPath: newContents.appendingPathComponent("MacOS/ClaudeUsage").path)
        else { throw UpdateError.invalid }

        // Swap the inside of the app (its "Contents" folder), so no admin password is needed.
        let appURL = Bundle.main.bundleURL
        let liveContents = appURL.appendingPathComponent("Contents")
        let backup = work.appendingPathComponent("PreviousContents")
        do {
            try fm.moveItem(at: liveContents, to: backup)
        } catch {
            throw UpdateError.install("Couldn't replace the app: \(error.localizedDescription)")
        }
        do {
            try fm.moveItem(at: newContents, to: liveContents)
        } catch {
            try? fm.moveItem(at: backup, to: liveContents)
            throw UpdateError.install("Couldn't install the new version: \(error.localizedDescription)")
        }
        _ = run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", appURL.path])
        try? fm.setAttributes([.modificationDate: Date()], ofItemAtPath: appURL.path)
    }

    /// Quits so the new version starts in a few seconds.
    static func relaunch() -> Never {
        if ProcessInfo.processInfo.environment["XPC_SERVICE_NAME"] == launchLabel {
            exit(3)   // started at login: macOS restarts it automatically (now the new version)
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "sleep 2; /usr/bin/open \"$0\"", Bundle.main.bundlePath]
        try? p.run()
        exit(0)
    }
}

// MARK: - Store

@MainActor
final class UsageStore: ObservableObject {
    @Published var session: Bucket?
    @Published var weekly: Bucket?
    @Published var status: String?
    @Published var detail: String?
    @Published var needsSignIn = false
    @Published var orgs: [Org] = []
    @Published var currentOrgID: String?
    @Published var orgName: String?
    @Published var lastUpdated: Date?
    @Published var now = Date()
    @Published var updateAvailable: String?
    @Published var updating = false

    /// Called (once per launch) when no working login was found anywhere.
    var onNeedsSignIn: (() -> Void)?

    private var loading = false
    private var askedForSignIn = false
    private var lastChromeCheck: Date?

    private enum Attempt { case ok, unauthorized, stop }

    func refresh(force: Bool = false) {
        Task { await self.load(force: force) }
    }

    func tick() {
        now = Date()
        if let r = session?.resetsAt, now > r.addingTimeInterval(15) {
            refresh()
        } else if let r = weekly?.resetsAt, now > r.addingTimeInterval(15) {
            refresh()
        }
    }

    private func setProblem(_ short: String, _ long: String) {
        status = short
        detail = long
    }

    private func load(force: Bool) async {
        if loading { return }
        loading = true
        defer { loading = false }

        var tried = Set<String>()

        // 1. The widget's own login (from its Sign In window, a pasted key, or saved from Chrome).
        if let own = await WebSession.sessionKey() {
            tried.insert(own)
            switch await attempt(own) {
            case .ok, .stop: return
            case .unauthorized: break
            }
        }

        // 2. Backup: borrow the login from Chrome (not more than every 10 minutes unless asked).
        var chromeProblem: String? = nil
        let recentlyChecked = lastChromeCheck.map { Date().timeIntervalSince($0) < 600 } ?? false
        if force || !recentlyChecked {
            lastChromeCheck = Date()
            let chrome = await Task.detached { ChromeCookies.read() }.value
            chromeProblem = chrome.problem
            for key in chrome.keys where !tried.contains(key) {
                tried.insert(key)
                switch await attempt(key) {
                case .ok:
                    await WebSession.save(key)   // keep it, so the widget stays signed in on its own
                    return
                case .stop:
                    return
                case .unauthorized:
                    continue
                }
            }
        }

        // Nothing worked: ask the user to sign in.
        needsSignIn = true
        orgName = nil
        let why: String
        if !tried.isEmpty {
            why = "Your claude.ai login expired."
        } else {
            why = chromeProblem ?? "The widget isn't signed in to claude.ai."
        }
        setProblem("Click to sign in",
                   why + " Click the widget and choose Sign In to Claude…")
        if !askedForSignIn {
            askedForSignIn = true
            onNeedsSignIn?()
        }
    }

    private func attempt(_ key: String) async -> Attempt {
        do {
            try await fetch(key: key)
            status = nil
            detail = nil
            needsSignIn = false
            return .ok
        } catch APIError.unauthorized {
            return .unauthorized
        } catch APIError.network(let message) {
            setProblem("Offline", "Couldn't reach claude.ai (\(message)). Retrying automatically.")
            return .stop
        } catch APIError.blocked {
            setProblem("Busy", "claude.ai turned the request away. Retrying automatically.")
            return .stop
        } catch {
            setProblem("Error", "Couldn't read usage from claude.ai. Retrying automatically.")
            return .stop
        }
    }

    private func fetch(key: String) async throws {
        let orgsResult = try await API.get("/api/organizations", key: key)
        guard let arr = orgsResult.json as? [[String: Any]] else { throw APIError.badData }
        let list = arr.compactMap { Org(json: $0) }
        guard let best = list.max(by: { $0.score < $1.score }) else { throw APIError.badData }
        let saved = UserDefaults.standard.string(forKey: "orgID")
        let org = list.first(where: { $0.id == saved }) ?? best

        let usageResult = try await API.get("/api/organizations/\(org.id)/usage", key: key)
        guard let usage = usageResult.json as? [String: Any] else { throw APIError.badData }

        if let renewed = usageResult.renewedKey ?? orgsResult.renewedKey {
            await WebSession.save(renewed)
        }
        orgs = list
        currentOrgID = org.id
        orgName = list.count > 1 ? org.name : nil
        session = Bucket(json: usage["five_hour"])
        weekly = Bucket(json: usage["seven_day"])
        lastUpdated = Date()
        now = Date()
    }
}

// MARK: - Sign In window (a small built-in browser showing claude.ai)

@MainActor
final class SignInWindow: NSObject, WKNavigationDelegate, WKUIDelegate, NSWindowDelegate {
    private var window: NSWindow?
    private var webView: WKWebView?
    private var popups: [NSWindow] = []
    private var timer: Timer?
    private var finished = false
    var onSignedIn: (() -> Void)?

    private static let safariUA =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        if let w = window {
            w.makeKeyAndOrderFront(nil)
            return
        }
        finished = false
        let config = WKWebViewConfiguration()
        config.websiteDataStore = WKWebsiteDataStore.default()
        let frame = NSRect(x: 0, y: 0, width: 480, height: 744)
        let barHeight: CGFloat = 44
        let wv = WKWebView(frame: NSRect(x: 0, y: 0, width: frame.width, height: frame.height - barHeight),
                           configuration: config)
        wv.customUserAgent = SignInWindow.safariUA
        wv.navigationDelegate = self
        wv.uiDelegate = self
        wv.autoresizingMask = [.width, .height]

        // A bar on top for signing in by email: paste the link from the email here,
        // so it opens inside this window instead of in Chrome.
        let bar = NSView(frame: NSRect(x: 0, y: frame.height - barHeight, width: frame.width, height: barHeight))
        bar.autoresizingMask = [.width, .minYMargin]
        let label = NSTextField(labelWithString: "Signing in by email? Copy the link in the email, then:")
        label.font = NSFont.systemFont(ofSize: 11)
        label.textColor = NSColor.secondaryLabelColor
        label.frame = NSRect(x: 12, y: 13, width: frame.width - 130, height: 18)
        label.autoresizingMask = [.width]
        label.lineBreakMode = .byTruncatingTail
        let button = NSButton(title: "Paste Link", target: self, action: #selector(pasteLink))
        button.bezelStyle = .rounded
        button.frame = NSRect(x: frame.width - 112, y: 8, width: 100, height: 28)
        button.autoresizingMask = [.minXMargin]
        bar.addSubview(label)
        bar.addSubview(button)

        let container = NSView(frame: frame)
        container.addSubview(wv)
        container.addSubview(bar)

        let w = NSWindow(contentRect: frame,
                         styleMask: [.titled, .closable, .miniaturizable, .resizable],
                         backing: .buffered, defer: false)
        w.title = "Sign in to Claude"
        w.isReleasedWhenClosed = false
        w.contentView = container
        w.delegate = self
        w.center()
        window = w
        webView = wv

        // Start from a clean slate so an old, expired login can't be mistaken for a new one.
        Task { @MainActor in
            await WebSession.clear()
            if let url = URL(string: "https://claude.ai/login") {
                wv.load(URLRequest(url: url))
            }
        }
        w.makeKeyAndOrderFront(nil)

        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.checkSignedIn() }
        }
    }

    /// Opens the sign-in link from the email (copied to the clipboard) inside this window.
    @objc func pasteLink() {
        guard let wv = webView else { return }
        let text = (NSPasteboard.general.string(forType: .string) ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let allowed = ["claude.ai", "claude.com", "anthropic.com"]
        if let url = URL(string: text), url.scheme == "https", let host = url.host?.lowercased(),
           allowed.contains(where: { host == $0 || host.hasSuffix("." + $0) }) {
            wv.load(URLRequest(url: url))
            return
        }
        let alert = NSAlert()
        alert.messageText = "Copy the sign-in link first"
        alert.informativeText = "In the sign-in email from Claude, right-click the sign-in button (or link) and choose Copy Link. Then come back here and click Paste Link. Don't click the link itself, because that opens it in your web browser instead."
        alert.addButton(withTitle: "OK")
        if let w = window {
            alert.beginSheetModal(for: w, completionHandler: nil)
        } else {
            alert.runModal()
        }
    }

    private func checkSignedIn() async {
        guard !finished, let wv = webView, let url = wv.url,
              let host = url.host, host.hasSuffix("claude.ai") else { return }
        let path = url.path
        if path.hasPrefix("/login") || path.hasPrefix("/magic-link") || path.hasPrefix("/logout") { return }
        if await WebSession.sessionKey() != nil {
            finished = true
            window?.close()
            onSignedIn?()
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in await self.checkSignedIn() }
    }

    // "Continue with Google" opens a pop-up window; show it in its own small window.
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        let frame = NSRect(x: 0, y: 0, width: 520, height: 680)
        let popup = WKWebView(frame: frame, configuration: configuration)
        popup.customUserAgent = SignInWindow.safariUA
        popup.uiDelegate = self
        popup.navigationDelegate = self
        let w = NSWindow(contentRect: frame, styleMask: [.titled, .closable, .resizable],
                         backing: .buffered, defer: false)
        w.title = "Sign in"
        w.isReleasedWhenClosed = false
        w.contentView = popup
        w.center()
        w.makeKeyAndOrderFront(nil)
        popups.append(w)
        return popup
    }

    func webViewDidClose(_ webView: WKWebView) {
        if let w = popups.first(where: { $0.contentView === webView }) {
            popups.removeAll { $0 === w }
            w.close()
        }
        Task { @MainActor in await self.checkSignedIn() }
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow, closing === window else { return }
        timer?.invalidate()
        timer = nil
        for p in popups { p.close() }
        popups = []
        webView = nil
        window = nil
    }
}

// MARK: - Views

struct UsageBar: View {
    let fraction: Double
    let color: Color

    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.14))
                Capsule()
                    .fill(color)
                    .frame(width: fraction > 0 ? max(6, g.size.width * CGFloat(min(fraction, 1))) : 0)
            }
        }
        .frame(height: 6)
    }
}

struct UsageRow: View {
    let title: String
    let bucket: Bucket?
    let now: Date
    let countdown: Bool

    private var color: Color {
        let u = bucket?.utilization ?? 0
        if u >= 90 { return Color(red: 0.95, green: 0.33, blue: 0.30) }
        if u >= 75 { return Color(red: 0.98, green: 0.62, blue: 0.20) }
        return Color(red: 0.33, green: 0.58, blue: 0.98)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .font(.system(size: 12, weight: .medium))
                Spacer()
                Text(Formatting.percent(bucket))
                    .font(.system(size: 13, weight: .semibold))
                    .monospacedDigit()
            }
            UsageBar(fraction: (bucket?.utilization ?? 0) / 100, color: color)
            Text(Formatting.reset(bucket?.resetsAt, countdown: countdown, now: now))
                .font(.system(size: 10.5))
                .foregroundColor(.secondary)
                .lineLimit(1)
        }
    }
}

struct WidgetView: View {
    @ObservedObject var store: UsageStore

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 6) {
                Text(store.orgName.map { "Claude · \($0)" } ?? "Claude Usage")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if let s = store.status {
                    Text(s)
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundColor(.orange)
                        .lineLimit(1)
                } else if store.updating {
                    Text("Updating…")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundColor(.blue)
                        .lineLimit(1)
                } else if store.updateAvailable != nil {
                    Text("Update ready")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundColor(.blue)
                        .lineLimit(1)
                }
                Image(systemName: "ellipsis")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundColor(.secondary)
            }
            Group {
                UsageRow(title: "Session", bucket: store.session, now: store.now, countdown: true)
                UsageRow(title: "Weekly", bucket: store.weekly, now: store.now, countdown: false)
            }
            .opacity(store.status == nil ? 1 : 0.55)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .frame(width: 236, alignment: .leading)
    }
}

/// A transparent layer on top of the widget that handles every mouse action itself:
/// click or right-click opens the menu, drag moves it. The position is saved only when
/// YOU drag it, never when macOS moves it around (for example while displays wake up).
final class InteractionView: NSView {
    var onClick: ((NSEvent) -> Void)? = nil
    var onRightClick: ((NSEvent) -> Void)? = nil
    var onDragEnd: (() -> Void)? = nil
    private var startMouse: NSPoint? = nil
    private var startOrigin: NSPoint? = nil
    private var moved = false

    override func hitTest(_ point: NSPoint) -> NSView? {
        frame.contains(point) ? self : nil
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if event.modifierFlags.contains(.control) {
            startMouse = nil
            onRightClick?(event)
            return
        }
        startMouse = NSEvent.mouseLocation
        startOrigin = window?.frame.origin
        moved = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard let m0 = startMouse, let o0 = startOrigin, let w = window else { return }
        let m = NSEvent.mouseLocation
        let dx = m.x - m0.x
        let dy = m.y - m0.y
        if !moved && abs(dx) < 3 && abs(dy) < 3 { return }
        moved = true
        w.setFrameOrigin(NSPoint(x: o0.x + dx, y: o0.y + dy))
    }

    override func mouseUp(with event: NSEvent) {
        guard startMouse != nil else { return }
        startMouse = nil
        startOrigin = nil
        if moved {
            onDragEnd?()
        } else {
            onClick?(event)
        }
    }

    override func rightMouseDown(with event: NSEvent) {
        onRightClick?(event)
    }
}

// MARK: - App

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let store = UsageStore()
    let signIn = SignInWindow()
    let widgetMenu = NSMenu()
    var interaction: InteractionView?
    var panel: NSPanel!
    var timers: [Timer] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).count > 1 {
            // Already running: ask that copy to show itself, then quit this one.
            DistributedNotificationCenter.default().postNotificationName(
                AppDelegate.showNotification, object: nil, userInfo: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        buildPanel()
        signIn.onSignedIn = { [weak self] in self?.store.refresh(force: true) }
        store.onNeedsSignIn = { [weak self] in self?.signIn.show() }
        store.refresh(force: true)

        timers.append(Timer.scheduledTimer(withTimeInterval: 120, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.store.refresh() }
        })
        timers.append(Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.store.tick() }
        })
        // Look for a new version shortly after starting, then about twice a day.
        timers.append(Timer.scheduledTimer(withTimeInterval: 60 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkForUpdates(userInitiated: false) }
        })
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 45_000_000_000)
            self?.checkForUpdates(userInitiated: false, force: true)
        }
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                self?.restorePosition()
                self?.store.refresh()
            }
        }
        // Displays connected, woke up or rearranged: put the widget back on its own display.
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.restorePosition()
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                self?.restorePosition()
            }
        }
        // Opening the app again while it's running brings the widget to the front briefly.
        DistributedNotificationCenter.default().addObserver(
            forName: AppDelegate.showNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.flashToFront() }
        }
        // Displays can take a few seconds to come up after login; check again shortly.
        Task { @MainActor [weak self] in
            for _ in 0..<6 {
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                self?.restorePosition()
            }
        }
    }

    static let showNotification = Notification.Name("com.joolbaby.claudeusage.show")

    // MARK: Position memory (which display + spot on it)

    private static let positionKey = "widgetPosition"

    private static func displayID(_ screen: NSScreen) -> Int {
        (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.intValue ?? 0
    }

    private func savePosition() {
        guard let screen = panel.screen ?? NSScreen.main else { return }
        let f = panel.frame
        let s = screen.frame
        let spot: [String: Any] = [
            "display": AppDelegate.displayID(screen),
            "name": screen.localizedName,
            "dx": Double(f.minX - s.minX),
            "dyTop": Double(s.maxY - f.maxY),
        ]
        UserDefaults.standard.set(spot, forKey: AppDelegate.positionKey)
    }

    /// Moves the widget to its saved spot if that display is connected.
    /// If it isn't (yet), parks it on the main display WITHOUT forgetting the saved spot.
    private func restorePosition() {
        guard panel != nil else { return }
        let size = panel.frame.size
        if let spot = UserDefaults.standard.dictionary(forKey: AppDelegate.positionKey) {
            let id = spot["display"] as? Int
            let name = spot["name"] as? String
            let screen = NSScreen.screens.first(where: { AppDelegate.displayID($0) == id })
                ?? NSScreen.screens.first(where: { $0.localizedName == name })
            if let screen = screen {
                let s = screen.frame
                let dx = CGFloat((spot["dx"] as? Double) ?? 24)
                let dyTop = CGFloat((spot["dyTop"] as? Double) ?? 24)
                let x = min(max(s.minX + dx, s.minX), s.maxX - size.width)
                let y = min(max(s.maxY - dyTop - size.height, s.minY), s.maxY - size.height)
                let target = NSPoint(x: x, y: y)
                if panel.frame.origin != target {
                    panel.setFrameOrigin(target)
                }
                return
            }
        }
        // No saved spot yet, or its display isn't connected: park on the main display.
        if let main = NSScreen.main {
            let v = main.visibleFrame
            let target = NSPoint(x: v.maxX - size.width - 24, y: v.maxY - size.height - 24)
            if panel.frame.origin != target {
                panel.setFrameOrigin(target)
            }
        }
    }

    private func flashToFront() {
        restorePosition()
        panel.level = .floating
        panel.orderFrontRegardless()
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            self?.applyLevel()
        }
    }

    private static func roundedMask(radius: CGFloat) -> NSImage {
        let edge = radius * 2 + 1
        let image = NSImage(size: NSSize(width: edge, height: edge), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }

    private func buildPanel() {
        let hosting = NSHostingView(rootView: WidgetView(store: store))
        let size = hosting.fittingSize
        let rect = NSRect(origin: .zero, size: size)

        panel = NSPanel(contentRect: rect,
                        styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered,
                        defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        panel.appearance = NSAppearance(named: .darkAqua)

        let effect = NSVisualEffectView(frame: rect)
        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.maskImage = AppDelegate.roundedMask(radius: 16)
        effect.autoresizingMask = [.width, .height]

        hosting.frame = effect.bounds
        hosting.autoresizingMask = [.width, .height]
        effect.addSubview(hosting)
        panel.contentView = effect

        let layer = InteractionView(frame: effect.bounds)
        layer.autoresizingMask = [.width, .height]
        effect.addSubview(layer)
        interaction = layer
        widgetMenu.delegate = self
        layer.onDragEnd = { [weak self] in self?.savePosition() }
        layer.onClick = { [weak self] event in self?.showMenu(event) }
        layer.onRightClick = { [weak self] event in self?.showMenu(event) }

        applyLevel()
        restorePosition()
        panel.orderFrontRegardless()
    }

    private func applyLevel() {
        if UserDefaults.standard.bool(forKey: "floatOnTop") {
            panel.level = .floating
        } else {
            panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
        }
    }

    // MARK: Updates

    private var autoUpdate: Bool {
        !UserDefaults.standard.bool(forKey: "autoUpdateOff")
    }

    func checkForUpdates(userInitiated: Bool, force: Bool = false) {
        let d = UserDefaults.standard
        if !userInitiated && !force,
           let last = d.object(forKey: "lastUpdateCheck") as? Date,
           Date().timeIntervalSince(last) < 12 * 60 * 60 {
            return
        }
        Task { @MainActor [weak self] in
            guard let self = self else { return }
            guard let latest = await Updater.latestVersion() else {
                if userInitiated { self.tell("Couldn't check for updates", "GitHub couldn't be reached. Try again later.") }
                return
            }
            d.set(Date(), forKey: "lastUpdateCheck")
            if Updater.isNewer(latest, than: Updater.currentVersion) {
                self.store.updateAvailable = latest
                if self.autoUpdate || userInitiated {
                    self.installUpdate()
                }
            } else {
                self.store.updateAvailable = nil
                if userInitiated {
                    self.tell("You're up to date", "Claude Usage \(Updater.currentVersion) is the newest version.")
                }
            }
        }
    }

    @objc func installUpdateNow() {
        installUpdate()
    }

    private func installUpdate() {
        guard !store.updating else { return }
        store.updating = true
        Task { @MainActor [weak self] in
            do {
                try await Updater.downloadAndInstall()
                Updater.relaunch()
            } catch {
                self?.store.updating = false
                var why = "The download didn't work."
                if case UpdateError.install(let message) = error { why = message }
                self?.tell("Update didn't finish", why + " It will try again later.")
            }
        }
    }

    private func tell(_ title: String, _ message: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }

    @objc func checkForUpdatesNow() {
        checkForUpdates(userInitiated: true)
    }

    @objc func toggleAutoUpdate() {
        let d = UserDefaults.standard
        d.set(!d.bool(forKey: "autoUpdateOff"), forKey: "autoUpdateOff")
    }

    private func showMenu(_ event: NSEvent) {
        guard let view = interaction else { return }
        let point = view.convert(event.locationInWindow, from: nil)
        widgetMenu.popUp(positioning: nil, at: point, in: view)
    }

    // The widget's menu, rebuilt each time it opens.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        if store.needsSignIn {
            add(menu, "Sign In to Claude…", #selector(showSignIn))
            menu.addItem(.separator())
        }
        if let v = store.updateAvailable, !store.updating {
            add(menu, "Update to Version \(v) Now", #selector(installUpdateNow))
            menu.addItem(.separator())
        }

        if let d = store.detail {
            let info = NSMenuItem(title: d, action: nil, keyEquivalent: "")
            info.isEnabled = false
            menu.addItem(info)
            menu.addItem(.separator())
        } else if let t = store.lastUpdated {
            let time = DateFormatter.localizedString(from: t, dateStyle: .none, timeStyle: .short)
            let info = NSMenuItem(title: "Updated \(time)", action: nil, keyEquivalent: "")
            info.isEnabled = false
            menu.addItem(info)
            menu.addItem(.separator())
        }

        add(menu, "Refresh Now", #selector(refreshNow))
        add(menu, "Open Usage Page", #selector(openUsage))
        menu.addItem(.separator())

        let top = add(menu, "Keep On Top of Windows", #selector(toggleTop))
        top.state = UserDefaults.standard.bool(forKey: "floatOnTop") ? .on : .off

        if store.orgs.count > 1 {
            let sub = NSMenu()
            for org in store.orgs {
                let item = NSMenuItem(title: org.name, action: #selector(selectOrg(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = org.id
                item.state = (org.id == store.currentOrgID) ? .on : .off
                sub.addItem(item)
            }
            let parent = NSMenuItem(title: "Account", action: nil, keyEquivalent: "")
            parent.submenu = sub
            menu.addItem(parent)
        }

        menu.addItem(.separator())
        add(menu, "Sign In to Claude…", #selector(showSignIn))
        add(menu, "Paste Session Key Manually…", #selector(enterKey))
        menu.addItem(.separator())
        add(menu, "Check for Updates…", #selector(checkForUpdatesNow))
        let auto = add(menu, "Install Updates Automatically", #selector(toggleAutoUpdate))
        auto.state = autoUpdate ? .on : .off
        let version = NSMenuItem(title: "Version \(Updater.currentVersion)", action: nil, keyEquivalent: "")
        version.isEnabled = false
        menu.addItem(version)
        menu.addItem(.separator())
        add(menu, "Quit Claude Usage", #selector(quit))
    }

    @discardableResult
    private func add(_ menu: NSMenu, _ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        menu.addItem(item)
        return item
    }

    @objc func refreshNow() {
        store.refresh(force: true)
    }

    @objc func showSignIn() {
        signIn.show()
    }

    @objc func openUsage() {
        if let url = URL(string: "https://claude.ai/settings/usage") {
            NSWorkspace.shared.open(url)
        }
    }

    @objc func toggleTop() {
        let d = UserDefaults.standard
        d.set(!d.bool(forKey: "floatOnTop"), forKey: "floatOnTop")
        applyLevel()
    }

    @objc func selectOrg(_ sender: NSMenuItem) {
        if let id = sender.representedObject as? String {
            UserDefaults.standard.set(id, forKey: "orgID")
            store.refresh()
        }
    }

    @objc func enterKey() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Paste your claude.ai session key"
        alert.informativeText = "Only needed if Sign In to Claude doesn't work. In Chrome on claude.ai press Option-Command-I, open Application → Cookies → https://claude.ai, and copy the value of \"sessionKey\"."
        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        field.placeholderString = "sk-ant-sid01-…"
        alert.accessoryView = field
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        if alert.runModal() == .alertFirstButtonReturn {
            let value = field.stringValue
                .replacingOccurrences(of: "sessionKey=", with: "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !value.isEmpty {
                Task { @MainActor [weak self] in
                    await WebSession.save(value)
                    self?.store.refresh(force: true)
                }
            }
        }
    }

    @objc func quit() {
        NSApp.terminate(nil)
    }
}

@main
@MainActor
struct ClaudeUsageMain {
    static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let delegate = AppDelegate()
        app.delegate = delegate
        withExtendedLifetime(delegate) {
            app.run()
        }
    }
}
