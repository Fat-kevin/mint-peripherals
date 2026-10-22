# RC003 Helper（随应用分发的 x64 载荷）

这个目录是「外设中枢」自包含安装所需的 RC003 增强捕获助手，不是构建时从
`remote-mic-app-windows` 读取的兄弟目录。应用发布时由
`DeviceCenterPreview.App.csproj` 复制到 `publish/app/helper/`，安装器再放到
`%LOCALAPPDATA%\DeviceCenterPreview\Helper\`。

## 文件

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `sayall-helper.exe` | 1,154,560 | `51DB816825019BF0956A3DAC8743947FA308C0E734D04864AE3465ED6E2B89ED` |
| `frida-gadget.dll` | 23,254,016 | `350BEB0E801DC7DC39D21512960D1048B9F21DC72C0EE5CED5CF5D9DC8EA6687` |

- Helper 来源：`remote-mic-app-windows/hardware/RC003/helper` 的 x64 release 构建
  （导入日期 2026-10-21；旧版本安装目录中的同名文件）。
- Gadget 版本：Frida 17.18.0，x86_64；完整性/许可证依据原项目的
  `vendor/frida-gadget.lock.json`（wxWindows Library Licence 3.1，允许二进制再分发）。
- Helper 本身是 GPL-3.0-only；分发二进制时需同时提供对应源码或书面源码提供方式。
  源码位置：`remote-mic-app-windows/hardware/RC003/helper`。

## 运行时约定

- 任务名：`SayAll RC003 Helper`（与 Helper 内常量及 sidecar 的自动启动代码一致）。
- 任务动作：`sayall-helper.exe --follow-app`，最高权限、仅手动触发。
- Helper 默认从**自身所在目录**找 `frida-gadget.dll`；所以这两个文件必须放在同一目录。
- 运行时目录为 `%ProgramData%\SayAll\rc003-helper`，由 Helper 自己创建/维护。
- 不要在这里放 `session.token`；它由 Helper 生成。
