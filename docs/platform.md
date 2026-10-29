# Cluster operations

This release adds schema version 6. Stop **all** hosts, back up the database and run the published CLI with the deployment identity:

```bash
dotnet Runner.Host.dll --upgrade-platform
```

The command applies versions 2–6, preserves existing definitions/jobs/packages, initializes existing channels with unlimited parallel capacity unless already sequential, and can be rerun. New installations use `--initialize`. Runtime identities need DML privileges; upgrades need a separate DDL identity. Rollback uses the backup and matching application version. Do not mix old and new hosts: older versions do not enforce placement, drain, cluster limits or workflow dependencies.

## 1. Server inventory and drain

Every execution host registers a unique process ID and its configured instance name, OS/architecture, resource **names**, tags, queues, handler types, concurrency and active count. API-only hosts do not advertise worker capacity. The dashboard's **Worker servers** section shows online/stale status and drain progress; `GET /api/nodes` returns up to 200 recent nodes. A heartbeat defaults to 10 seconds. The dashboard marks a node offline after three heartbeat periods plus five seconds; this status does not replace individual job leases.

```bash
export Runner__InstanceName=worker-a
export Runner__Concurrency=8
export Runner__Tags__0=finance
export Runner__Queues__0=default
export Runner__Queues__1=finance
```

An administrator can `POST /api/nodes/{id}/drain` with `{"enabled":true,"deadlineSeconds":300}`. The shared drain flag is checked in SQL during claims, including before the next local heartbeat. Running jobs continue to renew/finish. The node acknowledges the request on its heartbeat and becomes `Drained` after its active count reaches zero. At the deadline it cancels remaining work; cancellation-ignoring DLL/script children are terminated by the process executor. Remaining attempts can retry on peers. `{"enabled":false}` resumes claims. Scheduling and alert delivery can continue on a drained host; drain controls job execution capacity. Shutdown cancels work and leaves unfinished ownership for lease recovery; use drain before a planned restart.

A new process gets a new ID. A replacement is not automatically drained because the previous process was drained. Older node records are retained for investigation; the endpoint bounds its result set.

## 2. Manifest and placement

Add a root `runner.manifest.json` beside the files uploaded through the dashboard. It becomes part of the immutable archive and its SHA-256 digest:

```json
{
  "schemaVersion": 1,
  "minimumHostVersion": "1.0.0",
  "minimumSdkVersion": "0.1.0",
  "operatingSystems": ["linux"],
  "architectures": ["x64", "arm64"],
  "requiredResources": ["connection:Warehouse", "application:Reconcile"],
  "requiredTags": ["finance"]
}
```

Absent requirements mean any supported node. Packages without a manifest keep that behavior. `minimumHostVersion` and `minimumSdkVersion` are semantic numeric compatibility versions, independent of database schema version. This release advertises host `1.0.0` and SDK `0.1.0`. Resource/tag comparisons are case-sensitive. A resource entry declares availability, not an authorization boundary or a connectivity check; workers still need permissions and connectivity to the underlying system.

Workers cache **metadata**, refresh only when the shared registry marker changes, and filter compatible package versions before claiming. They do not download archives to calculate placement. The supported placement registry is bounded to 10,000 installed versions. A version with no matching node stays pending without consuming attempts. Its dashboard compatibility action and `GET /api/packages/{id}/versions/{version}/placement` explain each node's missing capabilities. Offline/draining status is also returned and must be considered when selecting usable capacity. An API-only installation host must still be able to inspect the package's managed assemblies; native dependencies and OS-specific business behavior require validation on their target platform.

## 3. Identity, roles and audit

The existing `Security__ApiKey` remains a bootstrap administrator key. Named keys support automation with separate identities/roles:

```bash
# Supply values from a secret manager, not committed settings.
export Security__ApiKeys__observer__Role=Viewer
export Security__ApiKeys__observer__Key="$RUNNER_VIEWER_KEY"
export Security__ApiKeys__dispatcher__Role=Operator
export Security__ApiKeys__dispatcher__Key="$RUNNER_OPERATOR_KEY"
```

Use the same key/session configuration across replicas. Keys require at least 32 characters and unique values. The same named key can sign in through the login form. An organization identity provider uses standard OIDC authorization code with PKCE:

```bash
export Security__Oidc__Authority=https://identity.example.com/your-tenant
export Security__Oidc__ClientId=runner-dashboard
export Security__Oidc__ClientSecret="$RUNNER_OIDC_CLIENT_SECRET"
export Security__Oidc__RoleClaimType=roles
export Security__Oidc__AdminRole=runner-admin
export Security__Oidc__OperatorRole=runner-operator
export Security__Oidc__ViewerRole=runner-viewer
```

