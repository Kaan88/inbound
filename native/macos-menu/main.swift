// inbound-menu: Inbound's menu-bar icon on macOS, the counterpart of the tray on Windows and Linux.
//
// The Host starts it (MacMenuBar.cs) and is its only peer: each menu choice is written to stdout as one line
// ("open", "settings", "pause", "resume", "exit"), and the Host writes "tooltip <text>" lines to stdin. When the Host
// ends, stdin closes and this ends too. Built by scripts/package-macos.sh with swiftc; no bundle, no Dock icon.
import AppKit

final class MenuBar: NSObject, NSApplicationDelegate {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)

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
        }
    }
}

signal(SIGPIPE, SIG_IGN)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = MenuBar()
app.delegate = delegate
app.run()
