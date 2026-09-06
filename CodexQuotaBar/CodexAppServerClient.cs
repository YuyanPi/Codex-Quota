using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexQuotaBar;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    public static string DiagnosticLogPath { get; } = Path.Combine(Path.GetTempPath(), "CodexQuotaBar.log");

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
        TryResetLog();
        var executable = FindCodexExecutable();
        Log($"Starting: {executable}");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
            throw new InvalidOperationException(
                "未找到本机 Codex 组件。请安装 ChatGPT 桌面版、VS Code Codex 扩展或 Codex CLI，并使用 ChatGPT 账户登录。",
                ex);
        }

        if (process is null)
        {
            throw new InvalidOperationException("无法启动 Codex App Server。");
        }

        var client = new CodexAppServerClient(process);
        Log($"Started PID {process.Id}");
        await client.InitializeAsync(cancellationToken);
        return client;
    }

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken)
    {
        // Read the small, essential payload first. config/read can be large on a
        // fully configured desktop install and must never block quota display.
        using var limits = await RequestAsync("account/rateLimits/read", new { }, cancellationToken);
        return QuotaMapper.Map(limits.RootElement, TryReadConfiguredModel());
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Log("Initializing");
        using var _ = await RequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "codex_quota_bar", title = "Codex Quota Bar", version = "1.0.5" },
                capabilities = new { experimentalApi = true }
            },
            cancellationToken);

        await SendAsync(new { method = "initialized" }, cancellationToken);
        Log("Initialized");
    }

    private async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        Log($"Request {id}: {method}");
        await SendAsync(new { id, method, @params = parameters }, cancellationToken);

        while (true)
        {
            var line = await _output.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                var detail = _errors.Length == 0 ? "进程已退出。" : _errors.ToString().Trim();
                throw new InvalidOperationException($"Codex App Server 未返回数据：{detail}");
            }

            Log($"Received line ({line.Length} chars)");

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

            Log($"stderr: {line[..Math.Min(line.Length, 300)]}");
        }
    }

    private static void TryResetLog()
    {
        try
        {
            File.WriteAllText(DiagnosticLogPath, $"{DateTimeOffset.Now:O} Codex Quota Bar 1.0.5{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not affect quota reading.
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(DiagnosticLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not affect quota reading.
        }
    }

    private static string FindCodexExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var desktopRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        var desktopCandidate = FindNewestFile(desktopRoot, "*", "codex.exe");
        if (desktopCandidate is not null)
        {
            return desktopCandidate;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extensionsRoot = Path.Combine(profile, ".vscode", "extensions");
        var extensionCandidate = FindNewestFile(
            extensionsRoot,
            "openai.chatgpt-*",
            Path.Combine("bin", "windows-x86_64", "codex.exe"));

        return extensionCandidate ?? "codex.exe";
    }

    private static string? FindNewestFile(string root, string directoryPattern, string relativeFile)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(root, directoryPattern, SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, relativeFile))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadConfiguredModel()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configPath = Path.Combine(profile, ".codex", "config.toml");
            if (!File.Exists(configPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                var equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex <= 0 ||
                    !trimmed[..equalsIndex].Trim().Equals("model", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = trimmed[(equalsIndex + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    return value[1..^1];
                }
            }
        }
        catch
        {
            // Model text is optional; quota data is still authoritative.
        }

        return null;
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
