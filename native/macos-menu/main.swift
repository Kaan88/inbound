// inbound-menu: Inbound's menu-bar icon on macOS, the counterpart of the tray on Windows and Linux.
//
// Built by scripts/package-macos.sh into Inbound.app/Contents/Helpers/Inbound Menu.app (a bundle of its own, so its
// notifications are Inbound's and it can offer a Services entry). Two ways it runs:
//
// - Started by the Host with --host (MacMenuBar.cs). The Host is its only peer, one line per message:
//   Host -> helper: "tooltip <text>", "clipboard on|off", "status <base64 json>", "notify <base64 json>".
//   helper -> Host: "open", "settings", "pause", "resume", "exit", "folder",
//     "clipboard <base64>" (copied text, while on), "add <base64>" (text dropped on the icon, sent by Services or
//     from Add from Clipboard),
//     "addfile <base64 path>" (a file dropped on the icon), "job <pause|resume|show|open|reveal|retry> <id>",
//     "wake", "clock", "network <expensive|constrained|normal>", "power <low|normal>",
//     "notifications <on|off|error>".
//   When the Host ends, stdin closes and this ends too.
// - Started by macOS for Services > Download with Inbound while no Host-run copy is up: no icon; it hands the text
//   to the CLI beside the Host and quits.
import AppKit
import Network
import UserNotifications

func send(_ line: String) {
    print(line)
    fflush(stdout)
}

func b64(_ text: String) -> String { Data(text.utf8).base64EncodedString() }

func unb64(_ text: Substring) -> String? {
    guard let data = Data(base64Encoded: String(text)) else { return nil }
    return String(data: data, encoding: .utf8)
}

struct Status: Decodable {
    struct Item: Decodable {
        let id: String
        let name: String
        let percent: Int?
        let state: String
    }
    let active: Int
    let speed: String?
    let items: [Item]
}

struct Notice: Decodable {
    let title: String
    let body: String
    let job: String?
    let kind: String? // "completed" or "failed"
}

/// Takes drops on the menu-bar icon: links and text become "add", files "addfile". Clicks go on to the button.
final class DropView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        registerForDraggedTypes([.URL, .fileURL, .string])
        autoresizingMask = [.width, .height]
    }
    required init?(coder: NSCoder) { fatalError() }

    override func mouseDown(with event: NSEvent) { superview?.mouseDown(with: event) }
    override func rightMouseDown(with event: NSEvent) { superview?.rightMouseDown(with: event) }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { .copy }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let board = sender.draggingPasteboard
        var handled = false
        if let urls = board.readObjects(forClasses: [NSURL.self], options: nil) as? [URL] {
            for url in urls {
                if url.isFileURL { send("addfile " + b64(url.path)) } else { send("add " + b64(url.absoluteString)) }
                handled = true
            }
        }
        if !handled, let text = board.string(forType: .string), !text.isEmpty {
            send("add " + b64(text))
            handled = true
        }
        return handled
    }
}

