---
id: 20261120-130030
type: fact
title: WH_KEYBOARD_LL sees and can swallow VK_MEDIA_* and VK_VOLUME_* on standard keyboards; vendor Fn hotkeys may bypass it
tags: [kvm, input, media-keys]
links: []
source: S-001
created: 2026-11-20
---
Source: https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-appcommand (WM_APPCOMMAND comes from DefWindowProc after the key); community hook snippets. Confidence medium, not hardware-tested.
Apply: capture media keys in the low-level hook; Raw Input on page 0x0C can only observe, never block; OEM/ACPI Fn keys are a known limitation.
