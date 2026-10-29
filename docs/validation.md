# Validation — 2026-10-28

Validated in the onboarding Linux environment using .NET SDK 10.0.403:

- Locked dependency restore: passed.
- Release solution build: passed, zero warnings/errors.
- Relational store tests: 68 passed with SQLite job storage, 68 passed with PostgreSQL 17 job storage, 68 passed with SQL Server 2022 job storage; none skipped.
- Business database provider integration: real PostgreSQL 17 and SQL Server 2022 Developer passed parameterized query/injection-literal, scalar/null, bounded results, execute, and timeout checks. Both live provider bindings were set; none of the 68 tests were skipped in the complete runs. Without bindings, the two external provider tests explicitly skip.
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

## Cluster policy and NuGet SDK — 2026-10-28

- All 68 tests passed on each of SQLite, PostgreSQL 17, and SQL Server 2022, with no skips in complete runs. New checks cover sequential claim exclusivity across hosts/versions, independent packages, parallel overlap, renewed gate leases, stale completion/renewal/log fencing, retry/final-crash release, active-mode drain guards, concurrent initial installs, and repeatable version 5 upgrades.
- Two independent host processes passed integration checks on PostgreSQL and MSSQL (and a local SQLite file): plugin attribute default, sequential non-overlap, parallel execution on both node identities, rejected mode changes while active, and takeover after killing the owner process. Separate local package materialization directories were used for the nodes. This does not simulate a network partition between physical machines.
- The SDK .nupkg and .snupkg were packed locally. Metadata, XML docs, build targets and symbol content were inspected. A consumer was restored/published from a fresh local NuGet cache, and its output excluded Runner.Contracts.dll. That SDK-built plugin was installed/executed by the two-host tests. No package was pushed to an external NuGet feed; namespace ownership and release licensing remain a publisher decision.
- Host schema version 5 is required on every node before enabling sequential policies. The gate protects valid DB ownership; it cannot stop arbitrary old code continuing external side effects after losing a lease. Cancellation and idempotency remain required.

## Operations platform — 2026-10-29 (Australia/Sydney)

- Release build and native publish passed with zero warnings/errors. Frozen solution/tool restore passed. The current NuGet sources reported no known vulnerable direct/transitive packages. No external NuGet publication occurred.
- All **93 tests passed on each of SQLite, PostgreSQL 17 and SQL Server 2022** with both real business-command providers configured and no skips. New checks include shared capacity/concurrency races, blocked backlog fairness, manifest placement, drain claim fencing, scheduler takeover, DAG prerequisites/failure propagation, cron/DST/misfire handling, database/monotonic clock behavior under wall-clock jumps, archive reuse/corruption repair, bounded retention catch-up and repeatable version 6 upgrades preserving jobs/packages/calendar definitions.
- Two real host processes passed the platform API checks on PostgreSQL and MSSQL: viewer/operator/admin authorization and audit attribution, successful and failed DAG branches, cron/time-zone validation, cluster limit 2 with actual overlapping business execution timestamps, tagged-node placement, drain/resume, incompatible pending jobs without consumed attempts, process crash, cancellation-ignoring script termination and absence of parent API/store environment credentials in children. A separate API-only host exercised outstanding capacity and eventual HTTP 429 burst rejection.
- Chromium passed a real authorization-code/PKCE exchange against a local RSA-signed OIDC issuer, canonical role mapping, denial of unmapped roles, and cookie revocation after session-version rotation. This is not validation against the deployment's actual identity provider.
- Native PostgreSQL and SQL Server backup/restore drills restored **separate generated databases**, verified job results, package bytes/digests, function source and time-zone schedules, and reran upgrades twice. Temporary databases/backup files were removed. Timings and payload sizes are in the linked JSON evidence.
- A local TCP fault proxy disconnected one node from each real database while its peer retained access. The first executor terminated after heartbeat failure, the peer recovered the expired lease at attempt 2, and work continued after connectivity repair. This tests a local asymmetric DB connection failure, not all physical network partitions.
- Live package smoke passed on all three stores with the process executor, including explicit plugin ILogger forwarding to centralized job logs. Existing full script/operation/report/signed-alert/auth smoke and two-process policy/crash checks remain regression gates.
- Real browser checks covered server drain/resume, workflow creation/results, calendar submission, audit views and mobile overflow; screenshots are committed under docs/images.

### Local command-rate and small-load evidence

Each run submitted 100 short sample jobs to two independent hosts (eight slots each), waited 35 seconds after work, then measured 30 idle seconds. Load measurements include API/audit/status-check traffic and the development machine's other activity. EF command counts exclude transaction protocol round trips, provider-internal operations and physical IO; reader commands can include INSERT/RETURNING. These are functional budget checks, not peak-throughput or sustained-load claims.

| Storage | Jobs | Observed jobs/s | P95 end-to-end ms | Idle EF commands/s, both nodes combined |
| --- | --- | --- | --- | --- |
| Sqlite | 100 | 11.55 | 1065.5 | 1.967 |
| Postgres | 100 | 20.00 | 351.6 | 1.967 |
| Mssql | 100 | 17.81 | 603.6 | 1.967 |

The idle assertion is under 3.5 EF commands/second for the **whole two-node cluster** at these default timings. One adaptive dispatcher per node makes idle discovery independent of slot count. A warm digest-addressed archive cache avoids per-execution DB blob downloads; small metadata, claims, journals/audit and job heartbeats still use the database. Retention uses indexed, bounded batches with catch-up rather than full-table expiry scans/unbounded deletes.

[Machine-readable local load/recovery evidence](validation/platform-local-2026-10-29.json) and [configuration/guarantees](platform.md).

Still not validated: production-scale soak/chaos targets, native Windows execution, deployment-specific TLS/identity/audit/telemetry integration, or an independent security review. Docker image build was attempted again but was blocked before compilation by the managed egress policy returning HTTP 403 from centralus.data.mcr.microsoft.com; native published-host validation succeeded. Process execution is lifecycle/crash isolation for trusted code, not an untrusted-code sandbox.

## Comparative benchmark harness — 2026-10-29 (Australia/Sydney)

- Added an isolated .NET 10 tool with real Hangfire.SqlServer 1.8.25, Quartz 4.6.2 SQL persistence and Runner's existing worker. Production projects/solution received no Hangfire or Quartz dependency. Frozen restore and Release build passed; Newtonsoft.Json 13.0.4 overrides Hangfire's vulnerable minimum without suppressing NuGet auditing.
- Three-engine smoke passed against SQL Server, including job bodies, durable framework drain, filtered server event counters and cleanup. Existing 93-test regression run passed with both business database providers configured, no skips. CI runs the functional benchmark smoke; performance thresholds are intentionally absent.
- Executed a local 200-job comparison of default and responsive settings, plus an explicit zero-poll Hangfire confirmation. Each case used fresh databases, two independent worker processes/eight slots each, 20 warm-up jobs, 35-second settling and a 30-second idle window. One local sample per engine/profile was recorded; default harness repetitions are three for future runs.
- Metrics use SQL Server Extended Events completed RPC/batch counts, separate SQL/SP statement events, active requests/connections, per-database physical file IO and worker CPU/working sets. Observer/result connections are excluded from engine counters, but common completion recording remains in elapsed throughput. These counts differ from the older EF interceptor measurement. No HTTP, auth/audit middleware or plugin process startup is included in any engine adapter.
- [Methodology, configuration and run commands](benchmarks/README.md), [local table/CSV](benchmarks/local-2026-10-29.md), raw JSON and an SVG plot are committed. The snapshot does not establish a universal winner or certify production capacity.
