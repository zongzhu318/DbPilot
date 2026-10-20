using System.Text.Json;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;

namespace DbPilot.Checks;
internal static class CatalogLiveChecks
{
    public static async Task<int> Run()
    {
        var results=new List<object>();int failed=0;using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));var ct=timeout.Token;
        foreach(var kind in new[]{DatabaseKind.MySQL,DatabaseKind.PostgreSQL,DatabaseKind.TDengine}){
            string env=kind switch{DatabaseKind.MySQL=>"MYSQL",DatabaseKind.PostgreSQL=>"PG",_=>"TD"};var password=Environment.GetEnvironmentVariable("DBPILOT_"+env+"_PASSWORD");if(password is null){failed++;results.Add(new{kind=kind.ToString(),status="not run",error="missing credential environment"});continue;}
            var profile=new ConnectionProfile{Kind=kind,Host=kind==DatabaseKind.TDengine?"127.0.0.1":"192.168.127.128",Port=kind switch{DatabaseKind.MySQL=>3306,DatabaseKind.PostgreSQL=>5432,_=>6041},Database=kind==DatabaseKind.PostgreSQL?"postgres":"",Username=kind switch{DatabaseKind.MySQL=>"zongzhu",DatabaseKind.PostgreSQL=>"postgres",_=>"root"},Password=password,AllowPublicKeyRetrieval=true,UsePooling=false};
            string database="dbpilot_test_"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"_"+Guid.NewGuid().ToString("N")[..8];bool owned=false;await using var admin=DbProviderFactory.Create(profile);
            try{
                await admin.TestAsync(ct);var vm=new MainViewModel();vm.Settings.PersistLogs=false;vm.Connection=profile;vm.Connections.Add(profile);vm.NewQuery();
                if(!await vm.ExecuteDdlAsync(profile,DatabaseDdl.CreateDatabase(admin,kind,database)))throw new Exception(vm.Status);owned=true;
                if(!(await vm.ReadDatabasesAsync(profile,ct)).Contains(database))throw new Exception("new database not listed");vm.SelectDatabase(database);
                var selected=vm.CurrentProfile!;await using(var db=DbProviderFactory.Create(selected)){
                    var table=new DbObject(database,kind==DatabaseKind.PostgreSQL?"public":"","catalog_table",kind==DatabaseKind.TDengine?"普通表":"表");
                    ColumnInfo[] columns=kind==DatabaseKind.TDengine?[new("ts","TIMESTAMP",false,false,"",""),new("value","INT",true,false,"","")]:[new("id","INT",false,true,"","",IsGenerated:true,IsIdentity:true),new("value","VARCHAR(40)",true,false,"","")];
                    if(!await vm.ExecuteDdlAsync(selected,SchemaTools.CreateTable(db,kind,table,columns)))throw new Exception(vm.Status);
                    string name=db.Quote(table.Name);await db.QueryAsync(kind==DatabaseKind.TDengine?$"INSERT INTO {name} VALUES ('2026-10-18 12:00:00.000',42)":$"INSERT INTO {name} ({db.Quote("value")}) VALUES ('selected database')",1,ct);
                    if((await db.QueryAsync($"SELECT * FROM {name}",10,ct)).Rows.Count!=1)throw new Exception("unqualified query failed");
                    if(kind!=DatabaseKind.TDengine){if(!await vm.ExecuteDdlAsync(selected,DatabaseDdl.RenameTable(db,kind,table,"renamed_table")))throw new Exception(vm.Status);table=table with{Name="renamed_table"};if((await db.QueryAsync("SELECT * FROM "+db.Quote(table.Name),10,ct)).Rows.Count!=1)throw new Exception("rename lost data");}
                    if(!await vm.ExecuteDdlAsync(selected,DatabaseDdl.DropTable(db,table)))throw new Exception(vm.Status);if((await db.ObjectsAsync(database,ct)).Any(o=>o.Name==table.Name))throw new Exception("table was not dropped");
                }
                vm.Disconnect();bool blocked=false;try{await vm.ReadDatabasesAsync(profile,ct);}catch(InvalidOperationException){blocked=true;}if(!blocked)throw new Exception("closed connection allowed metadata");await vm.ConnectAsync();if(!vm.IsOpen(profile))throw new Exception("reopen failed");
                Console.WriteLine("PASS "+kind+" create database/table, unqualified query, rename/drop and close/reopen");results.Add(new{kind=kind.ToString(),status="passed",rename=kind==DatabaseKind.TDengine?"unsupported by server":"passed"});
            }catch(Exception ex){failed++;Console.WriteLine("FAIL "+kind+": "+ex.Message);results.Add(new{kind=kind.ToString(),status="failed",error=ex.Message});}
            finally{if(owned)try{await admin.QueryAsync("DROP DATABASE "+admin.Quote(database),1,CancellationToken.None);Console.WriteLine("CLEANUP "+database);results.Add(new{cleanup=database,status="passed"});}catch(Exception ex){failed++;results.Add(new{cleanup=database,status="failed",error=ex.Message});}}
        }
        Directory.CreateDirectory("artifacts");await File.WriteAllTextAsync("artifacts/catalog-live-report.json",JsonSerializer.Serialize(new{finished=DateTimeOffset.UtcNow,failed,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
}
