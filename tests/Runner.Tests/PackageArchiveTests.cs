using Runner.Engine.Packaging;
using Xunit;
namespace Runner.Tests;

public sealed class PackageArchiveTests
{
    [Theory]
    [InlineData("../secret.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:\\Temp\\test.dll")]
    [InlineData("folder/CON.dll")]
    [InlineData("folder/escape.")]
    [InlineData("folder//entry.dll")]
    public void Unsafe_paths_are_rejected(string path) => Assert.Throws<PackageException>(() => PackageArchive.SafePath(path));
    [Theory]
    [InlineData("company.echo", "1.2.3", true)]
    [InlineData("company.echo", "1.2.3-rc.1", true)]
    [InlineData("company.echo", "01.2.3", false)]
    [InlineData("company.echo", "1.2.3-01", false)]
    [InlineData("../escape", "1.0.0", false)]
    [InlineData("company.echo", "latest", false)]
    public void Package_identity_is_versioned(string id, string version, bool expected) => Assert.Equal(expected, PackageArchive.ValidIdentity(id, version));
    [Fact] public void Duplicate_case_collisions_and_shared_contracts_are_rejected()
    {
        Assert.Throws<PackageException>(() => PackageArchive.Build([new("entry.dll", []), new("ENTRY.dll", [])], "entry.dll"));
        Assert.Throws<PackageException>(() => PackageArchive.Build([new("entry.dll", []), new("Runner.Contracts.dll", [])], "entry.dll"));
    }
    [Fact] public void Invalid_DLL_is_rejected_as_a_package_error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-invalid-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { File.WriteAllText(Path.Combine(directory, "entry.dll"), "Not an assembly"); Assert.Throws<PackageException>(() => PackageArchive.Inspect(directory, "entry.dll", null)); }
        finally { Directory.Delete(directory, true); }
    }
}
