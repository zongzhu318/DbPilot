using System.Runtime.CompilerServices;
using System.Text;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;

namespace DbPilot.Checks;
internal static class AdvancedChecks
{
    public static async Task Run(Action<bool,string> check,string folder)
    {
        async Task<List<ScriptBatch>> Parse(string sql,DatabaseKind kind){var list=new List<ScriptBatch>();await foreach(var b in SqlScript.ReadAsync(new StringReader(sql),kind,default))list.Add(b);return list;}
        var batches=await Parse("-- comment\nSELECT ';', 'a'';b'; SELECT 2;",DatabaseKind.PostgreSQL);
        check(batches.Count==2&&batches[0].Sql.Contains("a'';b")&&batches[0].Line==2,"script parser preserves quoted semicolons and source line");
        batches=await Parse("CREATE FUNCTION f() RETURNS void AS $body$ BEGIN PERFORM 1; END; $body$ LANGUAGE plpgsql;\nSELECT 2;",DatabaseKind.PostgreSQL);
        check(batches.Count==2&&batches[0].Sql.Contains("PERFORM 1;"),"PostgreSQL dollar bodies remain one batch");
        batches=await Parse("DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END$$\nDELIMITER ;\nSELECT 3;",DatabaseKind.MySQL);
        check(batches.Count==2&&batches[0].Line==2&&batches[1].Line==4,"MySQL custom delimiter and source lines");
        batches=await Parse("CREATE PROCEDURE p AS SELECT 1; SELECT 2;\nGO 2\nSELECT '[GO]';\nGO",DatabaseKind.SqlServer);
        check(batches.Count==3&&batches[0].Sql==batches[1].Sql,"SQL Server GO repeats complete batches without splitting procedures");
        batches=await Parse("/* outer /* inner */ more */\nSELECT '#', '--';",DatabaseKind.PostgreSQL);
        check(batches.Count==1&&batches[0].Line==2,"nested comments and comment-like literals are preserved");
        bool malformed=false;try{await Parse("SELECT 'unterminated",DatabaseKind.MySQL);}catch(FormatException){malformed=true;}
        check(malformed,"unfinished script quote is rejected");
        check(SqlTools.RequiresConfirmation("/* context */ DROP TABLE t;")&&!SqlTools.RequiresConfirmation("SELECT 'DROP TABLE t', \"drop\"; -- DROP TABLE t"),"destructive SQL confirmation detects commands after comments without matching quoted text");
        var col=new ColumnInfo("flag","boolean",false,false,"","");
        check(Convert.ToInt32(DataValues.ForColumn(true,col with{Type="tinyint(1)"},DatabaseKind.MySQL))==1&&Convert.ToInt32(DataValues.ForColumn(false,col with{Type="tinyint(1)"},DatabaseKind.MySQL))==0,"MySQL tinyint migration accepts driver boolean values");
        check(DataValues.ForColumn("0",col,DatabaseKind.PostgreSQL) is false&&DataValues.ForColumn("f",col,DatabaseKind.PostgreSQL) is false&&DataValues.ForColumn("TRUE",col,DatabaseKind.TDengine) is true,"CSV boolean aliases include false and TDengine values");
        check(Convert.ToInt32(DataValues.ForColumn("65535",col with{Type="smallint unsigned"},DatabaseKind.MySQL))==65535,"CSV conversion supports the full MySQL unsigned smallint range");
        var csv=new List<string[]>();await foreach(var row in CsvData.ReadAsync(new StringReader("id,text,empty\n1,\"line1\nline2 \"\"quoted\"\"\",\n2,plain,\\N"),default))csv.Add(row);
        check(csv.Count==3&&csv[1][1]=="line1\nline2 \"quoted\""&&csv[1][2]=="","CSV multiline quoted fields and trailing empties");
        var mapped=new List<object?[]>();await foreach(var row in CsvData.MapAsync(new StringReader("name,id\nhello,7\n\\N,8"),true,[1,0],[new("id","int",false,true,"",""),new("name","text",true,false,"","")],DatabaseKind.PostgreSQL,default))mapped.Add(row);
        check(mapped.Count==2&&Convert.ToInt32(mapped[0][0])==7&&mapped[1][1] is null,"CSV mapping reorders and converts values with NULL sentinel");
        static async IAsyncEnumerable<object?[]> BookRows(){yield return [1,"中文",12.5m,new DateTime(2026,1,2,3,4,5),true,null];yield return [2,"plain",-1.25,new DateTime(2026,2,3),false,"x"];await Task.CompletedTask;}
        await using var book=new MemoryStream();
        var written=await XlsxWorkbook.WriteAsync(book,["id","text","amount","when","flag","empty"],BookRows(),"结果",default);
        book.Position=0;var sheet=new List<string[]>();await foreach(var row in XlsxWorkbook.ReadAsync(book,default))sheet.Add(row);
        check(written==2&&sheet.Count==3&&sheet[0].Length==6&&sheet[0][0]=="id"&&sheet[1][1]=="中文"&&sheet[1].Length==5&&sheet[2][5]=="x","Excel workbook round trip preserves header, unicode, trailing omissions and row count");
        check(sheet[1][2]=="12.5"&&sheet[1][4]=="TRUE"&&sheet[1][3]=="2026-01-02 03:04:05.000","Excel workbook preserves numbers, booleans and dates");
        await using var exportedBook=new MemoryStream();
        await ResultExport.WriteAsync(new(["id","name"],[new object?[]{1,"第一行"},new object?[]{2,null}],0,false),exportedBook,"xlsx",null,DatabaseKind.MySQL,null,default);
        exportedBook.Position=0;var exportedRows=new List<string[]>();await foreach(var row in XlsxWorkbook.ReadAsync(exportedBook,default))exportedRows.Add(row);
        check(exportedRows.Count==3&&exportedRows[2][0]=="2"&&exportedRows[2].Length==1,"result export writes Excel workbooks and leaves null cells empty");
        static async IAsyncEnumerable<string[]> BlankRows(){yield return ["id","label"];yield return ["","x"];await Task.CompletedTask;}
        var blankMapped=new List<object?[]>();
        await foreach(var row in CsvData.MapAsync(BlankRows(),true,[0,1],[new("id","int",true,false,"",""),new("label","text",true,false,"","")],DatabaseKind.PostgreSQL,true,default))blankMapped.Add(row);
        check(blankMapped.Count==1&&blankMapped[0][0] is null&&(string?)blankMapped[0][1]=="x","blank Excel cells import as NULL while filled cells convert");
        var filterTable=new System.Data.DataTable();filterTable.CaseSensitive=false;
        filterTable.Columns.Add("c0",typeof(string));filterTable.Columns.Add("c1",typeof(int));filterTable.Columns.Add("c2",typeof(DateTime));
        filterTable.Rows.Add("Alice",1,new DateTime(2026,10,18,12,0,0));filterTable.Rows.Add("bob",22,new DateTime(2026,1,2));filterTable.Rows.Add("100% done [x]",333,new DateTime(2026,10,19));filterTable.Rows.Add(DBNull.Value,4,new DateTime(2025,5,6));
        var filterView=filterTable.DefaultView;
        filterView.RowFilter=ResultFilter.Search(filterTable,"alice 1");
        check(filterView.Count==1&&((string)filterView[0]["c0"])=="Alice","result search combines keywords with case-insensitive matching across string and numeric columns");
        filterView.RowFilter=ResultFilter.Search(filterTable,"100%");
        check(filterView.Count==1&&((string)filterView[0]["c0"]).StartsWith("100%"),"result search treats LIKE wildcards as literal text");
        filterView.RowFilter=ResultFilter.Search(filterTable,"[x]");
        check(filterView.Count==1,"result search escapes bracket characters");
        check(ResultFilter.Exact(filterTable,0,"bob") is {} exactString&&ResultFilter.Exact(filterTable,1,22) is {} exactNumber&&ResultFilter.Exact(filterTable,2,new DateTime(2026,10,18,12,0,0)) is {} exactDate&&ResultFilter.Exact(filterTable,0,null) is {} exactNull,"cell filter builds typed exact predicates");
        filterView.RowFilter=ResultFilter.Exact(filterTable,0,null)!;
        check(filterView.Count==1,"cell filter isolates NULL values");
        filterView.RowFilter=ResultFilter.Exact(filterTable,2,new DateTime(2026,10,18,12,0,0))!;
        check(filterView.Count==1,"cell filter matches date values exactly");
        check(ResultFilter.Exact(filterTable,0,new byte[]{1}) is null,"cell filter rejects binary values");
        var fake=new Fake();var outcomes=await ScriptRunner.RunAsync(fake,new StringReader("SELECT 1; BAD; SELECT 2;"),DatabaseKind.PostgreSQL,false,10,null,default);
        check(fake.Executed.Count==2&&outcomes.Last().Error=="simulated failure","script stops before later mutation after batch error");
        fake.Executed.Clear();outcomes=await ScriptRunner.RunAsync(fake,new StringReader("SELECT 1; BAD; SELECT 2;"),DatabaseKind.PostgreSQL,true,10,null,default);
        check(fake.Executed.Count==3&&outcomes.Count==3,"explicit continue-on-error executes later batches");
        var vm=new MainViewModel(_=>fake);vm.NewQuery(profile:new(){Kind=DatabaseKind.PostgreSQL});await vm.ExecuteAsync("SELECT 1; SELECT 2;");
        check(vm.Tab!.ResultSets.Count==2&&vm.Tab.ResultIndex==1,"manual execution retains separate results for script statements");
        vm.SelectResult(0);check(vm.Tab.ResultIndex==0&&ReferenceEquals(vm.Tab.Result,vm.Tab.ResultSets[0]),"result selection updates the displayed query result");
        var path=Path.Combine(folder,"atomic.csv");await File.WriteAllTextAsync(path,"original");bool cancelled=false;
        try{await AtomicFile.WriteAsync(path,async stream=>{await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"));throw new OperationCanceledException();},default);}catch(OperationCanceledException){cancelled=true;}
        check(cancelled&&await File.ReadAllTextAsync(path)=="original"&&!Directory.GetFiles(folder,"atomic.csv.*.tmp").Any(),"cancelled export preserves previous file and removes partial temporary file");
        await using var output=new MemoryStream();var n=await DataTransfer.ExportAsync(fake,new(){Kind=DatabaseKind.PostgreSQL},new("db","public","items","表"),output,"json",null,default);
        check(n==2&&Encoding.UTF8.GetString(output.ToArray()).Contains("second"),"whole-table export consumes every streamed batch");
        await using var pg=DbProviderFactory.Create(new(){Kind=DatabaseKind.PostgreSQL});
        var ddl=SchemaTools.CreateTable(pg,DatabaseKind.PostgreSQL,new("db","public","items","表"),[new("id","integer",false,true,"","",IsGenerated:true,IsIdentity:true),new("doubled","integer",true,false,"","",IsGenerated:true,GenerationExpression:"id * 2",Stored:true)]);
        check(ddl.Contains("GENERATED BY DEFAULT AS IDENTITY")&&ddl.Contains("GENERATED ALWAYS AS (id * 2) STORED"),"DDL distinguishes identity and computed expressions");
        await using var pgDdl=DbProviderFactory.Create(new(){Kind=DatabaseKind.PostgreSQL});
        await using var myDdl=DbProviderFactory.Create(new(){Kind=DatabaseKind.MySQL});
        await using var msDdl=DbProviderFactory.Create(new(){Kind=DatabaseKind.SqlServer});
        var ddlTable=new DbObject("report","public","items","表");var ddlCopy=ddlTable with{Name="items_copy"};
        check(DatabaseDdl.DropDatabase(pgDdl,"report")=="DROP DATABASE \"report\";","drop database quotes the database name");
        check(DatabaseDdl.RenameDatabase(pgDdl,DatabaseKind.PostgreSQL,"old","new")=="ALTER DATABASE \"old\" RENAME TO \"new\";"&&DatabaseDdl.RenameDatabase(msDdl,DatabaseKind.SqlServer,"old","new")=="ALTER DATABASE [old] MODIFY NAME = [new];","database rename follows provider syntax");
        bool unsupported=false;try{DatabaseDdl.RenameDatabase(myDdl,DatabaseKind.MySQL,"old","new");}catch(NotSupportedException){unsupported=true;}
        check(unsupported,"database rename reports unsupported providers clearly");
        check(DatabaseDdl.TruncateTable(pgDdl,DatabaseKind.PostgreSQL,ddlTable).EndsWith("RESTART IDENTITY;")&&DatabaseDdl.TruncateTable(myDdl,DatabaseKind.MySQL,ddlTable)=="TRUNCATE TABLE `report`.`items`;","truncate table restarts identity only for PostgreSQL");
        unsupported=false;try{DatabaseDdl.TruncateTable(pgDdl,DatabaseKind.TDengine,ddlTable);}catch(NotSupportedException){unsupported=true;}
        check(unsupported,"truncate table is rejected for TDengine");
        check(DatabaseDdl.CopyTable(myDdl,DatabaseKind.MySQL,ddlTable,ddlCopy,true).Contains("INSERT INTO")&&!DatabaseDdl.CopyTable(myDdl,DatabaseKind.MySQL,ddlTable,ddlCopy,false).Contains("INSERT"),"copy table includes existing rows only when requested");
        check(DatabaseDdl.CopyTable(pgDdl,DatabaseKind.PostgreSQL,ddlTable,ddlCopy,false).Contains("INCLUDING ALL")&&DatabaseDdl.CopyTable(msDdl,DatabaseKind.SqlServer,ddlTable,ddlCopy,false).StartsWith("SELECT TOP 0 * INTO"),"copy table preserves advanced structure for PostgreSQL and SQL Server");
        check(DatabaseDdl.Maintenance(myDdl,DatabaseKind.MySQL,ddlTable,"optimize")=="OPTIMIZE TABLE `report`.`items`;"&&DatabaseDdl.Maintenance(pgDdl,DatabaseKind.PostgreSQL,ddlTable,"vacuum")=="VACUUM \"public\".\"items\";"&&DatabaseDdl.Maintenance(msDdl,DatabaseKind.SqlServer,ddlTable,"statistics")=="UPDATE STATISTICS [public].[items];"&&DatabaseDdl.Maintenance(pgDdl,DatabaseKind.TDengine,ddlTable,"analyze") is null,"table maintenance maps actions per provider");
        var dumpTable=new DbObject("db","public","items","表");var dumpProfile=new ConnectionProfile{Kind=DatabaseKind.PostgreSQL,Database="db"};var dumpFake=new Fake{HasColumns=true};
        await using var dumpStructure=new MemoryStream();await DataTransfer.DumpAsync(dumpFake,dumpProfile,[dumpTable],dumpStructure,true,false,null,default);
        var structureDump=Encoding.UTF8.GetString(dumpStructure.ToArray());
        check(structureDump.Contains("CREATE TABLE")&&!structureDump.Contains("INSERT INTO"),"structure-only dump writes DDL without row data");
        await using var dumpData=new MemoryStream();await DataTransfer.DumpAsync(dumpFake,dumpProfile,[dumpTable],dumpData,false,true,null,default);
        var dataDump=Encoding.UTF8.GetString(dumpData.ToArray());
        check(dataDump.Contains("INSERT INTO")&&!dataDump.Contains("CREATE TABLE"),"data-only dump writes rows without DDL");
        bool emptyDump=false;try{await DataTransfer.DumpAsync(dumpFake,dumpProfile,[dumpTable],new MemoryStream(),false,false,null,default);}catch(ArgumentException){emptyDump=true;}
        check(emptyDump,"dump rejects selecting neither structure nor data");
        var sshStore=new WorkspaceStore(Path.Combine(folder,"ssh"));var profile=new ConnectionProfile{Password="database-secret",Ssh=new(){Password="ssh-secret",Passphrase="key-secret"}};await sshStore.SaveConnectionsAsync([profile]);
        var disk=await File.ReadAllTextAsync(Path.Combine(folder,"ssh","connections.json"));var loaded=(await sshStore.LoadConnectionsAsync())[0];
        check(!disk.Contains("ssh-secret")&&!disk.Contains("key-secret")&&loaded.Ssh.Password=="ssh-secret"&&loaded.Ssh.Passphrase=="key-secret"&&profile.Ssh.Password=="ssh-secret","SSH password and key passphrase are encrypted without mutating live settings");
        var backup=new WorkspaceBackup([profile],["group"],[new("private-template","SELECT 'private-sql'")]);var encrypted=PortableWorkspace.Encrypt(backup,"long-test-passphrase");
        var restored=PortableWorkspace.Decrypt(encrypted,"long-test-passphrase");
        check(restored.Connections[0].Ssh.Password=="ssh-secret"&&restored.Templates[0].Sql.Contains("private-sql")&&!Encoding.UTF8.GetString(encrypted).Contains("ssh-secret"),"portable encrypted backup restores credentials and custom templates");
        bool wrongPassword=false;try{PortableWorkspace.Decrypt(encrypted,"wrong-password");}catch(System.Security.Cryptography.CryptographicException){wrongPassword=true;}
        check(wrongPassword,"portable backup rejects incorrect passphrase");
        await sshStore.SaveTemplatesAsync(backup.Templates);await sshStore.SaveLogsAsync(["[Error] private-log"]);
        check((await sshStore.LoadTemplatesAsync())[0].Sql==backup.Templates[0].Sql&&!File.ReadAllText(Path.Combine(folder,"ssh","templates.json")).Contains("private-sql")&&(await sshStore.LoadLogsAsync())[0].Contains("private-log")&&!File.ReadAllText(Path.Combine(folder,"ssh","logs.json")).Contains("private-log"),"custom templates and persistent logs are encrypted at rest");
        batches=await Parse("/*!40101 SET @x=1 */;\nSELECT 1--2;",DatabaseKind.MySQL);
        check(batches.Count==2&&batches[0].Sql.Contains("SET @x")&&batches[1].Sql.Contains("1--2"),"MySQL executable comments and subtraction remain executable SQL");
        var references=SqlReferences.Read("SELECT u.id FROM public.users u JOIN \"order details\" o ON o.user_id=u.id WHERE u.name='JOIN hidden h'");
        check(references.Count==2&&references[0].Schema=="public"&&references[0].Alias=="u"&&references[1].Name=="order details"&&references[1].Alias=="o","completion recognizes schema-qualified tables, joins and aliases while ignoring literals");
        references=SqlReferences.Read("SELECT * FROM users JOIN orders ON users.id=orders.id");
        check(references.Count==2&&references[0].Alias=="users","JOIN keyword is never consumed as a table alias");
        fake.Executed.Clear();outcomes=await ScriptRunner.RunAsync(fake,new StringReader("BAD;"+string.Concat(Enumerable.Repeat("SELECT 1;",120))),DatabaseKind.PostgreSQL,true,10,null,default);
        check(outcomes.Count<=100&&outcomes.Any(o=>o.Error is not null)&&outcomes.Last().Batch==121,"bounded script history retains an earlier failure after many successful batches");
        var escaped=new List<object?[]>();await foreach(var row in CsvData.MapAsync(new StringReader("text\n\\\\N\n\\N"),true,[0],[new("text","text",true,false,"","")],DatabaseKind.PostgreSQL,default))escaped.Add(row);
        check(escaped[0][0]?.ToString()=="\\N"&&escaped[1][0] is null,"CSV escaping distinguishes literal NULL sentinel from database NULL");
        vm.Connection=vm.Tab.Connection;vm.Disconnect();var before=fake.Executed.Count;await vm.ExecuteAsync("SELECT 3;");
        check(fake.Executed.Count==before&&vm.Status.Contains("已断开"),"explicit disconnect prevents new editor queries");
        await vm.ConnectAsync();await vm.ExecuteAsync("SELECT 3;");check(fake.Executed.Count>before,"explicit reconnect restores query execution");
        var tdStatements=new List<string>();await using(var td=new TDengineProvider(new(){Kind=DatabaseKind.TDengine},new HttpStub(async(request,ct)=>{
            var statement=await request.Content!.ReadAsStringAsync(ct);tdStatements.Add(statement);
            var json=statement.StartsWith("DESCRIBE")?"{\"code\":0,\"column_meta\":[[\"name\",8,20],[\"type\",8,20],[\"length\",4,4],[\"note\",8,10]],\"data\":[[\"ts\",\"TIMESTAMP\",8,\"\"],[\"reading\",\"DOUBLE\",8,\"\"]]}":"{\"code\":0,\"data\":[]}";
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(json)};
        }))){
            async IAsyncEnumerable<object?[]> Rows(){for(int i=0;i<201;i++)yield return [new DateTime(2026,1,1).AddSeconds(i),i];await Task.CompletedTask;}
            var count=await td.InsertRowsAsync(new("meters","","sensor","普通表"),["ts","reading"],Rows(),null,default);
            check(count==201&&tdStatements.Count(s=>s.StartsWith("INSERT"))==2,"TDengine bulk import writes bounded batches through REST");
        }
    }
    private sealed class HttpStub(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);}
    private sealed class Fake : IDbProvider
    {
        public List<string> Executed { get; }=[];
        public bool HasColumns { get; init; }
        public Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct){Executed.Add(sql);if(sql.Contains("BAD"))throw new InvalidOperationException("simulated failure");return Task.FromResult(new QueryResult(["value"],[new object?[]{sql}],0,false));}
        public async IAsyncEnumerable<QueryResult> StreamAsync(string sql,int batchSize,[EnumeratorCancellation]CancellationToken ct){yield return new(["value"],[new object?[]{"first"}],0,false);await Task.Yield();ct.ThrowIfCancellationRequested();yield return new(["value"],[new object?[]{"second"}],0,false);}
        public Task TestAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DbObject>> ObjectsAsync(string database,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DbObject>>([]);
        public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ColumnInfo>>(HasColumns?[new("value","text",true,false,"","")]:[]);
        public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<IndexInfo>>([]);
        public string Quote(string name)=>"\""+name.Replace("\"","\"\"")+"\"";
        public string Qualified(DbObject table)=>Quote(table.Schema)+"."+Quote(table.Name);
        public string PageSql(DbObject table,string filter,int page,int size)=>"SELECT * FROM "+Qualified(table);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
