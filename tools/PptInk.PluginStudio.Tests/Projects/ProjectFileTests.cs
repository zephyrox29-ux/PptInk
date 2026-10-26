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
}