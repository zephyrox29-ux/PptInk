using PptInk.PluginStudio.Core.Validation;
using Xunit;

namespace PptInk.PluginStudio.Tests.Validation;

public class LuaSyntaxCheckerTests
{
    [Fact]
    public void Valid_NoError()
    {
        Assert.Empty(LuaSyntaxChecker.Check("function onActivate()\n showToast('hi')\nend\n"));
    }

    [Fact]
    public void Broken_HasError()
    {
        Assert.Contains(LuaSyntaxChecker.Check("function onActivate(\n"), i => i.IsError);
    }

    [Fact]
    public void Empty_NoError()
    {
        Assert.Empty(LuaSyntaxChecker.Check(""));
    }
}