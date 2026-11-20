---
id: T-001
title: Groundwork for layout, sync and tests
spec: S-001
status: done
depends: []
files: [tests/LocalSendDrop.Tests/LocalSendDrop.Tests.csproj, tests/LocalSendDrop.Tests/KvmZonesTests.cs, tests/LocalSendDrop.Tests/KvmSettingsTests.cs, LocalSendDrop.csproj, KvmZones.cs, KvmSettings.cs, KvmSync.cs, KvmLook.cs, WakeOnLan.cs, KvmWire.cs, Pairing.cs, Identity.cs, Kvm.cs, KvmRemote.cs, .lean/config.json]
tdd: strict
done: 2026-11-20
---
## Goal
Contracts card: the shared model, wire messages and test project every other S-001 card builds on, so T-002/T-003/T-005/T-006 can run in parallel with disjoint files. Many files because most are new and small (stubs other cards fill); its Kvm.cs/KvmRemote.cs edits are only message routing.

## Steps
1. Test project `tests/LocalSendDrop.Tests` (xUnit, net9.0-windows10.0.19041.0, UseWindowsForms, win-x64) with a ProjectReference to the app. In `LocalSendDrop.csproj`: `<Compile Remove="tests/**" />` and `InternalsVisibleTo LocalSendDrop.Tests`. Set `.lean/config.json` `test` to `dotnet test tests/LocalSendDrop.Tests`.
2. Test-first `KvmZones.cs`: `record DisplayInfo(int Index, Rectangle Bounds)`; `record Zone(int Display, Edge Side, double From, double To, string Peer, int TargetDisplay, Edge TargetSide, double TargetFrom, double TargetTo)`. Pure functions: `Hit(zones, displays, Point) -> Zone?` (on that display's edge, within span, not where another monitor continues), `Target(zone, fraction) -> fraction along target span`, `Migrate(PairedPc, displays)` → full-span zone on the outer monitor edge (A6), corner/notch hit helpers. Tests: `Hit_PartialSpan`, `Hit_InnerEdgeBetweenMonitors_IsNull`, `Target_MapsSpans`, `Migrate_RightEdge_TwoMonitors`.
3. Test-first `KvmSettings.cs`: `Stamped<T>(T Value, long SetAt, string By)` and `CrossingSettings` (Shortcut, DoubleTapMs=300, ResistancePx=120, Corners, Notch, Glow, Arrival, HideIdle, PointerSpeed, ScrollSpeed, ReverseScroll, Layout). `Merge(mine, theirs)` keeps the newer stamp per value; ties broken by `By`. Tests: `Merge_NewerWins`, `Merge_TieIsStable`, `Defaults_MatchSpec`.
4. `Identity.cs` Settings: `Crossing` (CrossingSettings), `Zones` (List<Zone>). `Pairing.cs` PairedPc: `Mac`. On load, an empty `Zones` is filled by migrating each PairedPc.Edge.
5. `KvmWire.cs`: `EnterAt` (u8 display, u8 side, u16 fraction), `Displays` (json DisplayInfo[]), `Sync` (json CrossingSettings). Old `Enter` kept for older peers.
6. `Kvm.cs`/`KvmRemote.cs`: route `Displays`/`Sync` to `KvmSync.OnMessage(fingerprint, m)`; on link connect call `KvmSync.Hello(send)`, which sends our displays. `KvmSync.cs` stores `PeerDisplays[fp]`; the settings merge is a stub for T-005. `HiddenCursor.Hide/Show(string reason)` with a reason set (crossing, idle) so T-006 can't unhide a crossed pointer.
7. `KvmLook.cs` stub with `Start()` called from the Kvm constructor; `KvmZones.Arrived` event (Point) for T-003 to raise. `WakeOnLan.cs`: `Send(mac)` magic packet to 255.255.255.255:9; `LearnMac(ip)` via `SendARP`.

## Done when
- [ ] `dotnet build` and `lean test` pass, tests above green
- [ ] A6 (model part): `Migrate_*` tests pass
- [ ] A13: `LocalSendDrop.exe --kvm-loopback` still passes

## Watch
- Zones replace one-edge-per-PC; all crossing math goes through zones (zk 20261120-122838-2)
- Sync: newer set-at stamp wins (zk 20261120-122839)
- No Beamer code copied, behaviour only (zk 20261120-122838)
