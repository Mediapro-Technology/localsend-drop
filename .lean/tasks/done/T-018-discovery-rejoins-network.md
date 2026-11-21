---
id: T-018
title: PCs keep hearing each other after Wi-Fi reconnects
spec: S-001
status: done
depends: [T-017]
files: [Discovery.cs, tests/LocalSendDrop.Tests/DiscoveryRejoinTests.cs]
tdd: strict
done: 2026-11-21
---
## Goal
2026-11-21: the rizwan PC showed up here only while its mascot was hovered (its own announce), never in answer to ours.
Discovery joined the multicast group once, at start, on the interfaces up then; a Wi-Fi reconnect (sleep, roaming)
drops the membership, so that PC stops hearing announcements while its own still go out.

## Steps
1. `Discovery.Rejoin(UdpClient, IEnumerable<IPAddress>)`: drop then join the group on every current IPv4 interface, errors ignored; returns how many joined.
2. Call it at start, on NetworkChange.NetworkAddressChanged / NetworkAvailabilityChanged, and before each periodic announce.
3. Tests: rejoining twice on the same socket works (no "already joined" failure); a datagram sent to the group arrives after a rejoin.

## Done when
- [ ] `lean test` passes
- [ ] DiscoveryRejoinTests pass
