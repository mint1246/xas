# Review and fix evidence

`code-review-2026-09-29.html` records the original review of the working tree before these fixes. Its numbered excerpts and reproduction results are historical evidence; they do not describe the corrected source.

`review-evidence/Program.cs` deliberately asserts the original defects. Do not treat that project as a current regression suite or regenerate its historical report against changed source. Current regression tests live in `tests/` and run through `tests/Xas.Tests/Xas.Tests.csproj`.

`fix-verification-2026-09-30.html` records the changes and their verification. Logs and standalone reproduction scripts are stored under `fix-evidence/`.

The installed Windows daemon and administrator broker service remained stopped throughout the repair. Display lifecycle tests use fake controllers and input routers. Network integration tests disable native KVM and mount orchestration.
