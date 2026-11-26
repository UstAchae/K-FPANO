# K-FPANO

L3Harris F-PANO（四筒全景融合夜视仪）和 Ops-Core RAILINK 供电导轨，用于 SPT 4.1.6。包括物品、服务端模组和热像客户端。

L3Harris F-PANO (fused panoramic night vision goggle) and the Ops-Core RAILINK powered rails for SPT 4.1.6: the items, their server mod and the thermal client.

## 0.2.63 beta 1：测试版

这是测试版本，还在游戏里测试中，可能会有改动或问题，发现不对的地方欢迎反馈。

This is a test version: the mod is still being tested in game, and things may change or break. Please report what looks wrong.

## 物品

都在维和部队（Peacekeeper）出售，有黑色和沙色（FDE）两种颜色。

| 物品 | 用途 | 价格 / 信任等级 |
|---|---|---|
| **F-PANO** | 四筒全景夜视仪（97 度视场，白磷），右内侧镜筒叠加热像，带数字罗盘和 I2 增益显示 | $2600，LL4 |
| **RAILINK** | 头盔供电导轨，电池内置。F-PANO 只有装在带 RAILINK 的头盔上才有电 | $830，LL3 |
| **SureFire HL2-A** | 头灯：红外弱、红外强、白光弱、白光强 | $380，LL2 |
| **Princeton Tec MPLS** | 小型灯：红光、白光 | $160，LL1 |
| **HEL-STAR 频闪灯** | 头顶频闪识别灯：红外 / 可见光，弱 / 强 | $500，LL2 |

### 能装在哪里

- **F-PANO** 能装在 9 种外形、共 13 顶头盔的夜视仪槽上（FAST MT、TC-2001/2002、TC 800、AirFrame、凯门鳄、EXFIL、Tor-2 等带 ARC 导轨的头盔），也能装在 RAILINK 自己的夜视仪槽上。
- **RAILINK** 装在这些头盔的耳机槽上（没有耳机槽的头盔会多出一个 RAILINK 槽）。装上后头盔原来的导轨会隐藏。RAILINK 上有：
  - 耳机槽：RAC、ComTac V、ComTac VI，耳机会卡在 RAILINK 的导轨上；
  - 左右各一个 ARC 卡座：PRA、SCRA；
  - 左右各一个前灯槽：HL2-A、MPLS；
  - 左右各一个频闪灯槽：HEL-STAR。
- **供电**：F-PANO 只有在带 RAILINK 的头盔上才有电。没电时可以翻下，但没有画面、不接电源线，物品图标右下角的灯不亮；有电时灯是绿色的。

### 冲突规则

- RAILINK 只和 GPNVG-18、ENVG-B 冲突，TATM 等其他夜视仪支架可以照常装在头盔上。
- RAILINK 上的 F-PANO 和头盔自己夜视仪槽里的设备在同一个位置，两者不能同时装。
- 装了 RAILINK 后，头盔自带的 ARC 卡座不能用，PRA、SCRA 只装在 RAILINK 左右两边；头盔自带卡座上已经有东西时，要先取下才能装 RAILINK。
- PRA、SCRA 和 ComTac V/VI 冲突（ComTac 卡在同一段导轨上）。
- 护颚、护耳按保护部位算：保护耳朵的（护耳、侧甲，以及 AF 护颚、NeoSteel、WTT 的 EXFIL 护颚这类二合一）和耳机冲突；只护下巴的（如凯门鳄护颚）不冲突。
- EXFIL 上被 RAILINK 占掉的护耳、护颚槽，改到 RAILINK 自己的槽里，位置和原来一样，只能装在原来能装它的头盔上。

## 快捷键

| 按键 | 作用 |
|---|---|
| 游戏的夜视仪开关（默认 **N**） | 翻下 / 翻上 F-PANO |
| **→** / **←** | F-PANO 翻下时：切换模式（纯夜视 → 轮廓 → 白热融合 → 黑热融合） |
| **↑** / **↓** | 调夜视管增益：点一下微调，按住连续变化（从 0 到满约 2.5 秒）；调节时热像框左边显示黄色的 I2 GAIN 竖条；调到 0 只剩热像 |
| **H** | 游戏原版：开关全部头灯 |
| **Ctrl + H** | 只切换 RAILINK 上频闪灯的效果（红外弱 / 红外强 / 可见光弱 / 可见光强） |
| **Ctrl + ←** | 切换 RAILINK 左侧（佩戴者左边）前灯的模式 |
| **Ctrl + →** | 切换 RAILINK 右侧前灯的模式 |

快捷键都可以在 F12 设置里改：「F-PANO 热像轮廓」和「RAILINK 照明」两栏。菜单、背包或地图打开时方向键不起作用。

配合 K-3PS-C 时，支架位置可以调：腰射时按住 **左 Alt + = / -** 升降，**左 Shift + = / -** 仰俯，**左 Ctrl + = / -** 前后；每个支架各自记住位置。

