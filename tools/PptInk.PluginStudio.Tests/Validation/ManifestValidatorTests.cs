using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

namespace PptInk.PluginStudio.Tests.Validation;

public class ManifestValidatorTests
{
    private static readonly Func<string, bool> EntryYes = _ => true;

    private static PluginManifest Base() => new() { Magic = 1, Id = "com.x.timer", Name = "计时", Entry = "main.lua" };

    [Theory]
    [InlineData("com.x.timer", false)]
    [InlineData("a", false)]
    [InlineData("1bad", false)]
    [InlineData("", true)]
    [InlineData("bad id", true)]
    [InlineData(".hidden", true)]
    public void Id_Rule(string id, bool hasError)
    {
        var m = Base();
        m.Id = id;
        var issues = ManifestValidator.Validate(m, EntryYes);
        Assert.Equal(hasError, issues.Any(i => i.IsError && i.Field == "id"));
    }

    [Fact]
    public void Magic_MustBe1to3()
    {
        var m = Base();
        m.Magic = 9;
        Assert.Contains(ManifestValidator.Validate(m, EntryYes), i => i.IsError && i.Field == "magic");
    }

    [Fact]
    public void Entry_MustExist()
    {
        var m = Base();
        Assert.Contains(ManifestValidator.Validate(m, _ => false), i => i.IsError && i.Field == "entry");
    }

    [Fact]
    public void Name_Required()
    {
        var m = Base();
        m.Name = "   ";
        Assert.Contains(ManifestValidator.Validate(m, EntryYes), i => i.IsError && i.Field == "name");
    }

    [Fact]
    public void Panel_ElementRules()
    {
        var m = Base();
        m.Panel = new PanelDecl
        {
            Elements = new[]
            {
                new PanelElement { Type = "slider", Min = 100, Max = 10, OnEvent = "onX" },
                new PanelElement { Type = "button", OnEvent = "1bad" },
                new PanelElement { Type = "widget", OnEvent = "onY" },
            },
        };
        var issues = ManifestValidator.Validate(m, EntryYes);
        Assert.Contains(issues, i => i.IsError && i.Field == "panel.elements[0].max");
        Assert.Contains(issues, i => i.IsError && i.Field == "panel.elements[1].onEvent");
        Assert.Contains(issues, i => i.IsError && i.Field == "panel.elements[2].type");
    }

    [Fact]
    public void Panel_OutOfRange_IsWarning_AndNormalizeClamps()
    {
        var m = Base();
        m.Panel = new PanelDecl { Width = 9999, Height = 5, Alpha = 2 };
        var issues = ManifestValidator.Validate(m, EntryYes);
        Assert.All(
            issues.Where(i => i.Field is "panel.width" or "panel.height" or "panel.alpha"),
            i => Assert.Equal(IssueLevel.Warning, i.Level));

        ManifestValidator.Normalize(m);
        Assert.InRange(m.Panel!.Width, 160, 600);
        Assert.InRange(m.Panel!.Height, 100, 500);
        Assert.InRange(m.Panel!.Alpha, 0.3, 1.0);
    }

    [Fact]
    public void UnknownPermission_IsError()
    {
        var m = Base();
        m.Permissions = new[] { "root", "file" };
        Assert.Contains(ManifestValidator.Validate(m, EntryYes), i => i.IsError && i.Field == "permissions");
    }

    [Fact]
    public void ValidManifest_NoErrors()
    {
        var m = Base();
        m.Panel = new PanelDecl
        {
            Elements = new[] { new PanelElement { Type = "button", Text = "开始", OnEvent = "onToggle" } },
        };
        Assert.Empty(ManifestValidator.Validate(m, EntryYes).Where(i => i.IsError));
    }
}