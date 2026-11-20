---
id: 20261120-131004
type: gotcha
title: Theme colours live in static class Ui (Theme.cs), not in a Theme class
tags: [ui, theme]
links: []
source: T-006
created: 2026-11-20
---
Why: the file is Theme.cs but the colour accessors are on Ui (e.g. Ui.Accent).
Apply: use Ui.* for colours in new windows.
