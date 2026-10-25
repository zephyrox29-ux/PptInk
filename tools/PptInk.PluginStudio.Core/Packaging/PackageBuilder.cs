using System.IO.Compression;
using System.Text;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;

namespace PptInk.PluginStudio.Core.Packaging;

/// <summary>把工程打包为插件 zip。打包前执行完整校验（manifest + Lua + 交叉），存在 Error 级问题即拒绝。</summary>
public static class PackageBuilder
{
    /// <summary>校验并写出 zip（覆盖已存在文件）。</summary>
    public static void Build(ProjectModel project, string zipPath)
    {
        var manifest = ManifestValidator.Normalize(CloneManifest(project.Manifest));
        var entry = (manifest.Entry ?? "").Replace('\\', '/');

        bool EntryExists(string e) =>
            PathsEqual(e, entry) || project.Resources.Any(r => PathsEqual(r.RelativePath, e));

        var issues = new List<ValidationIssue>();
        issues.AddRange(ManifestValidator.Validate(manifest, EntryExists));
        issues.AddRange(LuaSyntaxChecker.Check(project.LuaSource));
        var snapshot = new ProjectModel { Manifest = manifest, LuaSource = project.LuaSource, Resources = project.Resources };
        issues.AddRange(CrossConsistencyChecker.Check(snapshot));

        var firstError = issues.FirstOrDefault(i => i.IsError);
        if (firstError is not null)
            throw new InvalidOperationException($"校验未通过，无法打包：{firstError.Field} — {firstError.Message}");

        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteText(zip, PackageLayout.ManifestFile, PluginManifestJson.Serialize(manifest));
        WriteText(zip, entry, project.LuaSource ?? "");
        foreach (var res in project.Resources)
        {
            if (PathsEqual(res.RelativePath, entry)) continue;
            var e = zip.CreateEntry(res.RelativePath.Replace('\\', '/'));
            using var s = e.Open();
            s.Write(res.Content, 0, res.Content.Length);
        }
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name.Replace('\\', '/'));
        using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
        s.Write(content);
    }

    private static bool PathsEqual(string? a, string? b) =>
        string.Equals(a?.Replace('\\', '/'), b?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static PluginManifest CloneManifest(PluginManifest m) => new()
    {
        Magic = m.Magic,
        Id = m.Id,
        Name = m.Name,
        Version = m.Version,
        Author = m.Author,
        Description = m.Description,
        Entry = m.Entry,
        Permissions = (string[])(m.Permissions ?? Array.Empty<string>()).Clone(),
        Signature = m.Signature,
        Panel = m.Panel is null ? null : new PanelDecl
        {
            Dock = m.Panel.Dock,
            Width = m.Panel.Width,
            Height = m.Panel.Height,
            Alpha = m.Panel.Alpha,
            ShowInIsland = m.Panel.ShowInIsland,
            Elements = (m.Panel.Elements ?? Array.Empty<PanelElement>()).Select(e => new PanelElement
            {
                Type = e.Type,
                Id = e.Id,
                Text = e.Text,
                Min = e.Min,
                Max = e.Max,
                Value = e.Value,
                OnEvent = e.OnEvent,
            }).ToArray(),
        },
    };
}