using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Packaging;
using Xunit;

namespace PptInk.PluginStudio.Tests.Packaging;

public class PackageInstallerTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "psroot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Install_WritesAndOverwrites()
    {
        var root = NewRoot();
        try
        {
            var installer = new PackageInstaller(root);
            var p = PackageBuilderTests.ValidProject();

            Assert.True(installer.Install(p, out var err1), err1);
            Assert.True(File.Exists(Path.Combine(root, "a.b", "manifest.json")));

            Assert.True(installer.Install(p, out var err2), err2);   // 覆盖安装
            Assert.Empty(Directory.GetDirectories(root, ".staging-*"));
            Assert.Empty(Directory.GetDirectories(root, "*.backup-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Install_Invalid_Fails_AndLeavesNothingBehind()
    {
        var root = NewRoot();
        try
        {
            var p = PackageBuilderTests.ValidProject();
            p.Manifest.Id = "bad id";
            Assert.False(new PackageInstaller(root).Install(p, out var err));
            Assert.False(string.IsNullOrEmpty(err));
            if (Directory.Exists(root))
                Assert.Empty(Directory.GetDirectories(root, ".staging-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}