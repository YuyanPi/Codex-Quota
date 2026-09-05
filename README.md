# Codex Quota Bar

一个常驻 Windows 桌面右侧的 Codex 额度看板，显示当前 GPT 模型、5 小时额度、周额度、状态颜色和预计重置时间。

## 功能

- 从本机 Codex 登录态读取真实额度，不需要 API Key
- 5 小时与周额度进度条
- 绿色（51%–100%）、黄色（21%–50%）、红色（1%–20%）、灰色（0%）
- 显示当前模型、额度状态、重置时间和倒计时
- 每 60 秒自动刷新，也可手动刷新
- 置顶、可拖动、可缩放的 Windows 侧边栏

## 运行条件

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 已安装并登录 Codex 桌面版或 Codex CLI，且 `codex.exe` 可从 `PATH` 找到

## 本地运行

```powershell
dotnet run --project .\CodexQuotaBar\CodexQuotaBar.csproj
```

## 构建单文件版本

```powershell
dotnet publish .\CodexQuotaBar\CodexQuotaBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

输出位于 `CodexQuotaBar\bin\Release\net8.0-windows\win-x64\publish`。

## 数据与隐私

应用启动本机 `codex app-server --stdio` 子进程，通过 JSONL 请求 `config/read` 和 `account/rateLimits/read`。它不读取浏览器 Cookie，不保存访问令牌，也不会把额度发送到其他服务器。

`codex app-server` 目前仍标记为实验性接口。如果未来协议发生变化，应用会显示刷新错误；更新本机 Codex 或本项目即可适配。

## 数据口径

服务端提供 `usedPercent`，界面显示的剩余量为 `100 - usedPercent`。优先用 `windowDurationMins` 识别 300 分钟和 10,080 分钟窗口；旧版响应未提供时，按 `primary`（短窗口）与 `secondary`（长窗口）兼容处理。

## 官方资料

- [Codex App Server 协议](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Codex 开发者文档](https://developers.openai.com/codex/)

## License

MIT
