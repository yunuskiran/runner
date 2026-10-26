# Development

This cloud task already has an isolated checkout. Use this checkout; do not create a worktree unless the user explicitly requests one.

- Target .NET 10; honor global.json, Directory.Build.props and package lockfiles.
- Restore with `dotnet restore Runner.sln --locked-mode`; build Release, warnings are errors.
- Run `dotnet test tests/Runner.Tests -c Release`. For ownership/scheduling/storage changes, also run against PostgreSQL using TEST_POSTGRES and run scripts/smoke.py.
- Keep credentials in environment variables or ignored local data paths. Do not commit operator keys, DSNs or DB files.
- Preserve at-least-once semantics, atomic schedule dispatch, lease-token and expiry checks. Never rely on in-memory locks for cross-instance correctness.
- Plugins are trusted code, not sandboxed code. No exactly-once promises for external side effects.
- Use PostgreSQL for multi-instance deployments; local SQLite is a development/single-node option.
- Initial bootstrap uses EnsureCreated. The --upgrade-operations command applies version 2 scripting and version 3 execution journal/alert upgrades, and is repeatable. Future model changes need new versioned upgrades, preservation/repeatability tests on both providers, and deployment planning.
- Roslyn scripting is disabled by default and runs trusted code with host privileges. Preserve immutable function versions, queued function IDs, result fencing and cooperative cancellation; never describe in-process scripting as sandboxed.

- Built-in database/process operations use named Operations resources. Keep connection strings, executable paths and webhook secrets out of payloads and resource API responses. Never add shell-based execution.
- Completion, journal and terminal alert writes must commit atomically with valid lease ownership. Webhook deliveries are at least once; preserve delivery fencing and receiver deduplication IDs.
- Set TEST_DATABASE_POSTGRES and TEST_DATABASE_MSSQL to exercise real provider operations; default runs explicitly skip those two checks when services are unconfigured. Run SMOKE_OPERATIONS=true for logs, reports, process and signed webhook delivery checks.
