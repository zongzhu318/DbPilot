# Design

## Context

当前对象树的展开逻辑集中在 `MainViewModel.ExpandAsync`：所有节点类型都通过全局 `Guard` 执行，数据库节点调用 `IDbProvider.ObjectsAsync` 后一次性分组并创建全部子节点；TDengine 的 `ObjectsAsync` 会对全库 `information_schema.ins_tables` 发起无稳定排序的全量查询。对象树使用普通 `TreeDataTemplate`，没有虚拟化，展开回调由 `TreeViewItem.IsExpandedProperty` 类处理器触发。表目录、SQL 补全与数据库工具窗口也直接调用 `ObjectsAsync`。详见 proposal.md 的动机说明。

约束：

- 现有 `Guard` 会设置全局 `Busy`，用于阻止并发 SQL 与界面操作，增量分页不应占用它。
- 非 TDengine 连接由 `RelationalProvider` 提供完整对象列表，行为不能改变。
- SSH 连接通过 `SshDbProvider` 包装底层 Provider，需要保持能力透传。
- TDengine 元数据来自 `information_schema.ins_tables`，该表 `table_name`、`db_name`、`stable_name` 可用于过滤与排序；普通表的 `stable_name` 在不同版本可能为 `NULL` 或空字符串。

## Goals / Non-Goals

**Goals:**

- 将 TDengine 子表浏览改为服务端分页，单次请求有明确上限。
- 首屏与增量加载不占用全局忙碌状态，不阻塞 SQL 执行。
- 提供分类级取消、重置、失败可重试与节点去重。
- 支持超级表范围内的服务端模糊搜索，并能定位未加载的目标页。
- 让表目录、SQL 补全、工具窗口使用有界轻量查询。

**Non-Goals:**

- 不引入 TreeView 虚拟化，改以分页限制已物化节点数。
- 不改变关系型数据库对象树的加载方式，也不为其新增分页。
- 不改变备份、迁移、导入导出逻辑。
- 不调整 TDengine 服务端配置或元数据表结构。

## Decisions

### 1. 以可选能力接口暴露分页，而非扩展 `IDbProvider` 必选成员

新增 `IPagedObjectProvider`（含 `StablesAsync`、`TablesPageAsync`、`CountTablesAsync`、`TableRankAsync`、`ObjectsLiteAsync`）与 `ObjectPage` 记录类型。UI 通过能力检测决定是否走 TDengine 骨架；`ObjectsLiteAsync` 以扩展方法提供，非分页 Provider 回退到 `ObjectsAsync`。

理由：关系型 Provider 不需要分页语义，强制实现会制造空实现；能力接口让 SSH 包装层可以按内层能力透传或降级。备选方案是给 `IDbProvider` 加默认方法，但仍需在每个 Provider 中决定查询语义，且会污染公共契约。

### 2. 子表分页查询与排序策略

- 超级表节点来自 `SHOW <db>.STABLES`（该命令本身有返回规模上限，符合超级表数量级）。
- 子表页查询：`SELECT table_name FROM information_schema.ins_tables WHERE db_name = ? AND stable_name = ? [AND table_name LIKE ?] ORDER BY CAST(table_name AS VARCHAR(256)) LIMIT 201 OFFSET n`。
- 普通表分类使用相同查询，但条件为 `stable_name IS NULL OR stable_name = ''`；若服务端不接受该表达式，回退为 `stable_name = ''`。
- 用 `LIMIT pageSize + 1` 判断 `HasMore`，避免额外计数。
- 总数使用独立 `COUNT(*)` 查询，异步补充。
- 定位目标子表使用 `TableRankAsync`，返回同范围内 `CAST(table_name AS VARCHAR(256))` 小于目标的子表数量，据此计算目标所在页偏移，保证与排序分页一致。

理由：TDengine 3.3.2 对 `information_schema.ins_tables` 会把裸列 `ORDER BY table_name` 的排序下推到 `LIMIT/OFFSET` 之后，导致任意 `LIMIT`/`OFFSET` 页面与全局名称顺序不一致（已在 1.6 万子表上验证：无 `LIMIT` 时全局有序，加 `LIMIT` 后页面乱序）。把排序键改为 `CAST(table_name AS VARCHAR(256))` 可阻止该下推，`LIMIT` 与 `OFFSET` 页面均与全局名序一致，且排名使用的比较表达式与排序键保持一致。备选方案是每次拉取全量名称后本地切片，虽绝对可靠但违背分页初衷；当前方案已在真实 TDengine 3.3.2 上验证首屏、深偏移（16000）、反向锚定与模糊搜索均正确。

### 3. 分类节点承载分页状态，使用哨兵节点表达可继续加载

扩展 `ObjectNode`：增加 `ParentStable`、`BaseLabel`、`IsLazyCategory`、`IsLoadMoreHint`、`LoadMoreOf`、`IsExpanded`、`IsLoading`、`LoadedCount`、`TotalCount`、`HasMore`。分类标签由基础标签与计数格式化生成；“继续加载…”作为 `IsLoadMoreHint` 哨兵，点击时触发同一分页方法。

