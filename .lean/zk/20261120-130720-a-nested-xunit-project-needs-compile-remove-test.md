---
id: 20261120-130720
type: pattern
title: A nested xUnit project needs Compile Remove tests/** and InternalsVisibleTo in the main csproj
tags: [dotnet, tests]
links: []
source: T-001
created: 2026-11-20
---
Why: the main SDK project globs every .cs under its folder, so test files get compiled into the app; most app types are internal.
Apply: add <Compile Remove="tests/**" /> and InternalsVisibleTo LocalSendDrop.Tests before adding the test project.
