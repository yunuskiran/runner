ALTER TABLE [dbo].[Jobs] ADD [PackageId] nvarchar(48) COLLATE Latin1_General_100_BIN2 NULL;
ALTER TABLE [dbo].[Jobs] ADD [PackageVersion] nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
ALTER TABLE [dbo].[Schedules] ADD [PackageId] nvarchar(48) COLLATE Latin1_General_100_BIN2 NULL;
ALTER TABLE [dbo].[Schedules] ADD [PackageVersion] nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
CREATE TABLE [dbo].[PluginPackages] (
 [PackageId] nvarchar(48) COLLATE Latin1_General_100_BIN2 NOT NULL,
 [Version] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 [EntryAssembly] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
 [EntryType] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
 [Digest] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
 [Archive] varbinary(max) NOT NULL,
 [InstalledUtc] datetime2(7) NOT NULL,
 PRIMARY KEY ([PackageId], [Version])
);
CREATE TABLE [dbo].[PluginChannels] (
 [PackageId] nvarchar(48) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
 [ActiveVersion] nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
 [Enabled] bit NOT NULL,
 [Revision] int NOT NULL,
 [ChangedUtc] datetime2(7) NOT NULL
);
CREATE TABLE [dbo].[PackageChanges] (
 [Id] uniqueidentifier NOT NULL PRIMARY KEY,
 [PackageId] nvarchar(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
 [PreviousVersion] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
 [Version] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
 [Enabled] bit NOT NULL,
 [Revision] int NOT NULL,
 [TimestampUtc] datetime2(7) NOT NULL,
 [Actor] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL
);
CREATE UNIQUE INDEX [IX_PackageChanges_PackageId_Revision] ON [dbo].[PackageChanges] ([PackageId], [Revision]);
