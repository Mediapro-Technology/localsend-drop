# LocalSend Drop

Send files between PCs on the office network by dragging them into a corner of the screen. No LocalSend window, no accounts, nothing leaves the network.

- **Send:** drag files or folders into the drop corner (top left by default). A panel lists the PCs and phones nearby; drop on one.
- **Explorer:** right-click a file, then **Send with LocalSend Drop**.
- **Receive:** a card pops up in the corner. Accept, and files land in Downloads.
- **Recent:** the panel lists your last transfers. Click to open, right-click to show in folder, click a failed send to retry.
- **Settings:** open LocalSend Drop from the Start menu while it's running, or use the gear in the panel. Rename this PC, choose the save folder, how incoming files are accepted, the drop monitor and corner, sound, start with Windows, automatic updates.

It speaks the [LocalSend protocol v2](https://github.com/localsend/protocol), so phones running the normal LocalSend app can send and receive too. It replaces the LocalSend app on Windows PCs; quit LocalSend there.

## Install

1. Download **[LocalSendDrop-Setup.exe](../../releases/latest/download/LocalSendDrop-Setup.exe)**.
2. Double-click it. SmartScreen: **More info**, then **Run anyway**. Windows asks for admin approval: **Yes**.
3. Done: it trusts the Mediapro Technology certificate, installs the app (Start menu, right-click menu, firewall rules) and starts it.

It starts with Windows and updates itself from this repo's releases. Uninstall from **Settings > Apps**. Install it while signed in as the person who'll use it: the app is installed for the signed-in Windows account.

Or, in PowerShell as administrator: `irm https://github.com/Mediapro-Technology/localsend-drop/releases/latest/download/install.ps1 | iex`

Why not just the .msix? It's signed with our own certificate, and Windows won't open a package until the PC trusts its certificate, so the package can't install that certificate itself. The setup does both. With a bought code-signing certificate, double-clicking the .msix would be enough.

PCs on 1.x move to the package on their next automatic update, with no prompt if they had already approved the certificate.

## Build

Needs the .NET 9 SDK and the Windows SDK; rebuilding the right-click handler also needs Visual Studio's C++ tools.

```powershell
./release.ps1 2.0.1 "What changed"   # build exe + signed msix, tag, publish the GitHub release
```

Signing uses a self-made `CN=Mediapro Technology` certificate whose private key is kept outside the repo (`D:\Tools\LocalSendDrop-signing`). Back it up: a new key means every PC has to trust a new certificate.

Diagnostics: `LocalSendDrop.exe --list` prints the devices it can see; `--send <path> <name>` sends without the UI.

## How it works

| File | Role |
|---|---|
| `Discovery.cs` | Multicast announce on 224.0.0.167:53317; peers answer via `POST /register` |
| `Server.cs` | HTTPS receiver (Kestrel) on 53317, or the next free port |
| `Protocol.cs` | Sending: `prepare-upload`, then `upload` per file |
| `Identity.cs` | Self-signed RSA certificate (its SHA-256 is the device fingerprint) and settings |
| `DropPanel.cs`, `IncomingCard.cs`, `SettingsForm.cs`, `Theme.cs` | The UI |
| `Packaging.cs` | Package detection, certificate trust, installing the MSIX (and moving a plain .exe into it) |
| `Updater.cs` | Self-update from GitHub releases, verified against the asset's SHA-256 digest |
| `Shell.cs` | Pipe that batches multi-file right-click selections; classic menu entry for a plain .exe |
| `shell/` | Package manifest, C++ `IExplorerCommand` right-click handler, `build.ps1` that packages, signs and builds the setup |
| `setup/` | `LocalSendDrop-Setup.exe` (C++): carries the certificate and package, trusts one and installs the other |

Settings, identity and history live in `%APPDATA%\LocalSendDrop`.
