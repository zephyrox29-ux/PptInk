using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PptInk.Bridge;

/// <summary>单个自检环节的结果。</summary>
internal sealed record ProbeStage(string Name, bool Ok, string Detail);

/// <summary>端到端探测结果（含逐环节明细，供 UI 展示"到底卡在哪一环"）。</summary>
internal sealed record ProbeResult(bool Ok, string Message, IReadOnlyList<ProbeStage> Stages);

/// <summary>
/// 桥进程端到端探测：启动短命桥进程 → 结构化握手 → 合成圆形识别。
/// 与运行时 <c>PythonBridge.RestartProcess</c> 使用同一套启动参数/环境（UTF-8、无缓冲、工作目录），
/// 保证"自检能过 = 真正运行时也能过"。
/// </summary>
internal static class BridgeProbe
{
    private const string BridgeId = "pptink-bridge-v1";

    public static async Task<ProbeResult> RunAsync(string dir, string script, string pythonExe, CancellationToken ct)
    {
        var stages = new List<ProbeStage>();
        var stderr = new StringBuilder();
        using var process = new Process
        {
            StartInfo = MakeStartInfo(pythonExe, script, dir),
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lock (stderr) stderr.AppendLine(e.Data);
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                stages.Add(new("启动 Python 进程", false, "进程无法启动"));
                return Fail(stages, "无法启动 Python 进程", stderr);
            }
            process.BeginErrorReadLine();
            process.StandardInput.AutoFlush = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { cmd = "hello", client = BridgeId }));
            var helloLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(helloLine))
            {
                stages.Add(new("桥握手", false, "无响应"));
                return Fail(stages, "桥接进程没有返回握手响应", stderr);
            }

            bool cv2Available;
            using (var helloDoc = JsonDocument.Parse(helloLine))
            {
                var hello = helloDoc.RootElement;
                if (!hello.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                    !hello.TryGetProperty("server", out var server) || server.GetString() != "pptink-bridge")
                {
                    stages.Add(new("桥握手", false, "协议不匹配：" + helloLine));
                    return Fail(stages, "AI 桥握手协议不匹配", stderr);
                }
                cv2Available = hello.TryGetProperty("cv2", out var cv2) && cv2.ValueKind == JsonValueKind.True;
            }
            stages.Add(new("桥握手", true, "正常"));

            var points = Enumerable.Range(0, 48)
                .Select(i => new object[]
                {
                    Math.Round(100 + 80 * Math.Cos(2 * Math.PI * i / 48), 1),
                    Math.Round(100 + 80 * Math.Sin(2 * Math.PI * i / 48), 1),
                })
                .ToArray();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { cmd = "recognize", points }));
            var recLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(recLine))
            {
                stages.Add(new("合成圆形识别", false, "无响应"));
                return Fail(stages, "桥接进程没有返回识别结果", stderr);
            }

            string shape;
            double confidence;
            using (var recDoc = JsonDocument.Parse(recLine))
            {
                var rec = recDoc.RootElement;
                if (!rec.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                    !rec.TryGetProperty("shape", out var sh) || sh.GetString() != "circle")
                {
                    stages.Add(new("合成圆形识别", false, "未通过：" + recLine));
                    return Fail(stages, "合成圆形识别未通过", stderr);
                }
                shape = sh.GetString() ?? "circle";
                confidence = rec.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var n) ? n : 0;
            }
            stages.Add(new("合成圆形识别", true, $"{shape}，置信度 {confidence:P1}"));

            var message = string.Join(Environment.NewLine, new[]
            {
                "AI 功能检测通过。",
                "Python 桥：正常握手",
                "NumPy 与识别算法：正常",
                $"合成圆形识别：{shape}，置信度 {confidence:P1}",
                $"运行时：{pythonExe}",
                $"可选 OpenCV：{(cv2Available ? "可用" : "未启用（基础识别仍可运行）")}",
            });
            return new ProbeResult(true, message, stages);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stages.Add(new("端到端探测", false, "超时（35 秒）"));
            return Fail(stages, "AI 检测超时：Python/NumPy 启动或识别超过 35 秒（可把 bridge 目录加入杀毒白名单）", stderr);
        }
        catch (Exception ex)
        {
            stages.Add(new("端到端探测", false, ex.Message));
            return Fail(stages, "AI 检测失败：" + ex.Message, stderr);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>桥进程启动参数（探测与常驻共用口径）。统一 UTF-8 + 无缓冲，规避 cp936 把协议行弄乱。</summary>
    public static ProcessStartInfo MakeStartInfo(string pythonExe, string script, string dir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"-u \"{script}\"",
            WorkingDirectory = dir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ApplyPythonEnv(psi);
        return psi;
    }

    /// <summary>统一的 Python 环境变量：强制 UTF-8 输出、无缓冲、错误不被本地编码吞掉。</summary>
    public static void ApplyPythonEnv(ProcessStartInfo psi)
    {
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
    }

    private static ProbeResult Fail(List<ProbeStage> stages, string message, StringBuilder stderr)
    {
        string diagnostic;
        lock (stderr) diagnostic = stderr.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(diagnostic))
            message += Environment.NewLine + "Python 输出：" + Environment.NewLine + diagnostic;
        return new ProbeResult(false, message, stages.ToArray());
    }
}