Register the HTTPS callback `/signin-oidc` with the provider and emit the configured role claim in the ID token. `/auth/login` starts organization sign-in. Signature, issuer, audience, nonce, state and PKCE are validated by the ASP.NET Core OIDC handler. Tokens are not saved in the session cookie. Only explicitly mapped roles receive access. Local HTTP discovery/correlation cookies are permitted only in Development; production requires HTTPS. Share and protect the cookie key ring across replicas.

| Role | Permissions |
| --- | --- |
| Viewer | Read jobs/results/logs, reports, resources, packages/functions, schedules, workflows and nodes. |
| Operator | Viewer plus submit/cancel/retry jobs, create/delete schedules, create workflows and acknowledge/redeliver alerts. Executes already registered trusted business code. |
| Admin | Operator plus install/activate/configure packages, register/enable/disable source, drain/resume nodes and read audit records. |

`GET /api/session` returns the current identity/roles. The UI disables unauthorized mutation controls; the server enforces permissions independently. `Security__SessionVersion` defaults to `1`; rotate it consistently on all replicas to revoke older cookies on their next request. API key rotation alone does not revoke cookies. Shared keys identify the key owner, not individual humans; use OIDC for personal attribution.

Every authenticated API mutation and denied authorized-role check persists an audit **intent before execution**, then its HTTP outcome, actor, authentication source, trace ID, safe resource location and selected non-secret change metadata. `GET /api/audit?page=0` is admin-only. Source, payloads, SQL, credentials and process arguments are not copied to the audit table. Package history also records before/after mode and concurrency limits. A crash can leave an incomplete intent; audit completion is not the same transaction as every business mutation. Audit records are retained independently of job retention; export them to the organization's protected audit system and define retention there.

## 4. Process execution

`Execution__Isolation=Process` is the default for dashboard DLLs and Roslyn functions. `InProcess` is an explicit compatibility option for trusted plugins requiring host-only DI services. Legacy configured handlers and built-in database/process handlers keep their existing execution paths.

For each package/function attempt the parent starts the published host with an internal child mode and passes a bounded request over redirected stdin. The child starts **before** web hosting: it does not construct RunnerDb, authenticate operators, or inherit the parent's API keys, OIDC secrets or primary storage connection string through its environment. Only configured business-operation resources are passed privately for `Db`/`Apps` support. Their credentials remain trusted-code-accessible. Process isolation is enabled for executions; package inspection and source validation/compilation also involve the control plane, so uploaded code must remain trusted. Supported child services include logging, `DatabaseCommands`, `ApplicationCommands` and operation options; arbitrary host-only constructor dependencies are unavailable in process mode.

Bounded protocol messages carry business logs/results back to the parent's fenced journal writer. Exceptions/exit/crashes fail an attempt without exiting the worker host. On timeout, lease loss or shutdown the parent requests cooperative cancellation, allows up to two seconds, then kills the process tree and reaps the direct child. An EOF watchdog terminates the child if its parent dies. Explicit plugin ILogger Information/Warning/Error messages use the same bounded centralized sink. Standard console output is discarded; use `JobContext.WriteLogAsync`, `ReportResult`, or the script `Log` helper. No automatic main DB/key exposure is needed for business code.

This is crash/lifecycle isolation for **trusted** code, not a security sandbox. Children run under the same OS identity and retain its filesystem/network permissions; trusted code could deliberately escape process-tree tracking or inspect host files. Use OS/container isolation and distinct identities before accepting untrusted code. External effects remain at least once and require destination idempotency/fencing.

## 5. Queue policy and backpressure

Job submission accepts `queue` (default `default`, ASCII letters/digits/`._-`, up to 64 characters) and `priority` from `-10` to `10`. A node claims only its configured queues. Higher priorities move effective ordering time earlier by one minute per priority point. Waiting jobs therefore age past newly arriving high-priority work within a bounded 20-minute priority difference; this is not strict FIFO or equal resource allocation. Delayed jobs are still ineligible before their due time.

A package's activation request can include `{"maxConcurrency":3,"setLimit":true}` in addition to its existing version, enabled flag, revision and mode. `null` with `setLimit:true` removes the limit. Parallel packages enforce that valid running-lease count across all nodes/versions through the short channel-row lock. Sequential mode still allows only one valid lease. Changing mode **or** limit requires active leases to drain. The dashboard edits both policies. SQL excludes full/disabled/incompatible packages before the bounded candidate set, so a large blocked backlog cannot hide unrelated eligible work.

