using System.IO.Compression;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Packaging;
using Xunit;

namespace PptInk.PluginStudio.Tests.Packaging;

public class PackageBuilderTests
{
    internal static ProjectModel ValidProject() => new()
    {
        Manifest = new PluginManifest
        {
            Magic = 1,
            Id = "a.b",
            Name = "n",
            Entry = "main.lua",
            Panel = new PanelDecl
            {
                Elements = new[] { new PanelElement { Type = "button", Text = "开始", OnEvent = "onToggle" } },
            },
        },
        LuaSource = "function onActivate() end\nfunction onToggle() end\n",
    };

    [Fact]
    public void Build_WritesManifestAndEntry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            PackageBuilder.Build(ValidProject(), tmp);
            using var z = ZipFile.OpenRead(tmp);
            Assert.Contains(z.Entries, e => e.FullName == "manifest.json");
            Assert.Contains(z.Entries, e => e.FullName == "main.lua");
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void Build_Rejects_InvalidManifest()
    {
        var p = ValidProject();
        p.Manifest.Id = "bad id";
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.Throws<InvalidOperationException>(() => PackageBuilder.Build(p, tmp));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Build_Rejects_LuaSyntaxError()
    {
        var p = ValidProject();
        p.LuaSource = "function onActivate(\n";
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.Throws<InvalidOperationException>(() => PackageBuilder.Build(p, tmp));
    }
}