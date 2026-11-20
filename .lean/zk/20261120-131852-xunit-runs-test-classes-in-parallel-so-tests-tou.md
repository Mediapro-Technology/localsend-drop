---
id: 20261120-131852
type: gotcha
title: xUnit runs test classes in parallel, so tests touching KvmSync's static state must share [Collection("KvmSync")]
tags: [tests, xunit, kvm]
links: []
source: T-007
created: 2026-11-20
---
Why: a ReviewFixTests Hello test broke KvmSyncTests.Receive_Older_Ignored only in the full run.
Apply: any new test that calls KvmSync.Reset/Load/Hello goes in the KvmSync collection.
