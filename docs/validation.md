# Validation — 2026-10-26

Validated in the onboarding Linux environment using .NET SDK 10.0.403:

- Locked dependency restore: passed.
- Release solution build: passed, zero warnings/errors.
- Relational store tests: 10 passed on SQLite, 10 passed on PostgreSQL 17; none skipped.
- Real-host smoke: SQLite and PostgreSQL passed authentication, header mutation guard, plugin discovery/execution, idempotency replay/conflict, retry/dead-letter, explicit replay, pending/running cancellation, interval schedule execution and browser cookie login/logout.
- Published output: launched independently with sample plugin; smoke passed.
- Chromium: dashboard login, dispatch and successful completion passed. Invalid JSON feedback passed; no JavaScript errors. Desktop 1440px and mobile 390px screenshots captured under ignored artifacts/; no mobile horizontal overflow.
- NuGet vulnerability audit including transitive dependencies: no known vulnerable packages reported by configured sources at validation time.
- JavaScript syntax check and Docker Compose configuration validation: passed.
- Docker build: blocked before compilation by HTTP 403 from centralus.data.mcr.microsoft.com while fetching official Microsoft image layers. No container runtime readiness claim. Draft network addition `*.data.mcr.microsoft.com` saved; it does not apply to the current machine until runtime configuration is updated.

No cloud deployment, independent security review, benchmark/load/soak test, multi-host network partition test, OIDC/RBAC integration or versioned schema upgrade has been validated. Lease takeover is tested through independent DB contexts and expired/fenced owners; this is not a full chaos test of multiple production hosts.

The cloud draft stores install_script, start_skill and the MCR domain addition. The user must review/save environment settings and publish to activate/snapshot them; a fresh task restore has not been verified.
