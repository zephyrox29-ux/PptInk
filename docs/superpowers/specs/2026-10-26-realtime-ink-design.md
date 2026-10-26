# 实时笔迹（Realtime Ink）设计文档

- 日期：2026-10-26
- 状态：已确认方案（用户选择「双模式：实时为默认，平滑可回退」）
- 关联：主程序 `src/PptInk.App/Ink/`（SmoothingFilter / InkEngine / InkModels）与渲染层 `UI/UnifiedStageWindow.InkRender.cs`

## 1. 问题（用户反馈属实）

当前笔迹 = 采样点 → **OneEuro 位置平滑滤波** → **中点法二次贝塞尔拟合**：

- `SmoothingFilter.Feed` 对 X/Y 坐标做 OneEuro 低通（低速强平滑），位置被滤波拖后于笔尖 → **跟手滞后**；
- `EmitSmoothed()` 的平滑点进入 `_live.Points`；渲染层（`DrawStroke`/`DrawBatch`）与最终几何（`StrokeData.BuildGeometry`，内部再建一次 `SmoothingFilter`）都用"相邻点中点为段端点"的贝塞尔 → 曲线内缩圆滑、转角磨圆、天然半点滞后。

观感：采样高、但很圆、不跟手，不是真实笔尖轨迹。

## 2. 目标

- **默认实时笔迹**：用笔尖原始报点即时描边，0 位置滞后、落笔即所见、紧贴笔尖；
- **平滑可回退**：设置开关"平滑笔迹"（默认关），开启后走 OneEuro 抗抖；
- 所见即所得：live 层与抬笔后最终几何（`BuildGeometry`）用同一位置口径，抬笔不突变；
- 保留：飞点剔除、断触插值补点、压力/速度笔锋（这些不引入位置滞后）。

## 3. 方案（最小改动、可回退）

核心：**位置平滑做成开关，贯通"live 层 + 最终几何"两处**，渲染路径不动。

1. `AppConfig` 新增 `SmoothInk`（bool，默认 `false` = 实时笔迹）。`Normalize` 无需处理（bool）。
2. `SmoothingFilter` 新增构造参数 `bool positionSmoothing = true`：
   - `true`（平滑）：现状 OneEuro；
   - `false`（实时）：X/Y 直通原始点，**仍保留**飞点剔除与断触插值、压力 EMA（0.35）。
   `Feed` 与 `PushSmoothed` 同步处理。
3. `StrokeData.BuildGeometry(...)` 末尾加 `bool positionSmoothing = true` 并传入内部 `SmoothingFilter`；
   缓存 key `_cachedGeoFilterKey` 由 3 元组扩为 4 元组（口径含平滑开关，切档不误用缓存）。
4. `InkEngine.OnStylusDown` 新建 `SmoothingFilter` 时传 `AppConfig.Current.SmoothInk`（每笔落笔读一次 → 即时生效）。
5. `UnifiedStageWindow.InkRender.DrawStroke` 非荧光笔分支调 `BuildGeometry` 时传 `Core.AppConfig.Current.SmoothInk`
   （live 与缓存/导出用同一开关 → 抬笔不跳变）。
6. 设置中心「书写与识别」卡新增开关行 `SwitchRow("平滑笔迹（抗抖）", ...)`，绑定 `cfg.SmoothInk`，
   文案说明默认关=实时、开=抗抖但慢写略滞后，切换即时生效。

## 4. 不改动的部分

- 渲染路径（合批/圆头/点状/荧光笔 ribbon/收锋/渐入）完全不动；只换"点源"。
- 擦除、手势、历史树、AI、穿透等链路不动。
- `PenSmoothing`（平滑强度）等高级参数仅对平滑模式生效。

## 5. 风险与验证

- 改动集中在 `PptInk.App`（`net8.0-windows`，WPF）——**本环境（Linux）无法编译**，需在你的 Windows 上 `dotnet build`。
- 红外框在实时模式下会如实呈现笔尖轻微抖动（这是"真实感"的代价）；抖得严重的机型可在设置里开"平滑笔迹"回退。
- 兼容：配置文件无 `SmoothInk` 字段的旧版按默认 false（实时）处理，向前兼容。

## 6. 验收

1. 默认下笔：笔迹紧贴笔尖、无明显滞后；抬笔后形态与书写过程一致（不突变）。
2. 开启"平滑笔迹"后：抖动减小、但慢写有滞后（与改动前一致）。
3. 压感/速度笔锋、飞点剔除、断触补点仍正常。
4. `dotnet build PptInk.sln -c Release` 0 错误（Windows）。