using System.IO.Compression;
using System.Text;
using PptInk.PluginStudio.Core.Models;

namespace PptInk.PluginStudio.Core.Packaging;

/// <summary>从插件 zip / 目录导入为工程（用于反向编辑）。校验口径与主程序 InstallFromZip 一致。</summary>
public static class PackageImporter
{
    public static ProjectModel Import(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("插件包不存在", zipPath);
        using var archive = ZipFile.OpenRead(zipPath);

        if (archive.Entries.Count is < 1 or > PackageLayout.MaxEntries)
            throw new InvalidDataException($"插件包文件数不合法（1–{PackageLayout.MaxEntries}）");

        long total = 0;
        foreach (var e in archive.Entries)
        {
            var n = e.FullName.Replace('\\', '/');
            if (n.StartsWith('/') || Path.IsPathRooted(e.FullName) || n.Split('/').Any(p => p == ".."))
                throw new InvalidDataException("插件包包含越界路径");
            total += e.Length;
            if (total > PackageLayout.MaxUncompressedBytes)
                throw new InvalidDataException("插件包解压体积超过限制");
        }

        var manifestEntry = archive.GetEntry(PackageLayout.ManifestFile)
            ?? throw new InvalidDataException("缺少 manifest.json");
        string manifestJson;
        using (var s = manifestEntry.Open())
        using (var r = new StreamReader(s, Encoding.UTF8))
            manifestJson = r.ReadToEnd();

        var manifest = PluginManifestJson.Deserialize(manifestJson)
            ?? throw new InvalidDataException("manifest 反序列化为空");

        var project = new ProjectModel { Manifest = manifest };
        var entryName = (manifest.Entry ?? "").Replace('\\', '/');

        foreach (var e in archive.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;   // 目录项
            var n = e.FullName.Replace('\\', '/');
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            if (string.Equals(n, entryName, StringComparison.OrdinalIgnoreCase))
                project.LuaSource = Encoding.UTF8.GetString(ms.ToArray());
            else if (!string.Equals(n, PackageLayout.ManifestFile, StringComparison.OrdinalIgnoreCase))
                project.Resources.Add(new ResourceFile(n, ms.ToArray()));
        }
        return project;
    }

    public static ProjectModel ImportDirectory(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        var manifestPath = Path.Combine(dir, PackageLayout.ManifestFile);
        if (!File.Exists(manifestPath)) throw new InvalidDataException("目录缺少 manifest.json");

        var manifest = PluginManifestJson.Deserialize(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("manifest 反序列化为空");

        var project = new ProjectModel { Manifest = manifest };
        var entryName = (manifest.Entry ?? "").Replace('\\', '/');

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (string.Equals(rel, entryName, StringComparison.OrdinalIgnoreCase))
                project.LuaSource = File.ReadAllText(file);
            else if (!string.Equals(rel, PackageLayout.ManifestFile, StringComparison.OrdinalIgnoreCase))
                project.Resources.Add(new ResourceFile(rel, File.ReadAllBytes(file)));
        }
        return project;
    }
}