using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

namespace PptInk.PluginStudio.Tests.Validation;

/// <summary>
/// 包内容限制必须与主程序安装口径一致：入口脚本 256KB、包内 128 条目、解压后 20MB。
/// 旧版工作室只校验 manifest，导致"校验全绿 → 一键安装成功 → 重启扫描时插件消失"。
/// </summary>
public class ContentLimitTests
{
    [Fact]
    public void Script_AtLimit_IsAccepted()
    {
        var lua = new string('a', ManifestValidator.ScriptMaxBytes);
        Assert.Empty(ManifestValidator.ValidateContent(lua, null));
    }

    [Fact]
    public void Script_OverLimit_IsRejected()
    {
        var lua = new string('a', ManifestValidator.ScriptMaxBytes + 1);
        var issues = ManifestValidator.ValidateContent(lua, null);
        Assert.Contains(issues, i => i.IsError && i.Field == "lua");
    }

    [Fact]
    public void EntryCount_AtLimit_IsAccepted()
    {
        // manifest.json + 入口脚本 + 126 个资源 = 128 条目
        var res = Enumerable.Range(0, 126)
            .Select(i => new ResourceFile($"r/{i}.txt", new byte[] { 1 }))
            .ToList();
        Assert.Empty(ManifestValidator.ValidateContent("x", res));
    }

    [Fact]
    public void EntryCount_OverLimit_IsRejected()
    {
        var res = Enumerable.Range(0, 127)
            .Select(i => new ResourceFile($"r/{i}.txt", new byte[] { 1 }))
            .ToList();
        var issues = ManifestValidator.ValidateContent("x", res);
        Assert.Contains(issues, i => i.IsError && i.Field == "resources" && i.Message.Contains("128"));
    }

    [Fact]
    public void TotalBytes_OverLimit_IsRejected()
    {
        var res = new List<ResourceFile>
        {
            new("big.bin", new byte[(int)ManifestValidator.PackageMaxBytes + 1]),
        };
        var issues = ManifestValidator.ValidateContent("x", res);
        Assert.Contains(issues, i => i.IsError && i.Field == "resources" && i.Message.Contains("20 MB"));
    }

    [Fact]
    public void UnsafeResourcePath_IsRejected()
    {
        var res = new List<ResourceFile> { new("../evil.txt", new byte[] { 1 }) };
        var issues = ManifestValidator.ValidateContent("x", res);
        Assert.Contains(issues, i => i.IsError && i.Field == "resources");
    }
}