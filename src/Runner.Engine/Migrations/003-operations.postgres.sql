CREATE TABLE "Attempts" (
  "Id" uuid NOT NULL PRIMARY KEY,
  "JobId" uuid NOT NULL,
  "JobType" text NOT NULL,
  "Attempt" integer NOT NULL,
  "Worker" text NOT NULL,
  "StartedUtc" timestamp with time zone NOT NULL,
  "FinishedUtc" timestamp with time zone NULL,
  "Outcome" text NOT NULL,
  "DurationMilliseconds" double precision NULL,
  "ErrorCode" text NULL
);
CREATE TABLE "JobLogs" (
  "Id" uuid NOT NULL PRIMARY KEY,
  "JobId" uuid NOT NULL,
  "LeaseToken" uuid NOT NULL,
  "TimestampUtc" timestamp with time zone NOT NULL,
  "Level" text NOT NULL,
  "Message" text NOT NULL
);
CREATE TABLE "Alerts" (
  "Id" uuid NOT NULL PRIMARY KEY,
  "JobId" uuid NOT NULL,
  "JobType" text NOT NULL,
  "ErrorCode" text NOT NULL,
  "CreatedUtc" timestamp with time zone NOT NULL,
  "Acknowledged" boolean NOT NULL,
  "DeliveryState" text NOT NULL,
  "DeliveryAttempts" integer NOT NULL,
  "NextDeliveryUtc" timestamp with time zone NOT NULL,
  "DeliveryToken" uuid NULL,
  "DeliveryLeaseUtc" timestamp with time zone NULL
);
CREATE INDEX "IX_Attempts_JobId_StartedUtc" ON "Attempts" ("JobId", "StartedUtc");
CREATE INDEX "IX_Attempts_StartedUtc" ON "Attempts" ("StartedUtc");
CREATE INDEX "IX_JobLogs_JobId_TimestampUtc" ON "JobLogs" ("JobId", "TimestampUtc");
CREATE INDEX "IX_Alerts_DeliveryState_NextDeliveryUtc" ON "Alerts" ("DeliveryState", "NextDeliveryUtc");
CREATE INDEX "IX_Alerts_CreatedUtc" ON "Alerts" ("CreatedUtc");
