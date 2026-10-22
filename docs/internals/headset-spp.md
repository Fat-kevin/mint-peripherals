# 内部：耳机 SPP

## 服务与连接

- 设备：`YUANDAO OriG in`（经典蓝牙 + LE）。
- SPP 服务 UUID：`0000A100-1000-8000-4E48-434B4354524C`。
- 使用 `external/YuandaoTws.Application` / `external/YuandaoTws.Infrastructure`（自包含快照）：
  - `HeadsetControlService` 发控制命令；
  - `NiceHckFrameParser` / `YuandaoProtocol` 构帧；
  - `RealHeadsetController` 负责连接、重试、状态合并。

## 常用操作码（详见源项目协议文档）

| 操作 | opcode |
|---|---|
| 电量/状态 | `0x0005` / `0x0006` |
| 固件 | `0x0003` |
| ANC 读/写 | `0x0101` / `0x0201` |
| EQ 读/写 | `0x0107` / `0x0207` |
| 快捷开关 | `0x0108` / `0x06` / `0x05` / `0x09` / `0xE1` |
| 查找耳机 | `0x03A2`（1=鸣响，0=停止，best-effort） |

## 常见失败

- `0x80072740` / “未暴露 RFCOMM 服务”：原版 `YuandaoTws.Desktop` 已占用 SPP，或耳机未在 Windows 蓝牙中连接。
- 连接成功但页面无分量：检查 `RealPeripheralService.Deduplicate` 是否保留了 SPP 条目的 Components/Firmware（字段级 Merge）。
- 控制发送成功但无效果：先用原版程序验证设备状态；部分实验性模式可能被固件忽略。
- 充电盒只返回电量、不返回充电标志：协议层 `Charging == null` → `ToChargeState` 返回 `Unknown`，
  页面显示“未知”，不能按“未充电”处理。

## 安全/边界

- 查找耳机是唯一“会发声”的操作，必须先倒计时；不要在代码里直接调用 `FindAsync(true)`。
- 查找与所有 ANC/EQ/开关写入都经过 `IPeripheralService` 的模块/写入闸门；关闭写入后服务层直接拒绝。
- 不并发打开第二个 SPP 会话。
- `RealHeadsetController.Start()` 必须幂等：重复 `ApplySettings` 不能重复启动扫描，否则会在已连接时再次扫描导致原生崩溃。
