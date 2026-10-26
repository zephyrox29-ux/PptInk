using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;

namespace PptInk.PluginStudio.Core.Packaging;

/// <summary>
/// 把工程安装到插件根目录（默认 <c>%LocalAppData%\PptInk\plugins\&lt;Id&gt;</c>）。
/// 语义对齐主程序 InstallFromZip：staging 写入 → 备份已存在目录 → 替换 → 失败回滚。
/// </summary>
public sealed class PackageInstaller
{
    private readonly string _root;

    public PackageInstaller(string? pluginRoot = null) => _root = pluginRoot ?? AppPaths.PluginRoot;

    public bool Install(ProjectModel project, out string error)
    {
        error = "";
        string? staging = null;
        string? backup = null;
        try
        {
            var manifest = ManifestValidator.Normalize(project.Manifest);

            var issues = new List<ValidationIssue>();
            issues.AddRange(ManifestValidator.Validate(manifest, _ => true));
            issues.AddRange(LuaSyntaxChecker.Check(project.LuaSource));
            issues.AddRange(CrossConsistencyChecker.Check(project));
            var firstError = issues.FirstOrDefault(i => i.IsError);
            if (firstError is not null)
            {
                error = $"{firstError.Field}：{firstError.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.Id)) { error = "插件 Id 为空，无法安装"; return false; }

            Directory.CreateDirectory(_root);
            var finalDir = Path.Combine(_root, manifest.Id);

            staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

            File.WriteAllText(Path.Combine(staging, PackageLayout.ManifestFile), PluginManifestJson.Serialize(manifest));

            var entryRel = (manifest.Entry ?? "main.lua").Replace('\\', '/');
            var entryFull = Path.GetFullPath(Path.Combine(staging, entryRel.Replace('/', Path.DirectorySeparatorChar)));
            if (!entryFull.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("入口脚本路径越界");
            Directory.CreateDirectory(Path.GetDirectoryName(entryFull)!);
            File.WriteAllText(entryFull, project.LuaSource ?? "");

            foreach (var res in project.Resources)
            {
                var rel = res.RelativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.GetFullPath(Path.Combine(staging, rel));
                if (!dest.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, res.Content);
            }

            if (Directory.Exists(finalDir))
            {
                backup = finalDir + ".backup-" + Guid.NewGuid().ToString("N");
                Directory.Move(finalDir, backup);
            }
            try
            {
                Directory.Move(staging, finalDir);
                staging = null;
            }
            catch
            {
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(finalDir))
                {
                    Directory.Move(backup, finalDir);
                    backup = null;
                }
                throw;
            }

            if (backup is not null)
            {
                try { Directory.Delete(backup, true); } catch { }
                backup = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            FilePluginStudioLog.Instance.Error(ex, "插件安装失败");
            return false;
        }
        finally
        {
            if (staging is not null)
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }
}