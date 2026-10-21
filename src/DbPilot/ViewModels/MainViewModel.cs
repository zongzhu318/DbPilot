using System.Collections.ObjectModel;
using System.Diagnostics;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;

namespace DbPilot.ViewModels;
public sealed partial class MainViewModel : Observable
{
    private readonly Func<ConnectionProfile,IDbProvider> _createProvider;
    public MainViewModel(Func<ConnectionProfile,IDbProvider>? providerFactory=null)=>_createProvider=providerFactory??DbProviderFactory.Create;
    public WorkspaceStore Store { get; } = new();
    public ObservableCollection<ConnectionProfile> Connections { get; } = [];
    public ObservableCollection<string> Groups { get; } = [];
    public ObservableCollection<ObjectNode> Nodes { get; } = [];
    public ObservableCollection<QueryTab> Tabs { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<QueryHistoryEntry> History { get; } = [];
    public AppSettings Settings { get; private set; } = new();
    private ConnectionProfile? _connection;
    public ConnectionProfile? Connection { get => _connection; set {if(Set(ref _connection,value))Changed(nameof(ConnectionStatus));} }
    private readonly HashSet<Guid> _connected=[];
    private readonly HashSet<Guid> _disconnected=[];
    public string ConnectionStatus=>Connection is null?"未选择连接":_disconnected.Contains(Connection.Id)?"已断开":_connected.Contains(Connection.Id)?"已连接（定时检测）":"尚未连接";
    private void Connected(ConnectionProfile profile,bool reopen=false){if(_disconnected.Contains(profile.Id)&&!reopen)return;_connected.Add(profile.Id);_disconnected.Remove(profile.Id);Changed(nameof(ConnectionStatus));UpdateConnectionNodes(profile);}
    private void EnsureConnected(ConnectionProfile profile){if(_disconnected.Contains(profile.Id))throw new InvalidOperationException("该连接已断开，请先点击连接。");_usedProfiles[(profile.Id,profile.Database)]=CloneForDatabase(profile,null);}
    public void Disconnect(){if(Connection is not {} profile)return;Cancel();_connected.Remove(profile.Id);_disconnected.Add(profile.Id);Changed(nameof(ConnectionStatus));UpdateConnectionNodes(profile,true);foreach(var p in _usedProfiles.Where(p=>p.Key.Item1==profile.Id).Select(p=>p.Value).Concat(Tabs.Where(t=>t.Connection?.Id==profile.Id).Select(t=>t.Connection!)).Append(profile).DistinctBy(p=>p.Database)){if(p.Kind!=DatabaseKind.TDengine)try{RelationalProvider.ClearPool(p);}catch(Exception ex){Log("连接池释放失败："+ex.Message,"Warn");}}Log("已断开 · 已关闭连接 "+profile.Name);}
    public Task ConnectAsync()=>Guard(async ct=>{var profile=Connection??throw new InvalidOperationException("请选择连接。");await using var provider=_createProvider(profile);await provider.TestAsync(ct);Connected(profile,true);Log("连接成功 · "+profile.Name);});
    public async Task HeartbeatAsync()
    {
        if(Busy||Connection is not {} profile||!_connected.Contains(profile.Id))return;
        await Guard(async ct=>{try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(15,profile.Timeout)));await using var provider=_createProvider(profile);await provider.TestAsync(timeout.Token);}catch{_connected.Remove(profile.Id);_disconnected.Add(profile.Id);Changed(nameof(ConnectionStatus));UpdateConnectionNodes(profile,true);throw;}});
    }
    private QueryTab? _tab;
    public QueryTab? Tab { get => _tab; set {if(Set(ref _tab,value))Changed(nameof(HasTab));} }
    public bool HasTab=>_tab is not null;
    private string _status = "就绪 · 请新建或打开连接";
    public string Status { get=>_status; set=>Set(ref _status,value); }
    private bool _busy;
    public bool Busy { get=>_busy; private set=>Set(ref _busy,value); }
    private CancellationTokenSource? _operation;
    public event Action<QueryResult?>? ResultChanged;
    public int? LastScriptErrorLine { get; private set; }
    public async Task InitializeAsync()
    {
        await Guard(async ct => {
            Settings = await Store.LoadSettingsAsync();
            if(Settings.PersistLogs)foreach(var entry in await Store.LoadLogsAsync())Logs.Add(entry);
            foreach(var group in await Store.LoadGroupsAsync()) if(!Groups.Contains(group)) Groups.Add(group);
            foreach(var p in await Store.LoadConnectionsAsync()) { Connections.Add(p); if(!Groups.Contains(p.Group)) Groups.Add(p.Group); }
            foreach(var item in await Store.LoadHistoryAsync()) History.Add(item);
            RebuildTree();
        });
    }
    public void Log(string message,string level="Info") { Status = message; Logs.Insert(0,$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}]  {message}"); while (Logs.Count>500) Logs.RemoveAt(Logs.Count-1); }
    public async Task Guard(Func<CancellationToken,Task> action)
    {
        if (Busy) { Log("已有任务运行，请等待或终止当前任务。"); return; }
        Busy = true; using var source = new CancellationTokenSource(); _operation = source;
        try { await action(source.Token); }
        catch (OperationCanceledException) { Log("操作已取消。","Warn"); }
        catch (Exception ex) { Log($"操作失败：{ex.Message}","Error"); }
        finally { if(Settings.PersistLogs)try{await Store.SaveLogsAsync(Logs.ToArray());}catch(Exception ex){Log("日志保存失败："+ex.Message,"Warn");}_operation = null; Busy = false; }
    }
    public void Cancel() => _operation?.Cancel();
    public bool HasNoConnections=>Connections.Count==0&&Nodes.Count==0;
    public void RebuildTree(string filter = "")
    {
        Nodes.Clear();
        foreach(var p in Connections.Where(p=>string.IsNullOrWhiteSpace(filter) || p.Name.Contains(filter,StringComparison.OrdinalIgnoreCase))) {
            var node = new ObjectNode { Label = ConnectionNodeLabel(p), Profile = p, IsConnection=true, Kind=ObjectNodeKind.Connection, IsOpen=_connected.Contains(p.Id), IconColor=ConnectionIconColor(p) };
            node.Children.Add(new ObjectNode { Label="双击加载数据库",Profile=p,Kind=ObjectNodeKind.Hint,Loaded=true });
            Nodes.Add(node);
        }
        Changed(nameof(HasNoConnections));
    }

    public async Task ExpandAsync(ObjectNode node) => await Guard(async ct =>
    {
        EnsureConnected(node.Profile);
        Connection = node.Profile;
        if (node.Loaded) return;
        node.Children.Clear(); node.Children.Add(new ObjectNode { Label="正在加载…",Profile=node.Profile,Kind=ObjectNodeKind.Hint,Loaded=true });
        try {
            var profile = CloneForDatabase(node.Profile,node.Database);
            await using var provider = _createProvider(profile);
            if(node.Object is {} table){
                var columns=await provider.ColumnsAsync(table,ct);node.Children.Clear();
                foreach(var column in columns)node.Children.Add(new(){Label=$"{(column.IsTag?"标签":column.PrimaryKey?"主键":"字段")}  {column.Name} : {column.Type}",Profile=node.Profile,Database=node.Database,Kind=ObjectNodeKind.Column,Loaded=true});
            } else if(node.Database is null) {
                var databases = await provider.DatabasesAsync(ct); node.Children.Clear();
                foreach(var database in databases) { var child = new ObjectNode { Label=database, Profile=node.Profile,Database=database,Kind=ObjectNodeKind.Database }; child.Children.Add(new ObjectNode { Label="双击加载对象",Profile=node.Profile,Kind=ObjectNodeKind.Hint,Loaded=true }); node.Children.Add(child); }
            } else {
                var objects = await provider.ObjectsAsync(node.Database,ct); node.Children.Clear();
                foreach(var schema in objects.GroupBy(o=>o.Parent.Length>0?o.Kind+" / "+o.Parent:string.IsNullOrWhiteSpace(o.Schema) ? o.Kind : o.Schema + " / " + o.Kind)) {
                    var category = new ObjectNode { Label=schema.Key,Profile=node.Profile,Database=node.Database,Kind=ObjectNodeKind.Category,Loaded=true };
                    foreach(var obj in schema){
                        bool tableObject=obj.Kind is "表" or "视图" or "普通表" or "子表" or "超级表";
                        var item=new ObjectNode { Label=obj.Name+(obj.Signature.Length>0?"("+obj.Signature+")":""),Profile=node.Profile,Database=node.Database,Object=obj,Loaded=!tableObject,
                            Kind=tableObject ? (obj.Kind=="视图"?ObjectNodeKind.View:ObjectNodeKind.Table) : ObjectNodeKind.Object };
                        if(tableObject)item.Children.Add(new(){Label="展开查看字段",Profile=node.Profile,Kind=ObjectNodeKind.Hint,Loaded=true});category.Children.Add(item);
                    }
                    node.Children.Add(category);
                }
            }
            node.Loaded=true;Connected(node.Profile); Log($"已加载 {node.Label}");
        } catch { node.Children.Clear(); throw; }
    });
    public static ConnectionProfile CloneForDatabase(ConnectionProfile profile,string? database)
    {
        var clone = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        if(database is not null) clone.Database=database; return clone;
    }
    public void NewQuery(string? sql=null, ConnectionProfile? profile=null, DbObject? table=null)
    {
        var tab = new QueryTab { Title=table?.Name ?? $"查询 {Tabs.Count+1}",Connection=profile ?? CurrentProfile,Table=table };
        if(sql is not null) tab.Sql=sql;if(table is not null)tab.SavedSql=tab.Sql; Tabs.Add(tab); Tab=tab;
    }
    public void SelectTab(QueryTab? tab) { Tab=tab; if(tab?.Connection is not null) Connection=Connections.FirstOrDefault(c=>c.Id==tab.Connection.Id); ResultChanged?.Invoke(tab?.Result); }
    public async Task ExecuteAsync(string sql,int limit=500,bool tableBrowse=false,IReadOnlyList<ColumnInfo>? browseColumns=null,IReadOnlyList<int>? previewColumns=null) => await Guard(async ct =>
    {
        var tab=Tab ?? throw new InvalidOperationException("请新建查询。");
        LastScriptErrorLine=null;
        EnsureNoPendingEdits(tab);
        var profile=tab.Connection ?? Connection ?? throw new InvalidOperationException("请先选择连接。");
        EnsureConnected(profile);
        tab.Connection=profile;
        await using var provider=_createProvider(profile); var watch=Stopwatch.StartNew();
        Log($"正在执行 · {profile.Name}");
        QueryResult result;
        bool scriptFailed=false;
        if(tableBrowse){result=await provider.QueryAsync(sql,limit,ct);if(previewColumns is {Count:>0})result=result with{PreviewColumns=previewColumns};tab.ResultSets=[result];}
        else {
            using var reader=new StringReader(sql);
            var outcomes=await ScriptRunner.RunAsync(provider,reader,profile.Kind,Settings.ContinueOnScriptError,limit,null,ct,capReadOnlyQueries:true);
            foreach(var outcome in outcomes)if(outcome.Error is {} error){LastScriptErrorLine??=outcome.Line;scriptFailed=true;Log($"批次 {outcome.Batch} · 第 {outcome.Line} 行：{error}","Error");}
            tab.ResultSets=outcomes.SelectMany(o=>o.Results).TakeLast(100).ToArray();
            result=tab.ResultSets.LastOrDefault()??new QueryResult([],[],0,false);
        }
        tab.ResultIndex=Math.Max(0,tab.ResultSets.Count-1);tab.Result=result;tab.Edits=null;
        if(tableBrowse){tab.SavedSql=sql;tab.BrowseSql=sql;tab.PageSize=limit;tab.TableColumns=browseColumns;tab.PreviewColumns=previewColumns??[];await PrepareEditsAsync(provider,tab,result,browseColumns,ct);}
        else{tab.Table=null;tab.BrowseSql="";tab.TableColumns=null;tab.PreviewColumns=null;}
        if (ReferenceEquals(Tab,tab)) ResultChanged?.Invoke(result);
        Log($"{(scriptFailed?"执行结束（存在失败批次）":"执行完成")} · {watch.ElapsedMilliseconds} ms · {tab.ResultSets.Count} 个结果 · {result.Rows.Count} 行 · 受影响 {Math.Max(0,result.Affected)} 行" + (result.Truncated ? $" · 结果超过 {limit} 行，已截取前 {limit} 行（SQL 未改动）" : ""));
        if(scriptFailed)return;
        Connected(profile);
        History.Insert(0,new(DateTimeOffset.Now,profile.Id,profile.Name,sql,watch.ElapsedMilliseconds,result.Rows.Count,profile.Database));
        while(History.Count>200)History.RemoveAt(History.Count-1);
        try{await Store.SaveHistoryAsync(History);}catch(Exception ex){Log("查询已完成，但历史保存失败："+ex.Message);}
    });
    public void SelectResult(int index)
    {
        if(Busy || Tab is not {} tab || index<0 || index>=tab.ResultSets.Count || index==tab.ResultIndex)return;
        try{EnsureNoPendingEdits(tab);tab.ResultIndex=index;tab.Result=tab.ResultSets[index];ResultChanged?.Invoke(tab.Result);}
        catch(Exception ex){Log(ex.Message);}
    }
    public Task ExplainAsync(string sql)=>Guard(async ct=>{
        var profile=Tab?.Connection??Connection??throw new InvalidOperationException("请选择连接。");EnsureConnected(profile);
        using var reader=new StringReader(sql);var batches=new List<ScriptBatch>();await foreach(var batch in SqlScript.ReadAsync(reader,profile.Kind,ct))batches.Add(batch);
        if(batches.Count!=1)throw new InvalidOperationException("请选中单个语句或批次查看执行计划。");
        await using var provider=_createProvider(profile);var results=await provider.ExplainAsync(batches[0].Sql,ct);
        NewQuery(sql,profile);Tab!.Title="执行计划";Tab.ResultSets=results;Tab.ResultIndex=0;Tab.Result=results.FirstOrDefault();ResultChanged?.Invoke(Tab.Result);Log("执行计划已读取（未启用 ANALYZE）。");
    });
    public async Task OpenTableAsync(ObjectNode node,int size)
    {
        if(Busy){Log("请等待或终止当前任务后再打开表。");return;}
        if(node.Object is null) return;
        if(node.Object.Kind is not ("表" or "视图" or "超级表" or "普通表" or "子表")) { Log("该对象不可直接浏览，请在 SQL 编辑器中调用或查看定义。"); return; }
        if(IsClosed(node.Profile)){Log("请先打开连接再浏览数据。");return;}
        var profile=CloneForDatabase(node.Profile,node.Database);
        var existing=Tabs.FirstOrDefault(t=>t.Connection?.Id==profile.Id&&t.Connection.Database==profile.Database&&t.Table==node.Object);
        if(existing is not null){SelectTab(existing);Log("已切换到表浏览页 · "+node.Object);return;}
        await using var provider=_createProvider(profile);
        IReadOnlyList<ColumnInfo> columns;BrowseQuery browse;
        try{columns=await provider.ColumnsAsync(node.Object,CancellationToken.None);browse=provider.BuildBrowse(node.Object,columns,"",0,size);}
        catch(Exception ex){Log("读取表结构失败："+ex.Message,"Error");return;}
        NewQuery(browse.Sql,profile,node.Object);
        Tab!.TableColumns=columns;Tab.PreviewColumns=browse.PreviewColumns;
        await ExecuteAsync(browse.Sql,size,true,columns,browse.PreviewColumns);
    }
    public async Task BrowseAsync(string filter,int page,int size)
    {
        if(Busy){Log("已有任务运行，请等待或终止当前任务。");return;}
        if(Tab?.Edits is {} edits && (edits.PendingCount>0 || edits.CommitUncertain)){Log("请先提交或撤销未提交的数据改动；提交结果未知时请先核对数据库。");return;}
        if(Tab?.Table is not {} table) { Log("请先单击表节点打开数据浏览。"); return; }
        if (filter.Contains(';') || filter.Contains("--") || filter.Contains("/*")) { Log("筛选条件仅接受单个 WHERE 表达式。"); return; }
        await using var provider=_createProvider(Tab.Connection!);
        Tab.Page=Math.Max(0,page);Tab.Filter=filter;
        var columns=Tab.TableColumns;
        var browse=columns is null ? new BrowseQuery(provider.PageSql(table,filter,Tab.Page,size),[]) : provider.BuildBrowse(table,columns,filter,Tab.Page,size);
        Tab.Sql=browse.Sql;Tab.PreviewColumns=browse.PreviewColumns;
        await ExecuteAsync(Tab.Sql,size,true,columns,browse.PreviewColumns);
    }
    private static void EnsureNoPendingEdits(QueryTab tab)
    {
        if(tab.Edits is {} edits && (edits.PendingCount>0 || edits.CommitUncertain))throw new InvalidOperationException("请先提交或撤销数据改动；提交结果未知时请先核对数据库并重新加载。");
    }
    private async Task PrepareEditsAsync(IDbProvider provider,QueryTab tab,QueryResult result,IReadOnlyList<ColumnInfo>? columns,CancellationToken ct)
    {
        if(provider is not IEditableDbProvider || tab.Table is not {Kind:"表"} table)return;
        try{tab.Edits=new TableEditSession(result,columns??await provider.ColumnsAsync(table,ct));}
        catch(OperationCanceledException){throw;}
        catch(Exception ex){Log("结果保持只读："+ex.Message);}
    }
    public async Task CommitEditsAsync() => await Guard(async ct=>{
        var tab=Tab??throw new InvalidOperationException("请选择表浏览页。");
        EnsureConnected(tab.Connection??throw new InvalidOperationException("请选择连接。"));
        var edits=tab.Edits??throw new InvalidOperationException("当前结果不可编辑。");
        var changes=edits.GetChanges();if(changes.Count==0){Log("没有需要提交的数据改动。");return;}
        await using var provider=_createProvider(tab.Connection!);
        if(provider is not IEditableDbProvider editable)throw new InvalidOperationException("该数据库不支持事务编辑。");
        int affected;
        try{affected=await editable.ApplyChangesAsync(tab.Table!,changes,ct);}
        catch(EditCommitUncertainException){edits.CommitUncertain=true;ResultChanged?.Invoke(tab.Result);throw;}
        // Acknowledged commit is final even if the subsequent refresh fails. Never retry this batch.
        tab.Result=edits.AcceptCommitted();tab.Edits=null;
        if(ReferenceEquals(Tab,tab))ResultChanged?.Invoke(tab.Result);
        try{
            var refreshed=await provider.QueryAsync(tab.BrowseSql,tab.PageSize,ct);
            if(tab.PreviewColumns is {Count:>0})refreshed=refreshed with{PreviewColumns=tab.PreviewColumns};
            tab.Result=refreshed;
            await PrepareEditsAsync(provider,tab,refreshed,tab.TableColumns,ct);
            if(ReferenceEquals(Tab,tab))ResultChanged?.Invoke(refreshed);
            Log($"事务提交成功 · {affected} 行变更 · 数据已刷新。");
        }catch(Exception ex){Log($"事务已提交成功（{affected} 行），刷新失败：{ex.Message}。请重新加载表，不要重复提交。");}
    });
    public void RevertEdits()
    {
        if(Busy){Log("请等待当前任务完成。");return;}
        try{Tab?.Edits?.Revert();Log("已撤销本地未提交改动，数据库未发生修改。");}catch(Exception ex){Log(ex.Message);}
    }
    public void DiscardUncertainSession()
    {
        if(Busy)return;
        if(Tab is {} tab){tab.Edits=null;tab.Result=null;ResultChanged?.Invoke(null);Log("已丢弃本地待确认会话，请重新加载表核对服务器数据。");}
    }
    public async Task SaveProfileAsync(ConnectionProfile profile,Guid? replace=null)
    {
        await Guard(async ct=> {
            var next=Connections.Where(p=>p.Id!=replace).Append(profile).ToList();
            await Store.SaveConnectionsAsync(next); Connections.Clear(); foreach(var p in next) Connections.Add(p);
            Connection=profile; RebuildTree(); Log("连接配置已加密保存。");
        });
    }
    public async Task DeleteProfileAsync(ConnectionProfile profile) => await Guard(async ct=> {
        var next=Connections.Where(p=>p.Id!=profile.Id).ToList(); await Store.SaveConnectionsAsync(next);
        Connections.Remove(profile); Connection=null; RebuildTree(); Log("连接已删除。");
    });
}
