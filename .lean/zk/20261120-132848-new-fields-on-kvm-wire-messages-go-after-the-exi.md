---
id: 20261120-132848
type: gotcha
title: New fields on KVM wire messages go after the existing ones, so older peers still read them
tags: [kvm, wire, compat]
links: []
source: T-008
created: 2026-11-20
---
Why: Leave/Enter keep the fraction at offset 1; LeaveAt/EnterAt append display and side after it, and old peers ignore the tail.
Apply: extend a message by appending; never move or resize an existing field.
