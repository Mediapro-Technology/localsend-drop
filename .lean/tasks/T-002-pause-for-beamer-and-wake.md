---
id: T-002
title: Sharing pauses while Beamer runs
spec: S-001
status: todo
depends: [T-001]
files: [BeamerWatch.cs, Program.cs, DropPanel.cs, tests/LocalSendDrop.Tests/BeamerWatchTests.cs]
tdd: after
---
## Goal
While Beamer.exe runs, LocalSend Drop's mouse and keyboard sharing is off and the panel says why; it comes back by itself when Beamer quits (A1). Also a "Wake" item for an offline paired PC in the panel menu (A12, manual part).

## Steps
1. `BeamerWatch.cs`: poll `Process.GetProcessesByName("Beamer")` every 2 s; `event Action<bool> Changed`. The check sits behind a `Func<bool>` so tests can fake it.
2. `Program.cs`: Beamer running → `kvm.TakeBack()`, `kvm.Dispose()`, `kvm = null`; gone → create Kvm again. The existing lambdas capture the `kvm` variable, so they follow. Log both in KvmLog.
3. `DropPanel.cs`: "Paused: Beamer is running" line in the Mouse and keyboard menu while paused; "Wake" item in a paired device's submenu when it's offline and has a `Mac` (calls `WakeOnLan.Send`).
4. Test after: `BeamerWatchTests.Changed_FiresOnStartAndStop` with the fake check.

## Done when
- [ ] `lean test` passes
- [ ] A1: with Beamer.exe running, no crossing and the panel shows the pause line; within 5 s of quitting Beamer, crossing works
- [ ] A12: the Wake item sends a packet (KvmLog line)
