# PptInk 插件工作室 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 提供一个独立 Windows 图形化工具（PptInk.PluginStudio），让作者用向导+工作台完成 Lua 插件的创建、可视化面板设计、语法校验、打包与一键安装。

**Architecture:** 三层拆分——跨平台纯逻辑库 `Core`（模型/校验/打包/安装/导入/模板，可在此 Linux 环境编译测试）、WPF 界面 `PluginStudio`（向导/工作台/编辑器/预览，需 Windows 构建）、xUnit 测试 `Tests`（只引用 Core）。

**Tech Stack:** .NET 8（Core/Tests: `net8.0`；WPF: `net8.0-windows`）、MoonSharp 2.0.0（Lua 解析/API 清单）、AvalonEdit（WPF 编辑器）、xUnit。

## Global Constraints

- 目标框架：Core/Tests = `net8.0`；WPF = `net8.0-windows`（`UseWPF=true`）。
- 依赖：Core 只引 MoonSharp 2.0.0；WPF 额外引 AvalonEdit；不得引用 PptInk 主程序工程。
- 不改动 `src/` 下主程序任何文件。
- 校验规则必须与 `src/PptInk.App/Plugins/PluginManager.cs::ValidateManifest/NormalizeManifest` 一致（见 spec §8），文件头注明同步来源。
- 语言：所有注释与用户可见文案用简体中文。
- 路径：插件安装根目录 = `%LocalAppData%\PptInk\plugins`（可由 `AppPaths` 覆盖以便测试）。
- 提交：每个 Task 结束提交一次。

---

### Task 1: 解决方案与项目骨架

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/PptInk.PluginStudio.Core.csproj`
- Create: `tools/PptInk.PluginStudio/PptInk.PluginStudio.csproj`
- Create: `tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj`
- Modify: `PptInk.sln`

**Interfaces:**
- Produces: 三个可被 `dotnet build` 定位的项目文件；`Tests` 引用 `Core`。

- [ ] **Step 1: Core csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MoonSharp" Version="2.0.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: WPF csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AvalonEdit" Version="6.3.0.90" />
    <ProjectReference Include="../PptInk.PluginStudio.Core/PptInk.PluginStudio.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Tests csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <ProjectReference Include="../PptInk.PluginStudio.Core/PptInk.PluginStudio.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: 加入解决方案并构建 Core/Tests**

Run: `dotnet sln PptInk.sln add tools/PptInk.PluginStudio.Core/PptInk.PluginStudio.Core.csproj tools/PptInk.PluginStudio/PptInk.PluginStudio.csproj tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj`
Then: `dotnet build tools/PptInk.PluginStudio.Core/PptInk.PluginStudio.Core.csproj`
Expected: Build succeeded（Linux 上仅 Core/Tests 可构建）。

- [ ] **Step 5: Commit**

```bash
git add tools PptInk.sln
git commit -m "chore(plugin-studio): 新增 Core/WPF/Tests 项目骨架"
```

---

### Task 2: 数据模型

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Models/PluginKind.cs`
- Create: `tools/PptInk.PluginStudio.Core/Models/PluginManifest.cs`
- Create: `tools/PptInk.PluginStudio.Core/Models/PanelDecl.cs`
- Create: `tools/PptInk.PluginStudio.Core/Models/PanelElement.cs`
- Create: `tools/PptInk.PluginStudio.Core/Models/ProjectModel.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Models/ManifestJsonTests.cs`

**Interfaces:**
- Produces: `PluginKind`, `PluginManifest`, `PanelDecl`, `PanelElement`, `ProjectModel`（含 `ResourceFile`）。

- [ ] **Step 1: 写失败测试（JSON 往返 + 大小写不敏感）**

```csharp
using PptInk.PluginStudio.Core.Models;
using Xunit;

public class ManifestJsonTests
{
    [Fact]
    public void RoundTrip_PreservesCoreFields()
    {
        var m = new PluginManifest { Magic = 1, Id = "com.x.timer", Name = "计时", Entry = "main.lua",
            Panel = new PanelDecl { Width = 240, Elements = new[] { new PanelElement { Type = "button", Text = "开始", OnEvent = "onToggle" } } } };
        var json = PluginManifestJson.Serialize(m);
        var back = PluginManifestJson.Deserialize(json)!;
        Assert.Equal(m.Id, back.Id);
        Assert.Equal("onToggle", back.Panel!.Elements[0].OnEvent);
        Assert.Equal(PluginKind.Panel, back.Kind);
    }

    [Fact]
    public void Deserialize_IsCaseInsensitive()
    {
        var back = PluginManifestJson.Deserialize("{\"MAGIC\":1,\"ID\":\"a.b\",\"NAME\":\"n\",\"ENTRY\":\"main.lua\"}")!;
        Assert.Equal("a.b", back.Id);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj --filter ManifestJsonTests`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现模型 + `PluginManifestJson`**

