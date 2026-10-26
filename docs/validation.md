# Validation — 2026-10-26

Validated in the onboarding Linux environment using .NET SDK 10.0.403:

- Locked dependency restore: passed.
- Release solution build: passed, zero warnings/errors.
- Relational store tests: 38 passed with SQLite job storage, 38 passed with PostgreSQL 17 job storage; none skipped.
- Business database provider integration: real PostgreSQL 17 and SQL Server 2022 Developer passed parameterized query/injection-literal, scalar/null, bounded results, execute, and timeout checks. Both live provider bindings were set; none of the 38 tests were skipped in the complete runs. Without bindings, the two external provider tests explicitly skip.
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

No cloud deployment, external telemetry collector deployment, independent security review, benchmark/load/soak test, multi-host network partition test, OIDC/RBAC integration or future schema upgrades beyond the initial-release-to-version-2 and version-2-to-version-3 upgrades have been validated. Lease takeover is tested through independent DB contexts and expired/fenced owners; this is not a full chaos test of multiple production hosts.

The cloud draft stores install_script, start_skill and the MCR domain addition. The user must review/save environment settings and publish to activate/snapshot them; a fresh task restore has not been verified.
