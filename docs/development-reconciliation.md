# Development branch reconciliation

2026-10-30

The C# refactor replaces Python runtime dependencies with C# 5 source compiled in memory on Windows PowerShell 5.1. All 78 host/domain/LLM tests and the source-only compile check pass locally. CI uses the available Windows runner and explicitly bootstraps NuGet noninteractively before installing its pinned Pester version. Generated Bridge_Warden.package output is no longer tracked; Package_App continues to generate release/distribution files. Negative wildcard examples in the vendored port-helper comment are described in words; the actual validation and rejection behavior is unchanged. Muted text and colored button/logo fills are corrected for readable contrast without lowering the gate thresholds.

CI and the pinned shared gate now use isolated GitHub-hosted Windows 2022 workers to avoid stalled shared-runner queues and access to personal workstation files. Windows PowerShell 5.1 tests, noninteractive tool installation and all required gate checks remain enabled.