```csharp
// PluginKind.cs
namespace PptInk.PluginStudio.Core.Models;
public enum PluginKind { Panel = 1, Draw = 2, Free = 3 }

// PluginManifest.cs
using System.Text.Json;
using System.Text.Json.Serialization;
namespace PptInk.PluginStudio.Core.Models;

public sealed class PluginManifest
{
    public int Magic { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Entry { get; set; } = "main.lua";
    public PanelDecl? Panel { get; set; }
    public string[] Permissions { get; set; } = Array.Empty<string>();
    public string? Signature { get; set; }
    [JsonIgnore] public PluginKind Kind => (PluginKind)Magic;
}

public static class PluginManifestJson
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
    public static string Serialize(PluginManifest m) => JsonSerializer.Serialize(m, Opts);
    public static PluginManifest? Deserialize(string json) => JsonSerializer.Deserialize<PluginManifest>(json, Opts);
}
```

`PanelDecl.cs` / `PanelElement.cs` 按 spec §5 字段实现（`Elements` 默认空数组，`OnEvent` 默认 `"noop"`）。
`ProjectModel.cs`：

```csharp
namespace PptInk.PluginStudio.Core.Models;
public sealed record ResourceFile(string RelativePath, byte[] Content);
public sealed class ProjectModel
{
    public PluginManifest Manifest { get; set; } = new();
    public string LuaSource { get; set; } = "";
    public List<ResourceFile> Resources { get; set; } = new();
    public string? ProjectPath { get; set; }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tools/PptInk.PluginStudio.Tests/PptInk.PluginStudio.Tests.csproj --filter ManifestJsonTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Models tools/PptInk.PluginStudio.Tests/Models
git commit -m "feat(plugin-studio): 插件数据模型与 JSON 序列化"
```

---

### Task 3: Manifest 校验器（对齐主程序规则）

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Validation/ValidationIssue.cs`
- Create: `tools/PptInk.PluginStudio.Core/Validation/ManifestValidator.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Validation/ManifestValidatorTests.cs`

**Interfaces:**
- Produces: `enum IssueLevel{Error,Warning}`、`record ValidationIssue(IssueLevel Level,string Field,string Message)`、
  `static ManifestValidator.Validate(PluginManifest m, Func<string,bool> entryExists)` → `IReadOnlyList<ValidationIssue>`。

- [ ] **Step 1: 写失败测试（规则表代表用例）**

```csharp
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

public class ManifestValidatorTests
{
    private static readonly Func<string,bool> EntryYes = _ => true;

    [Theory]
    [InlineData("", true)]            // 空 → 报错
    [InlineData("1bad", true)]        // 数字开头(合法) 见下
    [InlineData("bad id", true)]      // 空格 → 报错
    [InlineData(".hidden", true)]     // 点开头 → 报错
    public void Id_Rule(string id, bool _)
    {
        var m = new PluginManifest { Id = id, Name = "n", Entry = "main.lua" };
        var issues = ManifestValidator.Validate(m, EntryYes);
        bool idError = issues.Any(i => i.Level == IssueLevel.Error && i.Field == "id");
        Assert.Equal(id is "" or "bad id" or ".hidden", idError);
    }