`Runner__MaxPendingJobs` defaults to 100,000 and bounds **outstanding** pending/running/blocked jobs, despite the setting name. API enqueue, workflow enqueue and scheduled enqueue share a short admission-row transaction and indexed outstanding-count check. Configure the same capacity limit on all participating hosts. Capacity is not consumed again by a retrying running attempt. Schedules remain due when capacity is full. API replay of an existing idempotency key does not require a new slot. An additional per-API-node token bucket permits 120 immediate job/workflow submissions and refills 60 per minute; it returns 429 with no unbounded HTTP waiting queue. Outstanding-capacity rejection returns 409. This admission count adds bounded database work per submission; benchmark realistic queue sizes.

## 6. Calendar scheduling

Create schedules through the dashboard or existing API:

```json
{
  "type": "sample.echo",
  "payload": {},
  "cron": "0 9 * * 1-5",
  "timeZoneId": "Australia/Sydney",
  "misfirePolicy": "Coalesce"
}
```

Cron uses **five fields**, minute resolution, via the small Cronos expression library; it is not a scheduler/storage engine. An absent cron keeps fixed intervals of 10 seconds or more. Time zones use IDs recognized by the installed .NET/OS tzdata. DST behavior follows Cronos: nonexistent local times move to the next valid time, fixed-time occurrences do not repeat in a fall overlap, and interval expressions preserve interval semantics. Keep tzdata current and test business calendars.

- `Coalesce` advances from now and emits one overdue occurrence.
- `Skip` advances from now and drops occurrences more than 60 seconds late.
- `CatchUp` advances from the previous occurrence and emits at most **one per schedule per scheduler cycle**, within the 50-schedule batch. Backlog drains gradually.

Schedule advancement and the pinned job insert remain one transaction; competing/stale schedulers use compare-and-swap. Cron is calculated only for due schedules. There are no per-schedule timers, polling loops or persisted tick rows. Schedule dispatch resolution also includes `Runner__SchedulerSeconds` (default 5 seconds) and worker discovery latency.

## 7. Dependency workflows

`POST /api/workflows` atomically creates 1–100 named steps and validates unique keys, existing dependencies, duplicate edges and cycles before insertion. The normal HTTP body limit still applies. Every step uses the same job payload validation, package version pinning, queue, priority, attempts and available time as standalone work. Step-specific idempotency keys are rejected; keep the returned workflow/job IDs and apply external operation idempotency normally.

```json
{
  "name": "invoice-pipeline",
  "steps": [
    { "key": "prepare", "job": { "type": "sample.echo", "payload": {} } },
    { "key": "deliver", "dependsOn": ["prepare"], "job": { "type": "sample.echo", "payload": {} } }
  ]
}
```

Root steps are pending; other steps are `Blocked`. A child becomes eligible only after **every** parent succeeds and its own delay has elapsed. Retrying parents keep children blocked. A terminal failure/cancellation marks descendants `Cancelled` with `dependency_failed`; independent branches continue. Propagation is bounded per scheduler cycle. Manual retries recheck dependencies; cancelled descendants are not silently resurrected when a parent is retried. Retry selected descendants explicitly after fixing their parents. Workflow jobs/edges are retained independently of ordinary job cleanup so dependencies cannot disappear; define archival/retention for that growing history before a long-running deployment. Workflows do not yet support result-to-input templating, transactional external effects or business checkpoints.

Use `GET /api/workflows` and `/api/workflows/{id}` or the dashboard to inspect runs, steps, results and edges. Sequential package policy remains separate from success dependencies.

## 8. Database cost and validation

One dispatcher per **node**, rather than one per execution slot, polls for available jobs. Its jittered exponential idle delay starts at `Runner__PollMilliseconds` (default 1,000 ms) and caps at `Runner__MaxIdlePollSeconds` (default 30 seconds). Local enqueue/completion/registry signals reduce latency and reset idle backoff. These signals are hints; periodic polling is the durable recovery path for other nodes and future due times. Increasing the maximum saves idle reads while increasing worst-case remote discovery latency. No Redis/cache/broker is required.

During execution, a dispatcher shares one ordered search of at most 64 candidate IDs across its free slots. It reads job bodies in groups of at most eight, bounded further by free capacity, and starts each job immediately after that job's lease, attempt and start log commit. It never holds one transaction across the entire window or leases work beyond its free capacity. The window reuses one pooled DB connection while filling slots and releases it before any idle wait; this also avoids repeated connection-reset requests. Every conditional claim rechecks drain, cancellation, queue/manifest placement, channel policy and the candidate's attempt/state/token in SQL. A local heartbeat failure also stops the remaining window. Competing/stale candidate snapshots cannot grant ownership; missed candidates are discovered again by the normal dispatcher. Job bodies remain immutable after submission. This saves repeated candidate/body reads without changing lease recovery or the package lock order.

