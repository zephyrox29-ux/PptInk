using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

namespace PptInk.PluginStudio.Tests.Validation;

public class CrossConsistencyCheckerTests
{
    private static ProjectModel Proj(int magic, string lua, string[] perms) => new()
    {
        Manifest = new PluginManifest { Magic = magic, Id = "a.b", Name = "n", Entry = "main.lua", Permissions = perms },
        LuaSource = lua,
    };

    [Fact]
    public void ApiWithoutPermission_IsError()
    {
        var issues = CrossConsistencyChecker.Check(Proj(3, "function onStart() writeClipboard('x') end", Array.Empty<string>()));
        Assert.Contains(issues, i => i.IsError && i.Field == "permissions");
    }

    [Fact]
    public void ApiWithPermission_Ok()
    {
        var issues = CrossConsistencyChecker.Check(Proj(3, "function onStart() writeClipboard('x') end", new[] { "clipboard" }));
        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void PanelOnEvent_NotDefined_IsError()
    {
        var p = Proj(1, "function onActivate() end", Array.Empty<string>());
        p.Manifest.Panel = new PanelDecl
        {
            Elements = new[] { new PanelElement { Type = "button", OnEvent = "onToggle" } },
        };
        Assert.Contains(CrossConsistencyChecker.Check(p), i => i.IsError && i.Field == "panel.elements[0].onEvent");
    }

    [Fact]
    public void PanelOnEvent_Defined_Ok()
    {
        var p = Proj(1, "function onToggle() end", Array.Empty<string>());
        p.Manifest.Panel = new PanelDecl
        {
            Elements = new[] { new PanelElement { Type = "button", OnEvent = "onToggle" } },
        };
        Assert.DoesNotContain(CrossConsistencyChecker.Check(p), i => i.IsError);
    }
}