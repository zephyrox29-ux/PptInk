using System.IO.Compression;
using System.Text;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;

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
        {
            var buf = new char[4096];
            var sb = new StringBuilder();
            int read;
            while ((read = r.Read(buf, 0, buf.Length)) > 0)
            {
                if (sb.Length + read > 1024 * 1024)
                    throw new InvalidDataException("manifest.json 超过 1 MB 限制");
                sb.Append(buf, 0, read);
            }
            manifestJson = sb.ToString();
        }

        var manifest = PluginManifestJson.Deserialize(manifestJson)
            ?? throw new InvalidDataException("manifest 反序列化为空");
        manifest = ManifestValidator.Normalize(manifest);
        var manifestIssues = ManifestValidator.Validate(manifest, _ => true);
        if (manifestIssues.Any(i => i.IsError))
            throw new InvalidDataException("manifest 校验失败：" +
                string.Join("；", manifestIssues.Where(i => i.IsError).Select(i => $"{i.Field} {i.Message}")));

        var project = new ProjectModel { Manifest = manifest };
        var entryName = (manifest.Entry ?? "").Replace('\\', '/');
        bool entryFound = false;
        long actualBytes = 0;

        foreach (var e in archive.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;   // 目录项
            var n = e.FullName.Replace('\\', '/');
            using var s = e.Open();
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int read;
            while ((read = s.Read(buf, 0, buf.Length)) > 0)
            {
                actualBytes += read;
                if (actualBytes > PackageLayout.MaxUncompressedBytes)
                    throw new InvalidDataException("插件包实际解压体积超过 20 MB 限制");
                ms.Write(buf, 0, read);
            }
            if (string.Equals(n, entryName, StringComparison.OrdinalIgnoreCase))
            {
                project.LuaSource = Encoding.UTF8.GetString(ms.ToArray());
                entryFound = true;
            }
            else if (!string.Equals(n, PackageLayout.ManifestFile, StringComparison.OrdinalIgnoreCase))
                project.Resources.Add(new ResourceFile(n, ms.ToArray()));
        }
        if (!entryFound)
            throw new InvalidDataException($"入口脚本不在包内：{manifest.Entry}");
        var contentIssues = ManifestValidator.ValidateContent(project.LuaSource, project.Resources);
        if (contentIssues.Any(i => i.IsError))
            throw new InvalidDataException("包内容校验失败：" +
                string.Join("；", contentIssues.Where(i => i.IsError).Select(i => $"{i.Field} {i.Message}")));
        return project;
    }

    public static ProjectModel ImportDirectory(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        var manifestPath = Path.Combine(dir, PackageLayout.ManifestFile);
        if (!File.Exists(manifestPath)) throw new InvalidDataException("目录缺少 manifest.json");

        var manifest = PluginManifestJson.Deserialize(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("manifest 反序列化为空");
        manifest = ManifestValidator.Normalize(manifest);
        var manifestIssues = ManifestValidator.Validate(manifest, _ => true);
        if (manifestIssues.Any(i => i.IsError))
            throw new InvalidDataException("manifest 校验失败：" +
                string.Join("；", manifestIssues.Where(i => i.IsError).Select(i => $"{i.Field} {i.Message}")));

        var project = new ProjectModel { Manifest = manifest };
        var entryName = (manifest.Entry ?? "").Replace('\\', '/');
        long actualBytes = 0;
        int entries = 0;
        bool entryFound = false;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (PathSafety.IsUnsafeRelative(rel))
                throw new InvalidDataException($"目录包含越界路径：{rel}");
            if (++entries > PackageLayout.MaxEntries)
                throw new InvalidDataException("目录文件数超过 128 个限制");
            var info = new FileInfo(file);
            actualBytes += info.Length;
            if (actualBytes > PackageLayout.MaxUncompressedBytes)
                throw new InvalidDataException("目录内容超过 20 MB 限制");
            if (string.Equals(rel, entryName, StringComparison.OrdinalIgnoreCase))
            {
                project.LuaSource = File.ReadAllText(file);
                entryFound = true;
            }
            else if (!string.Equals(rel, PackageLayout.ManifestFile, StringComparison.OrdinalIgnoreCase))
                project.Resources.Add(new ResourceFile(rel, File.ReadAllBytes(file)));
        }
        if (!entryFound) throw new InvalidDataException($"入口脚本不在目录内：{manifest.Entry}");
        var contentIssues = ManifestValidator.ValidateContent(project.LuaSource, project.Resources);
        if (contentIssues.Any(i => i.IsError))
            throw new InvalidDataException("包内容校验失败：" +
                string.Join("；", contentIssues.Where(i => i.IsError).Select(i => $"{i.Field} {i.Message}")));
        return project;
    }
}