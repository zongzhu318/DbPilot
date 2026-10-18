using System.Data;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.ViewModels;

namespace DbStudio.Checks;

internal static class EditingViewModelChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var profile=new ConnectionProfile{Kind=DatabaseKind.PostgreSQL,Database="sandbox"};
        var table=new DbObject("sandbox","public","items","表");
        var fake=new EditingProvider();var vm=new MainViewModel(_=>fake);
        vm.NewQuery(profile:profile,table:table);await vm.ExecuteAsync("SELECT * FROM items LIMIT 100",100,true);
        check(vm.Tab?.Edits is not null,"generated table browse initializes editing from metadata");
        vm.Tab!.Edits!.Data.Rows[0][1]="pending";
        await vm.BrowseAsync("id > 0",2,50);
        check(vm.Tab.Page==0 && vm.Tab.Edits.PendingCount==1,"pagination cannot discard staged edits");
        await vm.ExecuteAsync("SELECT 9");
        check(fake.Queries==1 && vm.Tab.Edits.PendingCount==1,"manual execution cannot discard staged edits");
        fake.ApplyError=new DBConcurrencyException("stale snapshot");await vm.CommitEditsAsync();
        check(vm.Tab.Edits?.PendingCount==1 && vm.Status.Contains("stale snapshot"),"failed transaction retains local changes");
        fake.ApplyError=null;fake.FailRefresh=true;await vm.CommitEditsAsync();
        check(fake.Applied==1 && vm.Tab.Edits is null && vm.Tab.Result!.Rows[0][1]!.Equals("pending"),"acknowledged commit clears pending batch before failed refresh");
        await vm.CommitEditsAsync();
        check(fake.Applied==1,"refresh failure cannot resubmit committed inserts or updates");
        fake.FailRefresh=false;await vm.ExecuteAsync("SELECT * FROM items LIMIT 100",100,true);
        vm.Tab.Edits!.Data.Rows[0][1]="again";
        fake.ApplyError=new EditCommitUncertainException("unknown commit",new IOException());await vm.CommitEditsAsync();
        check(vm.Tab.Edits?.CommitUncertain==true && vm.Tab.Edits.PendingCount==1,"unknown transaction outcome locks retained changes");
        vm.RevertEdits();check(vm.Tab.Edits!.CommitUncertain,"ordinary revert cannot hide an uncertain commit outcome");
        vm.DiscardUncertainSession();fake.ApplyError=null;await vm.ExecuteAsync("SELECT * FROM items LIMIT 100",100,true);
        check(vm.Tab.Edits?.PendingCount==0,"explicit reload recovers from uncertain session");
        await vm.ExecuteAsync("SELECT 1 AS id, 'untrusted' AS name");
        check(vm.Tab.Edits is null && vm.Tab.Table is null,"arbitrary SQL results never inherit table write privileges");
    }
    private sealed class EditingProvider : IDbProvider,IEditableDbProvider
    {
        public int Queries { get; private set; }
        public int Applied { get; private set; }
        public bool FailRefresh { get; set; }
        public Exception? ApplyError { get; set; }
        public Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct){Queries++;if(FailRefresh && Applied>0)throw new IOException("refresh unavailable");return Task.FromResult(new QueryResult(["id","name"],[new object?[]{1,"original"}],0,false,[typeof(int),typeof(string)]));}
        public Task<int> ApplyChangesAsync(DbObject table,IReadOnlyList<RowChange> changes,CancellationToken ct){if(ApplyError is not null)throw ApplyError;Applied++;return Task.FromResult(changes.Count);}
        public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ColumnInfo>>([new("id","integer",false,true,"",""),new("name","text",false,false,"","")]);
        public string PageSql(DbObject table,string filter,int page,int size)=>$"SELECT * FROM items LIMIT {size}";
        public Task TestAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DbObject>> ObjectsAsync(string database,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DbObject>>([]);
        public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<IndexInfo>>([]);
        public string Quote(string name)=>"\""+name+"\"";
        public string Qualified(DbObject table)=>Quote(table.Schema)+"."+Quote(table.Name);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