final class MenuBar: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    private let hosted = CommandLine.arguments.contains("--host")
    private var item: NSStatusItem?
    private let menu = NSMenu()
    private var status = Status(active: 0, speed: nil, items: [])
    private var clipboardTimer: Timer?
    private var lastChange = NSPasteboard.general.changeCount
    private let pathMonitor = NWPathMonitor()
    private var lastNetwork = ""
    private var notificationsOn = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.servicesProvider = self
        NSUpdateDynamicServices()
        guard hosted else {
            // Started by macOS for a Services request: answer it, then quit.
            DispatchQueue.main.asyncAfter(deadline: .now() + 15) { NSApp.terminate(nil) }
            return
        }

        let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item = statusItem
        if let button = statusItem.button {
            button.toolTip = "Inbound Download Manager"
            button.addSubview(DropView(frame: button.bounds))
        }
        setIcon(active: false)
        rebuildMenu()
        statusItem.menu = menu

        watchSystem()
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        center.requestAuthorization(options: [.alert, .sound]) { granted, error in
            DispatchQueue.main.async {
                self.notificationsOn = granted
                send("notifications " + (error != nil ? "error" : granted ? "on" : "off"))
            }
        }
        center.setNotificationCategories([
            UNNotificationCategory(identifier: "completed", actions: [
                UNNotificationAction(identifier: "open", title: "Open"),
                UNNotificationAction(identifier: "reveal", title: "Show in Finder"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: "failed", actions: [
                UNNotificationAction(identifier: "retry", title: "Retry"),
            ], intentIdentifiers: []),
        ])

        Thread {
            while let line = readLine() {
                DispatchQueue.main.async { self.receive(line) }
            }
            DispatchQueue.main.async { NSApp.terminate(nil) } // the Host has gone
        }.start()
    }

    // MARK: the menu

    private func setIcon(active: Bool) {
        let name = active ? "arrow.down.circle.fill" : "arrow.down.circle"
        let image = NSImage(systemSymbolName: name, accessibilityDescription: "Inbound Download Manager")
        image?.isTemplate = true // follows the menu bar's light or dark look
        item?.button?.image = image
    }

    private func rebuildMenu() {
        menu.removeAllItems()
        let summary: String
        switch status.active {
        case 0: summary = "No downloads running"
        case 1: summary = "1 download running"
        default: summary = "\(status.active) downloads running"
        }
        let header = NSMenuItem(title: status.speed.map { "\(summary) · \($0)" } ?? summary, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        for job in status.items.prefix(5) {
            let percent = job.percent.map { " — \($0)%" } ?? ""
            let name = job.name.count > 40 ? String(job.name.prefix(39)) + "…" : job.name
            let entry = NSMenuItem(title: name + percent, action: nil, keyEquivalent: "")
            let sub = NSMenu()
            let paused = job.state == "paused" || job.state == "stopped"
            sub.addItem(choice(paused ? "Resume" : "Pause", "job \(paused ? "resume" : "pause") \(job.id)"))
            sub.addItem(choice("Show in Inbound", "job show \(job.id)"))
            entry.submenu = sub
            menu.addItem(entry)
        }
        menu.addItem(.separator())
        menu.addItem(choice("Open Inbound", "open"))
        menu.addItem(choice("Add from Clipboard", "addclipboard"))
        menu.addItem(choice("Open Downloads Folder", "folder"))
        menu.addItem(choice("Options…", "settings"))
        menu.addItem(.separator())
        menu.addItem(choice("Pause All", "pause"))
        menu.addItem(choice("Resume All", "resume"))
        menu.addItem(.separator())
        menu.addItem(choice("Quit Inbound", "exit"))
    }

    private func choice(_ title: String, _ command: String) -> NSMenuItem {
        let entry = NSMenuItem(title: title, action: #selector(choose(_:)), keyEquivalent: "")
        entry.target = self
        entry.representedObject = command
        return entry
    }

    @objc private func choose(_ sender: NSMenuItem) {
        guard let command = sender.representedObject as? String else { return }
        if command == "addclipboard" {
            // What the user asked for, so not the watcher's rules; the Host takes the links out of the text.
            if let text = NSPasteboard.general.string(forType: .string), !text.isEmpty, text.utf8.count <= 65536 {
                send("add " + b64(text))
            }
            return
        }
        send(command)
    }

    // MARK: from the Host

    private func receive(_ line: String) {
        if line.hasPrefix("tooltip ") {
            item?.button?.toolTip = "Inbound Download Manager\n" + line.dropFirst("tooltip ".count)
        } else if line.hasPrefix("status "), let json = unb64(line.dropFirst("status ".count)),
                  let next = try? JSONDecoder().decode(Status.self, from: Data(json.utf8)) {
            status = next
            setIcon(active: next.active > 0)
            rebuildMenu()
        } else if line.hasPrefix("notify "), let json = unb64(line.dropFirst("notify ".count)),
                  let notice = try? JSONDecoder().decode(Notice.self, from: Data(json.utf8)) {
            post(notice)
        } else if line == "clipboard on" {
            guard clipboardTimer == nil else { return }
            lastChange = NSPasteboard.general.changeCount // what was copied before stays out
            clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.pollClipboard() }
        } else if line == "clipboard off" {
            clipboardTimer?.invalidate()
            clipboardTimer = nil
        }
    }

    private func pollClipboard() {
        let board = NSPasteboard.general
        guard board.changeCount != lastChange else { return }
        lastChange = board.changeCount
        // Passwords from password managers are marked concealed or transient: never read those.
        let types = board.types ?? []
        if types.contains(NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")) ||
            types.contains(NSPasteboard.PasteboardType("org.nspasteboard.TransientType")) { return }
        guard let text = board.string(forType: .string), !text.isEmpty, text.utf8.count <= 65536 else { return }
        send("clipboard " + b64(text))
    }

    // MARK: notifications

    private func post(_ notice: Notice) {
        let content = UNMutableNotificationContent()
        content.title = notice.title
        content.body = notice.body
        if let job = notice.job { content.userInfo = ["job": job] }
        if let kind = notice.kind, notice.job != nil { content.categoryIdentifier = kind }
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        if let job = response.notification.request.content.userInfo["job"] as? String {
            switch response.actionIdentifier {
            case "open": send("job open \(job)")
            case "reveal": send("job reveal \(job)")
            case "retry": send("job retry \(job)")
            case UNNotificationDefaultActionIdentifier: send("job show \(job)")
            default: break
            }
        } else if response.actionIdentifier == UNNotificationDefaultActionIdentifier {
            send("open")
        }
        completionHandler()
    }

    // MARK: sleep, clock, network and power

    private func watchSystem() {
        let workspace = NSWorkspace.shared.notificationCenter
        workspace.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { _ in send("wake") }
        for name in [Notification.Name.NSSystemClockDidChange, .NSSystemTimeZoneDidChange] {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in send("clock") }
        }
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let state = path.isConstrained ? "constrained" : path.isExpensive ? "expensive" : "normal"
            DispatchQueue.main.async {
                guard let self, state != self.lastNetwork else { return }
                self.lastNetwork = state
                send("network " + state)
            }
        }
        pathMonitor.start(queue: DispatchQueue(label: "inbound.network"))
        let power = { send("power " + (ProcessInfo.processInfo.isLowPowerModeEnabled ? "low" : "normal")) }
        NotificationCenter.default.addObserver(forName: .NSProcessInfoPowerStateDidChange, object: nil, queue: .main) { _ in power() }
        power()
    }

    // MARK: Services > Download with Inbound

    @objc func downloadWithInbound(_ board: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        var texts: [String] = []
        if let urls = board.readObjects(forClasses: [NSURL.self], options: nil) as? [URL] {
            texts = urls.filter { !$0.isFileURL }.map(\.absoluteString)
        }
        if texts.isEmpty, let text = board.string(forType: .string) { texts = [text] }
        guard !texts.isEmpty else {
            error.pointee = "Nothing to download was selected." as NSString
            return
        }
        if hosted {
            for text in texts { send("add " + b64(text)) }
            return
        }
        // No Host-run copy: through the CLI beside the Host (Contents/Helpers/Inbound Menu.app -> Contents/Resources/host).
        let cli = Bundle.main.bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Resources/host/DownloadManager.Cli")
        for link in texts.flatMap(webLinks) {
            let task = Process()
            task.executableURL = cli
            task.arguments = ["add", link]
            try? task.run()
            task.waitUntilExit()
        }
        NSApp.terminate(nil)
    }
}

/// The http and https addresses in some text, one per word: what the CLI is given (never a word that starts with "-").
func webLinks(_ text: String) -> [String] {
    text.split(whereSeparator: { $0.isWhitespace }).compactMap { word in
        guard let url = URL(string: String(word)), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", url.host != nil else { return nil }
        return url.absoluteString
    }
}

signal(SIGPIPE, SIG_IGN)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = MenuBar()
app.delegate = delegate
app.run()
