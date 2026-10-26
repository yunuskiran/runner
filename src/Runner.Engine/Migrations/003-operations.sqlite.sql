CREATE TABLE "Attempts" (
  "Id" TEXT NOT NULL PRIMARY KEY,
  "JobId" TEXT NOT NULL,
  "JobType" TEXT NOT NULL,
  "Attempt" integer NOT NULL,
  "Worker" TEXT NOT NULL,
  "StartedUtc" TEXT NOT NULL,
  "FinishedUtc" TEXT NULL,
  "Outcome" TEXT NOT NULL,
  "DurationMilliseconds" REAL NULL,
  "ErrorCode" TEXT NULL
);
CREATE TABLE "JobLogs" (
  "Id" TEXT NOT NULL PRIMARY KEY,
  "JobId" TEXT NOT NULL,
  "LeaseToken" TEXT NOT NULL,
  "TimestampUtc" TEXT NOT NULL,
  "Level" TEXT NOT NULL,
  "Message" TEXT NOT NULL
);
CREATE TABLE "Alerts" (
  "Id" TEXT NOT NULL PRIMARY KEY,
  "JobId" TEXT NOT NULL,
  "JobType" TEXT NOT NULL,
  "ErrorCode" TEXT NOT NULL,
  "CreatedUtc" TEXT NOT NULL,
  "Acknowledged" INTEGER NOT NULL,
  "DeliveryState" TEXT NOT NULL,
  "DeliveryAttempts" integer NOT NULL,
  "NextDeliveryUtc" TEXT NOT NULL,
  "DeliveryToken" TEXT NULL,
  "DeliveryLeaseUtc" TEXT NULL
);
CREATE INDEX "IX_Attempts_JobId_StartedUtc" ON "Attempts" ("JobId", "StartedUtc");
CREATE INDEX "IX_Attempts_StartedUtc" ON "Attempts" ("StartedUtc");
CREATE INDEX "IX_JobLogs_JobId_TimestampUtc" ON "JobLogs" ("JobId", "TimestampUtc");
CREATE INDEX "IX_Alerts_DeliveryState_NextDeliveryUtc" ON "Alerts" ("DeliveryState", "NextDeliveryUtc");
CREATE INDEX "IX_Alerts_CreatedUtc" ON "Alerts" ("CreatedUtc");
