# 内部：罗技 HID++

## 传输

- 接收器：`VID_046D&PID_C53F`（G304 LIGHTSPEED），Usage Page `0xFF00`，Usage 1/2。
- 复用 `external/VigilWatt.Core/Plugins/Logitech` 的 `HidDeviceScanner` / `HidppIO` / `HidppCodec`。
- 请求：`HidppIO.Request(devIndex, featureIdAndFunc, args)`；
  `featureIdAndFunc = (featureIndex << 8) | (function << 4) | swId`。
- **坑**：`HidppCodec.FeatureFunc(feature, func)` 只接受 0–15；
  `0x8100` 的 `0x10/0x20/0x50/0x60/0x70/0x80` 是“功能号 << 4”，
  要传 `func >> 4` 给 `FeatureFunc`，否则会被掩码成 0（表现为读到 format 响应）。

## 已用特征

| Feature | 用途 | 函数 |
|---|---|---|
| `0x0003` | 固件版本 | 0=entity count，1=entity info |
| `0x0005` | 设备名 | 0=length，1=chunk |
| `0x1004` / `0x1000` | 电量 | `0x1004:1`, `0x1000:0` |
| `0x2202` / `0x2201` | DPI 扩展/旧版 | get/set/list |
| `0x8061` / `0x8060` | 轮询率扩展/旧版 | list/get/set |
| `0x8100` | 板载配置 | 0x00 format、0x10/0x20 模式、0x50 读、0x60/0x70/0x80 写 |

## 板载 profile（G304，255 字节）

```
[0]      轮询率（ms，1=1000Hz）
[1]      默认 DPI 档索引
[2]      Shift 档索引
[3..12]  5 × LE16 DPI
[32+4i]  按键映射（每键 4 字节）
[160..207] UTF-16LE 配置名
[253..254] CRC16（CCITT, init 0xFFFF, poly 0x1021, 大端）
```

按键行为：`0x8 SEND`（`0=禁用`、`1=鼠标键`、`2=修饰键+键码`、`3=消费者码）、
`0x9 FUNCTION`（`0x05` = DPI 切换）、`0x0` 宏等。

写扇区流程：

1. `0x60 sectorHi sectorLo 0 0 lenHi lenLo`
2. `0x70 <16-byte payload>`（**不带 sector/offset**）
3. `0x80`（无参数）
4. 回读整个扇区，逐字节比较；不一致则报失败。

## 兼容性

- 通用读取（名称/电量/DPI/轮询率/固件）适用于多数 HID++ 鼠标。
- 当前完整写入只针对 G304 的 255 字节单 profile 格式；其他型号可能使用不同 format/size，
  必须先用 `0x8100:0x00` 读 memory/profile/buttons/sectors/size 再决定是否可写。
- `LogitechMouseService` 只有在 **型号名含 G304/G305 + `_profileValid` + `_profileWritable`** 时才置
  `WriteSupported=true`；否则 `ApplyAsync` 直接返回失败，避免把 G304 的 255 字节格式写到不兼容型号。
- 不支持非罗技品牌；OpenMouse 的 94 个驱动在外部 AGPL 包，未移植。

## 调试

- 日志：`%LOCALAPPDATA%\DeviceCenterPreview\logs\app-YYYYMMDD.log` 搜索 `mouse features` / `mouse onboard`。
- 写入日志：`mouse profile write begin/chunk/finish`；失败时看回读 CRC。
- 鼠标休眠时 `ProbeDeviceIndex` 找不到设备；移动鼠标后点“刷新鼠标”。
