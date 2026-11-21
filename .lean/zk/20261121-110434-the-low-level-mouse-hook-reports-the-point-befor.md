---
id: 20261121-110434
type: gotcha
title: The low-level mouse hook reports the point before Windows clips it to the screens
tags: [kvm, input, hook]
links: []
source: T-017
created: 2026-11-21
---
Why: pushing past the left edge, MSLLHOOKSTRUCT.pt reads X=-5 (measured on HARSHID-PC); any on-monitor test then fails, so the edge push reset every move and only a slow push ever crossed.
