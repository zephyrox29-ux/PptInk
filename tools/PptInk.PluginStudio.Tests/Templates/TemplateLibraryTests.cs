using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Templates;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

namespace PptInk.PluginStudio.Tests.Templates;

public class TemplateLibraryTests
{
    [Fact]
    public void Samples_AreValid()
    {
        foreach (var t in TemplateLibrary.All)
        {
            var p = TemplateLibrary.Create(t.Id);
            Assert.DoesNotContain(ManifestValidator.Validate(p.Manifest, _ => true), i => i.IsError);
            Assert.DoesNotContain(LuaSyntaxChecker.Check(p.LuaSource), i => i.IsError);
            Assert.DoesNotContain(CrossConsistencyChecker.Check(p), i => i.IsError);
        }
    }

    [Fact]
    public void Blank_AreValid()
    {
        foreach (var kind in new[] { PluginKind.Panel, PluginKind.Draw, PluginKind.Free })
        {
            var p = TemplateLibrary.Blank(kind);
            Assert.DoesNotContain(ManifestValidator.Validate(p.Manifest, _ => true), i => i.IsError);
            Assert.DoesNotContain(LuaSyntaxChecker.Check(p.LuaSource), i => i.IsError);
            Assert.DoesNotContain(CrossConsistencyChecker.Check(p), i => i.IsError);
        }
    }
}