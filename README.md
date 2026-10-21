# DbPilot

基于 Avalonia UI、AvaloniaEdit 和 .NET 8 的跨平台数据库桌面工作台。按《多数据库桌面管理工具需求文档》实现，以 Navicat 的连接树、查询页、结果表格作为交互参考。

当前交付是 **V0.3 开发版**，已加入数据库工具、SSH、CSV 导入、迁移、ER 图、图表和基础逻辑备份恢复。各功能范围与真实环境待验收项见 [功能进度](docs/功能进度.md)。

## 启动

Windows 已打包程序：`artifacts/win-x64/DbPilot.exe`，双击即可启动，无需单独安装 .NET。

从源码运行：双击根目录 `run.cmd`。脚本优先使用项目 `.tools/dotnet` 中的 SDK，也可使用系统 .NET 8 SDK。

```powershell
dotnet run --project src/DbPilot/DbPilot.csproj
dotnet run --project tests/DbPilot.Checks/DbPilot.Checks.csproj
```

完整构建和测试：`powershell -ExecutionPolicy Bypass -File build.ps1`。增加 `-Publish` 可生成 Windows x64 自包含单文件程序。开发依赖通过 NuGet 还原，需要网络。

## 使用流程

1. 点击“新建连接”，填写连接参数，测试后保存。支持 MySQL、PostgreSQL、SQL Server、TDengine。
2. 双击左侧连接加载数据库，双击数据库加载对象，单击表或视图打开分页数据，点击节点箭头展开字段。可右键查看字段、索引及生成 SQL 模板。
3. 在查询页编写 SQL；`Ctrl+R`（macOS `⌘R`）执行全部内容，`Ctrl+Shift+R`（macOS `⇧⌘R`）只执行选中内容；“终止”取消当前任务。
4. `Ctrl+Space` 补全关键字和已加载对象名；表浏览页额外加载当前表字段，TDengine 包含标签字段。
5. 表浏览支持 WHERE、20 / 50 / 100 / 500 行分页。“导出结果”导出当前结果的 CSV、JSON 或 SQL INSERT；“字段详情”查看和编辑大文本字段。
6. 拖动查询页可调整顺序；拖动连接到分组可移动归属；分组按钮可新建、重命名、删除分组。
7. 查询历史保存最近 200 条成功执行的 SQL，记录连接和数据库上下文；载入后需要手动执行。SQL 模板提供基础查询及 TDengine 超级表、子表、时间聚合示例。
8. “数据库工具”选择源/目标连接及数据库并加载对象，可使用映射导入、整表导出、对象设计、结构比较、迁移、ER 图、查询构建、基础逻辑备份与恢复。结构比较 SQL 绑定目标连接，生成 SQL 在编辑器审阅后执行。
9. “图表”从当前结果选择时间/数值列绘制折线图，可缩放、悬停和导出 SVG。“执行计划”读取 EXPLAIN / SHOWPLAN_TEXT。
10. SSH 在连接设置填写，首次测试显示待核对指纹；通过可信渠道核对后填写 SHA256 指纹。“配置备份”使用独立口令导出可跨机器恢复的加密配置。
11. “设置”可修改字体、编辑器选项、快捷键和脚本出错策略。“快捷键”页列出全部可配置命令，可点击组合键重新录入、清除或恢复默认。自定义模板及最近 500 条日志加密保存。

CSV 首行为字段名，可重新映射或跳过字段；`\N` 代表 NULL，真实以反斜杠开头的字符串需再加一个反斜杠（本软件导出会自动处理）。关系型导入整批事务提交；TDengine 每批至多 200 行，成功批次无法回滚，中断时报告已确认行数与未知批次。

基础逻辑备份包含字段/计算列、主键、外键和数据，不含索引、唯一/CHECK 约束、视图、触发器、用户、权限与分区存储，也不提供跨表一致性快照。恢复按批次执行，成功批次不会随后续错误自动回滚。MySQL 脚本保留原数据库名，恢复前请审阅。

## 快捷键

默认键位参照 Navicat，macOS 使用 Command 组合。全部命令均可在“设置 → 快捷键”中重新录入、清除或恢复默认；修改后的键位保存在 `settings.json`，同一组合不允许重复绑定。连接树、SQL 编辑器、数据表格和表浏览相关的命令只在对应区域有焦点时触发。

