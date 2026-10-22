# 安装包

生成一个单文件安装程序：

```powershell
# 1. 发布自包含运行目录（含 .NET 运行时和 Windows App SDK）
dotnet publish src/DeviceCenterPreview.App/DeviceCenterPreview.App.csproj `
  -c Release -p:Platform=x64 -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
  -o publish/app

# 2. 编译安装程序（需要 Inno Setup 6 的 ISCC.exe）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\DeviceCenterPreview.iss
```

输出：`dist\DeviceCenterPreview-Setup-1.0.3.exe`（83.1MB，未签名）

- SHA-256：`38FEB636B200DF4466DE9191C76F84200B9722171697F2E64D6FD4268A0A952D`
- 校验文件：`dist\DeviceCenterPreview-Setup-1.0.3.exe.sha256`

安装行为：

- 按当前用户安装到 `%LOCALAPPDATA%\Programs\DeviceCenterPreview`，主程序不需要管理员权限。
- 安装包已包含 .NET 8 Desktop Runtime、Windows App SDK、遥控器 sidecar，以及 RC003 Helper
  载荷（`publish\app\helper\`）；目标机器不需要另外安装 .NET，也不需要旧版「无线麦 SayAll」。
- Helper 文件安装到稳定的 `%LOCALAPPDATA%\DeviceCenterPreview\Helper\`（不在 `{app}` 内，
  升级不会锁文件），安装器会弹一次 UAC，执行 `sayall-helper.exe --install-task` 注册
  `SayAll RC003 Helper` 计划任务；之后应用启动/侧车会自动 `/run` 它，音量键和返回键不再依赖旧安装。
- 若用户在 UAC 中取消，安装仍完成，但音量键/返回键不可用；重新运行安装包即可修复。
- 创建开始菜单快捷方式；桌面快捷方式为可选项。
- 卸载时停止/移除 Helper 计划任务、清理 Helper 运行时目录，删除安装目录和卸载信息，并删除
  `DeviceCenterPreview` 的开机自启注册表值；`%LOCALAPPDATA%\DeviceCenterPreview` 中的用户设置/日志保留。
- 开机自启默认关闭，需要在应用设置页顶部手动打开。
- 关闭主窗口（X / Alt+F4）只隐藏到系统托盘，任务栏胶囊继续显示；要真正退出请用托盘右键“退出”。

## 发布前检查（必须）

1. `publish\app\helper\sayall-helper.exe` 和 `frida-gadget.dll` 必须存在，哈希与
   `vendor\rc003-helper\README.md` 一致；否则安装器编译会失败或音量键不可用。
2. 在**干净环境**（或先卸载旧版「无线麦 SayAll」并删除其 Helper 任务）安装，确认：
   - `%LOCALAPPDATA%\DeviceCenterPreview\Helper\sayall-helper.exe` 存在；
   - 任务 `SayAll RC003 Helper` 的 Command 指向这个新路径；
   - Helper 日志出现 `TARGETS-ACK`，包含 `0x0080/0x0081/0x00F1`；
   - 应用显示 `MI RC / ready`，音量键/返回键动作生效。
3. 重启一次，再执行 `schtasks /End /TN "SayAll RC003 Helper"`，30 秒内应自动恢复；
   同时确认只存在一份 `sayall-engine.exe`（stdout 不能被 `schtasks` 输出污染）。
4. 不要在开发机上“旧 Helper 还在跑”时判定通过；那会掩盖安装包缺载荷的问题。