Attempt completion computes nonnegative duration from its persisted start time in the same conditional SQL update, including attempts recovered after a long outage. Expired takeovers close the exact former lease token rather than scanning every attempt for the job. Attempts and lifecycle logs remain durable and transactional; these optimizations do not disable execution history.

These execution/query optimizations work with the existing version 6 schema; no additional schema upgrade or queue broker is required. They do not change the default idle discovery cap.

A short shared lease elects the scheduler/maintenance node. Due schedule, blocked workflow and expired-lease recovery queries run only there (default every five seconds). One existence-probe request gates those background operations, replacing four separate queries when there is no relevant work; due work still uses the ordinary fenced transactions. A concurrent insert after the probe is picked up on the next scheduler cycle. Node heartbeats read drain state and the package-registry marker together, alongside the normal clock sample and heartbeat update. Retention runs every `Runner__MaintenanceSeconds` (default 300), with indexed expiry scans and at most 1,000 rows per log/attempt/alert/job table per pass. A full batch schedules the next cleanup on the next scheduler cycle until the backlog clears, then returns to the normal maintenance interval. Large backlogs drain gradually rather than triggering one unbounded delete. Package placement refreshes use a small registry marker. Each worker also keeps an immutable digest-addressed ZIP cache: warm executions verify/read the local ZIP instead of downloading the DB blob again, then extract a unique working directory. Corrupt cache entries are refreshed from the DB. Concurrent cold starts may fetch the same archive more than once; publication is atomic. These caches are disposable performance data, not job state. Configure disk quotas and remove stale archives/temp files while workers are stopped; automatic archive eviction is not implemented. Aggregate dashboard reports/stats are coalesced and cached for 15 seconds per API host; visible browser dashboards refresh every 15 seconds, audit loads on demand, and readiness uses one no-scan schema query cached for 15 seconds.

Lease time is sampled from the storage server once per node heartbeat, then advances through a monotonic local timestamp. Samples slower than two seconds are rejected; a failed node heartbeat pauses new claims, and job heartbeat failures cancel execution. This removes local wall-clock jumps from normal lease renewal/expiry. Keep database and host clocks/time zones managed for operational timestamps, business code and calendar correctness. No additional DB clock round trip is required per claim or per job heartbeat. The primary database must still have adequate indexed read/write capacity: durable state, audit, journals and ownership changes have real costs.

`GET /api/diagnostics/database` exposes process-local EF reader/nonquery/scalar command counts without querying the database. These are command counts, not physical IO/transaction round trips. Reader commands can include INSERT/RETURNING. Test scripts write machine-readable evidence under ignored `artifacts/`:

```bash
# Build first. Use disposable databases for provider smoke/fault/load runs.
dotnet restore Runner.sln --locked-mode
dotnet build Runner.sln -c Release --no-restore
python3 scripts/platform_smoke.py
node scripts/oidc_smoke.cjs             # Chromium + Playwright required
python3 scripts/load_smoke.py           # 100 jobs, idle command budget assertion
# Set one of SMOKE_POSTGRES / SMOKE_MSSQL for the following:
python3 scripts/fault_smoke.py          # local TCP DB partition and child/lease recovery

# Separate native backup/restore drill; creates and removes generated databases only.
dotnet restore tools/Runner.Recovery --locked-mode
dotnet build tools/Runner.Recovery -c Release --no-restore
# RECOVERY_PROVIDER=Postgres or Mssql; RECOVERY_CONNECTION supplied securely.
# RECOVERY_CONTAINER names a disposable test container containing native tools/files.
dotnet tools/Runner.Recovery/bin/Release/net10.0/Runner.Recovery.dll
```

The fault drill requires Linux `/proc` to verify child termination and a loopback-accessible real DB; it cuts connections to one node while its peer retains access. The recovery drill uses the local Docker socket and native `pg_dump/pg_restore` or SQL Server BACKUP/CHECKSUM/RESTORE into **different generated databases**. It does not restore over deployment databases. The load drill includes API/audit/test traffic and asserts fewer than 3.5 EF commands/second across two idle nodes with default timings, independently of their eight slots each. These are development-environment checks, not a production soak, physical multi-site partition test, independent security review, or certified RTO/RPO/capacity target. Review [validation evidence](validation.md) for what actually ran.
