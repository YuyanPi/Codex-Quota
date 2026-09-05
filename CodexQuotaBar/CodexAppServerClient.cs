using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexQuotaBar;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly StringBuilder _errors = new();
    private int _nextId;

    private CodexAppServerClient(Process process)
    {
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _ = CaptureErrorsAsync(process.StandardError);
    }

    public static async Task<CodexAppServerClient> StartAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "codex.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("未找到 Codex CLI。请先安装并登录 Codex 桌面版或 CLI。", ex);
        }

        if (process is null)
        {
            throw new InvalidOperationException("无法启动 Codex App Server。");
        }

        var client = new CodexAppServerClient(process);
        await client.InitializeAsync(cancellationToken);
        return client;
    }

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken)
    {
        string? model = null;
        try
        {
            using var config = await RequestAsync("config/read", new { includeLayers = false }, cancellationToken);
            if (config.RootElement.TryGetProperty("config", out var configValue) &&
                configValue.TryGetProperty("model", out var modelValue) &&
                modelValue.ValueKind == JsonValueKind.String)
            {
                model = modelValue.GetString();
            }
        }
        catch
        {
            // Model display is optional; quota reading should still proceed.
        }

        using var limits = await RequestAsync("account/rateLimits/read", new { }, cancellationToken);
        return QuotaMapper.Map(limits.RootElement, model);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var _ = await RequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "codex_quota_bar", title = "Codex Quota Bar", version = "1.0.0" },
                capabilities = new { experimentalApi = true }
            },
            cancellationToken);

        await SendAsync(new { method = "initialized" }, cancellationToken);
    }

    private async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        await SendAsync(new { id, method, @params = parameters }, cancellationToken);

        while (true)
        {
            var line = await _output.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                var detail = _errors.Length == 0 ? "进程已退出。" : _errors.ToString().Trim();
                throw new InvalidOperationException($"Codex App Server 未返回数据：{detail}");
            }

            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id)
            {
                message.Dispose();
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : error.GetRawText();
                message.Dispose();
                throw new InvalidOperationException($"Codex App Server：{text}");
            }

            if (!root.TryGetProperty("result", out var result))
            {
                message.Dispose();
                throw new InvalidOperationException("Codex App Server 返回了无效响应。");
            }

            var copy = JsonDocument.Parse(result.GetRawText());
            message.Dispose();
            return copy;
        }
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await _input.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    private async Task CaptureErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (_errors.Length < 4_096)
            {
                _errors.AppendLine(line);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _input.Close();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // Best-effort cleanup on application shutdown.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
