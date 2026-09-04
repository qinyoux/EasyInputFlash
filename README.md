# EasyInputFlash · ESP32 一键烧录工具

[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6)](README.md)
[![ESP-IDF](https://img.shields.io/badge/ESP--IDF-v5.x-red)](README.md)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Language](https://img.shields.io/badge/Language-PowerShell%20%2F%20C%23-4B8BBE)](README.md)

> A one-click ESP32 firmware tool for Windows: auto-detect serial port → identify chip (write guard) → build → flash → serial monitor.

本工具用于在 Windows 上对 ESP32 固件进行 **自动识别 / 编译 / 烧录 / 串口监视**，适配 **ESP-IDF v5.x**（本项目基线 v5.5.5）。核心设计为「识别接口」：自动枚举串口、按 USB VID 判定是否为 ESP32、并在烧录前用 `esptool chip_id` 做写前验身。

## 功能特性

- **两种形态**：图形界面版 `EasyInputFlashGUI.exe`（WPF 现代深色主题）+ 命令行版 `EasyInputFlash.exe` / `flash-esp32.ps1`
- **自动端口识别**：按 USB VID（`VID_303A`=Espressif、`VID_10C4`=CP210x、`VID_1A86`=CH340）判定 ESP32 串口
- **写前验身（门禁）**：烧录前用 `esptool chip_id` 读取芯片型号与 MAC，确认无误再写入
- **一键烧录**：自动完成「选端口 → 验身 → 编译 → 烧录」整条流程
- **串口监视**：实时查看设备运行输出
- **状态记忆**：自动记住上次的项目路径 / 端口 / ESP-IDF 版本
- **参数化自动化**：支持命令行参数，便于脚本与 CI 集成
- **免手动配环境**：自动加载 ESP-IDF 激活脚本，无需手动 `export.ps1`

---

## 1. 文件组成

| 文件 | 说明 |
|---|---|
| `EasyInputFlashGUI.exe` | **图形界面版**，双击即可用，鼠标操作，内置日志与串口监视。 |
| `EasyInputFlash.exe` | **命令行主程序（单文件）**，已内嵌脚本，可单独拷贝。 |
| `flash-esp32.ps1` | 实际逻辑脚本（exe 运行时内嵌的是同一份）。 |
| `flash-esp32.bat` | 双击启动器，绕过 PowerShell 执行策略，可直接调用脚本。 |
| `flash-esp32.state.json` | 状态记忆文件，自动生成，记录上次使用的项目路径与端口。 |
| `gui-build\EasyInputFlash.WPF.cs` | **图形界面（WPF 现代深色版）源码**，纯 C# + XamlReader 构建，界面更炫酷，便于后续维护/改版。 |

> 使用 `EasyInputFlash.exe` 时，状态文件会保存在 exe 所在目录；使用脚本时保存在脚本同目录。
> 使用 `EasyInputFlashGUI.exe` 时会生成 `EasyInputFlashGUI.state.txt` 记忆上次的项目 / 端口 / 版本。

---

## 2. 前置条件

1. **已安装 ESP-IDF v5.x**（Espressif 安装器方式），默认环境位于 `C:\Espressif\tools`。
2. **开发板已通过 USB 连接**，且安装了对应驱动（Espressif USB / CP210x / CH340）。
3. 需要 **能读取到串口**。若固件运行后接管了 USB（变成键盘/HID 设备），下载串口会消失——此时需 **按住 BOOT 进入下载模式** 再插拔或复位。

---

## 3. 快速开始（推荐）

双击 `EasyInputFlash.exe`，进入交互菜单后直接按对应数字键（**免回车**）：

| 按键 | 功能 |
|---|---|
| `1` | 选择 / 重新识别端口 |
| `2` | 识别设备芯片与 MAC（烧录前验身） |
| `3` | 仅构建 `idf.py build` |
| `4` | 构建 + 烧录 |
| `5` | 仅烧录（跳过构建） |
| `6` | 串口监视（`Ctrl+]` 退出） |
| `7` | 修改项目路径 |
| `8` | **一键烧录**：自动识别端口 → 写前验身 → 构建 → 烧录 |
| `Q` | 退出 |

**最常用的就是按 `8`**：它会自动完成「选端口 → 验身 → 编译 → 烧录」整条流程。

---

## 4. 图形界面版（推荐新手，WPF 现代深色主题）

双击 `EasyInputFlashGUI.exe` 打开图形界面，**全程鼠标操作，无需记按键**。窗口为深色渐变背景 + 圆角卡片风格，分 5 个区域，左侧卡片区可上下滚动：

| 区域 | 说明 |
|---|---|
| ① 编译器 / ESP-IDF 版本 | 自动扫描本机已装的 ESP-IDF 版本，做成下拉选项；默认「自动检测」会选最高版本。切换版本后，后续构建/烧录会用对应版本的 `idf.py` / `esptool`。 |
| ② 设备端口与项目地址 | 「刷新」自动枚举串口并标记（如 `COM4 · ESP32 · USB 串行设备`）；项目地址可「浏览…」选择（需含 `CMakeLists.txt`）或直接手填。 |
| ③ 操作方式 | 单选：构建+烧录 / 仅烧录(跳过编译) / 仅构建 / 识别设备芯片·MAC。 |
| ④ 串口监视 | 「▶ 开始监视」启动串口日志，「■ 停止监视」结束。 |
| ⑤ 日志 / 输出 | 实时滚动显示构建、烧录、识别、监视的完整输出，自动着色（绿=成功、橙红=错误、黄=提醒）。右上角有「跟随日志」开关（默认勾选），勾选时新日志会自动滚到底部，便于随时看到最新输出；取消勾选可自由回看上翻历史日志不被接管。 |

顶部标题栏为自绘按钮（最小化 / 最大化 / 关闭），点击可正常操作窗口；拖动标题栏空白处可移动窗口，最大化会自动避让任务栏。

- **工具图标**：窗口与系统托盘共用一枚 ESP32 芯片 + 闪电（烧录/上电）图标，主题为深蓝渐变 + 亮青芯片，缩小后在托盘里也能一眼辨识出是本工具。
- **缩到托盘**：点标题栏「最小化」会把窗口收进右下角通知区（任务栏图标消失），托盘处保留上述芯片图标；**双击托盘图标**或右键「显示主界面」即可恢复窗口。
- **注意**：点「✕ 关闭」也是收进托盘而非直接退出；要完全退出程序，请用**托盘右键 → 退出**。

顶部还有三个按钮：

- **「► 一键烧录」**（大按钮）：自动识别端口 → 写前验身 → 编译 → 烧录，一步到位。
- **「执行所选操作」**：按 ③ 区当前勾选的方式执行。
- **「停止」**：终止当前构建/烧录/监视进程。

**推荐的「傻瓜式」用法**：

1. 双击 `EasyInputFlashGUI.exe`。
2. ② 区「刷新」选好串口，「浏览…」选好项目（软件会自动校验 `CMakeLists.txt`）。
3. 直接点**「► 一键烧录」**。
4. 右侧日志会依次显示：写前验身（芯片型号/ MAC）→ 编译 → 三段写入 → `Hash of data verified` 成功。
5. 若要看设备运行输出，④ 区点「开始监视」；结束点「停止监视」。

> 提示：烧录完成后若设备接管了 USB（串口消失），属正常现象；要再次烧录需按住 BOOT 进入下载模式。每次操作后软件会自动记忆上次的项目/端口/版本，并保存到 `EasyInputFlashGUI.state.txt`。

---

## 5. 命令行参数（非交互 / 自动化）

```
EasyInputFlash.exe [参数]
```

| 参数 | 说明 |
|---|---|
| `-Project <路径>` | 指定 ESP-IDF 项目目录。默认用上次记录 / 当前目录。 |
| `-Port <COMx>` | 指定串口，如 `COM4`。默认自动识别唯一 ESP32 串口。 |
| `-ListPorts` | 仅列出可用串口。 |
| `-Identify` | 识别端口上设备芯片与 MAC（写前验身）。 |
| `-Build` | 仅构建。 |
| `-Flash` | 构建并烧录。 |
| `-SkipBuild` | 与 `-Flash` 联用，跳过构建直接烧录。 |
| `-Monitor` | 串口监视。 |
| `-Help` | 查看内置帮助。 |

### 常用示例

```powershell
# 交互菜单
EasyInputFlash.exe

# 列出串口
EasyInputFlash.exe -ListPorts

# 指定端口做写前验身
EasyInputFlash.exe -Port COM4 -Identify

# 指定项目并烧录
EasyInputFlash.exe -Project D:\myapp -Flash

# 跳过构建，直接烧录
EasyInputFlash.exe -Port COM4 -Flash -SkipBuild
```

脚本对应用法（去掉 `.exe` 换成 `flash-esp32.ps1` 即可）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\flash-esp32.ps1 `-Port COM4 -Identify
```

---

## 6. 关键逻辑说明

- **端口识别**：枚举 `COM` 口，结合 PnP 设备信息，按 USB VID 判定 ESR（`VID_303A`=Espressif、`VID_10C4`=CP210x、`VID_1A86`=CH340）。多个 / 无法确认时会提示你手动选择。
- **写前验身（门禁）**：烧录前用 `esptool chip_id` 读取芯片型号与 MAC，确认目标设备无误后再写入。
- **烧录输出**：三段写入（bootloader @0x0、分区表 @0x8000、app @0x10000），看到 `Hash of data verified` 即写入成功。
- **环境加载**：自动点载 ESP-IDF 激活脚本以取得 `idf.py` / `esptool`，无需手动运行 `export.ps1`。
- **默认项目目录**：优先取「上次记录」的有效项目（必须是含 `CMakeLists.txt` 的 ESP-IDF 项目），其次取当前目录；两者都不是有效项目时，会明确提示你用 `[7]` 或 `-Project` 指定，而不会误用工具自身目录。

---

## 7. 常见问题

**Q：提示「未检测到可用串口」**
- 插好 USB、确认驱动已装、开发板是否处于下载模式。
- 固件正常运行时若接管了 USB，请按 BOOT 进入下载模式后再试。

**Q：烧录时报「无法自动识别唯一 ESP32」（0 或多个设备）**
- 用 `-Port COMx` 明确指定，或在菜单按 `1` 手动选择。

**Q：提示「CMakeLists.txt not found in project directory」**
- 工具把项目路径解析错了（通常误取成工具自身所在目录）。已修复：现在会校验项目必须含 `CMakeLists.txt`，找不到会提示你用 `[7]` 或 `-Project <路径>` 指定正确项目目录。

**Q：`-Flash` 一直卡在编译**
- 首次编译较慢属正常，等待 `构建成功` 即可。

**Q：中文提示乱码 / 解析报错**
- 若直接用脚本，请确保文件带 **UTF-8 BOM**（exe 已自动处理）。

**Q：固件烧完后串口不在了**
- 这是应用接管 USB 导致的正常现象；重新烧录需进入下载模式。

---

## 8. 安全注意

- 烧录会覆盖设备固件，请确认目标设备正确（建议配合 `-Identify` 验身）。
- 本工具只做识别、构建、烧录、监视，不修改 GPIO、BOOT 流程、分区、设备身份或通信协议。
- 状态记忆文件不含任何敏感信息；工具的运行日志不会输出密码、密钥、SSID、网络主机、BLE 地址或用户输入内容。

---

## 9. 项目结构

```
EasyInputFlash/
├── flash-esp32.ps1             # 核心逻辑脚本（端口识别 / 验身 / 构建 / 烧录 / 监视）
├── flash-esp32.bat             # 双击启动器（绕过 PowerShell 执行策略）
├── EasyInputFlash.exe          # 命令行版（单文件，已内嵌脚本）
├── EasyInputFlashGUI.exe       # 图形界面版（WPF 深色主题）
├── gui-build/
│   └── EasyInputFlash.WPF.cs   # 图形界面源码（C# + XamlReader）
├── README.md                   # 本文档
├── CHANGELOG.md                # 变更日志
└── LICENSE                     # MIT 开源协议
```

## 10. 源码运行与构建

### 命令行版（免编译，直接运行脚本）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\flash-esp32.ps1
```

> 直接跑脚本时请确保文件为 **UTF-8 BOM** 编码（避免中文乱码）；`flash-esp32.bat` 已处理执行策略，可双击使用。

### 图形界面版（从 C# 源码构建）

`gui-build\EasyInputFlash.WPF.cs` 是纯 C# + `XamlReader` 构建的 WPF 程序，不依赖 `.xaml` 工程文件：

```powershell
# 用 csc.exe 或 dotnet 直接编译（示例）
csc /target:winexe /out:EasyInputFlashGUI.exe gui-build\EasyInputFlash.WPF.cs
```

也可以新建一个 .NET Framework 的 WPF 工程，将 `EasyInputFlash.WPF.cs` 作为唯一源文件加入后编译。

## 11. 开源协议

本项目采用 [MIT License](LICENSE) 开源。烧录会覆盖设备固件，请自行确认目标设备正确（建议配合 `-Identify` 做写前验身）。

## 12. 变更日志

详见 [CHANGELOG.md](CHANGELOG.md)。
