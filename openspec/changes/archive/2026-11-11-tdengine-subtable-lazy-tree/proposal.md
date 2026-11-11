# Proposal

## Why

TDengine 的超级表下可能包含数万张子表。当前展开数据库节点时，应用会一次性查询全库 `information_schema.ins_tables`，为每张子表创建树节点，再交给未虚拟化的 TreeView 渲染，导致对象树长时间无响应甚至卡死；同样的全量查询还出现在表目录、SQL 补全与数据库工具窗口中。需要将 TDengine 元数据浏览改为服务端分页、按需加载，并提供超级表范围内的子表搜索与定位能力。

## What Changes

- 对象树展开数据库时只加载轻量骨架：超级表目录、普通表分类，以及每个超级表下的空子表分类，不预先加载子表明细。
- 子表分类按每页 200 条从服务端分页加载，使用稳定的名称排序与 `LIMIT/OFFSET`。
- 滚动到子表列表接近底部时自动加载下一页；保留“继续加载…”占位项，供无法滚动时显式触发。
- 分类标签显示已加载数量，总数通过独立异步查询补充；总数未返回时先展示已加载数。
- 超级表分类右键菜单新增“搜索子表…”，仅在该超级表范围内按子表名称模糊搜索，由服务端过滤。
- 搜索结果被选中后关闭搜索窗口，在对象树中逐级展开并定位到对应子表；目标页未加载时先加载再定位。
- 新增“重新加载子表”，并在刷新树、断开连接、关闭窗口时取消在途分页请求并清理分页状态。
- 表目录、SQL 补全与数据库工具窗口的 TDengine 元数据改用轻量、有界查询，仅返回超级表与普通表，并提示子表应在对象树中按超级表查看。
- 非 TDengine 连接保持现有浏览行为不变，不套用 TDengine 的分页骨架。

无 **BREAKING** 变更。

## Capabilities

### New Capabilities

- `tdengine-object-tree`: TDengine 对象树的子表分页懒加载、加载进度显示、超级表范围内模糊搜索与树中定位，以及表目录、SQL 补全、工具窗口的轻量元数据查询约束。

### Modified Capabilities

无。

## Impact

- Providers：`IDbProvider` 增加可选的分页对象能力；`TDengineProvider` 实现服务端分页、计数与轻量查询；`SshDbProvider` 透传或降级适配。
- Models / ViewModel：`ObjectNode` 增加分页与加载状态；`MainViewModel` 承担分类分页、去重、取消与重置逻辑。
- Views：`MainWindow` 的对象树、右键菜单、表目录、SQL 补全；新增子表搜索窗口；`DatabaseToolsWindow` 的对象加载与关系图。
- Tests：`tests/DbPilot.Checks` 增加离线行为检查与 TDengine live 检查。
- 服务端：依赖 TDengine 3.x `information_schema.ins_tables` 的 `WHERE`、`LIKE`、`ORDER BY`、`LIMIT/OFFSET`、`COUNT(*)` 支持。
- 不影响连接配置与工作区持久化格式。
