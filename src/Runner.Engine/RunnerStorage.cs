using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

public static class RunnerStorage
{
    public static bool IsSupported(string provider) => provider is "Sqlite" or "Postgres" or "Mssql";
    public static void Configure(DbContextOptionsBuilder builder, string provider, string connectionString)
    {
        switch (provider)
        {
            case "Sqlite": builder.UseSqlite(connectionString); break;
            case "Postgres": builder.UseNpgsql(connectionString); break;
            case "Mssql": builder.UseSqlServer(connectionString); break;
            default: throw new InvalidOperationException("Storage:Provider must be Sqlite, Postgres or Mssql");
        }
    }
}
