# 第三方组件声明（Third-Party Notices）

PptInk（融绘）分发包中包含以下第三方组件，在此依照各自许可证要求随附声明：

## MoonSharp

- 用途：Lua 插件脚本沙箱宿主（`PptInk.App` 唯一 NuGet 依赖）
- 版本：2.0.0.0
- 许可证：MIT License
- 版权声明：Copyright (c) 2014-2016 Marco Mastropaolo

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Python 与 NumPy（可选 AI 形状识别）

便携包默认不含 Python 解释器；仅当启用 AI 形状识别并按提示配置运行时后，
由 [Python Software Foundation](https://www.python.org/)（PSF License）
发布的嵌入式 Python 与由 NumPy 社区（BSD 3-Clause）发布的 NumPy 会被使用。
本仓库与分发包不修改上述组件的版权与许可声明。
