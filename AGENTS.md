# Development

This cloud task already has an isolated checkout. Use this checkout; do not create a worktree unless the user explicitly requests one.

- Target .NET 10; honor global.json, Directory.Build.props and package lockfiles.
- Restore with `dotnet restore Runner.sln --locked-mode`; build Release, warnings are errors.
- Run `dotnet test tests/Runner.Tests -c Release`. For ownership/scheduling/storage changes, also run against PostgreSQL using TEST_POSTGRES and SQL Server using TEST_MSSQL (one per run) and run scripts/smoke.py.
- Keep credentials in environment variables or ignored local data paths. Do not commit operator keys, DSNs or DB files.
- Preserve at-least-once semantics, atomic schedule dispatch, lease-token and expiry checks. Never rely on in-memory locks for cross-instance correctness.
- Plugins are trusted code, not sandboxed code. No exactly-once promises for external side effects.
- Use PostgreSQL or SQL Server for multi-instance deployments; local SQLite is a development/single-node option.
- Initial bootstrap uses EnsureCreated. The --upgrade-concurrency command applies versions 2–5 (functions, operations, packages and cluster concurrency) and is repeatable. Future model changes need new versioned upgrades, preservation/repeatability tests on both providers, and deployment planning.
- Roslyn scripting is disabled by default and runs trusted code with host privileges. Preserve immutable function versions, queued function IDs, result fencing and cooperative cancellation; never describe in-process scripting as sandboxed.

- Built-in database/process operations use named Operations resources. Keep connection strings, executable paths and webhook secrets out of payloads and resource API responses. Never add shell-based execution.
- Completion, journal and terminal alert writes must commit atomically with valid lease ownership. Webhook deliveries are at least once; preserve delivery fencing and receiver deduplication IDs.
- Set TEST_DATABASE_POSTGRES and TEST_DATABASE_MSSQL to exercise real provider operations; default runs explicitly skip those two checks when services are unconfigured. Run SMOKE_OPERATIONS=true for logs, reports, process and signed webhook delivery checks.

- Dashboard-installed packages implement IRunnerPlugin and run via runner.plugin. Preserve immutable package blobs/digests, entry-point validation, activation revision/history and enqueue-time version pinning. Disabled package channels must be filtered in both job claims and schedule dispatch.
- User-selected files/folders are uploaded; never require a fixed source plugin folder or add Windows Service/SCM integration. Never execute a job-supplied DLL path.
- For packaging changes run scripts/package_smoke.py with all three job storage providers. Package upload size limits, portable paths, dependency loading, pause/resume and rollback are part of the contract.

- SQL Server storage uses dbo and per-column Latin1_General_100_BIN2 collation for consistent case-sensitive keys. Nullable idempotency keys must allow multiple NULLs through the SQL Server filtered unique index. Preserve provider-specific versioned SQL and repeatable schema-ledger inserts.

- Sequential plugin mode uses a shared channel lease across versions. Always lock the channel row before job row writes in claims, renewal, completion, expiry and mode changes; preserve the token/expiry checks and transaction boundaries. Process-local locks cannot enforce cluster policy.
- Changing execution mode requires all valid leases for that package to drain. The plugin attribute sets only a newly installed package ID's initial policy; channel policy is operator-managed afterward.
- SDK package artifacts stay in artifacts/nuget. Do not publish to an external feed without an explicit release instruction/endpoint. Keep the Runner.Contracts assembly identity compatible and ensure the SDK target excludes it from plugin runtime output.
- Verify SDK consumers from the packed local feed and run scripts/cluster_smoke.py on PostgreSQL and MSSQL for concurrency changes. It starts two processes and kills one to verify recovery.
