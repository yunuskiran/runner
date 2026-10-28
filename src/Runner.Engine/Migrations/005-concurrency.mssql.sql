ALTER TABLE [dbo].[PluginPackages] ADD [DefaultExecutionMode] int NOT NULL DEFAULT 0;
ALTER TABLE [dbo].[PluginChannels] ADD [ExecutionMode] int NOT NULL DEFAULT 0;
ALTER TABLE [dbo].[PackageChanges] ADD [ExecutionMode] int NOT NULL DEFAULT 0;
ALTER TABLE [dbo].[PackageChanges] ADD [PreviousExecutionMode] int NOT NULL DEFAULT 0;
ALTER TABLE [dbo].[PluginChannels] ADD [ExecutionToken] uniqueidentifier NULL;
ALTER TABLE [dbo].[PluginChannels] ADD [ExecutionLeaseUtc] datetime2(7) NULL;
