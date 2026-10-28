ALTER TABLE "PluginPackages" ADD "DefaultExecutionMode" integer NOT NULL DEFAULT 0;
ALTER TABLE "PluginChannels" ADD "ExecutionMode" integer NOT NULL DEFAULT 0;
ALTER TABLE "PackageChanges" ADD "ExecutionMode" integer NOT NULL DEFAULT 0;
ALTER TABLE "PackageChanges" ADD "PreviousExecutionMode" integer NOT NULL DEFAULT 0;
ALTER TABLE "PluginChannels" ADD "ExecutionToken" TEXT NULL;
ALTER TABLE "PluginChannels" ADD "ExecutionLeaseUtc" TEXT NULL;
