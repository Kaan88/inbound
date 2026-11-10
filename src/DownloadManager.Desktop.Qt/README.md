# Qt 6 desktop prototype

Build with Qt 6 Widgets and a C++17 compiler. From a separate build directory,
run `qmake6 /path/to/src/DownloadManager.Desktop.Qt/DownloadManager.Desktop.Qt.pro`
and `make` (or the matching Windows make tool).

From the repo root, publish the reader with
`dotnet publish src/DownloadManager.Desktop.Qt.HostReader -o /path/to/qt-build`.
The Qt executable finds the reader beside itself. Run both against an already
running Inbound Host; they inherit the Host's instance selection from the
environment. For development, use a separate Host profile.

The helper uses the shared .NET Contracts client, including its local peer
verification. It streams display fields to Qt and sends explicit download
commands. The window keeps the last list on disconnect and disables commands
until the Host returns. Remove Entry keeps the downloaded file.
