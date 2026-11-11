using System.Net;
using System.Text.Json;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.ViewModels;

namespace DbPilot.Checks;

internal static class TDenginePagingChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        await ProviderChecks(check);
        ViewModelChecks(check);
    }
    private static async Task ProviderChecks(Action<bool,string> check)
    {
        var profile=new ConnectionProfile{Kind=DatabaseKind.TDengine,Host="localhost",Port=6041,Database="db",Username="root",Password="x"};
        var sqls=new List<string>();
        long countValue=12345,rankValue=450;
        await using var provider=new TDengineProvider(profile,new RecordingHandler(async(request,ct)=>{
            var sql=await request.Content!.ReadAsStringAsync(ct);sqls.Add(sql);
            if(sql.StartsWith("SHOW"))return TextRows("meters","meters2");
            if(sql.Contains("COUNT(*)"))return CountRow(sql.Contains("CAST(table_name AS VARCHAR(256)) <")?rankValue:countValue);
            if(sql.Contains("SELECT table_name"))return TextRows(Enumerable.Range(0,201).Select(i=>"meter_"+i.ToString("000")).ToArray());
            return TextRows();
        }));
        var page=await provider.TablesPageAsync("db","meters",null,400,200,CancellationToken.None);
        var pageSql=sqls[^1];
        check(pageSql.Contains("information_schema.ins_tables")&&pageSql.Contains("db_name = 'db'")&&pageSql.Contains("stable_name = 'meters'")&&pageSql.Contains("ORDER BY CAST(table_name AS VARCHAR(256))")&&pageSql.Contains("LIMIT 201")&&pageSql.Contains("OFFSET 400"),"TDengine subtable page SQL sorts by CAST name with bound and offset");
        check(page.Names.Count==200&&page.HasMore,"TDengine page size plus one detects more rows");
        sqls.Clear();await provider.TablesPageAsync("db",null,null,0,200,CancellationToken.None);
        check(sqls[^1].Contains("stable_name IS NULL")&&sqls[^1].Contains("stable_name = ''"),"ordinary tables use a NULL or empty stable filter");
        sqls.Clear();await provider.TablesPageAsync("db","meters","m1",0,200,CancellationToken.None);
        check(sqls[^1].Contains("table_name LIKE '%m1%'"),"subtable search is filtered on the server");
        check((await provider.CountTablesAsync("db","meters",null,CancellationToken.None))==12345,"table count reads COUNT from the server");
        check((await provider.TableRankAsync("db","meters","meter_5",CancellationToken.None))==450&&sqls[^1].Contains("CAST(table_name AS VARCHAR(256)) < 'meter_5'"),"table rank counts names before the target with the same sort key");
        check((await provider.StablesAsync("db",CancellationToken.None)).SequenceEqual(new[]{"meters","meters2"}),"stable list uses SHOW STABLES");
        var lite=await provider.ObjectsLiteAsync("db",CancellationToken.None);
        check(lite.Count(o=>o.Kind=="超级表")==2&&lite.Count(o=>o.Kind=="普通表")==201&&lite.All(o=>o.Kind!="子表"),"light metadata returns stables and ordinary tables without children");
        bool invalid=false;try{await provider.TablesPageAsync("db","meters",null,-1,200,CancellationToken.None);}catch(ArgumentOutOfRangeException){invalid=true;}
        check(invalid,"provider rejects negative offsets");

        var objects=new[]
        {
            new DbObject("db","","z","子表",Parent:"s"),new DbObject("db","","a","子表",Parent:"s"),
            new DbObject("db","","n","普通表"),new DbObject("db","","m","子表",Parent:"other")
        };
        var fallback=ObjectPaging.Slice(objects,"s",null,0,1);
        check(fallback.Names.SequenceEqual(new[]{"a"})&&fallback.HasMore,"in-memory paging sorts and reports more rows");
        check(ObjectPaging.Slice(objects,null,null,0,10).Names.SequenceEqual(new[]{"n"}),"in-memory ordinary tables exclude children");
        check(ObjectPaging.Count(objects,"s",null)==2&&ObjectPaging.Rank(objects,"s","z")==1,"in-memory count and rank stay in stable scope");
        check(ObjectPaging.Slice(objects,"s","z",0,10).Names.SequenceEqual(new[]{"z"}),"in-memory search filters by name");

        var plain=new PlainProvider();
        check((await ((IDbProvider)plain).ObjectsLiteAsync("db",CancellationToken.None)).Count==2,"light metadata falls back to full objects for non-paging providers");
    }
    private static void ViewModelChecks(Action<bool,string> check)
    {
        var fake=new FakePagedProvider();
        for(int i=0;i<600;i++)fake.Objects.Add(new DbObject("db","","meter_"+i.ToString("000"),"子表",Parent:"meters"));
        fake.Objects.Add(new DbObject("db","","meters","超级表"));
        fake.Objects.Add(new DbObject("db","","meters2","超级表"));
        fake.Objects.Add(new DbObject("db","","normal_a","普通表"));
        var vm=new MainViewModel(_=>fake);
        var profile=new ConnectionProfile{Kind=DatabaseKind.TDengine,Database="db"};
        var databaseNode=new ObjectNode{Label="db",Profile=profile,Database="db",Kind=ObjectNodeKind.Database};
        databaseNode.Children.Add(new(){Label="双击加载对象",Profile=profile,Kind=ObjectNodeKind.Hint,Loaded=true});
        vm.ExpandAsync(databaseNode).GetAwaiter().GetResult();
        check(databaseNode.Loaded&&databaseNode.Children.Any(c=>c.Label=="超级表")&&databaseNode.Children.Any(c=>c.Label=="普通表"),"TDengine database expansion builds a lightweight skeleton");
        check(databaseNode.Children.SelectMany(c=>c.Children).All(c=>c.Object?.Kind!="子表"),"skeleton contains no subtable detail nodes");
        var category=databaseNode.Children.Single(c=>c.IsLazyCategory&&c.ParentStable=="meters");
        check(category.Label=="子表 / meters"&&category.ParentStable=="meters"&&!category.Loaded,"lazy subtable category starts without counts");
        category.LoadedCount=5;
        check(category.Label=="子表 / meters (5)","category label shows loaded count before the total arrives");
        category.TotalCount=12;
        check(category.Label=="子表 / meters (5 / 12)","category label shows loaded and total counts");
        category.LoadedCount=0;category.TotalCount=null;

        vm.EnsureLazyCategoryAsync(category).GetAwaiter().GetResult();
        check(category.Children.Count(c=>c.Object is not null)==200&&category.HasMore&&category.Children.Any(c=>c.IsLoadMoreHint),"first expansion loads exactly one page and keeps the load-more entry");
        WaitFor(()=>category.TotalCount is not null);
        check(category.TotalCount==600&&category.Label.Contains("(200 / 600)"),"total count arrives asynchronously and updates the label");
        vm.LoadMoreObjectPageAsync(category).GetAwaiter().GetResult();
        var loaded=category.Children.Where(c=>c.Object is not null).Select(c=>c.Object!.Name).ToArray();
        check(loaded.Length==400&&loaded.Distinct(StringComparer.OrdinalIgnoreCase).Count()==400,"next page appends without duplicates");
        fake.DuplicateNextPage=true;
        vm.LoadMoreObjectPageAsync(category).GetAwaiter().GetResult();
        loaded=category.Children.Where(c=>c.Object is not null).Select(c=>c.Object!.Name).ToArray();
        check(loaded.Length==600&&loaded.Distinct(StringComparer.OrdinalIgnoreCase).Count()==600,"duplicate names returned by the server are ignored");
        fake.DuplicateNextPage=false;

        vm.ReloadLazyCategoryAsync(category).GetAwaiter().GetResult();
        check(category.Children.Count(c=>c.Object is not null)==200&&category.LoadedCount==200&&category.HasMore,"reload restarts from the first page");

        var gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Gate=gate.Task;
        var pending=vm.LoadMoreObjectPageAsync(category);
        Thread.Sleep(20);
        fake.Gate=null;
        vm.ReloadLazyCategoryAsync(category).GetAwaiter().GetResult();
        gate.SetResult(true);
        pending.GetAwaiter().GetResult();
        check(category.Children.Count(c=>c.Object is not null)==200&&category.LoadedCount==200,"late page results after a reset are discarded");

        fake.LastOffset=-1;
        var existing=vm.LocateSubtableAsync(category,new DbObject("db","","meter_000","子表",Parent:"meters")).GetAwaiter().GetResult();
        check(existing?.Object?.Name=="meter_000"&&fake.LastOffset==-1,"locating an already loaded subtable does not request a page");
        var located=vm.LocateSubtableAsync(category,new DbObject("db","","meter_450","子表",Parent:"meters")).GetAwaiter().GetResult();
        check(located?.Object?.Name=="meter_450"&&fake.LastOffset==400,"locating an unloaded subtable uses rank to load the containing page");

        vm.CancelObjectPaging();
        var states=(System.Collections.ICollection)typeof(MainViewModel).GetField("_lazyStates",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
        check(states.Count==0,"cancelling paging clears category state");
    }
    private static void WaitFor(Func<bool> condition)
    {
        for(int i=0;i<100&&!condition();i++)Thread.Sleep(10);
    }
    private static HttpResponseMessage TextRows(params string[] names)
        =>new(HttpStatusCode.OK){Content=new StringContent("{\"code\":0,\"column_meta\":[[\"table_name\",8,64]],\"data\":["+string.Join(",",names.Select(name=>"["+JsonSerializer.Serialize(name)+"]"))+"]}")};
    private static HttpResponseMessage CountRow(long value)
        =>new(HttpStatusCode.OK){Content=new StringContent("{\"code\":0,\"column_meta\":[[\"count(*)\",6,8]],\"data\":[["+value+"]]}")};

    private sealed class RecordingHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request,cancellationToken);
    }
    private sealed class FakePagedProvider : IDbProvider,IPagedObjectProvider
    {
        public List<DbObject> Objects { get; }=[];
        public bool DuplicateNextPage { get; set; }
        public Task? Gate { get; set; }
        public int LastOffset { get; set; }=-1;
        public Task TestAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>>(["db"]);
        public Task<IReadOnlyList<DbObject>> ObjectsAsync(string database,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DbObject>>(Objects.ToArray());
        public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ColumnInfo>>([new("ts","timestamp",false,false,"","")]);
        public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<IndexInfo>>([]);
        public Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct)=>Task.FromResult(new QueryResult([],[],0,false));
        public string Quote(string name)=>"`"+name.Replace("`","``")+"`";
        public string Qualified(DbObject table)=>Quote(table.Database)+"."+Quote(table.Name);
        public string PageSql(DbObject table,string filter,int page,int size)=>"SELECT * FROM "+Qualified(table);
        public Task<IReadOnlyList<string>> StablesAsync(string database,CancellationToken ct)
            =>Task.FromResult<IReadOnlyList<string>>(Objects.Where(o=>o.Kind=="超级表").Select(o=>o.Name).ToArray());
        public async Task<ObjectPage> TablesPageAsync(string database,string? stableName,string? search,int offset,int pageSize,CancellationToken ct)
        {
            LastOffset=offset;
            var gate=Gate;
            if(gate is not null)await gate;
            var scoped=Objects.Where(o=>stableName is null?o.Kind=="普通表":o.Kind=="子表"&&o.Parent==stableName);
            if(!string.IsNullOrWhiteSpace(search))scoped=scoped.Where(o=>o.Name.Contains(search,StringComparison.OrdinalIgnoreCase));
            var all=scoped.OrderBy(o=>o.Name,StringComparer.Ordinal).Select(o=>o.Name).ToList();
            var page=all.Skip(offset).Take(pageSize).ToList();
            if(DuplicateNextPage&&offset>0&&all.Count>0)page.Insert(0,all[offset-1]);
            return new(page,offset+pageSize<all.Count);
        }
        public Task<long> CountTablesAsync(string database,string? stableName,string? search,CancellationToken ct)
            =>Task.FromResult(ObjectPaging.Count(Objects,stableName,search));
        public Task<long> TableRankAsync(string database,string? stableName,string name,CancellationToken ct)
            =>Task.FromResult(ObjectPaging.Rank(Objects,stableName,name));
        public Task<IReadOnlyList<DbObject>> ObjectsLiteAsync(string database,CancellationToken ct)
            =>Task.FromResult<IReadOnlyList<DbObject>>(Objects.Where(o=>o.Kind is "超级表" or "普通表").ToArray());
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class PlainProvider : IDbProvider
    {
        public Task TestAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>>(["db"]);
        public Task<IReadOnlyList<DbObject>> ObjectsAsync(string database,CancellationToken ct)
            =>Task.FromResult<IReadOnlyList<DbObject>>([new("db","public","items","表"),new("db","public","v","视图")]);
        public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ColumnInfo>>([]);
        public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<IndexInfo>>([]);
        public Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct)=>Task.FromResult(new QueryResult([],[],0,false));
        public string Quote(string name)=>name;
        public string Qualified(DbObject table)=>table.Name;
        public string PageSql(DbObject table,string filter,int page,int size)=>"SELECT 1";
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
