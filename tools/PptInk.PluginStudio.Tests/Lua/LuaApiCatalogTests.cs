using PptInk.PluginStudio.Core.Lua;
using PptInk.PluginStudio.Core.Models;
using Xunit;

namespace PptInk.PluginStudio.Tests.Lua;

public class LuaApiCatalogTests
{
    [Fact]
    public void ClipboardApi_OnlyWithPermission()
    {
        Assert.DoesNotContain(LuaApiCatalog.ForPermissions(Array.Empty<string>()), a => a.Name == "readClipboard");
        Assert.Contains(LuaApiCatalog.ForPermissions(new[] { "clipboard" }), a => a.Name == "readClipboard");
    }

    [Fact]
    public void FileApi_OnlyWithPermission()
    {
        Assert.DoesNotContain(LuaApiCatalog.ForPermissions(Array.Empty<string>()), a => a.Name == "captureScreen");
        Assert.Contains(LuaApiCatalog.ForPermissions(new[] { "file" }), a => a.Name == "captureScreen");
    }

    [Fact]
    public void CommonApi_AlwaysAvailable()
    {
        var a = LuaApiCatalog.ForPermissions(Array.Empty<string>());
        Assert.Contains(a, x => x.Name == "updatePanelElement");
        Assert.Contains(a, x => x.Name == "showToast");
        Assert.Contains(a, x => x.Name == "startTimer");
    }

    [Fact]
    public void EntryCallback_PerKind()
    {
        Assert.Contains("onActivate", LuaApiCatalog.EntryCallbacks(PluginKind.Panel));
        Assert.Contains("onStart", LuaApiCatalog.EntryCallbacks(PluginKind.Draw));
        Assert.Contains("onStart", LuaApiCatalog.EntryCallbacks(PluginKind.Free));
    }
}