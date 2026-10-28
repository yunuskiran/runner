ALTER TABLE "Jobs" ADD COLUMN "PackageId" TEXT NULL;
ALTER TABLE "Jobs" ADD COLUMN "PackageVersion" TEXT NULL;
ALTER TABLE "Schedules" ADD COLUMN "PackageId" TEXT NULL;
ALTER TABLE "Schedules" ADD COLUMN "PackageVersion" TEXT NULL;
CREATE TABLE "PluginPackages" (
 "PackageId" TEXT NOT NULL,
 "Version" TEXT NOT NULL,
 "EntryAssembly" TEXT NOT NULL,
 "EntryType" TEXT NOT NULL,
 "Digest" TEXT NOT NULL,
 "Archive" BLOB NOT NULL,
 "InstalledUtc" TEXT NOT NULL,
 PRIMARY KEY ("PackageId", "Version")
);
CREATE TABLE "PluginChannels" (
 "PackageId" TEXT NOT NULL PRIMARY KEY,
 "ActiveVersion" TEXT NULL,
 "Enabled" INTEGER NOT NULL,
 "Revision" integer NOT NULL,
 "ChangedUtc" TEXT NOT NULL
);
CREATE TABLE "PackageChanges" (
 "Id" TEXT NOT NULL PRIMARY KEY,
 "PackageId" TEXT NOT NULL,
 "PreviousVersion" TEXT NULL,
 "Version" TEXT NULL,
 "Enabled" INTEGER NOT NULL,
 "Revision" integer NOT NULL,
 "TimestampUtc" TEXT NOT NULL,
 "Actor" TEXT NOT NULL
);
CREATE UNIQUE INDEX "IX_PackageChanges_PackageId_Revision" ON "PackageChanges" ("PackageId", "Revision");
