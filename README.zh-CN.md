# Teardown Boundary Remover

这是一个 Windows 图形工具，用于检查 Teardown 地图、备份符合条件的文件，并在用户明确确认后移除经过验证的边界记录。

Windows 的界面语言为中文时显示中文；其他语言环境显示英文。

## 自动扫描范围

- **玩家本地 Mod**：使用 Windows 实际的“文档”Known Folder，扫描 `Teardown\mods`。即使“文档”被重定向到 D 盘或 OneDrive，也不写死 C 盘路径。
- **Steam 创意工坊订阅内容**：从 Steam 注册表位置和 `libraryfolders.vdf` 找出所有 Steam Library，然后逐个扫描 `steamapps\workshop\content\1167630`。
- **游戏自带地图/内容**：从 `appmanifest_1167630.acf` 找到 Teardown 安装目录，再扫描游戏 `data` 内容树；只有解析后根节点精确为 `<scene>` 的 XML 才作为内置关卡候选。
- **额外目录**：玩家可通过“添加位置…”手动补充特殊安装/Mod 管理器目录。

本地 Mod 和 Workshop Mod 优先从 `info.txt` 读取 `name` 与 `author`。游戏自带 XML 如果没有 `info.txt`，保守地显示 XML/文件夹名称，不猜一个可能错误的显示名。

## 删除规则

程序**不用正则表达式修改 XML**。

只有满足以下条件才可能被勾选：

1. XML 能被 .NET XML Parser 正常解析；
2. DTD 与外部实体解析被禁用；
3. 文件被识别为 Teardown 关卡 XML；
4. 最终写入时根节点必须精确为小写 `<scene>`；
5. 目标元素必须是**无 namespace、精确小写**的 `<boundary>`；
6. 文件具有写权限；无权限文件会显示但锁定，不能被“全选”选中。

`<Boundary>`、其他 namespace 的 `boundary`、非 `<scene>` XML 都不会被删除。

## 执行前后的保护

一次真正的修改会经历：

1. 对所有选中文件重新计算 SHA-256，必须与扫描时完全一致。
2. 重新确认 Boundary 数量。
3. 对所有文件做非破坏性的可写性检查。
4. 显示完整项目/XML 清单，让玩家确认。
5. Workshop / 游戏自带文件额外要求风险确认。
6. **先完整备份所有待改 XML**。
7. 对每个备份再次计算 SHA-256，必须与原文件一致。
8. 备份成功后再弹出一次“最后确认”。
9. 用 XML DOM 删除 `boundary` 节点，输出到临时文件。
10. 重新解析临时文件，确认没有 Boundary。
11. 对删除后的“预期 XML 树”和实际写出的 XML 树做语义比较；除 Boundary 外有结构差异就拒绝覆盖。
12. 写回后再次解析验证。
13. 如果同一批后续任一文件失败，自动用已验证备份回滚此前已修改的文件。

程序不会主动删除地图、Mod、XML、Lua、VOX、图片或其他玩家文件。只有程序自己的临时文件会在完成后清理。

## 备份位置

```text
Documents\Teardown Boundary Remover\Backups\<时间戳会话>\
```

每个备份会话包含 `manifest.json`，记录：

- 原文件完整路径；
- 备份路径；
- 项目名称和来源；
- Workshop ID（若有）；
- 修改前 SHA-256；
- 修改后 SHA-256；
- 实际删除 Boundary 数量。

UI 有“恢复最近一次备份…”按钮。

## 图形界面和分辨率适配

程序使用 Windows Forms 原生控件，不做自绘的游戏风 UI。启用 `PerMonitorV2` DPI awareness，并使用：

- `TableLayoutPanel` 百分比布局；
- `SplitContainer` 自适应主列表/详情区域；
- `FlowLayoutPanel`，窄窗口时按钮可自动换行；
- `Dock = Fill`；
- 可调整窗口；
- 低分辨率时允许滚动，而不是让控件互相遮挡。

支持 Windows 缩放设置（125%、150%、200% 等）和跨不同 DPI 显示器移动窗口。

主列表包括：

- 三态“全选可处理项目”复选框；
- 地图 / Mod 名称；
- 来源（本地 / Workshop / 游戏自带 / 自定义）；
- 关卡 XML 数量；
- Boundary 数量；
- 当前安全状态；
- 名称搜索；
- 来源筛选；
- “只显示含 Boundary”；
- 只读 XML 预览；
- 打开所在文件夹；
- 恢复备份。

“全选”只会选择通过安全检查、确实含 Boundary、并且有写权限的项目。异常 XML、无 Boundary、无写权限的项目永远不会因为全选而被选中。

## 编译

目标：Windows 10 / Windows 11 x64。

安装 .NET 8 SDK 后直接运行：

```bat
build-win-x64.cmd
```

输出：

```text
publish\win-x64\TeardownBoundaryRemover.exe
```

项目也附带 `.github/workflows/build.yml`，可以在 GitHub Actions 的 Windows runner 上先运行自测，再生成单文件、自包含 EXE。

个人用途的受保护构建需要自行安装 Dotfuscator Community 7.7，然后明确指定其 `cli` 目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-protected-win-x64.ps1 `
  -DotfuscatorDirectory "<Dotfuscator Community 的 cli 目录>"
```

脚本会输出独立的单 EXE，以及使用相同已重命名业务 DLL 的多文件对照版。私有重命名映射保存在 `protect-map-private`，不得随发布包分发。

## 当前必须做的实机验证

官方 Teardown 文档确认了：本地 Mod 的 `Documents/Teardown/mods`、`info.txt`、Content Mod 的 `main.xml`、多场景 XML、Built-In Mods，以及游戏 `data` 内容树。但官方没有发布当前完整的游戏关卡 XML schema / 完整内置地图文件清单。

因此，本原型对游戏自带地图采用保守规则：“`data` 树中解析后根节点为 `<scene>` 的 XML”。在正式发布前，应当拿**当前版本真实 Teardown 安装目录**跑一次只读扫描，核对是否覆盖了所有你想支持的内置地图，以及真实 Boundary 节点的落盘形式。

版本 0.5.2 已在 Windows x64 与 .NET 8 SDK 环境完成未保护版、受保护单文件版和受保护多文件版自测。自测不会改动已安装的 Teardown 或 Workshop 文件。只有在 `TBR_TEST_TDBIN_PATH` 指向已知输入文件时才运行可选的 TDBIN 集成测试，并且测试前会先把文件复制到临时目录。
