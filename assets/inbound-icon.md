# Inbound app icon

The artwork is the owner's own, AI-generated (`inbound-icon-source.png`): a plane descending towards an inbox tray. `scripts\prepare-inbound-icon.ps1` makes it a transparent 1024 px master (`inbound-icon.png`), and `scripts\build-icons.ps1` writes `app.ico`, `app.png` and the extension icons.

The extension's 128 px icon has 96 px of artwork and 16 px of transparent padding per side, as the Chrome Web Store asks.

Existing application IDs, native-host name, extension key, update feed identity, install/data directories and shortcut filenames are retained for upgrade compatibility. Display branding is Inbound Download Manager.
