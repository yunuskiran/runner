-- Version 6. Run once through --upgrade-platform with all hosts stopped. Transaction owned by the upgrade CLI.

ALTER TABLE "Jobs" ADD "WorkflowId" TEXT NULL;

ALTER TABLE "Jobs" ADD "StepKey" TEXT NULL;

ALTER TABLE "Jobs" ADD "Queue" TEXT NOT NULL DEFAULT 'default';

ALTER TABLE "Jobs" ADD "Priority" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "Jobs" ADD "PriorityUtc" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z';

ALTER TABLE "PluginPackages" ADD "ManifestJson" TEXT NOT NULL DEFAULT '{}';

ALTER TABLE "PluginChannels" ADD "MaxConcurrency" INTEGER NULL;

ALTER TABLE "Schedules" ADD "Cron" TEXT NULL;

ALTER TABLE "Schedules" ADD "TimeZoneId" TEXT NOT NULL DEFAULT 'UTC';

ALTER TABLE "Schedules" ADD "MisfirePolicy" TEXT NOT NULL DEFAULT 'Coalesce';

UPDATE "Jobs" SET "PriorityUtc" = "CreatedUtc";

CREATE TABLE "AuditRecords" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AuditRecords" PRIMARY KEY,
    "Actor" TEXT NOT NULL,
    "AuthenticationType" TEXT NOT NULL,
    "Method" TEXT NOT NULL,
    "Path" TEXT NOT NULL,
    "TraceId" TEXT NOT NULL,
    "DetailsJson" TEXT NOT NULL,
    "StartedUtc" TEXT NOT NULL,
    "CompletedUtc" TEXT NULL,
    "StatusCode" INTEGER NULL
);

CREATE TABLE "ClusterLeases" (
    "Name" TEXT NOT NULL CONSTRAINT "PK_ClusterLeases" PRIMARY KEY,
    "Owner" TEXT NULL,
    "UntilUtc" TEXT NOT NULL
);

CREATE TABLE "JobDependencies" (
    "JobId" TEXT NOT NULL,
    "DependsOnJobId" TEXT NOT NULL,
    CONSTRAINT "PK_JobDependencies" PRIMARY KEY ("JobId", "DependsOnJobId")
);

CREATE TABLE "WorkerNodes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WorkerNodes" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "StartedUtc" TEXT NOT NULL,
    "HeartbeatUtc" TEXT NOT NULL,
    "DrainRequested" INTEGER NOT NULL,
    "DrainDeadlineUtc" TEXT NULL,
    "DrainAcknowledgedUtc" TEXT NULL,
    "ActiveJobs" INTEGER NOT NULL,
    "Concurrency" INTEGER NOT NULL,
    "CapabilitiesJson" TEXT NOT NULL
);

CREATE TABLE "Workflows" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Workflows" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL
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
