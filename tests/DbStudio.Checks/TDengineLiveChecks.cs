using System.Text;
using System.Text.Json;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;
using DbStudio.ViewModels;

namespace DbStudio.Checks;
internal static class TDengineLiveChecks
{
    public static async Task<int> Run()
    {
        var profile=new ConnectionProfile{Kind=DatabaseKind.TDengine,Host="127.0.0.1",Port=6041,Username=Environment.GetEnvironmentVariable("DBSTUDIO_TD_USER")??"taos",Password=Environment.GetEnvironmentVariable("DBSTUDIO_TD_PASSWORD")??throw new Exception("Set DBSTUDIO_TD_PASSWORD")};
        string database="dbpilot_test_"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"_"+Guid.NewGuid().ToString("N")[..8];bool owned=false;int passed=0,failures=0;string version="unknown";var results=new List<object>();var started=DateTimeOffset.UtcNow;
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var ct=deadline.Token;
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        async Task Test(string name,Func<Task> action){try{await action();passed++;Console.WriteLine("PASS "+name);results.Add(new{name,status="passed"});}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);results.Add(new{name,status="failed",error=ex.Message});}}
        await using var admin=DbProviderFactory.Create(profile);
        try{
            await admin.TestAsync(ct);version=(await admin.QueryAsync("SELECT SERVER_VERSION()",1,ct)).Rows[0][0]?.ToString()??"unknown";Console.WriteLine("SERVER "+version);
            await Test("Authentication and database listing",async()=>{Check((await admin.DatabasesAsync(ct)).Count>0,"database list");});
            await admin.QueryAsync("CREATE DATABASE "+admin.Quote(database)+" PRECISION 'ms'",1,ct);owned=true;
            var testProfile=MainViewModel.CloneForDatabase(profile,database);await using var db=DbProviderFactory.Create(testProfile);
            var stable=new DbObject(database,"","meters","超级表");var child=new DbObject(database,"","meter_a","子表",Parent:"meters");var normal=new DbObject(database,"","normal_data","普通表");string Q(DbObject t)=>db.Qualified(t);
            await db.QueryAsync($"CREATE STABLE {Q(stable)} (ts TIMESTAMP, reading DOUBLE, flag BOOL, note NCHAR(100)) TAGS (location NCHAR(40), device INT)",1,ct);
            await db.QueryAsync($"CREATE TABLE {Q(child)} USING {Q(stable)} TAGS ('中文位置',1)",1,ct);
            await db.QueryAsync($"CREATE TABLE {Q(normal)} (ts TIMESTAMP, reading DOUBLE, flag BOOL, note NCHAR(100))",1,ct);
            await Test("Stable, child, ordinary table, tags and SHOW CREATE metadata",async()=>{
                var objects=await db.ObjectsAsync(database,ct);Check(objects.Any(o=>o.Name==stable.Name&&o.Kind=="超级表")&&objects.Any(o=>o.Name==child.Name&&o.Kind=="子表"&&o.Parent==stable.Name)&&objects.Any(o=>o.Name==normal.Name&&o.Kind=="普通表"),"object types/parent");
                var columns=await db.ColumnsAsync(stable,ct);Check(columns.Count(c=>c.IsTag)==2&&columns.Any(c=>c.Name=="note"&&c.Type.Contains("100")),"tag/type metadata");foreach(var t in new[]{stable,child,normal})Check((await db.DefinitionAsync(t,ct)).Contains(t.Name),"definition "+t.Name);
            });
            await Test("CSV mapping, Unicode, quotes, NULL and child inserts",async()=>{
                var fields=(await db.ColumnsAsync(normal,ct)).Where(c=>!c.IsTag).ToArray();string csv="ts,reading,flag,note\n2026-10-18 10:00:00.001,12.375,true,中文🚀\n2026-10-18 10:00:00.002,\\N,false,O'Reilly\\path";
                Check(await db.InsertRowsAsync(normal,fields.Select(c=>c.Name).ToArray(),CsvData.MapAsync(new StringReader(csv),true,[0,1,2,3],fields,DatabaseKind.TDengine,ct),null,ct)==2,"csv insert");
                var r=await db.QueryAsync($"SELECT * FROM {Q(normal)} ORDER BY ts",10,ct);Check(r.Rows.Count==2&&r.Rows[0][3]?.ToString()=="中文🚀"&&r.Rows[1][1] is null&&r.Rows[1][3]?.ToString()=="O'Reilly\\path","round trip");
                async IAsyncEnumerable<object?[]> Rows(){yield return [new DateTime(2026,10,18,10,0,0),4.5,true,"child"];await Task.CompletedTask;}
                Check(await db.InsertRowsAsync(child,["ts","reading","flag","note"],Rows(),null,ct)==1,"child insert");Check((await db.QueryAsync($"SELECT * FROM {Q(stable)}",10,ct)).Rows.Count==1,"stable read");
            });
            await Test("Multi-batch 1200-row import, page boundaries and all exports",async()=>{
                var bulk=normal with{Name="bulk_data"};await db.QueryAsync($"CREATE TABLE {Q(bulk)} (ts TIMESTAMP,reading DOUBLE,flag BOOL,note NCHAR(100))",1,ct);
                async IAsyncEnumerable<object?[]> Rows(){for(int i=0;i<1200;i++){yield return [new DateTime(2026,10,18,11,0,0).AddMilliseconds(i),i*0.5,i%2==0,"批量 "+i];if(i%200==0)await Task.Yield();}}
                Check(await db.InsertRowsAsync(bulk,["ts","reading","flag","note"],Rows(),null,ct)==1200,"bulk count");Check(Convert.ToInt32((await db.QueryAsync($"SELECT COUNT(*) FROM {Q(bulk)}",1,ct)).Rows[0][0])==1200,"server count");
                Check((await db.QueryAsync(db.PageSql(bulk,"reading >= 0",2,500),500,ct)).Rows.Count==200,"last page");
                foreach(var format in new[]{"csv","json","sql"}){await using var output=new MemoryStream();Check(await DataTransfer.ExportAsync(db,testProfile,bulk,output,format,null,ct)==1200&&output.Length>0,"export "+format);}
            });
            await Test("Tag alteration, column DDL and time-window aggregation",async()=>{
                await db.QueryAsync($"ALTER TABLE {Q(child)} SET TAG location='新位置'",1,ct);Check((await db.QueryAsync($"SELECT location FROM {Q(child)}",1,ct)).Rows[0][0]?.ToString()=="新位置","tag update");
                await db.QueryAsync($"ALTER TABLE {Q(normal)} ADD COLUMN extra INT",1,ct);Check((await db.ColumnsAsync(normal,ct)).Any(c=>c.Name=="extra"),"add column");await db.QueryAsync($"ALTER TABLE {Q(normal)} DROP COLUMN extra",1,ct);
                Check((await db.QueryAsync($"SELECT _wstart,AVG(reading) FROM {Q(normal)} INTERVAL(1s)",10,ct)).Rows.Count>0,"interval aggregation");
            });
            await Test("Errors, pre-cancellation and subsequent REST recovery",async()=>{
                bool rejected=false;try{await db.QueryAsync("SELECT * FROM missing_dbpilot_table",1,ct);}catch{rejected=true;}Check(rejected,"SQL error");
                using var cancel=new CancellationTokenSource();cancel.Cancel();bool cancelled=false;try{await db.QueryAsync("SELECT SERVER_VERSION()",1,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancel");await db.TestAsync(ct);
            });
        }catch(Exception ex){failures++;Console.WriteLine("FATAL "+ex.Message);results.Add(new{name="setup",status="failed",error=ex.Message});}
        finally{
            if(owned)try{await using var cleanup=DbProviderFactory.Create(profile);await cleanup.QueryAsync("DROP DATABASE "+cleanup.Quote(database),1,CancellationToken.None);Console.WriteLine("CLEANUP "+database);results.Add(new{name="cleanup "+database,status="passed"});}catch(Exception ex){failures++;results.Add(new{name="cleanup "+database,status="failed",error=ex.Message});Console.WriteLine("CLEANUP FAILED "+ex.Message);}
            Directory.CreateDirectory("artifacts");await File.WriteAllTextAsync("artifacts/tdengine-live-report.json",JsonSerializer.Serialize(new{host=profile.Host,port=profile.Port,version,started,finished=DateTimeOffset.UtcNow,passed,failures,results},new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine($"LIVE TDENGINE: {passed} passed, {failures} failed.");return failures==0?0:1;
    }
}
