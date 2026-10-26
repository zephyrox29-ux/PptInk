using System.IO.Compression;
using System.Text;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Packaging;
using Xunit;

namespace PptInk.PluginStudio.Tests.Packaging;

public class PackageImporterTests
{
    [Fact]
    public void BuildThenImport_RoundTrips()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var p = PackageBuilderTests.ValidProject();
            p.Resources.Add(new ResourceFile("resources/icon.txt", new byte[] { 1, 2, 3 }));
            PackageBuilder.Build(p, tmp);

            var back = PackageImporter.Import(tmp);
            Assert.Equal(p.Manifest.Id, back.Manifest.Id);
            Assert.Contains("onToggle", back.LuaSource);
            Assert.Contains(back.Resources, r => r.RelativePath == "resources/icon.txt");
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void Import_MissingManifest_Throws()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("main.lua");
                using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
                s.Write("-- hi");
            }
            Assert.Throws<InvalidDataException>(() => PackageImporter.Import(tmp));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}