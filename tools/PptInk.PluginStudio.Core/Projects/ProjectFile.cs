using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;

namespace PptInk.PluginStudio.Core.Projects;

/// <summary>
/// .ppstudio 工程读写：工程目录内 `project.ppstudio`（manifest JSON）+ 入口脚本 + `resources/`。
/// </summary>
public static class ProjectFile
{
    public const string FileName = "project.ppstudio";

    public static void Save(ProjectModel project, string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var manifest = ManifestValidatorNormalize(project.Manifest);
        File.WriteAllText(Path.Combine(projectDir, FileName), PluginManifestJson.Serialize(manifest));

        var entryRel = (manifest.Entry ?? "main.lua").Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        var entryFull = ResolveInside(projectDir, entryRel, "入口脚本");
        Directory.CreateDirectory(Path.GetDirectoryName(entryFull)!);
        File.WriteAllText(entryFull, project.LuaSource ?? "");

        var resourcesDir = Path.Combine(projectDir, "resources");
        foreach (var res in project.Resources)
        {
            var dest = Path.GetFullPath(Path.Combine(resourcesDir, res.RelativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(resourcesDir) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, res.Content);
        }

        project.ProjectPath = Path.Combine(projectDir, FileName);
    }

    public static ProjectModel Load(string projectDir)
    {
        var metaPath = Path.Combine(projectDir, FileName);
        if (!File.Exists(metaPath)) throw new FileNotFoundException("工程文件不存在", metaPath);

        var manifest = PluginManifestJson.Deserialize(File.ReadAllText(metaPath))
            ?? throw new InvalidDataException("工程文件损坏（manifest 反序列化为空）");

        var project = new ProjectModel { Manifest = manifest, ProjectPath = metaPath };
        var entryRel = (manifest.Entry ?? "main.lua").Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        var entryFull = ResolveInside(projectDir, entryRel, "入口脚本");
        if (File.Exists(entryFull)) project.LuaSource = File.ReadAllText(entryFull);

        var resourcesDir = Path.Combine(projectDir, "resources");
        if (Directory.Exists(resourcesDir))
        {
            foreach (var file in Directory.EnumerateFiles(resourcesDir, "*", SearchOption.AllDirectories))
                project.Resources.Add(new ResourceFile(
                    Path.GetRelativePath(resourcesDir, file).Replace('\\', '/'), File.ReadAllBytes(file)));
        }
        return project;
    }

    private static PluginManifest ManifestValidatorNormalize(PluginManifest m) => ManifestValidator.Normalize(m);

    /// <summary>不可信工程/导入包的 Entry 只能落在工程目录内；绝对路径、..、盘符
    /// 相对路径都拒绝。Save 与 Load 共用，防止“打开恶意工程→点保存”任意写文件。</summary>
    private static string ResolveInside(string root, string relative, string what)
    {
        if (PathSafety.IsUnsafeRelative(relative))
            throw new InvalidDataException($"{what}路径非法：{relative}");
        string fullRoot = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what}越过工程目录：{relative}");
        return full;
    }
}