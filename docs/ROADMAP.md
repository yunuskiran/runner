# Remaining work

Runner has durable claims, cluster-wide package execution policies, immutable package versions, retries, logs, reports and a leased alert outbox. These provide a tested foundation, but do not complete operational readiness. The items below describe remaining work, not implemented functionality or release commitments.

## Production priorities

| Priority | Area | Current behavior | Remaining work and acceptance criteria |
| --- | --- | --- | --- |
| P0 | Worker inventory and draining | Attempts identify their worker; shutdown cancels cooperative handlers and leaves leases to expire. | Persist expiring node heartbeats and capabilities; show online/stale status and utilization. Add an authenticated per-node drain action that stops new claims while renewing and finishing existing work within a deadline. Verify draining one node does not stop its peers. |
| P0 | Identity and audit | One shared operator key/cookie; package changes have shared-operator history. | Add OIDC integration or a documented, validated identity gateway; enforce viewer/operator/admin permissions for API and UI. Record attributable changes to jobs, schedules, functions, packages and configuration without storing secrets. Test denied actions and session revocation. |
| P0 | Package compatibility and placement | Installation checks the managed entry type and declared managed dependencies on the receiving host. | Add a versioned manifest for SDK/host compatibility, OS/architecture, native dependencies and required named resources. Claim only on compatible workers; expose reasons why a package cannot run. Validate heterogeneous Linux/Windows nodes and missing resources before execution. |
| P0 | Failure and capacity validation | Automated tests cover three storage providers and two-process crash recovery. | Run sustained load tests with realistic package sizes, DB outages, network partitions, slow queries, clock skew and cancellation-ignoring code. Measure queue age, claim latency, renewal headroom and DB growth. Verify deployment backup/restore and upgrade recovery with an explicit operational target. |
| P1 | Isolated execution | Trusted DLLs and Roslyn code execute inside the host; cancellation and unload are cooperative. | Offer an optional process/container executor with explicit inputs, scoped resources, output limits and termination of hung work. Preserve lease fencing and at-least-once behavior. Required before accepting untrusted code; process separation alone is not a complete security sandbox. |
| P1 | Queue controls and fairness | Concurrency is bounded per host; a package is parallel or sequential. | Add bounded cluster concurrency per package, queue priorities, submission limits and fair scheduling. Verify a blocked or busy package cannot starve unrelated work and competing replicas cannot exceed limits. |
| P1 | Package lifecycle and releases | Archives are retained in the DB and extracted for each execution; SDK packing and explicit feed publishing scripts exist. | Add safe cleanup that preserves queued/scheduled version references, cache reconciliation and measured extraction optimizations. Establish a manifest/signature trust policy, SDK compatibility checks, release licensing and a versioned release process. Remote NuGet discovery/install is separate from the SDK authoring package. |

## Features to add when required

| Feature | Existing support | Gap |
| --- | --- | --- |
| Calendar schedules | Fixed UTC intervals with coalesced missed ticks. | Cron/calendar expressions, time zones, DST behavior and configurable misfire policies. These can be implemented without Quartz. |
| Workflows | Sequential policy permits one valid package lease at a time. | Durable dependencies such as “start B only after A succeeds”, failure propagation, grouped ordering and checkpoints. Sequential mode alone does not enforce success-order dependencies. |
| Plugin configuration UX | JSON payloads and deployment-defined named resources. | Versioned configuration/payload schemas with validation and generated forms; named secret references whose values never enter payloads or reports. |
| Operations experience | Central attempt logs, reports, CSV, terminal alerts and optional OTLP. | Cross-job search/filtering, saved operational dashboards, queue-age/lease-loss alert rules and deployment-specific collector/incident routing presets. Existing log limits and retention still apply. |
| Deployment automation | Published host, Dockerfile, Compose and explicit schema upgrade CLI. | Validated cloud/on-prem deployment presets, repeatable rollout/drain procedures and a worker-only hosting mode. Windows Service/SCM integration is outside the requested scope. |

Recommended first implementation: worker inventory and drain, followed by package compatibility/placement and identity/audit. Validate failure recovery and capacity alongside those changes before declaring a deployment production-ready.

## Guarantees to preserve

- Delivery remains at least once. External writes must be idempotent or fenced by the destination system.
- Sequential execution protects one valid lease across replicas, not physical exclusion of an old process after lease loss.
- Archive digests detect content changes; they do not establish publisher identity.
- Database upgrades remain explicit and repeatable, with tested preservation and recovery procedures.
