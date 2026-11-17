# 游戏图形 API 检测器 2.0

扫描本地游戏库，检测每个游戏实际使用的图形 API（Vulkan / DirectX / OpenGL），并识别、安装 DXVK。
2.0 版本使用 **Rust + egui** 完全重写，替代原 C# WinForms 实现。

![Rust](https://img.shields.io/badge/Rust-2024-000000?logo=rust) ![egui](https://img.shields.io/badge/egui-0.31-0d6e69) ![Platform](https://img.shields.io/badge/Windows-10%2F11-0078d4)

## 功能

- **扫描本地游戏库**：Steam、Epic、GOG、EA、Ubisoft、Battle.net、Xbox，以及手动添加的第三方本地游戏
- **图表化列表**：游戏 / 平台 / 运行状态 / 图形 API / DXVK 状态 / 安装目录
- **图形 API 检测**：读取游戏进程已加载的图形运行库，判定 Vulkan、DirectX 12/11/10/9、OpenGL
- **DXVK 识别与安装**：检查游戏目录内 `d3d8` / `d3d9` / `d3d10core` / `d3d11` / `dxgi` 的版本信息，一键安装或还原
- **以 Vulkan 启动**：带参数预设、工作目录、启动参数复制，方便对比 DXVK 前后效果
- **本地游戏管理**：手动添加可执行文件，地址保存到 `%LOCALAPPDATA%\GameApiDetector\games.tsv`
- **搜索过滤**：按游戏名、平台、图形 API、DXVK、安装路径实时筛选
- **双击重新检测**：对运行中的游戏即时刷新检测结果

## 截图说明

主界面顶部为操作栏（扫描游戏 / 以 Vulkan 启动 / 安装 DXVK / 本地游戏 / 启动游戏 / 搜索框），
中部为游戏列表，底部为选中游戏的详情面板（平台、运行状态、图形 API、DXVK、架构、安装目录、可执行文件）。

## 检测原理

| 图形 API | 判定依据 |
| --- | --- |
| Vulkan | 进程已加载 `vulkan-1.dll` |
| DirectX 12 | 已加载 `d3d12.dll` / `d3d12core.dll` |
| DirectX 11 | 已加载 `d3d11.dll` |
| DirectX 10 | 已加载 `d3d10*.dll` |
| DirectX 9 | 已加载 `d3d9.dll` |
| OpenGL | 已加载 `opengl32.dll` |

DXVK 状态通过读取游戏目录内 D3D 相关 DLL 的版本信息（ProductName = DXVK）判断，不依赖文件大小或文件名猜测。

## 构建

环境：Windows 10/11、Rust stable（GNU 或 MSVC 工具链均可）

```bash
cargo build --release
```

产物：`target/release/GameApiDetector.exe`（已嵌入 `app.ico` 图标）

## 2.0 变更

- 使用 Rust + egui 重写全部界面与逻辑，单文件可执行，无运行时依赖
- 移除画质调整与帧数对比功能，聚焦图形 API 与 DXVK 检测
- 游戏库识别范围扩展到 EA / Ubisoft / Battle.net / Xbox
- 新增 DXVK 状态列与一键安装、还原
- 新增以 Vulkan / DXVK 启动对话框与参数预设
- 列表支持搜索过滤、行选中详情、双击重新检测
