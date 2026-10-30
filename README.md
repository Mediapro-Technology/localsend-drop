# LocalSend Drop

Send files between PCs on the office network by dragging them into a corner of the screen. No LocalSend window, no accounts, nothing leaves the network.

- **Send:** drag files or folders into the drop corner (top left by default). A panel lists the PCs and phones nearby; drop on one.
- **Explorer:** right-click a file, then **Send with LocalSend Drop**. On Windows 11 the app adds itself to the short menu on first run; Windows asks once for admin approval to trust the Mediapro Technology certificate. Decline and it stays under *Show more options*; add it later from Settings.
- **Receive:** a card pops up in the corner. Accept, and files land in Downloads.
- **Recent:** the panel lists your last transfers. Click to open, right-click to show in folder, click a failed send to retry.
- **Settings:** run the .exe again, or use the gear in the panel. Rename this PC, choose the save folder, how incoming files are accepted, the drop monitor and corner, sound, start with Windows, automatic updates.

It speaks the [LocalSend protocol v2](https://github.com/localsend/protocol), so phones running the normal LocalSend app can send and receive too. It replaces the LocalSend app on Windows PCs; quit LocalSend there.

## Install

1. Download `LocalSendDrop.exe` from the [latest release](../../releases/latest).
2. Put it somewhere permanent and writable, such as `C:\Tools\LocalSendDrop.exe` (automatic updates replace the file in place).
3. Run it. SmartScreen: **More info**, then **Run anyway**. Firewall: allow on private networks, or you won't be able to receive.

It starts with Windows and checks this repo's releases for updates every 6 hours.

## Build

Needs the .NET 9 SDK on Windows.

```powershell
dotnet build                     # debug
./release.ps1 1.2.1 "What changed"   # publish dist/LocalSendDrop.exe and create the GitHub release
```

Diagnostics: `LocalSendDrop.exe --list` prints the devices it can see; `--send <path> <name>` sends without the UI.

## How it works

| File | Role |
|---|---|
| `Discovery.cs` | Multicast announce on 224.0.0.167:53317; peers answer via `POST /register` |
| `Server.cs` | HTTPS receiver (Kestrel) on 53317, or the next free port |
| `Protocol.cs` | Sending: `prepare-upload`, then `upload` per file |
| `Identity.cs` | Self-signed RSA certificate (its SHA-256 is the device fingerprint) and settings |
| `DropPanel.cs`, `IncomingCard.cs`, `SettingsForm.cs`, `Theme.cs` | The UI |
| `Updater.cs` | Self-update from GitHub releases, verified against the asset's SHA-256 digest |
| `Shell.cs` | Classic Explorer menu entry and the pipe that batches multi-file selections |
| `ShellPackage.cs`, `shell/` | Windows 11 short-menu entry: C++ `IExplorerCommand` DLL in a signed sparse MSIX. Rebuild with `shell/build.ps1` (needs VS C++ tools, Windows SDK, and the signing key kept outside the repo); the outputs in `shell/bin` are committed and embedded in the exe |

Settings, identity and history live in `%APPDATA%\LocalSendDrop`.
