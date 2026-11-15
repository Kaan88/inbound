// inbound-menu: Inbound's menu-bar icon on macOS, the counterpart of the tray on Windows and Linux.
//
// The Host starts it (MacMenuBar.cs) and is its only peer: each menu choice is written to stdout as one line
// ("open", "settings", "pause", "resume", "exit"), and the Host writes "tooltip <text>" lines to stdin. When the Host
// ends, stdin closes and this ends too. MAC.5: "clipboard on" / "clipboard off" from the Host; while on, text copied
// after that comes back as "clipboard <base64>" (the change count is read twice a second; the text only on a change). Built by scripts/package-macos.sh with swiftc; no bundle, no Dock icon.
import AppKit

final class MenuBar: NSObject, NSApplicationDelegate {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private var clipboardTimer: Timer?
    private var lastChange = NSPasteboard.general.changeCount

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let button = item.button {
            let image = NSImage(systemSymbolName: "arrow.down.circle", accessibilityDescription: "Inbound Download Manager")
            image?.isTemplate = true // follows the menu bar's light or dark look
            button.image = image
            button.toolTip = "Inbound Download Manager"
        }

        let menu = NSMenu()
        add(menu, "Open Inbound", "open")
        add(menu, "Options…", "settings")
        menu.addItem(.separator())
        add(menu, "Pause All", "pause")
        add(menu, "Resume All", "resume")
        menu.addItem(.separator())
        add(menu, "Quit Inbound", "exit")
        item.menu = menu

        Thread {
            while let line = readLine() {
                DispatchQueue.main.async { self.receive(line) }
            }
            DispatchQueue.main.async { NSApp.terminate(nil) } // the Host has gone
        }.start()
    }

    private func add(_ menu: NSMenu, _ title: String, _ command: String) {
        let entry = NSMenuItem(title: title, action: #selector(choose(_:)), keyEquivalent: "")
        entry.target = self
        entry.representedObject = command
        menu.addItem(entry)
    }

    @objc private func choose(_ sender: NSMenuItem) {
        guard let command = sender.representedObject as? String else { return }
        print(command)
        fflush(stdout)
    }

    private func receive(_ line: String) {
        if line.hasPrefix("tooltip ") {
            item.button?.toolTip = "Inbound Download Manager\n" + line.dropFirst("tooltip ".count)
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
        print("clipboard " + Data(text.utf8).base64EncodedString())
        fflush(stdout)
    }
}

signal(SIGPIPE, SIG_IGN)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = MenuBar()
app.delegate = delegate
app.run()
