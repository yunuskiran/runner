-- Version 6. Run once through --upgrade-platform with all hosts stopped. Transaction owned by the upgrade CLI.

ALTER TABLE [dbo].[Jobs] ADD [WorkflowId] uniqueidentifier NULL;
GO

ALTER TABLE [dbo].[Jobs] ADD [StepKey] nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
GO

ALTER TABLE [dbo].[Jobs] ADD [Queue] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT 'default';
GO

ALTER TABLE [dbo].[Jobs] ADD [Priority] int NOT NULL DEFAULT 0;
GO

ALTER TABLE [dbo].[Jobs] ADD [PriorityUtc] datetime2(7) NOT NULL DEFAULT '1970-01-01T00:00:00Z';
GO

ALTER TABLE [dbo].[PluginPackages] ADD [ManifestJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT '{}';
GO

ALTER TABLE [dbo].[PluginChannels] ADD [MaxConcurrency] int NULL;
GO

ALTER TABLE [dbo].[Schedules] ADD [Cron] nvarchar(120) COLLATE Latin1_General_100_BIN2 NULL;
GO

ALTER TABLE [dbo].[Schedules] ADD [TimeZoneId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT 'UTC';
GO

ALTER TABLE [dbo].[Schedules] ADD [MisfirePolicy] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT 'Coalesce';
GO

UPDATE [dbo].[Jobs] SET [PriorityUtc] = [CreatedUtc];
GO

CREATE TABLE [dbo].[AuditRecords] (
    [Id] uniqueidentifier NOT NULL,
    [Actor] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [AuthenticationType] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Method] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Path] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [TraceId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [DetailsJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [StartedUtc] datetime2 NOT NULL,
    [CompletedUtc] datetime2 NULL,
    [StatusCode] int NULL,
    CONSTRAINT [PK_AuditRecords] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [dbo].[ClusterLeases] (
    [Name] nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Owner] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
    [UntilUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_ClusterLeases] PRIMARY KEY ([Name])
);
GO

CREATE TABLE [dbo].[JobDependencies] (
    [JobId] uniqueidentifier NOT NULL,
    [DependsOnJobId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_JobDependencies] PRIMARY KEY ([JobId], [DependsOnJobId])
);
GO

CREATE TABLE [dbo].[WorkerNodes] (
    [Id] nvarchar(160) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Name] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [StartedUtc] datetime2 NOT NULL,
    [HeartbeatUtc] datetime2 NOT NULL,
    [DrainRequested] bit NOT NULL,
    [DrainDeadlineUtc] datetime2 NULL,
    [DrainAcknowledgedUtc] datetime2 NULL,
    [ActiveJobs] int NOT NULL,
    [Concurrency] int NOT NULL,
    [CapabilitiesJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT [PK_WorkerNodes] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [dbo].[Workflows] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_Workflows] PRIMARY KEY ([Id])
);
GO

INSERT INTO [dbo].[ClusterLeases] ([Name], [Owner], [UntilUtc]) VALUES ('admission', NULL, '1970-01-01T00:00:00Z'), ('packages', NULL, '1970-01-01T00:00:00Z'), ('scheduler', NULL, '1970-01-01T00:00:00Z');
GO

CREATE INDEX [IX_AuditRecords_StartedUtc] ON [dbo].[AuditRecords] ([StartedUtc]);
GO

CREATE INDEX [IX_JobDependencies_DependsOnJobId] ON [dbo].[JobDependencies] ([DependsOnJobId]);
GO

CREATE INDEX [IX_WorkerNodes_HeartbeatUtc] ON [dbo].[WorkerNodes] ([HeartbeatUtc]);
GO

CREATE INDEX [IX_Jobs_PackageId_State_LeaseUntilUtc] ON [dbo].[Jobs] ([PackageId], [State], [LeaseUntilUtc]);
GO

CREATE INDEX [IX_Jobs_State_PriorityUtc] ON [dbo].[Jobs] ([State], [PriorityUtc]);
GO

CREATE UNIQUE INDEX [IX_Jobs_WorkflowId_StepKey] ON [dbo].[Jobs] ([WorkflowId], [StepKey]) WHERE [WorkflowId] IS NOT NULL AND [StepKey] IS NOT NULL;

ALTER TABLE [dbo].[PackageChanges] ADD [MaxConcurrency] int NULL;
GO
ALTER TABLE [dbo].[PackageChanges] ADD [PreviousMaxConcurrency] int NULL;

CREATE INDEX [IX_Jobs_FinishedUtc] ON [dbo].[Jobs] ([FinishedUtc]);

CREATE INDEX [IX_Attempts_FinishedUtc] ON [dbo].[Attempts] ([FinishedUtc]);

CREATE INDEX [IX_JobLogs_TimestampUtc] ON [dbo].[JobLogs] ([TimestampUtc]);
