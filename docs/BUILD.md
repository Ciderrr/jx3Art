# 构建与验证

玩家可以直接下载安装版或 Windows ZIP。 [下载 Release](https://github.com/Ciderrr/jx3Art/releases)

已用 Windows x64、.NET SDK 10.0.402、Node.js 24.16.0 和 WebView2 Runtime 验证演示构建。前端依赖由 `product/frontend/package-lock.json` 固定，.NET 依赖由项目文件声明。

## 隔离演示与检查

从仓库根目录运行以下命令。演示和检查使用各自的模拟数据目录。

```powershell
npm --prefix product/frontend ci --ignore-scripts
npm --prefix product/frontend run build
dotnet publish product/JX3Artist/JX3Artist.csproj -c Release -p:IncludeGameTemplates=false -o output/demo
dotnet output/demo/JX3Artist.dll --web-ui-test --out output/checks/web-ui
dotnet output/demo/JX3Artist.dll --ui-offscreen --out output/checks/render
```

运行 `output/demo/Preview.cmd` 可以体验界面。关闭后可删除构建目录。默认演示脚本的独立数据位于 `%LOCALAPPDATA%\JX3ArtistAutomation\ui-preview`，日常辅助数据位于同一父目录下的其他子目录。

## 完整识别构建所需输入

识别子进程通过 Windows Python Launcher `py` 启动。开发验证环境使用 Python 3.12、`opencv-python==5.0.0.93` 和 `numpy==2.5.4`。安装前确认 `py` 指向你准备用于运行助手的环境。

```powershell
py -3 -m pip install opencv-python==5.0.0.93 numpy==2.5.4
```

完整构建需要下列六个**有相应使用权限且符合检测契约**的文件。对应名称是加载位置，随意裁一张图放在同一路径不足以使识别有效。源码仓库提供加载契约和代码。已发布的 Windows ZIP 自带运行所用的六个模板。

| 路径 | 用途 |
| --- | --- |
| `vis2/template/d1_icon_template.png` | 开始表演图标 |
| `int1/calibration/assets/panel_painted_v1.png` | 表演面板绘制核心 |
| `int1/calibration/assets/panel_painted_mask_v1.png` | 面板核心掩码 |
| `int1/calibration/assets/panel_clock_v1.png` | 时钟定位 |
| `int1/calibration/assets/panel_roses_v1.png` | 面板下部控件定位 |
| `int1/calibration/assets/start_bar_gold_v1.png` | 入口旁金色区域诊断 |

已准备兼容模板时，可使用原完整包构建与验证脚本。它核对引擎资源、识别冒烟、程序回归、界面联调与发布产物，检查过程走隔离环境。`--self-test` 包含依赖模板的识别用例，模板齐备后才能完整运行。

```powershell
pwsh -NoProfile -File product/Publish-Candidate.ps1 -OutputDirectory delivery/JX3Artist-0.1.77 -ManagedDiagnostics
```

完整应用启动仍使用原 Windows 管理员授权流程。关闭程序后，可删除程序目录和对应桌面快捷方式。设置、经验和日志保存在 `%LOCALAPPDATA%\JX3ArtistAutomation`，可以按需要保留。

## 安装器和更新包

安装器用 Inno Setup 6.7.3 编译，视觉素材可运行 `node product/installer/render-art.mjs` 重新生成。编译时通过 `/DPayload` 指向完整的 Windows x64 自包含包，通过 `/DOutput` 指定产物目录。发布包包含 `distribution.txt`、本地 Python 环境与 `prerequisites/MicrosoftEdgeWebview2Setup.exe`。引导程序须与启动检查源码中记录的签名验证后 SHA‑256 一致。

发行名为 `JX3Artist-<版本>-Setup.exe` 和 `JX3Artist-<版本>-win-x64.zip`，ZIP 的根目录与其文件名去掉后缀一致。更新器按安装方式选取对应文件，使用 GitHub 正式 Release 的大小和 SHA‑256 校验。安装版保留原目录，便携版生成独立目录。

```powershell
ISCC.exe /DPayload="C:\build\JX3Artist-1.0-win-x64" /DOutput="C:\build\release" product/installer/JX3Artist.iss
```
