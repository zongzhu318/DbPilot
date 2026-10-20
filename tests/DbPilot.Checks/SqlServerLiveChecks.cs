using System.Data;
using System.Text;
using System.Text.Json;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;

namespace DbPilot.Checks;
internal static class SqlServerLiveChecks
{
    public static async Task<int> Run()
    {
        var profile=new ConnectionProfile{Kind=DatabaseKind.SqlServer,Host=Environment.GetEnvironmentVariable("DBPILOT_SQLSERVER_HOST")??"192.168.127.128",Port=int.Parse(Environment.GetEnvironmentVariable("DBPILOT_SQLSERVER_PORT")??"1433"),Username="sa",Password=Environment.GetEnvironmentVariable("DBPILOT_SQLSERVER_PASSWORD")??throw new Exception("Set DBPILOT_SQLSERVER_PASSWORD"),Database="master",Timeout=8,UsePooling=false};
        string suffix=DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"_"+Guid.NewGuid().ToString("N")[..8],database="dbpilot_test_"+suffix,restored="dbpilot_restore_"+suffix;
        var owned=new HashSet<string>();var results=new List<object>();int passed=0,failures=0;string version="unknown";using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=deadline.Token;
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        async Task Test(string name,Func<Task> action){try{await action();passed++;Console.WriteLine("PASS "+name);results.Add(new{name,status="passed"});}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);results.Add(new{name,status="failed",error=ex.Message});}}
        await using var admin=DbProviderFactory.Create(profile);
        try{
            await admin.TestAsync(ct);version=(await admin.QueryAsync("SELECT @@VERSION",1,ct)).Rows[0][0]?.ToString()??"unknown";Console.WriteLine("SERVER "+version);
            await Test("Authentication, database list and Unicode",async()=>{Check((await admin.DatabasesAsync(ct)).Count>0,"database list");Check((await admin.QueryAsync("SELECT N'中文🚀'",1,ct)).Rows[0][0]?.ToString()=="中文🚀","Unicode");});
            foreach(var name in new[]{database,restored}){await admin.QueryAsync(DatabaseDdl.CreateDatabase(admin,profile.Kind,name),1,ct);owned.Add(name);}
            var selected=MainViewModel.CloneForDatabase(profile,database);await using var db=DbProviderFactory.Create(selected);var table=new DbObject(database,"dbo","items","表");string q=db.Qualified(table);
            await db.QueryAsync($"CREATE TABLE dbo.parents(id int PRIMARY KEY); INSERT INTO dbo.parents VALUES(1); CREATE TABLE {q}(id int IDENTITY(1,1) PRIMARY KEY,parent_id int NULL REFERENCES dbo.parents(id),name nvarchar(80) NOT NULL,qty int NOT NULL DEFAULT 7,amount decimal(12,3),flag bit,payload varbinary(20),happened datetime2(6),doubled AS (qty*2) PERSISTED,rv rowversion); CREATE INDEX idx_name ON {q}(name)",1,ct);
            await Test("Table/view/index/identity/computed/rowversion/FK metadata",async()=>{
                await db.QueryAsync($"CREATE VIEW dbo.item_view AS SELECT id,name FROM {q}",1,ct);var fields=await db.ColumnsAsync(table,ct);Check(fields.Single(c=>c.Name=="id").IsIdentity&&fields.Single(c=>c.Name=="doubled").GenerationExpression.Length>0&&fields.Single(c=>c.Name=="rv").IsGenerated,"generated fields");Check((await db.ObjectsAsync(database,ct)).Any(o=>o.Kind=="视图"&&o.Name=="item_view"),"view");Check((await db.IndexesAsync(table,ct)).Any(i=>i.Kind=="主键"),"index");Check((await db.ForeignKeysAsync(database,ct)).Count>0,"FK");Check((await db.DefinitionAsync(table,ct)).Contains("CREATE TABLE"),"definition");
            });
            Dictionary<string,object?> Snapshot(QueryResult r)=>r.Columns.Select((name,i)=>(name,r.Rows[0][i])).ToDictionary(p=>p.name,p=>p.Item2);
            RowChange Insert(string name,int parent=1)=>new(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name",name},{"parent_id",parent},{"amount",12.375m},{"flag",true},{"payload",new byte[]{0,255}},{"happened",new DateTime(2026,10,18,12,0,0).AddTicks(1234560)}});
            await Test("Typed inserts, defaults, update, rowversion conflict and batch rollback",async()=>{
                var edit=(IEditableDbProvider)db;Check(await edit.ApplyChangesAsync(table,[Insert("中文🚀 O'Reilly")],ct)==1,"insert");var r=await db.QueryAsync("SELECT * FROM items",1,ct);Check(Convert.ToInt32(r.Rows[0][3])==7,"default");var original=Snapshot(r);await edit.ApplyChangesAsync(table,[new(RowChangeKind.Update,original,new Dictionary<string,object?>{{"name","updated"},{"payload",null}})],ct);
                bool conflict=false;try{await edit.ApplyChangesAsync(table,[new(RowChangeKind.Delete,original,new Dictionary<string,object?>())],ct);}catch(DBConcurrencyException){conflict=true;}Check(conflict,"stale delete");bool failed=false;try{await edit.ApplyChangesAsync(table,[Insert("rollback"),Insert("bad",999)],ct);}catch{failed=true;}Check(failed&&Convert.ToInt32((await db.QueryAsync("SELECT COUNT(*) FROM items WHERE name='rollback'",1,ct)).Rows[0][0])==0,"rollback");
            });
            await Test("GO batches, procedure, multiple results, pagination and EXPLAIN",async()=>{
                string sql="CREATE PROCEDURE dbo.test_proc AS BEGIN SELECT 11 AS a; SELECT 22 AS b; END;\nGO\nEXEC dbo.test_proc;";var outcomes=await ScriptRunner.RunAsync(db,new StringReader(sql),profile.Kind,false,20,null,ct);Check(outcomes.All(o=>o.Error is null),string.Join("; ",outcomes.Select(o=>o.Error)));Check(outcomes.Last().Results.Count(r=>r.Columns.Count>0)==2,"multiple results");Check((await db.QueryAsync(db.PageSql(table,"",0,1),1,ct)).Rows.Count==1,"page");Check((await db.ExplainAsync("SELECT * FROM dbo.items",ct)).Count>0,"plan");
            });
            await Test("CSV import, all exports and migration",async()=>{
                var source=new DbObject(database,"dbo","transfer_source","表");var target=source with{Name="transfer_target"};foreach(var t in new[]{source,target})await db.QueryAsync($"CREATE TABLE {db.Qualified(t)}(id int PRIMARY KEY,label nvarchar(80),flag bit,amount decimal(12,3))",1,ct);var columns=await db.ColumnsAsync(source,ct);
                Check(await db.InsertRowsAsync(source,["id","label","flag","amount"],CsvData.MapAsync(new StringReader("id,label,flag,amount\n1,中文,true,12.375\n2,\\N,false,-1.250"),true,[0,1,2,3],columns,profile.Kind,ct),null,ct)==2,"CSV");foreach(var format in new[]{"csv","json","sql"}){await using var output=new MemoryStream();Check(await DataTransfer.ExportAsync(db,selected,source,output,format,null,ct)==2,"export "+format);}await using var other=DbProviderFactory.Create(selected);Check(await DataTransfer.MigrateAsync(db,selected,source,other,selected,target,null,ct)==2,"migration");
            });
            await Test("Logical backup/restore and identity continuation",async()=>{
                await using var output=new MemoryStream();await DataTransfer.BackupAsync(db,selected,[new(database,"dbo","parents","表"),table],output,null,ct);await using var restore=DbProviderFactory.Create(MainViewModel.CloneForDatabase(profile,restored));var outcomes=await ScriptRunner.RunAsync(restore,new StringReader(Encoding.UTF8.GetString(output.ToArray())),profile.Kind,false,20,null,ct);Check(outcomes.All(o=>o.Error is null),string.Join("; ",outcomes.Select(o=>o.Error)));await restore.QueryAsync("INSERT INTO dbo.items(name) VALUES(N'after restore')",1,ct);Check(Convert.ToInt32((await restore.QueryAsync("SELECT COUNT(*) FROM dbo.items",1,ct)).Rows[0][0])==2,"restored count");
            });
            await Test("Rename/drop table and close/reopen connection",async()=>{
                var t=new DbObject(database,"dbo","rename_target","表");await db.QueryAsync(SchemaTools.CreateTable(db,profile.Kind,t,[new("id","int",false,true,"","")]),1,ct);await db.QueryAsync(DatabaseDdl.RenameTable(db,profile.Kind,t,"renamed_target"),1,ct);await db.QueryAsync(DatabaseDdl.DropTable(db,t with{Name="renamed_target"}),1,ct);var vm=new MainViewModel{Connection=selected};vm.Settings.PersistLogs=false;vm.NewQuery();vm.Disconnect();Check(!await vm.ExecuteDdlAsync(selected,"CREATE TABLE dbo.must_not_exist(id int)"),"closed DDL");await vm.ConnectAsync();Check(vm.IsOpen(selected),"reopen");
            });
            await Test("Query cancellation and original connection recovery",async()=>{using var cancel=new CancellationTokenSource(500);bool cancelled=false;try{await db.QueryAsync("WAITFOR DELAY '00:00:10'; SELECT 1",1,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&Convert.ToInt32((await db.QueryAsync("SELECT 1",1,ct)).Rows[0][0])==1,"cancel/recovery");});
        }catch(Exception ex){failures++;Console.WriteLine("FATAL "+ex.Message);results.Add(new{name="setup",status="failed",error=ex.Message});}
        finally{
            foreach(var name in owned)try{await using var cleanup=DbProviderFactory.Create(profile);RelationalProvider.ClearPool(MainViewModel.CloneForDatabase(profile,name));await cleanup.QueryAsync("DROP DATABASE "+cleanup.Quote(name),1,CancellationToken.None);Console.WriteLine("CLEANUP "+name);results.Add(new{name="cleanup "+name,status="passed"});}catch(Exception ex){failures++;Console.WriteLine("CLEANUP FAILED "+ex.Message);results.Add(new{name="cleanup "+name,status="failed",error=ex.Message});}
            Directory.CreateDirectory("artifacts");await File.WriteAllTextAsync("artifacts/sqlserver-live-report.json",JsonSerializer.Serialize(new{host=profile.Host,port=profile.Port,version,finished=DateTimeOffset.UtcNow,passed,failures,results},new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine($"LIVE SQLSERVER: {passed} passed, {failures} failed.");return failures==0?0:1;
    }
}
