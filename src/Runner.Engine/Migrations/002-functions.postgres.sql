ALTER TABLE "Jobs" ADD COLUMN "ResultJson" text NULL;
CREATE TABLE "Functions" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Name" character varying(120) NOT NULL,
    "Version" integer NOT NULL,
    "Source" text NOT NULL,
    "SourceHash" character varying(64) NOT NULL,
    "Enabled" boolean NOT NULL,
    "CreatedUtc" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX "IX_Functions_Name_Version" ON "Functions" ("Name", "Version");
