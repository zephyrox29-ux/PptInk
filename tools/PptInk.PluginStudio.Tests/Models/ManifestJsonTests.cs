using PptInk.PluginStudio.Core.Models;
using Xunit;

namespace PptInk.PluginStudio.Tests.Models;

public class ManifestJsonTests
{
    [Fact]
    public void RoundTrip_PreservesCoreFields()
    {
        var m = new PluginManifest
        {
            Magic = 1,
            Id = "com.x.timer",
            Name = "计时",
            Entry = "main.lua",
            Panel = new PanelDecl
            {
                Width = 240,
                Elements = new[] { new PanelElement { Type = "button", Text = "开始", OnEvent = "onToggle" } },
            },
        };

        var json = PluginManifestJson.Serialize(m);
        var back = PluginManifestJson.Deserialize(json)!;

        Assert.Equal(m.Id, back.Id);
        Assert.Equal(m.Name, back.Name);
        Assert.Equal("onToggle", back.Panel!.Elements[0].OnEvent);
        Assert.Equal(PluginKind.Panel, back.Kind);
    }

    [Fact]
    public void Deserialize_IsCaseInsensitive()
    {
        var back = PluginManifestJson.Deserialize("{\"MAGIC\":1,\"ID\":\"a.b\",\"NAME\":\"n\",\"ENTRY\":\"main.lua\"}")!;
        Assert.Equal("a.b", back.Id);
        Assert.Equal("n", back.Name);
        Assert.Equal(PluginKind.Panel, back.Kind);
    }
}