# Validation — 2026-10-28

Validated in the onboarding Linux environment using .NET SDK 10.0.403:

- Locked dependency restore: passed.
- Release solution build: passed, zero warnings/errors.
- Relational store tests: 59 passed with SQLite job storage, 59 passed with PostgreSQL 17 job storage, 59 passed with SQL Server 2022 job storage; none skipped.
- Business database provider integration: real PostgreSQL 17 and SQL Server 2022 Developer passed parameterized query/injection-literal, scalar/null, bounded results, execute, and timeout checks. Both live provider bindings were set; none of the 59 tests were skipped in the complete runs. Without bindings, the two external provider tests explicitly skip.
- Process checks: literal arguments, bounded concurrent stdout/stderr draining, environment isolation, nonzero exit handling, timeout and cancellation passed on Linux. Native Windows executable execution has not been validated here.
- Central journal/outbox tests: atomic failure/alert creation, expired final-attempt alert deduplication, delivery claim concurrency/fencing, bounded retries/parking, and repeatable version 3 upgrades passed on both job storage providers.
- Operations smoke: ready database/process jobs, safe resource metadata, centralized business/process logs, attempt reports and CSV, incident acknowledgement, Roslyn Db/Log helpers, and a real local HTTP webhook receiver passed. The receiver rejected the first signed request with 500; the second request delivered the same alert ID with valid HMAC.
- Real-host smoke: SQLite and PostgreSQL passed authentication, header mutation guard, plugin discovery/execution, idempotency replay/conflict, retry/dead-letter, explicit replay, pending/running cancellation, interval schedule execution and browser cookie login/logout.
- Published output: launched independently with sample plugin, Roslyn, ready database/process operations and the central journal/alert pipeline; smoke passed.
- Roslyn smoke on both providers: registration, compile diagnostics, immutable version pinning, persisted JSON output, enable/disable, and scheduled script execution passed.
- Version 2 upgrade: tested twice against an initial-release schema on both providers, preserving existing jobs.
- Chromium: dashboard login, dispatch and successful completion passed. Invalid JSON feedback passed; no JavaScript errors. Desktop 1440px and mobile 390px screenshots, function editor, ready operation forms, centralized report/alert inbox, and persisted result dialog captured in committed docs/images/; no mobile horizontal overflow. Compiler error feedback, registration, and script execution also passed in the browser.
- NuGet vulnerability audit including transitive dependencies: no known vulnerable packages reported by configured sources at validation time.
- JavaScript syntax check and Docker Compose configuration validation: passed.
- Earlier Docker build: blocked before compilation by HTTP 403 from centralus.data.mcr.microsoft.com while fetching official Microsoft image layers. SQL Server image pull now succeeded and its running container was used for integration tests. The Runner image has not been revalidated; no Runner container runtime readiness claim. Draft network addition `*.data.mcr.microsoft.com` saved; it does not apply to the current machine until runtime configuration is updated.

No cloud deployment, external telemetry collector deployment, independent security review, benchmark/load/soak test, multi-host network partition test, OIDC/RBAC integration or future schema upgrades beyond the initial-release-to-version-2 and version-2-to-version-3 and version-3-to-version-4 upgrades have been validated. Lease takeover is tested through independent DB contexts and expired/fenced owners; this is not a full chaos test of multiple production hosts.

The cloud draft stores install_script, start_skill and the MCR domain addition. The user must review/save environment settings and publish to activate/snapshot them; a fresh task restore has not been verified.

## Dashboard package manager — 2026-10-28

- Package tests passed on SQLite and PostgreSQL: portable path validation, immutable/deterministic archives, IRunnerPlugin entry validation, non-plugin entry rejection, integrity failures, activation revision conflicts/history, pinned version execution, disabled package job/schedule behavior, global package execution disable, and repeatable version 4 upgrades preserving old jobs.
- Real-host package smoke passed on both stores: multipart upload larger than the normal 128 KiB API body limit, two immutable versions, invalid DLL/traversal rejection, activation without restart, stale-revision rejection, queued version pinning, pause/resume with no attempt consumed, schedule coalescing, rollback and history. The pause assertion compares the stored schedule timestamp rather than the pre-save timestamp, accounting for PostgreSQL's microsecond precision.
- Chromium selected a folder outside the application tree and a separate set of DLL/dependency files, installed and ran two versions, disabled the package and rolled back live. Desktop/mobile layout and history views passed with no JavaScript errors; screenshots are in docs/images/package-*.png.
- Existing plugin, script, ready operation, central logging/reporting and signed alert delivery smoke checks passed after the package changes on both stores. Schedule smoke assertions match the current run’s schedule idempotency key so older jobs in a reused test database cannot count as passing results.
- No Windows Service/SCM integration was added. Native Windows execution, untrusted-code sandboxing, automatic worker OS routing, package publisher signatures, and package-load benchmarks are not validated or implemented by this feature.

## SQL Server primary storage — 2026-10-28

- SQL Server 2022 Developer passed all 59 tests with no skips, including job claim concurrency, leases, cancellation, retry/dead-letter, journal/outbox atomicity, package installation/activation/pinning, archive integrity and repeatable schema upgrades 2–4.
- Fresh databases and upgrade SQL use dbo, datetime2(7), varbinary(max) and binary string collation. Nullable idempotency keys allow multiple nulls, and keys/prerelease versions differing by case remain distinct.
- SQLite and PostgreSQL regression runs passed the same 59 tests each.
- SQL Server package and full application smoke checks exercised live upload/rollback, scripts, database/process jobs, reporting/CSV, centralized logs and signed webhook retry. The published application was tested with SQL Server storage.
- A provider cancellation edge case was addressed by checking cancellation and elapsed budget before returning database command results. Timeout integration checks now assert the database_timeout classification.
- CI includes MSSQL primary storage test and smoke steps. Runner storage and business-command connection providers are configured independently. No cross-provider data migration is performed when selecting a different store.
