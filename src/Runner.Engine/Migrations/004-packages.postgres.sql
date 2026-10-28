ALTER TABLE "Jobs" ADD COLUMN "PackageId" character varying(48) NULL;
ALTER TABLE "Jobs" ADD COLUMN "PackageVersion" character varying(64) NULL;
ALTER TABLE "Schedules" ADD COLUMN "PackageId" character varying(48) NULL;
ALTER TABLE "Schedules" ADD COLUMN "PackageVersion" character varying(64) NULL;
CREATE TABLE "PluginPackages" (
 "PackageId" character varying(48) NOT NULL,
 "Version" character varying(64) NOT NULL,
 "EntryAssembly" text NOT NULL,
 "EntryType" text NOT NULL,
 "Digest" text NOT NULL,
 "Archive" bytea NOT NULL,
 "InstalledUtc" timestamp with time zone NOT NULL,
 PRIMARY KEY ("PackageId", "Version")
);
CREATE TABLE "PluginChannels" (
 "PackageId" character varying(48) NOT NULL PRIMARY KEY,
 "ActiveVersion" character varying(64) NULL,
 "Enabled" boolean NOT NULL,
 "Revision" integer NOT NULL,
 "ChangedUtc" timestamp with time zone NOT NULL
);
CREATE TABLE "PackageChanges" (
 "Id" uuid NOT NULL PRIMARY KEY,
 "PackageId" text NOT NULL,
 "PreviousVersion" text NULL,
 "Version" text NULL,
 "Enabled" boolean NOT NULL,
 "Revision" integer NOT NULL,
 "TimestampUtc" timestamp with time zone NOT NULL,
 "Actor" text NOT NULL
);
CREATE UNIQUE INDEX "IX_PackageChanges_PackageId_Revision" ON "PackageChanges" ("PackageId", "Revision");