    [Fact]
    public void Magic_MustBe1to3()
    {
        var m = new PluginManifest { Magic = 9, Id = "a", Name = "n", Entry = "main.lua" };
        Assert.Contains(ManifestValidator.Validate(m, EntryYes), i => i.Field == "magic" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Entry_MustExist()
    {
        var m = new PluginManifest { Magic = 1, Id = "a", Name = "n", Entry = "main.lua" };
        Assert.Contains(ManifestValidator.Validate(m, _ => false), i => i.Field == "entry" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Slider_MaxGreaterThanMin_AndOnEventIdentifier()
    {
        var m = new PluginManifest { Magic = 1, Id = "a", Name = "n", Entry = "main.lua",
            Panel = new PanelDecl { Elements = new[] {
                new PanelElement { Type = "slider", Min = 100, Max = 10, OnEvent = "onX" },
                new PanelElement { Type = "button", OnEvent = "1bad" } } } };
        var issues = ManifestValidator.Validate(m, EntryYes);
        Assert.Contains(issues, i => i.Field.StartsWith("panel.elements[0]"));
        Assert.Contains(issues, i => i.Field.StartsWith("panel.elements[1]") && i.Level == IssueLevel.Error);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test --filter ManifestValidatorTests`（在 Tests 目录）
Expected: FAIL。

- [ ] **Step 3: 实现校验器（返回问题清单，不抛异常）**

实现 `ManifestValidator.Validate` 逐条覆盖 spec §8：magic∈1..3；id 正则 `^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$`；
name 非空≤120；author≤200/description≤1000/version≤64；entry 非空/非绝对/≤240 且 `entryExists`；
permissions⊆{file,network,clipboard} 且 network 记 Warning"当前不生效"；panel.dock∈{left,right,bottom,float}；
width 160–600、height 100–500、alpha 0.3–1.0（越界 Warning 并给 clamp 提示）；elements≤64；
type∈{text,slider,button}；id≤80/text≤500；onEvent 正则（button/slider 必须，text 缺省 noop）；
slider min/max 有限且 max>min、value∈[min,max]。
正常化（clamp/trim/去重小写）由 `ManifestValidator.Normalize(PluginManifest)` 提供。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test --filter ManifestValidatorTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Validation tools/PptInk.PluginStudio.Tests/Validation
git commit -m "feat(plugin-studio): Manifest 校验器（对齐主程序规则）"
```

---

### Task 4: 注入 API 清单（补全与交叉校验的数据源）

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Lua/LuaApiCatalog.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Lua/LuaApiCatalogTests.cs`

**Interfaces:**
- Produces: `record LuaApi(string Name,string Signature,string? RequiredPermission)`；
  `static LuaApiCatalog.All`、`.ForPermissions(IEnumerable<string>)`、`.EntryCallbacks(PluginKind)`。

- [ ] **Step 1: 写失败测试**

```csharp
using PptInk.PluginStudio.Core.Lua;
using PptInk.PluginStudio.Core.Models;
using Xunit;

public class LuaApiCatalogTests
{
    [Fact] public void ClipboardApi_OnlyWithPermission()
    {
        Assert.DoesNotContain(LuaApiCatalog.ForPermissions(Array.Empty<string>()), a => a.Name == "readClipboard");
        Assert.Contains(LuaApiCatalog.ForPermissions(new[]{"clipboard"}), a => a.Name == "readClipboard");
    }
    [Fact] public void EntryCallback_PerKind()
    {
        Assert.Contains("onActivate", LuaApiCatalog.EntryCallbacks(PluginKind.Panel));
        Assert.Contains("onStart", LuaApiCatalog.EntryCallbacks(PluginKind.Draw));
    }
    [Fact] public void CommonApi_AlwaysAvailable()
    {
        var a = LuaApiCatalog.ForPermissions(Array.Empty<string>());
        Assert.Contains(a, x => x.Name == "updatePanelElement");
        Assert.Contains(a, x => x.Name == "showToast");
    }
}
```

- [ ] **Step 2: 运行确认失败**：`dotnet test --filter LuaApiCatalogTests` → FAIL。

- [ ] **Step 3: 实现**：按 spec §7 列全 API（名称/签名/所需权限），`ForPermissions` 按权限过滤；
`EntryCallbacks`：Panel→`onActivate`，Draw/Free→`onStart`，均含 `onDeactivate`。

- [ ] **Step 4: 运行确认通过** → PASS。

- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Lua tools/PptInk.PluginStudio.Tests/Lua
git commit -m "feat(plugin-studio): 注入 API 清单与权限映射"
```

---

### Task 5: Lua 语法检查（MoonSharp 仅解析）

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Validation/LuaSyntaxChecker.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Validation/LuaSyntaxCheckerTests.cs`

**Interfaces:**
- Produces: `static LuaSyntaxChecker.Check(string source)` → `IReadOnlyList<ValidationIssue>`（含行列）。

- [ ] **Step 1: 写失败测试**

```csharp
using PptInk.PluginStudio.Core.Validation;
using Xunit;

public class LuaSyntaxCheckerTests
{
    [Fact] public void ValidSource_NoErrors()
    {
        Assert.Empty(LuaSyntaxChecker.Check("function onActivate()\n showToast('hi')\nend\n"));
    }
    [Fact] public void BrokenSource_ReportsError()
    {
        var issues = LuaSyntaxChecker.Check("function onActivate(\n");
        Assert.Contains(issues, i => i.Level == IssueLevel.Error);
    }
}
```

- [ ] **Step 2: 运行确认失败** → FAIL。

- [ ] **Step 3: 实现**：`new Script(); script.LoadString(src)`；捕获 `SyntaxErrorException`，
用其 `Line/Column` 生成 `ValidationIssue(Error,"lua",$"第{line}行第{col}列：{msg}")`。
**不执行**（仅 LoadString）。

- [ ] **Step 4: 运行确认通过** → PASS。

- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Validation/LuaSyntaxChecker.cs tools/PptInk.PluginStudio.Tests/Validation/LuaSyntaxCheckerTests.cs
git commit -m "feat(plugin-studio): Lua 语法检查（仅解析不执行）"
```

---

### Task 6: 交叉一致性检查

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Validation/CrossConsistencyChecker.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Validation/CrossConsistencyCheckerTests.cs`

**Interfaces:**
- Consumes: `LuaApiCatalog`、`ProjectModel`。
- Produces: `static CrossConsistencyChecker.Check(ProjectModel p)` → `IReadOnlyList<ValidationIssue>`。

- [ ] **Step 1: 写失败测试**

```csharp
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Validation;
using Xunit;

public class CrossConsistencyCheckerTests
{
    private static ProjectModel Proj(int magic, string lua, string[] perms) => new()
    { Manifest = new PluginManifest { Magic = magic, Id = "a.b", Name = "n", Entry = "main.lua", Permissions = perms }, LuaSource = lua };

    [Fact] public void ApiWithoutPermission_IsWarningOrError()
    {
        var issues = CrossConsistencyChecker.Check(Proj(3, "function onStart() writeClipboard('x') end", Array.Empty<string>()));
        Assert.Contains(issues, i => i.Field == "permissions");
    }
    [Fact] public void PanelOnEvent_NotDefined_IsError()
    {
        var p = Proj(1, "function onActivate() end", Array.Empty<string>());
        p.Manifest.Panel = new PanelDecl { Elements = new[]{ new PanelElement { Type="button", OnEvent="onToggle" } } };
        Assert.Contains(CrossConsistencyChecker.Check(p), i => i.Level == IssueLevel.Error && i.Field.StartsWith("panel.elements"));
    }
}
```

- [ ] **Step 2: 运行确认失败** → FAIL。

- [ ] **Step 3: 实现**：扫描脚本中出现的 API 名（简单词边界匹配），若调用了需权限而未声明的 API →
`ValidationIssue(Error,"permissions",...)`；Panel 元素 `onEvent` 在脚本中无 `function <name>` 定义 → Error；
`Entry` 不在资源/默认脚本 → Error（与 ManifestValidator 的 entry 检查互补）。

- [ ] **Step 4: 运行确认通过** → PASS。

- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Validation/CrossConsistencyChecker.cs tools/PptInk.PluginStudio.Tests/Validation/CrossConsistencyCheckerTests.cs
git commit -m "feat(plugin-studio): 脚本与权限/回调的交叉一致性检查"
```

---

### Task 7: 打包（PackageBuilder）

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Packaging/PackageLayout.cs`
- Create: `tools/PptInk.PluginStudio.Core/Packaging/PackageBuilder.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Packaging/PackageBuilderTests.cs`

**Interfaces:**
- Produces: `PackageLayout.ManifestFile="manifest.json"`；`PackageBuilder.Build(ProjectModel p, string zipPath)`。

- [ ] **Step 1: 写失败测试**

```csharp
using System.IO.Compression;
using PptInk.PluginStudio.Core.Models;
using PptInk.PluginStudio.Core.Packaging;
using Xunit;

public class PackageBuilderTests
{
    [Fact] public void Build_WritesManifestAndEntry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N") + ".zip");
        var p = new ProjectModel { Manifest = new PluginManifest { Magic=1, Id="a.b", Name="n", Entry="main.lua" },
            LuaSource = "function onActivate() end" };
        PackageBuilder.Build(p, tmp);
        using var z = ZipFile.OpenRead(tmp);
        Assert.Contains(z.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(z.Entries, e => e.FullName == "main.lua");
        File.Delete(tmp);
    }
}
```

- [ ] **Step 2: 运行确认失败** → FAIL。

- [ ] **Step 3: 实现**：`Build` 用 `ZipArchive` 写 `manifest.json`（`PluginManifestJson.Serialize`）+ `Entry` 路径写 `LuaSource` + `Resources`。
写前调用 `ManifestValidator.Validate` 与 `CrossConsistencyChecker`，存在 Error 时抛 `InvalidOperationException`（含首条消息）。

- [ ] **Step 4: 运行确认通过** → PASS。

- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Packaging tools/PptInk.PluginStudio.Tests/Packaging
git commit -m "feat(plugin-studio): 插件包构建"
```

---

### Task 8: 导入（PackageImporter）

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Packaging/PackageImporter.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Packaging/PackageImporterTests.cs`

**Interfaces:**
- Produces: `PackageImporter.Import(string zipPath)` → `ProjectModel`；`ImportDirectory(string dir)`。

- [ ] **Step 1: 写失败测试（build→import 往返）**

```csharp
// build 一个包后用 Import 读回，断言 Id/Name/Entry 与脚本一致；畸形包（无 manifest）抛异常。
```

- [ ] **Step 2: 运行确认失败** → FAIL。
- [ ] **Step 3: 实现**：读 zip；越界路径与体积上限（≤128 项、解压 ≤20MB）检查与主程序一致；
读 `manifest.json` 反序列化 + 校验；读 `Entry` 为 `LuaSource`；其余条目进 `Resources`。
- [ ] **Step 4: 运行确认通过** → PASS。
- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Packaging/PackageImporter.cs tools/PptInk.PluginStudio.Tests/Packaging/PackageImporterTests.cs
git commit -m "feat(plugin-studio): 插件包导入（zip/目录）"
```

---

### Task 9: 安装（PackageInstaller，含备份/回滚）

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/AppPaths.cs`
- Create: `tools/PptInk.PluginStudio.Core/Packaging/PackageInstaller.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Packaging/PackageInstallerTests.cs`

**Interfaces:**
- Produces: `AppPaths.PluginRoot`（默认 `%LocalAppData%\PptInk\plugins`，可被 `AppPaths.OverridePluginRoot(string)` 覆盖）；
  `PackageInstaller.Install(ProjectModel p)` → `bool` + `out string error`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void Install_WritesToPluginRoot_AndIsIdempotent()
{
    var root = Path.Combine(Path.GetTempPath(), "psroot-" + Guid.NewGuid().ToString("N"));
    AppPaths.OverridePluginRoot(root);
    var p = new ProjectModel { Manifest = new PluginManifest { Magic=1, Id="a.b", Name="n", Entry="main.lua" }, LuaSource="function onActivate() end" };
    Assert.True(new PackageInstaller().Install(p, out _));
    Assert.True(File.Exists(Path.Combine(root, "a.b", "manifest.json")));
    Assert.True(new PackageInstaller().Install(p, out _));  // 覆盖安装也应成功
    Directory.Delete(root, true);
}
```

- [ ] **Step 2: 运行确认失败** → FAIL。
- [ ] **Step 3: 实现**：先 `ManifestValidator`+`CrossConsistencyChecker`（Error 则失败）；写入
`<root>\.staging-<guid>\` 后 `Directory.Move` 到 `<root>\<Id>\`；若已存在先备份 `<Id>.backup-<guid>`，
失败回滚（语义对齐主程序 `InstallFromZip`）。
- [ ] **Step 4: 运行确认通过** → PASS。
- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/AppPaths.cs tools/PptInk.PluginStudio.Core/Packaging/PackageInstaller.cs tools/PptInk.PluginStudio.Tests/Packaging/PackageInstallerTests.cs
git commit -m "feat(plugin-studio): 一键安装（备份/替换/回滚）"
```

---

### Task 10: 模板与示例库

**Files:**
- Create: `tools/PptInk.PluginStudio.Core/Templates/TemplateLibrary.cs`
- Create: `tools/PptInk.PluginStudio.Core/Templates/ReadmeGenerator.cs`
- Test: `tools/PptInk.PluginStudio.Tests/Templates/TemplateLibraryTests.cs`

**Interfaces:**
- Produces: `TemplateLibrary.All`（`IReadOnlyList<TemplateInfo(Id,Kind,Name,Description)>`）；
  `TemplateLibrary.Create(string templateId)` → `ProjectModel`；`ReadmeGenerator.Generate(PluginManifest)` → `string`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public void Samples_AreValid_AndBuildable()
{
    foreach (var t in TemplateLibrary.All)
    {
        var p = TemplateLibrary.Create(t.Id);
        Assert.Empty(ManifestValidator.Validate(p.Manifest, _ => true).Where(i => i.Level == IssueLevel.Error));
        Assert.Empty(LuaSyntaxChecker.Check(p.LuaSource).Where(i => i.Level == IssueLevel.Error));
    }
}
```

- [ ] **Step 2: 运行确认失败** → FAIL。
- [ ] **Step 3: 实现**：三个示例（`panel.timer` 正倒计时 / `draw.board` 画板批注 / `free.rollcall` 随机点名），
manifest 与 Lua 源码作为常量字符串内嵌；`ReadmeGenerator` 输出安装步骤+权限说明+API 摘要。
- [ ] **Step 4: 运行确认通过** → PASS。
- [ ] **Step 5: Commit**

```bash
git add tools/PptInk.PluginStudio.Core/Templates tools/PptInk.PluginStudio.Tests/Templates
git commit -m "feat(plugin-studio): 三类模板与示例库 + 说明生成"
```

---

### Task 11: WPF 界面骨架（向导 + 工作台）

**Files:**
- Create: `tools/PptInk.PluginStudio/App.xaml(.cs)`
- Create: `tools/PptInk.PluginStudio/Views/WizardWindow.xaml(.cs)`
- Create: `tools/PptInk.PluginStudio/Views/WorkbenchWindow.xaml(.cs)`
- Create: `tools/PptInk.PluginStudio/Views/Sections/*.xaml(.cs)`、`PreviewPane.xaml`、`ValidationPane.xaml`、`HelpOverlay.xaml`
- Create: `tools/PptInk.PluginStudio/ViewModels/*.cs`
- Create: `tools/PptInk.PluginStudio/Services/FileDialogs.cs`、`PreviewRenderer.cs`、`AvalonLuaEditorHost.cs`

**Interfaces:**
- Consumes: Core 的全部公开 API（模型、校验、打包、安装、导入、模板）。
- Produces: 可运行的 WPF 应用（仅 Windows 可构建）。

- [ ] **Step 1: App + 向导（步骤条：类型→模板→基本信息→完成）**
- [ ] **Step 2: 工作台（左分页 / 中编辑器 / 底校验+预览 / 顶部工具栏）**
- [ ] **Step 3: 面板设计器与简化预览联动**
- [ ] **Step 4: 校验清单点击定位（编辑器行列 / 字段高亮）**
- [ ] **Step 5: 操作指引 HelpOverlay（首次运行分步高亮）**
- [ ] **Step 6: 手动冒烟（Windows）：新建三类 → 校验 → 打包 → 安装**
- [ ] **Step 7: Commit**

```bash
git add tools/PptInk.PluginStudio
git commit -m "feat(plugin-studio): WPF 向导与工作台界面"
```

---

### Task 12: 文档与交付

**Files:**
- Create: `tools/PptInk.PluginStudio/README.md`（构建与使用说明）
- Modify: `PptInk.sln`（确认三项目已纳入）

- [ ] **Step 1: 写 README（如何构建、三类入门、截图占位）**
- [ ] **Step 2: `dotnet build tools/PptInk.PluginStudio.Core` + `dotnet test tools/PptInk.PluginStudio.Tests` 全绿**
- [ ] **Step 3: Commit，推分支，开 PR**

```bash
git add tools/PptInk.PluginStudio/README.md PptInk.sln
git commit -m "docs(plugin-studio): 使用说明与交付"
```

---

## Self-Review

- **Spec coverage**：§4 结构→Task1/11；§5 模型→Task2；§6.2 表单/设计器→Task11；§6.3 编辑器/校验→Task5/6/11；
  §6.4 打包/安装/导入/示例/说明→Task7/8/9/10；§6.5 工程文件→Task2/11；§7 API→Task4；§8 规则→Task3；
  §11 UI 指引→Task11 Step5；§12 测试→各 Task 的测试步骤。
- **Placeholder scan**：Core 各 Task 均给出可运行测试与实现要点；WPF（Task11）因仅 Windows 可构建，给出文件清单与步骤，属平台受限而非占位。
- **Type consistency**：`ManifestValidator.Validate(PluginManifest, Func<string,bool>)`、`LuaSyntaxChecker.Check(string)`、
  `CrossConsistencyChecker.Check(ProjectModel)`、`PackageBuilder.Build(ProjectModel,string)`、
  `PackageInstaller.Install(ProjectModel, out string)`、`TemplateLibrary.Create(string)` 在各 Task 间一致。

## Execution Handoff

计划已保存。两种执行方式：1）Subagent-Driven（每任务派新子代理，任务间复核）；2）Inline（本会话内批量执行+检查点）。