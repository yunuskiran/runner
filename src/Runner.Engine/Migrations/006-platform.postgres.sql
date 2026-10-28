-- Version 6. Run once through --upgrade-platform with all hosts stopped. Transaction owned by the upgrade CLI.

ALTER TABLE "Jobs" ADD "WorkflowId" uuid NULL;

ALTER TABLE "Jobs" ADD "StepKey" character varying(64) NULL;

ALTER TABLE "Jobs" ADD "Queue" character varying(64) NOT NULL DEFAULT 'default';

ALTER TABLE "Jobs" ADD "Priority" integer NOT NULL DEFAULT 0;

ALTER TABLE "Jobs" ADD "PriorityUtc" timestamp with time zone NOT NULL DEFAULT '1970-01-01T00:00:00Z';

ALTER TABLE "PluginPackages" ADD "ManifestJson" text NOT NULL DEFAULT '{}';

ALTER TABLE "PluginChannels" ADD "MaxConcurrency" integer NULL;

ALTER TABLE "Schedules" ADD "Cron" character varying(120) NULL;

ALTER TABLE "Schedules" ADD "TimeZoneId" text NOT NULL DEFAULT 'UTC';

ALTER TABLE "Schedules" ADD "MisfirePolicy" text NOT NULL DEFAULT 'Coalesce';

UPDATE "Jobs" SET "PriorityUtc" = "CreatedUtc";

CREATE TABLE "AuditRecords" (
    "Id" uuid NOT NULL,
    "Actor" text NOT NULL,
    "AuthenticationType" text NOT NULL,
    "Method" text NOT NULL,
    "Path" text NOT NULL,
    "TraceId" text NOT NULL,
    "DetailsJson" text NOT NULL,
    "StartedUtc" timestamp with time zone NOT NULL,
    "CompletedUtc" timestamp with time zone,
    "StatusCode" integer,
    CONSTRAINT "PK_AuditRecords" PRIMARY KEY ("Id")
);

CREATE TABLE "ClusterLeases" (
    "Name" character varying(120) NOT NULL,
    "Owner" text,
    "UntilUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClusterLeases" PRIMARY KEY ("Name")
);

CREATE TABLE "JobDependencies" (
    "JobId" uuid NOT NULL,
    "DependsOnJobId" uuid NOT NULL,
    CONSTRAINT "PK_JobDependencies" PRIMARY KEY ("JobId", "DependsOnJobId")
);

CREATE TABLE "WorkerNodes" (
    "Id" character varying(160) NOT NULL,
    "Name" text NOT NULL,
    "StartedUtc" timestamp with time zone NOT NULL,
    "HeartbeatUtc" timestamp with time zone NOT NULL,
    "DrainRequested" boolean NOT NULL,
    "DrainDeadlineUtc" timestamp with time zone,
    "DrainAcknowledgedUtc" timestamp with time zone,
    "ActiveJobs" integer NOT NULL,
    "Concurrency" integer NOT NULL,
    "CapabilitiesJson" text NOT NULL,
    CONSTRAINT "PK_WorkerNodes" PRIMARY KEY ("Id")
);

CREATE TABLE "Workflows" (
    "Id" uuid NOT NULL,
    "Name" text NOT NULL,
    "CreatedUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Workflows" PRIMARY KEY ("Id")
);

INSERT INTO "ClusterLeases" ("Name", "Owner", "UntilUtc") VALUES ('admission', NULL, '1970-01-01T00:00:00Z'), ('packages', NULL, '1970-01-01T00:00:00Z'), ('scheduler', NULL, '1970-01-01T00:00:00Z');

CREATE INDEX "IX_AuditRecords_StartedUtc" ON "AuditRecords" ("StartedUtc");

CREATE INDEX "IX_JobDependencies_DependsOnJobId" ON "JobDependencies" ("DependsOnJobId");

CREATE INDEX "IX_WorkerNodes_HeartbeatUtc" ON "WorkerNodes" ("HeartbeatUtc");

CREATE INDEX "IX_Jobs_PackageId_State_LeaseUntilUtc" ON "Jobs" ("PackageId", "State", "LeaseUntilUtc");

CREATE INDEX "IX_Jobs_State_PriorityUtc" ON "Jobs" ("State", "PriorityUtc");

CREATE UNIQUE INDEX "IX_Jobs_WorkflowId_StepKey" ON "Jobs" ("WorkflowId", "StepKey");

ALTER TABLE "PackageChanges" ADD "MaxConcurrency" integer NULL;
ALTER TABLE "PackageChanges" ADD "PreviousMaxConcurrency" integer NULL;

CREATE INDEX "IX_Jobs_FinishedUtc" ON "Jobs" ("FinishedUtc");

CREATE INDEX "IX_Attempts_FinishedUtc" ON "Attempts" ("FinishedUtc");

CREATE INDEX "IX_JobLogs_TimestampUtc" ON "JobLogs" ("TimestampUtc");