| 操作 | Windows / Linux | macOS |
| --- | --- | --- |
| 新建连接 | Ctrl+Shift+N | ⇧⌘N |
| 打开 / 关闭连接 | Ctrl+Shift+O / Ctrl+Shift+D | ⇧⌘O / ⇧⌘D |
| 编辑 / 复制连接（连接树） | Ctrl+E / Ctrl+Shift+C | ⌘E / ⇧⌘C |
| 新建数据库 / 新建表 | Ctrl+Alt+N / Ctrl+N | ⌥⌘N / ⌘N |
| 新建查询 | Ctrl+Q | ⌘Y |
| 打开 / 保存脚本 | Ctrl+O / Ctrl+S | ⌘O / ⌘S |
| 关闭查询 | Ctrl+W | ⌘W |
| 执行 SQL | Ctrl+R | ⌘R |
| 执行选中 SQL（编辑器） | Ctrl+Shift+R | ⇧⌘R |
| 终止任务 | Ctrl+T | ⌘T |
| 格式化 SQL | Ctrl+Shift+F | ⇧⌘F |
| SQL 补全（编辑器） | Ctrl+Space | Ctrl+Space |
| 查询历史 / SQL 模板 | Ctrl+L / Ctrl+Alt+T | ⌘L / ⌥⌘T |
| 执行计划 / 导出结果 | Ctrl+Shift+P / Ctrl+Shift+E | ⇧⌘P / ⇧⌘E |
| 显示 / 隐藏侧栏 | F6 | F6 |
| 展开 / 收起 SQL 编辑器 | Ctrl+Alt+E | ⌥⌘E |
| 显示 / 隐藏日志 | Ctrl+Alt+L | ⌥⌘L |
| 专注模式 / 切换主题 | F11 / Ctrl+Alt+D | F11 / ⌥⌘D |
| 表列表 / 刷新 | F7 / F5 | F7 / F5 |
| 下一个 / 上一个查询标签 | Ctrl+Tab / Ctrl+Shift+Tab | Ctrl+Tab / Ctrl+Shift+Tab |
| 筛选条件（表浏览） | Ctrl+F | ⌘F |
| 上一页 / 下一页（表浏览） | Ctrl+PageUp / Ctrl+PageDown | Ctrl+PageUp / Ctrl+PageDown |
| 字段详情（表格） | Alt+Enter | Alt+Enter |
| 复制行 / 新增行（表格） | Ctrl+D / Ctrl+Shift+A | ⌘D / ⇧⌘A |
| 删除选中行（表格） | Ctrl+Delete | ⌘⌫ |
| 设为 NULL（表格） | Ctrl+0 | ⌘0 |
| 提交 / 撤销变更（表格） | Ctrl+Enter / Ctrl+Shift+Z | ⌘Enter / ⇧⌘Z |
| 图表 / 数据库工具 | Ctrl+Alt+G / Ctrl+Alt+B | ⌥⌘G / ⌥⌘B |
| 设置 | Ctrl+, | ⌘, |

删除连接、配置备份、检查更新和“关于”默认不占用组合键，可在设置中自行绑定。旧版本保存的自定义 `ExecuteGesture` / `CompletionGesture` 会在首次读取设置时自动迁移到新键位表。

## 在线编辑与事务提交

从对象树双击打开 **有主键的 MySQL / PostgreSQL / SQL Server 普通表**，自动进入可编辑的表浏览页。直接运行任意 SQL、视图、无主键表以及 TDengine 查询结果保持只读。

- 双击单元格修改内容；“新增行”“删除选中行”只暂存本地变更。
- “设为 NULL”写入数据库空值，和空字符串不同。
- 自增、IDENTITY、serial、计算列、rowversion 及二进制 / 数组字段保持只读。新增行不填写生成字段；未填写的其他字段由服务器默认值或 NULL 决定。空表也保留驱动返回的数据类型。
- “提交变更”确认后，将该页所有变更作为一个事务执行。SQL 全部参数化，UPDATE / DELETE 使用完整主键与原始行快照校验；影响行数不为 1 或任一命令失败会回滚整批修改。MySQL 表引擎必须支持事务。
- “撤销变更”恢复当前页尚未提交的新增、修改和删除。更换查询页保留草稿，关闭前会提示；翻页、执行其他 SQL、切换连接前需先提交或撤销。
- 提交成功后自动重新加载当前页，读取服务器生成的字段。若提交成功但刷新失败，不会再次发送已提交变更。
- 若提交或回滚的服务器回执丢失，界面会标记“提交结果未知”，阻止重复提交。先核对数据库，再点击“撤销变更”丢弃本地会话并重新加载；此操作不会回滚已经在服务器提交的事务。
- 终止操作会在执行期间取消并回滚；开始 COMMIT 后等待服务器回执，避免客户端主动取消造成状态不明。执行事务时需等任务结束后再关闭窗口。

并发检查采用数据库字段的等值比较语义。特殊类型若不支持等值比较，数据库会拒绝并回滚；带有特殊触发器、行级安全策略、级联操作或自定义类型的表仍需实库联调。此功能不提供跨查询页、跨连接的长期事务。

## 数据库配置

| 数据库 | 默认端口 | 驱动 / 协议 |
| --- | --- | --- |
| MySQL | 3306 | MySqlConnector |
| PostgreSQL | 5432 | Npgsql |
| SQL Server | 1433 | Microsoft.Data.SqlClient，可选 Windows 身份验证 |
| TDengine 3.x | 6041 | taosAdapter REST，需先启用服务器 taosAdapter |

