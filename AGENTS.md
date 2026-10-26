# Development

This cloud task already has an isolated checkout. Use this checkout; do not create a worktree unless the user explicitly requests one.

- Target .NET 10; honor global.json, Directory.Build.props and package lockfiles.
- Restore with `dotnet restore Runner.sln --locked-mode`; build Release, warnings are errors.
- Run `dotnet test tests/Runner.Tests -c Release`. For ownership/scheduling/storage changes, also run against PostgreSQL using TEST_POSTGRES and run scripts/smoke.py.
- Keep credentials in environment variables or ignored local data paths. Do not commit operator keys, DSNs or DB files.
- Preserve at-least-once semantics, atomic schedule dispatch, lease-token and expiry checks. Never rely on in-memory locks for cross-instance correctness.
- Plugins are trusted code, not sandboxed code. No exactly-once promises for external side effects.
- Use PostgreSQL for multi-instance deployments; local SQLite is a development/single-node option.
- Schema is currently initial-bootstrap only. Any model upgrade needs explicit versioned migration and deployment planning before production use.
