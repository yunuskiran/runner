CREATE TABLE [dbo].[Attempts] (
  [Id] uniqueidentifier NOT NULL PRIMARY KEY,
  [JobId] uniqueidentifier NOT NULL,
  [JobType] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [Attempt] int NOT NULL,
  [Worker] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [StartedUtc] datetime2(7) NOT NULL,
  [FinishedUtc] datetime2(7) NULL,
  [Outcome] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [DurationMilliseconds] float NULL,
  [ErrorCode] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL
);
CREATE TABLE [dbo].[JobLogs] (
  [Id] uniqueidentifier NOT NULL PRIMARY KEY,
  [JobId] uniqueidentifier NOT NULL,
  [LeaseToken] uniqueidentifier NOT NULL,
  [TimestampUtc] datetime2(7) NOT NULL,
  [Level] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [Message] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL
);
CREATE TABLE [dbo].[Alerts] (
  [Id] uniqueidentifier NOT NULL PRIMARY KEY,
  [JobId] uniqueidentifier NOT NULL,
  [JobType] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [ErrorCode] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [CreatedUtc] datetime2(7) NOT NULL,
  [Acknowledged] bit NOT NULL,
  [DeliveryState] nvarchar(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
  [DeliveryAttempts] int NOT NULL,
  [NextDeliveryUtc] datetime2(7) NOT NULL,
  [DeliveryToken] uniqueidentifier NULL,
  [DeliveryLeaseUtc] datetime2(7) NULL
);
CREATE INDEX [IX_Attempts_JobId_StartedUtc] ON [dbo].[Attempts] ([JobId], [StartedUtc]);
CREATE INDEX [IX_Attempts_StartedUtc] ON [dbo].[Attempts] ([StartedUtc]);
CREATE INDEX [IX_JobLogs_JobId_TimestampUtc] ON [dbo].[JobLogs] ([JobId], [TimestampUtc]);
CREATE INDEX [IX_Alerts_DeliveryState_NextDeliveryUtc] ON [dbo].[Alerts] ([DeliveryState], [NextDeliveryUtc]);
CREATE INDEX [IX_Alerts_CreatedUtc] ON [dbo].[Alerts] ([CreatedUtc]);