理由：树模板绑定 `Children`，用哨兵节点可以复用现有模板与点击事件，不需要为分类引入新的控件类型。备选方案是滚动时仅依赖事件、不显示入口，但在列表不足一屏或滚动条不可用时无法继续加载。

### 4. 增量分页绕开全局 `Guard`，使用分类级取消与代次校验

为每个分类维护独立的 `CancellationTokenSource`、偏移量与已加载名称集合（按名称忽略大小写去重）。重置时先取消并清理，再写入新的集合与偏移。应用返回结果前校验该分类仍是当前代次，防止取消后的迟到响应追加节点。分页与计数使用较短超时，失败时记录日志并在分类内保留重试提示。

理由：`Guard` 是全局互斥的忙碌状态，分页会与 SQL 执行互相阻塞；分类级取消可以精确支持“重新加载子表”、刷新树、断开与关闭窗口。备选方案是复用 `Guard`，会把普通浏览变成阻塞式操作。

### 5. 滚动续页由 `ScrollViewer` 路由事件驱动

在对象树上注册 `ScrollViewer.ScrollChangedEvent`（`handledEventsToo: true`），当纵向偏移接近底部阈值时，查找最后一个已展开且 `HasMore` 的懒加载分类并加载下一页。

理由：TreeView 内部滚动由模板中的 `ScrollViewer` 处理，直接监听其路由事件无需改动控件模板。备选方案是监听 `TreeViewItem` 的 `Loaded` 或鼠标滚轮，前者无法感知滚动位置，后者会漏掉拖拽滚动与键盘滚动。

### 6. 搜索使用独立模态窗口，结果定位复用分页状态

搜索窗口按超级表与数据库克隆连接配置，输入去抖后在服务端执行带 `LIKE '%keyword%'` 的分页查询；回车或按钮确认选中结果。主窗口收到结果后确保连接、数据库、分类节点逐级展开；若目标不在已加载集合中，使用 `TableRankAsync` 计算页码，加载对应页后选中并滚动到可见区域。

理由：复用分页接口避免客户端过滤全量数据；按序号定位比逐页扫描更稳定，长途翻页成本从 O(n/200) 降为常数级。备选方案是把搜索结果单独作为一个列表展示而不定位树，与已确认的交互不符。

### 7. 辅助界面统一走轻量有界查询

表目录、SQL 补全、工具窗口对象加载与关系图改用 `ObjectsLiteAsync`，TDengine 下返回超级表与普通表并在界面提示子表需在对象树中查看；SQL 生成与表结构读取仍针对用户已选择的具体对象，不受影响。

理由：这些界面只需要对象名称集合，不需要子表明细；统一入口可避免同类全量查询回归。备选方案是逐处添加 `Kind` 过滤，仍会先拉取全量子表，无法解决性能问题。

### 8. SSH 包装层按能力透传，UI 仅对 TDengine 应用骨架

`SshDbProvider` 实现 `IPagedObjectProvider`：内层支持分页时直接委托，否则在内存中完成过滤、排序与切片；UI 的懒加载骨架分支显式限定 `DatabaseKind.TDengine`。关系型数据库即使经 SSH 连接也继续使用原有一次性加载。

理由：避免因 SSH 包装导致关系型数据库被误套分页骨架，同时保留 TDengine over SSH 的分页能力。

## Risks / Trade-offs

- [TDengine 版本差异导致 `stable_name IS NULL` 不可用] → 首次失败时回退到 `stable_name = ''`，并在日志中记录。
- [`COUNT(*)` 在超大元数据表上较慢] → 总数异步查询、按分类缓存、随分类取消，不阻塞分页展示。
- [滚动事件高频触发造成重复请求] → `IsLoading` 与 `HasMore` 双重门禁，分类级取消，结果应用前校验代次。
- [200 条节点仍未虚拟化，超大页可能布局变慢] → 页大小作为常量集中配置，必要时可下调；虚拟化列为后续独立优化。
- [定位时元数据并发变化导致页码偏移] → 排序稳定；即使目标移动，加载页后若未命中则回退到按名称搜索刷新并提示重试。
- [SSH 每页额外往返增加延迟] → 分页请求有界且仅在展开/滚动时触发；总数单独异步，不影响首屏。
- [重新加载与在途请求竞争] → 重置前取消并清空状态，代次校验拒绝迟到结果。

## Migration Plan

- 无数据迁移；连接配置与工作区文件格式不变。
- 变更仅影响 TDengine 的元数据浏览路径，关系型路径保持原逻辑；如需回退，恢复相关 Provider、ViewModel 与视图改动即可。
- 上线后在包含大量子表的 TDengine 库验证首屏、滚动续页、搜索定位与断开清理。

## Open Questions

无。
