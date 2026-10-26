using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Projects;
using Xunit;

namespace PptInk.PluginStudio.Tests.Projects;

public class ProjectFileTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psproj-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new ProjectModel
            {
                Manifest = new PluginManifest { Magic = 3, Id = "a.b", Name = "n", Entry = "main.lua" },
                LuaSource = "function onStart() showToast('x') end\n",
            };
            p.Resources.Add(new ResourceFile("extra/a.txt", new byte[] { 9 }));

            ProjectFile.Save(p, dir);
            var back = ProjectFile.Load(dir);

            Assert.Equal("a.b", back.Manifest.Id);
            Assert.Contains("showToast", back.LuaSource);
            Assert.Contains(back.Resources, r => r.RelativePath == "extra/a.txt");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_Rejects_TraversalEntry_AndWritesNothingOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "psproj-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "proj");
        try
        {
            var p = new ProjectModel
            {
                Manifest = new PluginManifest { Magic = 3, Id = "a.b", Name = "n", Entry = "../evil.lua" },
                LuaSource = "function onStart() end\n",
            };

            Assert.Throws<InvalidDataException>(() => ProjectFile.Save(p, dir));
            Assert.False(File.Exists(Path.Combine(root, "evil.lua")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_Rejects_TraversalEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psproj-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var manifest = new PluginManifest { Magic = 3, Id = "a.b", Name = "n", Entry = "../evil.lua" };
            File.WriteAllText(Path.Combine(dir, ProjectFile.FileName), PluginManifestJson.Serialize(manifest));

            Assert.Throws<InvalidDataException>(() => ProjectFile.Load(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_Skips_TraversalResource()
    {
        var root = Path.Combine(Path.GetTempPath(), "psproj-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "proj");
        try
        {
            var p = new ProjectModel
            {
                Manifest = new PluginManifest { Magic = 3, Id = "a.b", Name = "n", Entry = "main.lua" },
                LuaSource = "function onStart() end\n",
            };
            p.Resources.Add(new ResourceFile("../evil.txt", new byte[] { 1 }));

            ProjectFile.Save(p, dir);

            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));   // 也不得落进 resources 的上一级
            Assert.True(File.Exists(Path.Combine(dir, "main.lua")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}