## 四种模式

- **纯夜视**：只有白磷夜视。
- **轮廓**：温差的边缘画成琥珀色的线。
- **白热融合**：整幅热像叠在夜视上，热的亮。
- **黑热融合**：整幅热像叠在夜视上，冷的亮、热的暗。

热像框上方是数字罗盘：黄色圆环，中间是当前朝向的度数（青色）。

### F12 里常用的设置（「F-PANO 热像轮廓」）

- 显示内容：当前模式。
- 夜视管增益、夜视亮度、白磷夜视开关。
- 轮廓亮度、轮廓多少、草的轮廓强度。
- 「高级」栏里还有热像分辨率、气温、延迟等参数，一般不用动。

### 热像是怎么画的

- 热像用物体表面自己的明暗，人体按约 34 °C 标出，轮廓模式里另外画草的轮廓。
- 和真实热像一样：窗户玻璃不透，画成一块均匀的面；墙上的字、涂鸦等贴花不出现在热像里。
- 按真实的方式画：在夜视仪的热像物镜处（右内侧镜筒上方）放一台相机，按传感器的分辨率（每度 16 像素）拍场景，朝向和眼睛一致。近处的东西出现在物镜看到的位置，所以枪上的瞄具挡住热像的地方比挡住眼睛的地方偏下；瞄具的倍率不会放大热像。这台相机每帧约 1.5 毫秒（在藏身处测得）。F12 高级里「热像从物镜位置拍（真实画法）」可以关掉，关掉后热像取自眼睛这台相机。
- 瞄具会在热像上挖洞：从物镜看过去，视线穿过红点、全息或倍镜的玻璃、从瞄具前端穿出去的地方，只显示夜视画面；瞄具自己的罩子和镜筒内壁保留热像，挡在瞄具前面的手、头盔也保留自己的热像。
- 枪和手只用当前一帧的热像，晃枪时不会拖影。

## 模型来源

- **F-PANO**：3D Military Assets（3DMA）的 L3Harris F-PANO NVGs 模型：https://www.3dmilitaryassets.com/en-gb/products/l3-harris-f-pano-nvgs
- **RAILINK、HL2-A、MPLS、HEL-STAR 频闪灯**：3D Military Assets（3DMA）的 Ops-Core RAILINK Data ARC Rail System 模型：https://www.3dmilitaryassets.com/en-gb/products/opscore-railink-data-arc-rail-system
- **RAC、ComTac V/VI 耳机**：游戏自带的模型，本模组只是把它们的形状调整到 RAILINK 导轨上。

模型按 3DMA 的授权用于免费模组，只以游戏资源包的形式发布，不包含原始模型文件。感谢 3D Military Assets。

Models: 3D Military Assets (3DMA), L3Harris F-PANO NVGs and Ops-Core RAILINK Data ARC Rail System (links above); the RAC and ComTac headsets are the game's own. Used under 3DMA's licence for free mods, shipped only as game bundles; the source models are not included.

## 安装

把发布的压缩包解压到 SPT 文件夹，会得到 `SPT_Runtime/user/mods/UstAchae-FPANO`（服务端）和 `BepInEx/plugins/JustachaeFPANO`（客户端）。

- 配合 **K-3PS-C 1.9.0 beta 1 或更新版本**：F-PANO 通过四个镜筒的目镜成像，双眼合并，支架可调，热像画在右内侧镜筒里，大小和位置按眼睛实际看到的镜片来定。
- 用旧版 K-3PS-C（最后一个正式版是 1.8.2）或不装：F-PANO 用游戏原版的全屏夜视，加上本模组的白磷效果，热像铺满整个屏幕。

With K-3PS-C 1.9.0 beta 1 or later its eyepieces image the device and the thermal picture is drawn into the right inner tube. With an older K-3PS-C (1.8.2 is the last release) or without it, the F-PANO uses the game's own full-screen night vision, its white phosphor and the thermal picture laid over the whole screen.

旧版本里单独的 F-PANO 支架和电池物品从 0.2.69 起不再存在：电池是 RAILINK 的一部分，支架是 F-PANO 的一部分。

## 仓库（开发用）

- `item/` - Blender export (`export_parts.py`), frames, seat on the helmet, flip hinge and figure-8 eyepiece glass (`prepare.py`), items split (`split_items.py`), RAILINK fits per helmet (`fit_*.py`), ComTac bakes (`bake_headsets.py`), Unity bundle build (`Unity/Assets/Editor/FpanoBuilder.cs`, icon checks `IconCheck*.cs`), game shader swap (`finalize.py`). `nodes.json` lists the prefab's nodes, the thermal lens marker and the eyepiece discs for the other mods.
- `server/` - SPT server mod, `install.py`.
- `client/` - BepInEx plugin (thermal fusion, RAILINK power, lights, headsets), `build/build.sh`.
- `package.py` - the release zip from the built outputs (checks server and client carry one version).

The purchased model sources are never committed (see 模型来源 above).
