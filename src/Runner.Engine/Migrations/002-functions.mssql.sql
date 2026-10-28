ALTER TABLE [dbo].[Jobs] ADD [ResultJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL;
CREATE TABLE [dbo].[Functions] (
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Name] nvarchar(120) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Version] int NOT NULL,
    [Source] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [SourceHash] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Enabled] bit NOT NULL,
    [CreatedUtc] datetime2(7) NOT NULL
);
CREATE UNIQUE INDEX [IX_Functions_Name_Version] ON [dbo].[Functions] ([Name], [Version]);
