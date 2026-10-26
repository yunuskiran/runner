ALTER TABLE "Jobs" ADD COLUMN "ResultJson" TEXT NULL;
CREATE TABLE "Functions" (
    "Id" TEXT NOT NULL PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    "Source" TEXT NOT NULL,
    "SourceHash" TEXT NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "CreatedUtc" TEXT NOT NULL
);
CREATE UNIQUE INDEX "IX_Functions_Name_Version" ON "Functions" ("Name", "Version");
