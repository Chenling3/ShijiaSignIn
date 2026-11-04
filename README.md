# 石之家自动签到 (ShijiaSignIn)

一个卫月（Dalamud）插件，在游戏内自动完成 FF14 国服「石之家」社区（`ff14risingstones`）的每日签到。

## 在线安装（自定义插件仓库）

游戏里输入 `/xlsettings` → 试验性 → 自定义插件仓库，粘贴下面的网址并添加：

```
https://Chenling3.github.io/ShijiaSignIn/pluginmaster.json
```

添加后在插件安装器搜「石之家自动签到」即可安装、自动更新。

## 功能

- **自动打开浏览器登录并读取 Cookie**：首次使用或 Cookie 失效时，自动弹出 Chrome/Edge 浏览器，你在窗口里登录（扫码/账号密码），插件通过 CDP 自动读取盛大通行证 Cookie 并保存，无需手动复制粘贴
- 每日定时自动签到（默认 00:05，可改）
- 启动后若已过签到时间且当天未签，会自动补签
- 手动签到：`/shijia sign` 或设置窗口里的「立即签到」
- Cookie 失效时自动重新打开浏览器登录（可关）
- 设置窗口：`/shijia` 或插件列表里的设置入口

## 使用前必读

1. **自动登录读取 Cookie 的原理**：插件会启动一个**全新的临时浏览器配置**（临时 `--user-data-dir`），打开石之家登录页，等你手动登录。登录成功后插件通过 CDP 的 `Storage.getCookies` 读取所有 `sdo.com` Cookie（以 `CASTGC` 为登录成功标志）并保存。读取完成后会自动关闭该浏览器窗口。
   - 因为是全新临时配置，**不会记住你的浏览器密码**，每次都要重新登录；但 Cookie 有效期通常有几十天，失效前不会重复要求登录。
   - 若自动检测不到浏览器，可在设置里指定浏览器路径。

2. **EdgeOne WAF 风险（仅签到请求）**。读取 Cookie 走的是真实浏览器（不会触发 WAF），但后续的**签到 POST 请求**用的是 .NET 自带 `HttpClient`。石之家接口受腾讯 EdgeOne WAF 保护，社区工具 [ff14checkin](https://github.com/SakagamiJun/ff14checkin) 专门用 TLS 指纹伪装来「绕过 WAF」。若签到返回 403 等非 200，说明请求被拦。
   此时更稳的做法是：改用浏览器脚本（Greasyfork「石之家自动盖章」）或 Windows 计划任务跑 `ff14checkin`，而不是用游戏内插件。

3. **`DalamudApiLevel`**。清单 `ShijiaSignIn.json` 里写的是 `15`（对应国服当前卫月版本）。如果未来国服卫月升级 API 等级导致插件加载失败，请把该字段改成实际值。

## 命令

| 命令 | 作用 |
| --- | --- |
| `/shijia` | 打开设置窗口 |
| `/shijia sign` | 立即签到 |
| `/shijia login` | 打开浏览器登录并读取 Cookie |
| `/shijia help` | 查看帮助 |

## 编译

前置：已安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，并启用卫月的开发者插件模式。

本仓库 `ref/` 目录已内置国服卫月的 `Dalamud.dll` 与 `Dalamud.Bindings.ImGui.dll` 引用，本地编译直接用：

```powershell
dotnet build -c Release -p:DalamudLibPath="D:\deepseek\ShijiaSignIn\ref\"
```

产物在 `bin\Release\` 下（`ShijiaSignIn.dll` + `ShijiaSignIn.deps.json` + `ShijiaSignIn.json`）。打包成 `latest.zip`：

```powershell
$dist = "dist\ShijiaSignIn"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item "bin\Release\ShijiaSignIn.dll" $dist
Copy-Item "bin\Release\ShijiaSignIn.deps.json" $dist
Copy-Item "ShijiaSignIn.json" $dist
Compress-Archive -Path "$dist\*" -DestinationPath "latest.zip" -Force
```

或直接走 GitHub Actions：打 tag（如 `v1.0.1.0`）推送，或在仓库 Actions 页面手动触发，会自动编译、打包、发 Release、更新 `pluginmaster.json` 并部署到 GitHub Pages。

## 项目结构

```
ShijiaSignIn/
├── ShijiaSignIn.csproj    # 工程文件
├── ShijiaSignIn.json      # 插件清单(manifest)
├── Service.cs             # Dalamud 服务定位器
├── Configuration.cs       # 配置模型
├── SignInService.cs       # 签到核心逻辑(HTTP 请求)
├── CookieBrowserService.cs# 打开浏览器登录并读取 Cookie(CDP)
├── Plugin.cs              # 插件入口 + 聊天命令
└── PluginUI.cs            # 设置窗口(ImGui)
```

## 免责声明

本插件仅用于个人学习与自动化日常签到，请勿用于商业用途或恶意刷分；任何滥用导致的账号风险由使用者自行承担。
