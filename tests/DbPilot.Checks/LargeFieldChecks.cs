using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;
using DbPilot.Views;

namespace DbPilot.Checks;

internal static class LargeFieldChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        check(LargeColumns.IsLarge("longtext",DatabaseKind.MySQL) && LargeColumns.IsLarge("varchar(2000)",DatabaseKind.MySQL) && !LargeColumns.IsLarge("varchar(255)",DatabaseKind.MySQL),"large-field detection follows MySQL types and declared length");
        check(LargeColumns.IsLarge("text",DatabaseKind.PostgreSQL) && LargeColumns.IsLarge("bytea",DatabaseKind.PostgreSQL) && !LargeColumns.IsLarge("character varying(255)",DatabaseKind.PostgreSQL),"large-field detection follows PostgreSQL types");
        check(LargeColumns.IsLarge("nvarchar(max)",DatabaseKind.SqlServer) && LargeColumns.IsLarge("varchar(2000)",DatabaseKind.SqlServer) && !LargeColumns.IsLarge("nvarchar(255)",DatabaseKind.SqlServer),"large-field detection follows SQL Server types");
        check(LargeColumns.IsLarge("varchar(2000)",DatabaseKind.TDengine) && !LargeColumns.IsLarge("binary(64)",DatabaseKind.TDengine),"large-field detection follows TDengine types");

        ColumnInfo[] schema=[new("id","bigint",false,true,"",""),new("body","longtext",true,false,"",""),new("name","varchar(80)",true,false,"",""),new("payload","longblob",true,false,"","")];
        check(LargeColumns.PreviewIndexes(schema,DatabaseKind.MySQL).SequenceEqual([1,3]),"primary keys are never downgraded to previews");

        foreach(var kind in new[]{DatabaseKind.MySQL,DatabaseKind.PostgreSQL,DatabaseKind.SqlServer}){
            ColumnInfo[] providerSchema=kind switch{
                DatabaseKind.PostgreSQL=>[new("id","bigint",false,true,"",""),new("body","text",true,false,"",""),new("name","character varying(80)",true,false,"",""),new("payload","bytea",true,false,"","")],
                DatabaseKind.SqlServer=>[new("id","bigint",false,true,"",""),new("body","nvarchar(max)",true,false,"",""),new("name","nvarchar(80)",true,false,"",""),new("payload","varbinary(max)",true,false,"","")],
                _=>schema
            };
            await using var provider=DbProviderFactory.Create(new ConnectionProfile{Kind=kind});
            var table=new DbObject("sandbox","public","docs","表");
            var browse=provider.BuildBrowse(table,providerSchema,"id > 0",1,50);
            check(browse.PreviewColumns.SequenceEqual([1,3]),kind+" browse marks preview columns");
            check(browse.Sql.Contains("body")&&browse.Sql.Contains("payload")&&browse.Sql.Contains("id > 0")&&(browse.Sql.Contains("LIMIT 50")||browse.Sql.Contains("FETCH NEXT 50 ROWS")),kind+" browse projects only large fields while keeping filters and paging");
            check(browse.Sql.Contains("LEFT(")||browse.Sql.Contains("SUBSTRING("),kind+" browse truncates large fields server-side");
            var plain=provider.BuildBrowse(table,[providerSchema[0],providerSchema[2]],"",0,50);
            check(plain.PreviewColumns.Count==0 && plain.Sql.Contains("SELECT *"),kind+" browse leaves ordinary tables unprojected");
        }
        await using(var td=DbProviderFactory.Create(new ConnectionProfile{Kind=DatabaseKind.TDengine})){
            ColumnInfo[] tdSchema=[new("ts","timestamp",false,false,"",""),new("note","varchar(2000)",true,false,"","")];
            var browse=td.BuildBrowse(new DbObject("meters","","m1","普通表"),tdSchema,"",0,50);
            check(browse.PreviewColumns.SequenceEqual([1]) && browse.Sql.Contains("SUBSTR(")&&browse.Sql.Contains("LIMIT 50"),"TDengine browse uses SUBSTR previews");
        }

        var editSchema=schema.Take(3).ToArray();
        var original=new QueryResult(["id","body","name"],[new object?[]{1,"PREVIEW-BODY","Alice"}],0,false,[typeof(long),typeof(string),typeof(string)],new[]{1});
        var session=new TableEditSession(original,editSchema);
        check(!session.CanEditColumn(1)&&session.CanEditColumn(2),"preview columns stay read-only while ordinary columns remain editable");
        session.Data.Rows[0][2]="Alice updated";
        var change=session.GetChanges().Single();
        check(change.PreviewColumns?.Contains("body")==true,"row changes carry preview column names");
        foreach(var kind in new[]{DatabaseKind.MySQL,DatabaseKind.PostgreSQL,DatabaseKind.SqlServer}){
            await using var provider=DbProviderFactory.Create(new ConnectionProfile{Kind=kind});
            var command=EditCommandBuilder.Build(provider,kind,new DbObject("sandbox","public","docs","表"),editSchema,[change]).Single();
            check(!command.Parameters.Any(p=>Equals(p.Value,"PREVIEW-BODY"))&&command.Parameters.Any(p=>Equals(p.Value,"Alice updated")),kind+" optimistic predicate skips truncated previews but keeps full originals");
        }

        var profile=new ConnectionProfile{Kind=DatabaseKind.PostgreSQL,Database="sandbox"};
        var tableInfo=new DbObject("sandbox","public","docs","表");
        var preview=new QueryResult(["id","body","name"],[
            new object?[]{1,"short","A"},
            new object?[]{2,new string('x',LargeColumns.PreviewLength),"B"},
            new object?[]{3,null,"C"}
        ],0,false,[typeof(long),typeof(string),typeof(string)],new[]{1});
        var tab=new QueryTab{Table=tableInfo,Connection=profile,TableColumns=editSchema,PreviewColumns=[1],Result=preview};
        var fake=new LargeValueProvider();
        var reader=LargeValueReader.Create(tab,preview,p=>fake,out var reason);
        check(reader is not null&&reason is null,"large-value reader initializes from preview metadata");
        var full=await reader!.ReadFullResultAsync(preview,CancellationToken.None);
        check(fake.Reads==1&&fake.LastKeys.SequenceEqual(new object?[]{2}),"only truncated rows trigger a primary-key read");
        check(full.Rows[0][1]!.Equals("short")&&full.Rows[1][1]!.Equals("FULL-BODY-2")&&full.Rows[2][1] is null,"full values replace previews without disturbing complete or NULL cells");
        check(full.PreviewColumns is {Count:0},"exported results no longer claim preview columns");
        await reader.DisposeAsync();

        ColumnInfo[] noKeySchema=[new("body","text",true,false,"",""),new("name","varchar(80)",true,false,"","")];
        var noKeyPreview=new QueryResult(["body","name"],[new object?[]{new string('x',LargeColumns.PreviewLength),"A"}],0,false,[typeof(string),typeof(string)],new[]{0});
        var missingKeyTab=new QueryTab{Table=tableInfo,Connection=profile,TableColumns=noKeySchema,PreviewColumns=[0],Result=noKeyPreview};
        check(LargeValueReader.Create(missingKeyTab,noKeyPreview,out var missing) is null&&missing!.Contains("主键"),"tables without primary keys cannot load full values");
    }
    public static void RunUi(MainWindow window,MainViewModel vm,QueryTab restore,Action<bool,string> check)
    {
        var profile=new ConnectionProfile{Kind=DatabaseKind.MySQL,Database="sandbox"};
        ColumnInfo[] columns=[new("id","bigint",false,true,"",""),new("body","longtext",true,false,"",""),new("name","varchar(80)",true,false,"","")];
        var body=new string('x',LargeColumns.PreviewLength);
        var result=new QueryResult(["id","body","name"],[new object?[]{1,body,"A"},new object?[]{2,"short","B"}],0,false,[typeof(long),typeof(string),typeof(string)],new[]{1});
        var tab=new QueryTab{Title="预览检查",Table=new DbObject("sandbox","public","docs","表"),Connection=profile,TableColumns=columns,PreviewColumns=[1],Result=result,ResultSets=[result]};
        tab.Edits=new TableEditSession(result,columns);
        vm.Tabs.Add(tab);vm.SelectTab(tab);Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var grid=window.FindControl<DataGrid>("Results")!;
        check(grid.Columns[1].IsReadOnly&&!grid.Columns[2].IsReadOnly,"preview grid column stays read-only while ordinary columns remain editable");
        var bodyLabel=grid.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==body);
        var nameLabel=grid.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text=="B");
        check(bodyLabel.MaxLines==1&&bodyLabel.TextTrimming==TextTrimming.CharacterEllipsis&&ToolTip.GetTip(bodyLabel) is not null,"preview cells are single-line with a full-value hint");
        check(nameLabel.MaxLines==1&&ToolTip.GetTip(nameLabel) is null,"ordinary cells stay single-line without a preview hint");
        tab.Edits=null;vm.Tabs.Remove(tab);vm.SelectTab(restore);Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
    private sealed class LargeValueProvider : IDbProvider
    {
        public int Reads { get; private set; }
        public object?[] LastKeys { get; private set; }=[];
        public Task<object?[]> ReadValuesAsync(DbObject table,IReadOnlyList<ColumnInfo> columns,IReadOnlyList<ColumnInfo> keyColumns,IReadOnlyList<object?> keyValues,CancellationToken ct)
        {
            Reads++;LastKeys=keyValues.ToArray();
            return Task.FromResult(columns.Select(c=>c.Name=="body"?(object?)("FULL-BODY-"+keyValues[0]):null).ToArray());
        }
        public Task TestAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DbObject>> ObjectsAsync(string database,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DbObject>>([]);
        public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ColumnInfo>>([]);
        public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<IndexInfo>>([]);
        public Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct)=>throw new NotSupportedException();
        public string Quote(string name)=>name;
        public string Qualified(DbObject table)=>table.Name;
        public string PageSql(DbObject table,string filter,int page,int size)=>"SELECT 1";
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
