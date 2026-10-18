using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;
using DbStudio.ViewModels;

namespace DbStudio.Checks;
internal static class MySqlLiveChecks
{
    public static async Task<int> Run()
    {
        var host=Environment.GetEnvironmentVariable("DBSTUDIO_MYSQL_HOST")??throw new InvalidOperationException("Set DBSTUDIO_MYSQL_HOST.");
        var password=Environment.GetEnvironmentVariable("DBSTUDIO_MYSQL_PASSWORD")??throw new InvalidOperationException("Set DBSTUDIO_MYSQL_PASSWORD.");
        var profile=new ConnectionProfile{Kind=DatabaseKind.MySQL,Host=host,Port=int.Parse(Environment.GetEnvironmentVariable("DBSTUDIO_MYSQL_PORT")??"3306"),Username=Environment.GetEnvironmentVariable("DBSTUDIO_MYSQL_USER")??"root",Password=password,Timeout=8,UsePooling=false,AllowPublicKeyRetrieval=true};
        string suffix=DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"_"+Guid.NewGuid().ToString("N")[..8];
        string database="dbpilot_test_"+suffix,restored="dbpilot_restore_"+suffix;
        var owned=new HashSet<string>();var results=new List<object>();int failures=0,passed=0;string version="unknown";var started=DateTimeOffset.UtcNow;
        string report=Path.GetFullPath(Environment.GetEnvironmentVariable("DBSTUDIO_MYSQL_REPORT")??"artifacts/mysql-live-report.json");Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=deadline.Token;
        void Check(bool okay,string name){if(!okay)throw new Exception(name);}
        async Task Test(string name,Func<Task> action){try{await action();passed++;Console.WriteLine("PASS "+name);results.Add(new{name,status="passed"});}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);results.Add(new{name,status="failed",error=ex.Message});}}
        await using var admin=DbProviderFactory.Create(profile);
        try{
            await admin.TestAsync(ct);
            var server=await admin.QueryAsync("SELECT VERSION(), @@version_comment, @@sql_mode",1,ct);version=string.Join(" | ",server.Rows[0]);Console.WriteLine("SERVER "+version);
            await Test("Authentication, database listing and UTF-8 round trip",async()=>{
                var databases=await admin.DatabasesAsync(ct);Check(databases.Count>0,"database listing empty");
                var value=await admin.QueryAsync("SELECT '中文🚀' AS text_value, NULL AS null_value, 123 AS numeric_value",2,ct);Check(value.Rows[0][0]?.ToString()=="中文🚀"&&value.Rows[0][1] is null,"UTF-8/NULL mismatch");
            });
            foreach(var name in new[]{database,restored}){await admin.QueryAsync($"CREATE DATABASE {admin.Quote(name)} CHARACTER SET utf8mb4",1,ct);owned.Add(name);}
            var testProfile=MainViewModel.CloneForDatabase(profile,database);await using var db=DbProviderFactory.Create(testProfile);
            var parent=new DbObject(database,"","parents","表");var items=new DbObject(database,"","items","表");
            string Q(DbObject obj)=>db.Qualified(obj);
            await db.QueryAsync($"CREATE TABLE {Q(parent)} (id INT NOT NULL PRIMARY KEY, name VARCHAR(80) NOT NULL) ENGINE=InnoDB",1,ct);
            await db.QueryAsync($"CREATE TABLE {Q(items)} (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, parent_id INT NULL, name VARCHAR(80) NOT NULL, note TEXT NULL, qty INT NOT NULL DEFAULT 7, amount DECIMAL(12,3) NOT NULL DEFAULT 0, flag TINYINT(1) NOT NULL DEFAULT 0, payload VARBINARY(32) NULL, doc JSON NULL, happened DATETIME(6) NULL, doubled INT GENERATED ALWAYS AS (qty * 2) STORED, INDEX idx_name(name), CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parents(id) ON DELETE SET NULL ON UPDATE CASCADE) ENGINE=InnoDB",1,ct);
            await db.QueryAsync($"INSERT INTO {Q(parent)} VALUES (1,'parent')",1,ct);
            var fields=await db.ColumnsAsync(items,ct);
            await Test("Table, view, indexes, identity, generated column and foreign-key metadata",async()=>{
                await db.QueryAsync($"CREATE VIEW {db.Quote(database)}.item_view AS SELECT id,name FROM {Q(items)}",1,ct);
                var objects=await db.ObjectsAsync(database,ct);Check(objects.Any(o=>o.Name=="items"&&o.Kind=="表")&&objects.Any(o=>o.Name=="item_view"&&o.Kind=="视图"),"object discovery");
                Check(fields.Single(c=>c.Name=="id").IsIdentity&&fields.Single(c=>c.Name=="doubled").GenerationExpression.Length>0,"generated metadata");
                Check(fields.Single(c=>c.Name=="qty").HasDefault&&!fields.Single(c=>c.Name=="name").Nullable,"default/nullable metadata");
                var indexes=await db.IndexesAsync(items,ct);Check(indexes.Any(i=>i.Kind=="主键")&&indexes.Any(i=>i.Name=="idx_name"),"indexes");
                var keys=await db.ForeignKeysAsync(database,ct);Check(keys.Any(f=>f.Name=="fk_parent"&&f.OnDelete=="SET NULL"&&f.OnUpdate=="CASCADE"),"foreign-key actions");
                Check((await db.DefinitionAsync(items,ct)).Contains("CREATE TABLE")&&(await db.DefinitionAsync(new(database,"","item_view","视图"),ct)).Contains("VIEW"),"SHOW CREATE definitions");
            });
            await Test("Parameterized inserts, defaults, Unicode, JSON, binary and microsecond timestamps",async()=>{
                var editable=(IEditableDbProvider)db;
                var changes=new[]{new RowChange(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name","中文🚀 O'Reilly"},{"note","line1\nline2,\"quoted\""},{"parent_id",1},{"qty",9},{"amount",12.375m},{"flag",true},{"payload",new byte[]{0,1,255}},{"doc","{\"k\":\"中文\"}"},{"happened",new DateTime(2026,10,18,10,11,12).AddTicks(1234560)}}),new RowChange(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name","default-row"}})};
                Check(await editable.ApplyChangesAsync(items,changes,ct)==2,"insert affected rows");
                var data=await db.QueryAsync($"SELECT * FROM {Q(items)} ORDER BY id",10,ct);Check(data.Rows.Count==2&&data.Rows[0][2]?.ToString()=="中文🚀 O'Reilly","Unicode name");
                int I(string name)=>data.Columns.ToList().IndexOf(name);
                Check(data.Rows[0][I("payload")] is byte[] b&&b.SequenceEqual(new byte[]{0,1,255})&&Convert.ToInt32(data.Rows[0][I("doubled")])==18,"binary/generated values");
                Check(Convert.ToInt32(data.Rows[1][I("qty")])==7&&data.Rows[1][I("note")] is null,"server defaults/NULL");
            });
            Dictionary<string,object?> Snapshot(QueryResult result,int row)=>result.Columns.Select((name,i)=>(name,result.Rows[row][i])).ToDictionary(p=>p.name,p=>p.Item2);
            await Test("Online edit, NULL assignment, optimistic concurrency and rollback",async()=>{
                var result=await db.QueryAsync($"SELECT * FROM {Q(items)} ORDER BY id",10,ct);var original=Snapshot(result,0);var editable=(IEditableDbProvider)db;
                Check(await editable.ApplyChangesAsync(items,[new(RowChangeKind.Update,original,new Dictionary<string,object?>{{"name","updated"},{"note",null}})],ct)==1,"update");
                bool conflict=false;try{await editable.ApplyChangesAsync(items,[new(RowChangeKind.Update,original,new Dictionary<string,object?>{{"name","stale"}})],ct);}catch(DBConcurrencyException){conflict=true;}Check(conflict,"stale update was not rejected");
                var updated=await db.QueryAsync($"SELECT * FROM {Q(items)} ORDER BY id",10,ct);Check(updated.Rows[0][2]?.ToString()=="updated"&&updated.Rows[0][3] is null,"updated state");
                bool failed=false;try{await editable.ApplyChangesAsync(items,[new(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name","must-rollback"}}),new(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name",new string('x',500)}})],ct);}catch{failed=true;}Check(failed,"invalid batch unexpectedly succeeded");
                Check(Convert.ToInt32((await db.QueryAsync($"SELECT COUNT(*) FROM {Q(items)} WHERE name='must-rollback'",1,ct)).Rows[0][0])==0,"rollback left earlier insert");
            });
            await Test("Composite-key updates and deletes",async()=>{
                var composite=new DbObject(database,"","composite_items","表");await db.QueryAsync($"CREATE TABLE {Q(composite)} (a INT,b INT,label VARCHAR(40), PRIMARY KEY(a,b)) ENGINE=InnoDB",1,ct);await db.QueryAsync($"INSERT INTO {Q(composite)} VALUES (1,2,'before')",1,ct);
                var r=await db.QueryAsync($"SELECT * FROM {Q(composite)}",1,ct);var editable=(IEditableDbProvider)db;await editable.ApplyChangesAsync(composite,[new(RowChangeKind.Update,Snapshot(r,0),new Dictionary<string,object?>{{"b",3},{"label","after"}})],ct);
                r=await db.QueryAsync($"SELECT * FROM {Q(composite)}",1,ct);Check(Convert.ToInt32(r.Rows[0][1])==3,"key update");await editable.ApplyChangesAsync(composite,[new(RowChangeKind.Delete,Snapshot(r,0),new Dictionary<string,object?>())],ct);Check((await db.QueryAsync($"SELECT * FROM {Q(composite)}",2,ct)).Rows.Count==0,"delete");
            });
            await Test("CSV mapping, batch import, full export and migration",async()=>{
                var source=new DbObject(database,"","transfer_source","表");var target=new DbObject(database,"","transfer_target","表");
                foreach(var table in new[]{source,target})await db.QueryAsync($"CREATE TABLE {Q(table)} (id INT PRIMARY KEY, label TEXT NULL, flag TINYINT(1), amount DECIMAL(12,3), happened DATETIME(6), payload VARBINARY(32)) ENGINE=InnoDB",1,ct);
                var columns=await db.ColumnsAsync(source,ct);string csv="id,label,flag,amount,happened,payload\n1,\"中文, first\",1,12.345,2026-10-18 12:30:00.123456,AAH/\n2,\\N,0,0,2026-10-18 12:31:00.000000,\n3,\\\\N,1,-5.125,2026-10-18 12:32:00.000000,AA==";
                Check(await db.InsertRowsAsync(source,columns.Select(c=>c.Name).ToArray(),CsvData.MapAsync(new StringReader(csv),true,Enumerable.Range(0,columns.Count).ToArray(),columns,DatabaseKind.MySQL,ct),null,ct)==3,"CSV insert count");
                foreach(var format in new[]{"csv","json","sql"}){await using var output=new MemoryStream();Check(await DataTransfer.ExportAsync(db,testProfile,source,output,format,null,ct)==3&&output.Length>0,"whole table "+format);}
                await using var targetProvider=DbProviderFactory.Create(testProfile);Check(await DataTransfer.MigrateAsync(db,testProfile,source,targetProvider,testProfile,target,null,ct)==3,"migration count");
                var r=await db.QueryAsync($"SELECT * FROM {Q(target)} ORDER BY id",10,ct);Check(r.Rows.Count==3&&r.Rows[1][1] is null&&r.Rows[2][1]?.ToString()=="\\N","migration values");
            });
            await Test("Paging, multi-result truncation, streaming and EXPLAIN",async()=>{
                var source=new DbObject(database,"","transfer_source","表");
                var r=await db.QueryAsync(db.PageSql(source,"id > 0",1,1),1,ct);Check(r.Rows.Count==1,"paging");
                var results=await db.QueryMultipleAsync($"SELECT * FROM {Q(source)} ORDER BY id; SELECT 42 AS answer;",1,ct);Check(results.Count==2&&results[0].Truncated&&Convert.ToInt32(results[1].Rows[0][0])==42,"multiple result draining");
                int rows=0,batches=0;await foreach(var batch in db.StreamAsync($"SELECT * FROM {Q(source)} ORDER BY id",2,ct)){rows+=batch.Rows.Count;batches++;}Check(rows==3&&batches>=2,"streaming");
                Check((await db.ExplainAsync($"SELECT * FROM {Q(source)} WHERE id=1",ct)).Any(r=>r.Rows.Count>0),"EXPLAIN");
            });
            await Test("MySQL DELIMITER, stored procedure discovery and definition",async()=>{
                string script=$"DELIMITER $$\nCREATE PROCEDURE {db.Quote(database)}.test_proc() BEGIN SELECT 11 AS a; SELECT 22 AS b; END$$\nDELIMITER ;\nCALL {db.Quote(database)}.test_proc();";
                var outcomes=await ScriptRunner.RunAsync(db,new StringReader(script),DatabaseKind.MySQL,false,20,null,ct);Check(outcomes.All(o=>o.Error is null),string.Join("; ",outcomes.Where(o=>o.Error is not null).Select(o=>o.Error)));
                Check(outcomes.Last().Results.Count(r=>r.Columns.Count>0)==2,"procedure result sets");var objects=await db.ObjectsAsync(database,ct);var procedure=objects.Single(o=>o.Name=="test_proc");Check((await db.DefinitionAsync(procedure,ct)).Contains("PROCEDURE"),"procedure definition");
            });
            await Test("Function and trigger execution, discovery and definitions",async()=>{
                var table=new DbObject(database,"","trigger_target","表");
                await db.QueryAsync($"CREATE TABLE {Q(table)} (id INT PRIMARY KEY, value INT NOT NULL) ENGINE=InnoDB",1,ct);
                string script=$"DELIMITER $$\nCREATE FUNCTION {db.Quote(database)}.plus_one(x INT) RETURNS INT DETERMINISTIC NO SQL BEGIN RETURN x+1; END$$\nCREATE TRIGGER {db.Quote(database)}.before_target BEFORE INSERT ON {Q(table)} FOR EACH ROW BEGIN SET NEW.value=NEW.value+10; END$$\nDELIMITER ;";
                var outcomes=await ScriptRunner.RunAsync(db,new StringReader(script),DatabaseKind.MySQL,false,20,null,ct);
                Check(outcomes.All(o=>o.Error is null),string.Join("; ",outcomes.Select(o=>o.Error)));
                await db.QueryAsync($"INSERT INTO {Q(table)} VALUES (1,5)",1,ct);
                var r=await db.QueryAsync($"SELECT value, {db.Quote(database)}.plus_one(value) FROM {Q(table)}",1,ct);
                Check(Convert.ToInt32(r.Rows[0][0])==15&&Convert.ToInt32(r.Rows[0][1])==16,"function/trigger values");
                var objects=await db.ObjectsAsync(database,ct);
                foreach(var (name,kind) in new[]{("plus_one","函数"),("before_target","触发器")}){
                    var obj=objects.Single(o=>o.Name==name&&o.Kind==kind);
                    Check((await db.DefinitionAsync(obj,ct)).Contains(name),"definition "+name);
                }
            });
            await Test("Unsigned boundaries, ENUM, SET, BIT, TIME and quoted identifiers",async()=>{
                var table=new DbObject(database,"","类型 ` test","表");
                await db.QueryAsync($"CREATE TABLE {Q(table)} (id BIGINT UNSIGNED PRIMARY KEY, u SMALLINT UNSIGNED, e ENUM('甲','乙'), s SET('a','b'), bits BIT(8), duration TIME(6), text_value TEXT) ENGINE=InnoDB",1,ct);
                var columns=await db.ColumnsAsync(table,ct);
                async IAsyncEnumerable<object?[]> Rows(){yield return [ulong.MaxValue,ushort.MaxValue,"乙","a,b",255UL,TimeSpan.FromHours(30)+TimeSpan.FromTicks(1234560),"中文🚀\\'\n"];await Task.CompletedTask;}
                Check(await db.InsertRowsAsync(table,columns.Select(c=>c.Name).ToArray(),Rows(),null,ct)==1,"boundary insert");
                var r=await db.QueryAsync($"SELECT * FROM {Q(table)}",2,ct);
                Check(Convert.ToUInt64(r.Rows[0][0])==ulong.MaxValue&&Convert.ToInt32(r.Rows[0][1])==65535,"unsigned precision");
                Check(r.Rows[0][2]?.ToString()=="乙"&&r.Rows[0][3]?.ToString()=="a,b"&&Convert.ToUInt64(r.Rows[0][4])==255,"enum/set/bit");
                Check(r.Rows[0][5] is TimeSpan span&&span==TimeSpan.FromHours(30)+TimeSpan.FromTicks(1234560),"TIME precision");
                Check(r.Rows[0][6]?.ToString()=="中文🚀\\'\n","quoted table and string");
            });
            await Test("2000-row import, streaming export, late failure and cancellation rollback",async()=>{
                var table=new DbObject(database,"","bulk_target","表");await db.QueryAsync($"CREATE TABLE {Q(table)} (id INT PRIMARY KEY,label VARCHAR(100)) ENGINE=InnoDB",1,ct);
                async IAsyncEnumerable<object?[]> Rows(int count,bool duplicate=false){for(int i=0;i<count;i++){yield return [duplicate&&i==count-1?10000:10000+i,"批量🚀 "+i];if(i%100==0)await Task.Yield();}}
                var timer=System.Diagnostics.Stopwatch.StartNew();
                Check(await db.InsertRowsAsync(table,["id","label"],Rows(2000),null,ct)==2000,"bulk count");
                int count=0;await foreach(var batch in db.StreamAsync($"SELECT * FROM {Q(table)}",127,ct))count+=batch.Rows.Count;Check(count==2000,"bulk stream count");
                await using var output=new MemoryStream();Check(await DataTransfer.ExportAsync(db,testProfile,table,output,"json",null,ct)==2000,"bulk export count");
                Console.WriteLine($"METRIC 2000-row insert/stream/export: {timer.ElapsedMilliseconds} ms, JSON {output.Length} bytes");
                await db.QueryAsync($"DELETE FROM {Q(table)}",1,ct);
                bool failed=false;try{await db.InsertRowsAsync(table,["id","label"],Rows(250,true),null,ct);}catch{failed=true;}Check(failed,"duplicate key accepted");
                Check(Convert.ToInt32((await db.QueryAsync($"SELECT COUNT(*) FROM {Q(table)}",1,ct)).Rows[0][0])==0,"late error rollback");
                using var cancel=new CancellationTokenSource();
                async IAsyncEnumerable<object?[]> CancelRows(){yield return [1,"cancelled"];await Task.Yield();cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();}
                bool cancelled=false;try{await db.InsertRowsAsync(table,["id","label"],CancelRows(),null,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled&&Convert.ToInt32((await db.QueryAsync($"SELECT COUNT(*) FROM {Q(table)}",1,ct)).Rows[0][0])==0,"cancel rollback/reuse");
            });
            await Test("Independent-client conflict and foreign-key rejection",async()=>{
                var table=new DbObject(database,"","concurrent_target","表");await db.QueryAsync($"CREATE TABLE {Q(table)} (id INT PRIMARY KEY,label VARCHAR(40)) ENGINE=InnoDB; INSERT INTO {Q(table)} VALUES (1,'original')",1,ct);
                var original=Snapshot(await db.QueryAsync($"SELECT * FROM {Q(table)}",1,ct),0);
                await using var other=DbProviderFactory.Create(testProfile);await other.QueryAsync($"UPDATE {Q(table)} SET label='other client' WHERE id=1",1,ct);
                bool conflict=false;try{await ((IEditableDbProvider)db).ApplyChangesAsync(table,[new(RowChangeKind.Delete,original,new Dictionary<string,object?>())],ct);}catch(DBConcurrencyException){conflict=true;}
                Check(conflict&&(await db.QueryAsync($"SELECT label FROM {Q(table)}",1,ct)).Rows[0][0]?.ToString()=="other client","stale delete");
                bool rejected=false;try{await ((IEditableDbProvider)db).ApplyChangesAsync(items,[new(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name","invalid-fk"},{"parent_id",99999}})],ct);}catch{rejected=true;}
                Check(rejected&&Convert.ToInt32((await db.QueryAsync($"SELECT COUNT(*) FROM {Q(items)} WHERE name='invalid-fk'",1,ct)).Rows[0][0])==0,"foreign-key rollback");
            });
            await Test("Structure comparison and generated DDL application",async()=>{
                var table=new DbObject(database,"","ddl_target","表");await db.QueryAsync($"CREATE TABLE {Q(table)} (id INT NOT NULL PRIMARY KEY,label VARCHAR(40)) ENGINE=InnoDB",1,ct);
                var before=await db.ColumnsAsync(table,ct);var wanted=before.Select(c=>c.Name=="label"?c with{Type="varchar(80)",Nullable=false}:c).Append(new ColumnInfo("extra","int",true,false,"3","",HasDefault:true)).ToArray();var plan=SchemaTools.Compare(db,DatabaseKind.MySQL,table,wanted,before,"PRIMARY");
                foreach(var diff in plan)await db.QueryAsync(diff.Sql,1,ct);var after=await db.ColumnsAsync(table,ct);Check(after.Any(c=>c.Name=="extra")&&after.Single(c=>c.Name=="label").Type.Contains("80"),"DDL compare plan");
            });
            await Test("Logical backup and restore into second isolated database",async()=>{
                await using var stream=new MemoryStream();await DataTransfer.BackupAsync(db,testProfile,[parent,items],stream,null,ct);
                string sql=Encoding.UTF8.GetString(stream.ToArray()).Replace(db.Quote(database)+".",db.Quote(restored)+".",StringComparison.Ordinal);
                await using var restoreProvider=DbProviderFactory.Create(MainViewModel.CloneForDatabase(profile,restored));var outcomes=await ScriptRunner.RunAsync(restoreProvider,new StringReader(sql),DatabaseKind.MySQL,false,20,null,ct);
                Check(outcomes.All(o=>o.Error is null),string.Join("; ",outcomes.Where(o=>o.Error is not null).Select(o=>$"line {o.Line}: {o.Error}")));
                var restoredTable=items with{Database=restored};var r=await restoreProvider.QueryAsync($"SELECT COUNT(*),SUM(qty) FROM {restoreProvider.Qualified(restoredTable)}",1,ct);Check(Convert.ToInt32(r.Rows[0][0])==2&&Convert.ToInt32(r.Rows[0][1])==16,"restored row content");Check((await restoreProvider.ForeignKeysAsync(restored,ct)).Any(f=>f.OnDelete=="SET NULL"),"restored foreign key");
            });
            await Test("Query cancellation and subsequent connection recovery",async()=>{
                await using var cancelProvider=DbProviderFactory.Create(testProfile);using var cancel=new CancellationTokenSource(500);bool cancelled=false;
                try{await cancelProvider.QueryAsync("SELECT SLEEP(10)",1,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled,"query was not cancelled");Check(Convert.ToInt32((await cancelProvider.QueryAsync("SELECT 1",1,ct)).Rows[0][0])==1,"same connection recovery");await using var recovered=DbProviderFactory.Create(testProfile);Check(Convert.ToInt32((await recovered.QueryAsync("SELECT 1",1,ct)).Rows[0][0])==1,"recovery");
            });
        }catch(Exception ex){failures++;Console.WriteLine("FATAL "+ex.Message);results.Add(new{name="setup",status="failed",error=ex.Message});}
        finally{
            foreach(var name in owned){
                try{if(!name.StartsWith("dbpilot_test_",StringComparison.Ordinal)&&!name.StartsWith("dbpilot_restore_",StringComparison.Ordinal))throw new InvalidOperationException("cleanup name rejected");
                    await using var cleanup=DbProviderFactory.Create(profile);await cleanup.QueryAsync("DROP DATABASE "+cleanup.Quote(name),1,CancellationToken.None);Console.WriteLine("CLEANUP "+name);results.Add(new{name="cleanup "+name,status="passed"});
                }catch(Exception ex){failures++;Console.WriteLine("CLEANUP FAILED "+name+": "+ex.Message);results.Add(new{name="cleanup "+name,status="failed",error=ex.Message});}
            }
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{host,port=profile.Port,version,started,finished=DateTimeOffset.UtcNow,passed,failures,results},new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine($"LIVE MYSQL: {passed} passed, {failures} failed. Report: {report}");return failures==0?0:1;
    }
}
