---
status: done
done: 2026-11-20
---
﻿---
id: T-014
title: Only one PC has the mouse and keyboard at a time
spec: S-001
status: todo
depends: []
files: [KvmRemote.cs, Kvm.cs, KvmWire.cs, tests/LocalSendDrop.Tests/OwnerTests.cs]
tdd: strict
---
## Goal
Root cause from both logs (16:01): HARSHID-PC controlled DEV_PC, then DEV_PC's own double-tap shortcut crossed to
HARSHID-PC (at {1918,540}, the shortcut's mid-edge point), and HARSHID-PC accepted while its own input was away.
Both PCs then held each other's input. Beamer has one owner of the input (core/owner.py). The controlled PC
switches the input home and never starts its own take, and a PC whose input is away refuses with "busy".
## Steps
1. Test first: EnterRefusal(..., away) gives Busy (KvmMsg.Busy = 18); HandBack/Controlled with no session.
2. KvmRemote: Away hook (set by Kvm), Busy refusal, Controlled, HandBack(why) sends Leave and lets go.
3. Kvm: the shortcut while controlled hands back; a crossing while controlled is dropped (Free); Busy releases with a message.
## Done when
- [ ] `lean test` passes with OwnerTests; `--kvm-loopback` passes
- [ ] Double-tap on DEV_PC while HARSHID-PC controls it gives control back; it never ends with both PCs captured
