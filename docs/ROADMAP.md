# Implementation status and remaining work

The eight requested areas now have implementations and automated validation paths. See [configuration and guarantees](platform.md) and [validation evidence](validation.md). This status does not certify an arbitrary deployment as production-ready.

| Area | Implemented | Validation |
| --- | --- | --- |
| Server management | Durable node inventory, capabilities, online/stale status, SQL claim drain gate, deadline cancellation and resume. | Store tests and two-host platform smoke. |
| Compatibility/placement | Immutable manifest, OS/architecture/version/resource/tag checks, metadata cache invalidation, pre-claim filtering and per-node explanations. | Manifest/placement tests and actual tagged-node execution. |
| Identity/audit | OIDC code/PKCE, named keys, Viewer/Operator/Admin roles, mapped identities, session-version revocation, durable mutation intents/outcomes and selected safe change metadata. | Browser flow against a local signed OIDC issuer plus real API allow/deny/audit checks. |
| Process execution | Separate package/function process, scrubbed environment, bounded IPC, cancellation/termination and parent EOF watchdog. | Child crash, cancellation-ignoring code, credential-environment check and DB-failure/peer-recovery drill. |
| Queue controls | Shared package concurrency limits, named queues, priority aging, durable admission limit and per-API submission rate limit. | Competing claims/admissions, busy-package backlog test and two-host global-limit smoke. |
| Calendar scheduling | Cronos five-field cron calculation, named zones, DST handling and Coalesce/Skip/bounded CatchUp. | DST/misfire store tests and real schedule API validation. |
| Workflows | Atomic DAG creation, validation, blocked steps, success dependencies, terminal-failure propagation and retry dependency checks. | Store tests and real success/failure workflows on two hosts. |
| Database cost/recovery | One adaptive dispatcher per node, leased scheduler, bounded periodic cleanup, report/readiness caches, sampled DB/monotonic lease clock, command metrics, load/fault and native restore tools. | Three-provider tests and local load, TCP-fault, restore and clock-jump checks described in validation. |

Still required for each deployment:

- Establish capacity, queue-latency SLOs and RTO/RPO with production-like data, packages, database/network topology and sustained soak tests. Local development measurements do not replace those results.
- Configure the real identity provider, TLS, secret rotation, protected cookie keys, least-privilege DB roles, audit export/retention and the telemetry/incident receiver. Independent security review remains outstanding.
- Validate native DLL dependencies and actual Windows/Linux business behavior on the target OS. Manifest selection is implemented; native Windows runtime validation has not been performed in the Linux development environment.
- Approve a release license/name ownership and publish Runner.PluginSdk explicitly to the selected feed. Local package creation does not reserve its name or publish it.

Remaining optional product work includes: a hardened container/OS sandbox for untrusted code; package publisher signature policy and remote feed discovery; safe package/archive cache cleanup; workflow/audit archival policies; workflow result bindings/checkpoints; schema-driven plugin configuration forms; native incident adapters; and a separate worker-only executable/deployment presets. These features were not substituted for the eight implementations above. No Windows Service/SCM integration is planned under the current scope.

At-least-once delivery, external idempotency requirements, one-valid-lease sequential semantics and explicit repeatable schema upgrades remain part of the contract.
