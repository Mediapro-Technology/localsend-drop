---
id: 20261120-122838
type: decision
title: Rebuild Beamer features in our own C# KVM instead of speaking Beamer wire v6
tags: [kvm, beamer, licence]
links: []
source: S-001
created: 2026-11-20
---
Why: office is Windows-only, so Beamer interop (Mac support) buys nothing; Beamer is GPL-3.0 and LocalSend Drop has no GPL licence, so its code can't be copied.
Apply: implement from our own design; read Beamer only for behaviour, never paste its code.
