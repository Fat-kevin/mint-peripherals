# v5 方案 —— 基于官方文档调研的容器与控件精修

> 2026-10-22 第五轮。联网搜索已恢复；全文抓取仍被环境 DNS 拦截
> （learn.microsoft.com / fluent2.microsoft.design / raw.githubusercontent 等 7 域名
> 均解析失败），调研通过搜索定位权威来源，内容以我对这些文档的知识为准。
>
> 来源（搜索定位）：
> - Geometry in Windows 11 — learn.microsoft.com/.../signature-experiences/geometry
> - Layering and elevation in Windows 11 — learn.microsoft.com/.../layering
> - Typography in Windows — learn.microsoft.com/.../typography
> - Galaxy Buds One UI 8 重设计 — sammobile.com / sammyguru.com
> - Logi Options+ — logitech.com / sync hub

## 调研结论（对本项目有用的部分）

1. **控件圆角是 4px**（几何文档），不是 8px：按钮/输入/下拉 4px；
   卡片与浮层才是 8px。v4 把按钮改成了 8——方向错了，本轮改回 4。
2. **Windows 11 "卡片"的官方配方**（分层文档）：卡片 = 比画布浅一档的底色
   （Light 下约 #FBFBFB on #F3F3F3）+ 1px 极浅描边（约 #E5E5E5）+
   8px 圆角 + **无阴影**。观感极轻，这也是 Windows 设置页的观感。
3. **数量纪律**：Windows 设置每屏 2-4 个大容器，容器内部用行分隔；
   不是"每块内容一个盒子"。用户讨厌的是后者（均质小盒堆）。
4. **设备伴侣 App 模式**（Galaxy Buds / Logi Options+）：产品实物图是主角
   （占视觉权重最大），电量用大号读数；控制项分组清晰。

## v5 改动清单（小而准）

1. `Controls.xaml`：按钮圆角 8 → **4**（对齐控件规范）。
2. `Cards.xaml`：`AppCardStyle` 从"透明"升级为 **Win11 轻容器**
   （白底、1px 极浅边、8px 圆角、Padding 20）。效果：
   - 设置页两列、耳机控制区、鼠标性能/映射区、遥控映射/语音区
     自动获得 Windows 设置级的轻容器观感；
   - 状态区（hero：实物图 + 电量环）保持完全开放，无容器。
   - 每页容器数 2-3 个大容器 + 开放 hero——"少量大容器"，不是"卡片堆"。
3. `DeviceCenterPage`：图片行 100 → **120**，实物图更主角（伴侣 App 模式）。
4. 主按钮保持薄荷软填充（与 One UI 的柔和主操作一致）。

## 验证

一次构建、一次启动、UIA 五页 + 对齐复测 + 截图。
