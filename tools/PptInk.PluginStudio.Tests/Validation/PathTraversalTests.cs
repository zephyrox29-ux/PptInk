using System.IO.Compression;
using System.Text;
using PptInk.PluginStudio.Core;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Packaging;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

namespace PptInk.PluginStudio.Tests.Validation;

/// <summary>自检补充：路径穿越必须被拦截（入口脚本、资源、导入的 zip 条目）。</summary>
public class PathTraversalTests
{
    private static PluginManifest Base() => new() { Magic = 1, Id = "a.b", Name = "n", Entry = "main.lua" };

    [Theory]
    [InlineData("../evil.lua", true)]
    [InlineData("sub/main.lua", false)]
    [InlineData("a/../../b.lua", true)]
    [InlineData("/abs.lua", true)]
    public void Entry_Traversal(string entry, bool hasError)
    {
        var m = Base();
        m.Entry = entry;
        var issues = ManifestValidator.Validate(m, _ => true);
        Assert.Equal(hasError, issues.Any(i => i.IsError && i.Field == "entry"));
    }

    [Fact]
    public void Build_Rejects_TraversalResource()
    {
        var project = new ProjectModel
        {
            Manifest = new PluginManifest { Magic = 3, Id = "a.b", Name = "n", Entry = "main.lua" },
            LuaSource = "function onStart() end\n",
        };
        project.Resources.Add(new ResourceFile("../evil.txt", new byte[] { 1 }));

        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.Throws<InvalidOperationException>(() => PackageBuilder.Build(project, tmp));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Import_Rejects_TraversalEntry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("../evil.txt");
                using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
                s.Write("x");
            }
            Assert.Throws<InvalidDataException>(() => PackageImporter.Import(tmp));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void PathSafety_Rules()
    {
        Assert.True(PathSafety.IsUnsafeRelative("../x"));
        Assert.True(PathSafety.IsUnsafeRelative("/x"));
        Assert.True(PathSafety.IsUnsafeRelative(""));
        Assert.False(PathSafety.IsUnsafeRelative("res/icon.png"));
        Assert.False(PathSafety.IsUnsafeRelative("icon.png"));
    }
}