PostgreSQL 每个数据库使用独立连接上下文；SQL Server 在元数据查询时切换目标数据库。SSL 开启时验证服务端证书，需正确配置证书信任。TDengine 的 SSL 开关选择 HTTPS。

## 本地数据

默认保存于当前用户的本地应用数据目录 `DbPilot`（Windows 为 `%LOCALAPPDATA%\DbPilot`）。可设置环境变量 `DBPILOT_DATA_DIR` 指定独立目录。

- `connections.json`：连接配置，数据库密码、SSH 密码和私钥口令经 AES-256-GCM 加密。
- `history.json`：查询历史，SQL 文本经 AES-256-GCM 加密。
- `secret.key`：随机生成的本机密钥。密钥与配置应一起备份，丢失后无法解密原密码和 SQL 历史。加密主要防止密码以明文出现；能读取密钥的本机账户仍可解密，应依赖账户和目录权限保护整个数据目录。
- `groups.json`、`settings.json`：分组、主题、编辑器和布局设置。
- `templates.json`、`logs.json`：自定义模板和最近 500 条日志，内容经 AES-256-GCM 加密。

保存配置和本地数据导出使用临时文件替换，失败或取消不以半成品覆盖原文件。解密失败会报告错误，不会自动清空配置。

## 查询边界

- 表浏览由 Provider 生成带分页条件的 SQL；手写 SQL 原样发送至服务器，默认最多展示 500 行。此上限限制客户端结果，不等同于限制服务器扫描量，查询大表时请主动加 LIMIT 或时间范围。
- ADO.NET 使用异步顺序读取；TDengine REST 响应设置 32 MB 硬上限。当前结果仍会缓存在内存中用于排序与导出，未实现任意规模结果的持续流式浏览。
- 支持多结果集、MySQL DELIMITER、PG dollar quote、SQL Server GO/GO N。每批默认最多显示 500 行，保留至多 100 项结果/批次记录并保留首个失败，单批次上限 64 MB。大文件恢复逐批读取。
- 关系型普通表的分页浏览支持上述暂存和事务提交；任意 SQL 查询结果保持只读。SQL 编辑器直接执行的修改语句沿用数据库默认提交行为，不进入网格的本地变更事务。
- WHERE 筛选是 SQL 表达式，基础检查会拒绝分号和注释符；此功能面向有数据库权限的使用者，并非 SQL 安全审计器。
- CREATE TABLE / INSERT / INDEX 是可编辑模板，建表模板不完整还原约束、默认表达式、权限、分区等数据库专属属性，执行前需核对。
- SQL 格式化会大写关键字，按子句换行缩进并拆分 SELECT / SET / VALUES 列表；字符串、标识符、注释、dollar quote 及 DELIMITER / GO 行保持原样，未实现完整 SQL 语法树格式化。
- OFFSET 分页会受表数据并发变化影响；SQL Server 表浏览采用无特定字段顺序的分页。有稳定排序需求时，请自行编写带唯一 ORDER BY 的查询。

## 项目结构

```text
src/DbPilot/
  Models/       连接、对象、查询页、设置模型
  Providers/    统一接口、Provider 工厂、关系型驱动与 TDengine REST
  Services/     加密持久化、SQL 高亮 / 格式化 / 补全、结果适配、编辑会话与参数化命令生成
  ViewModels/   异步任务、查询执行、导航与分组业务
  Views/        Avalonia 窗口、交互与系统文件选择器
tests/DbPilot.Checks/  可直接执行的回归检查和 Avalonia Headless UI 检查
```

核心业务位于 ViewModel / Provider / Service，文件选择、剪贴板和编辑器交互位于 View。后台任务支持取消，并统一记录中文错误前缀与数据库原始错误内容。

## 验证与发布

已检查加密、分页、结果类型、历史、TDengine 协议模拟，以及事务编辑、并发冲突回滚、取消、复合主键、提交结果未知、提交后刷新失败等路径。Avalonia Headless 检查覆盖真实控件、单元格渲染和双向编辑。事务协议通过 ADO.NET 测试替身验证，此环境没有四种数据库实例，尚未完成真实数据库联调、长时间压力测试、macOS / Linux 实机测试。

可设置 `DBPILOT_PREVIEW` 为 PNG 文件的绝对路径，让检查程序保存主窗口渲染图。

```powershell
dotnet publish src/DbPilot/DbPilot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
dotnet publish src/DbPilot/DbPilot.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
dotnet publish src/DbPilot/DbPilot.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/osx-arm64
```

macOS 的应用包、签名、公证，以及 Windows 安装器、代码签名尚未制作。

## 安装包与自动更新

GitHub Actions 支持生成 macOS ARM64 / Intel DMG、Windows x64 EXE 安装包，并通过 Sparkle / WinSparkle 从 GitHub Releases 更新。签名密钥、Apple 公证配置与发布步骤见 [自动更新与发布](docs/自动更新与发布.md)。
