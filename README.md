# Codex Quota Bar

一个小巧的 Windows 桌面 Codex 额度看板，显示 5 小时额度、周额度和预计重置时间。默认窗口约 `230 × 146 px`，每次启动显示在右下角，也可以拖动或取消置顶。

## 功能

- 从本机 Codex 登录态读取真实额度，不需要 API Key
- 5 小时与周额度进度条
- 绿色（51%–100%）、黄色（21%–50%）、红色（1%–20%）、灰色（0%）
- 显示额度百分比、状态颜色和重置时间
- 每 60 秒自动刷新，也可手动刷新
- 紧凑、置顶、可拖动、可缩放；标题栏圆点按钮可取消置顶
- 胡萝卜应用图标，用于 EXE、任务栏、窗口和桌面快捷方式

## 推荐安装：只在 GitHub 构建，本机不改源码

1. 打开仓库的 **Actions** 页面，选择 **Build Windows packages**。
2. 打开最新一次成功运行，在页面底部下载 `CodexQuotaBar-Windows`。
3. 在 D 盘创建 `D:\Codex-Quota\Downloads`，把下载内容解压到这里。
4. 选择需要的版本：
   - `CodexQuotaBar-lite-win-x64.zip`：体积小，需要本机已有 .NET 8 Desktop Runtime。
   - `CodexQuotaBar-standalone-win-x64.zip`：体积较大，但无需另装 .NET。
5. 再次解压所选压缩包，双击 `CodexQuotaBar.exe` 即可便携运行。

如果希望固定安装，在解压目录打开 PowerShell，运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

默认安装到 `D:\Codex-Quota\App` 并创建桌面快捷方式。需要开机启动时运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1 -StartWithWindows
```

## 运行条件

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 已安装并登录 ChatGPT Windows 桌面版、VS Code Codex 扩展或 Codex CLI（任一即可）

应用会自动查找 ChatGPT 桌面版和 VS Code 扩展自带的 `codex.exe`，不要求另装一个“Codex 桌面版”，也不要求手工配置 `PATH`。

## 本地运行

```powershell
dotnet run --project .\CodexQuotaBar\CodexQuotaBar.csproj
```

## 构建单文件版本

```powershell
dotnet publish .\CodexQuotaBar\CodexQuotaBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

输出位于 `CodexQuotaBar\bin\Release\net8.0-windows\win-x64\publish`。

## GitHub 自动发布

每次推送到 `main`，GitHub Actions 都会自动构建两个 Windows 压缩包。推送 `v1.0.0` 这类标签时，还会自动创建 GitHub Release。本机只需下载 Release，不需要保存或修改源码。

## 桌面版与 VS Code 插件

ChatGPT 桌面版、Codex CLI 和 VS Code Codex 扩展使用同一个 ChatGPT 账号登录时，共用同一账户额度。不要在其中一处改用 API Key，否则那一处会切换到 API 按量计费口径。

## 数据与隐私

应用启动本机 `codex app-server --stdio` 子进程，通过 JSONL 请求 `account/rateLimits/read`，并从共享的 `.codex` 配置显示当前模型。它不读取浏览器 Cookie，不保存访问令牌，也不会把额度发送到其他服务器。

如果读取失败，可查看 `%TEMP%\CodexQuotaBar.log`；日志只记录连接阶段，不记录额度响应、密钥或访问令牌。

`codex app-server` 目前仍标记为实验性接口。如果未来协议发生变化，应用会显示刷新错误；更新本机 Codex 或本项目即可适配。

## 数据口径

服务端提供 `usedPercent`，界面显示的剩余量为 `100 - usedPercent`。优先用 `windowDurationMins` 识别 300 分钟和 10,080 分钟窗口；旧版响应未提供时，按 `primary`（短窗口）与 `secondary`（长窗口）兼容处理。

## 官方资料

- [Codex App Server 协议](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [Codex 开发者文档](https://developers.openai.com/codex/)

## License

MIT
