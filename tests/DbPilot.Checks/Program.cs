using System.Security.Cryptography;
using System.Text.Json;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using DbPilot.Views;
using DbPilot.Checks;
using Avalonia.VisualTree;

if(args.Contains("--mysql-live")){Environment.ExitCode=await MySqlLiveChecks.Run();return;}
if(args.Contains("--pgsql-live")){Environment.ExitCode=await PostgreSqlLiveChecks.Run();return;}
if(args.Contains("--tdengine-live")){Environment.ExitCode=await TDengineLiveChecks.Run();return;}
if(args.Contains("--catalog-live")){Environment.ExitCode=await CatalogLiveChecks.Run();return;}
if(args.Contains("--sqlserver-live")){Environment.ExitCode=await SqlServerLiveChecks.Run();return;}

var count=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAILED: "+name);Console.WriteLine("PASS "+name);count++;}
void Press(TopLevel target,Key key,RawInputModifiers modifiers)=>target.KeyPress(key,modifiers,PhysicalKey.None,null);
var appAssembly=typeof(DbPilot.App).Assembly;
var channel=appAssembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute),false).Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a=>a.Key=="BuildChannel").Value;
var appInfo=appAssembly.GetType("DbPilot.AppInfo")!;
var dataFolder=(string)appInfo.GetProperty("DataFolderName")!.GetValue(null)!;
Check(channel=="Development"?dataFolder=="DbPilot Dev":dataFolder=="DbPilot","build channel isolates application data");
var dirtyTab=new QueryTab();dirtyTab.Sql="SELECT 42";
Check(dirtyTab.DisplayTitle.EndsWith(" •"),"query tab marks unsaved SQL");
dirtyTab.SavedSql=dirtyTab.Sql;Check(!dirtyTab.DisplayTitle.EndsWith(" •"),"saved query clears unsaved marker");
foreach(var text in new[]{"SELECT 'unfinished","-- comment","/* nested /* block */ still open","SELECT $$unfinished","SELECT $body$abc","SELECT \"unfinished"})Check(SqlCompletionContext.Read(text,text.Length) is null,"completion suppresses lexical region: "+text);
var completionContext=SqlCompletionContext.Read("SELECT u.na FROM public.users u",11)!;
Check(SqlCompletionContext.Read("-- comment\nSELECT 1",10) is null,"completion stays suppressed immediately before comment newline");
Check(completionContext.Prefix=="na"&&completionContext.Qualifier=="u"&&SqlReferences.Read(completionContext.Statement).Single().Alias=="u","completion resolves prefix and alias with FROM after caret");
Check(SqlCompletionContext.Read("SELECT 1; SELECT * FROM pub.us",30) is {TablePosition:true,Qualifier:"pub",Prefix:"us"},"completion recognizes qualified table position");
Check(SqlCompletionContext.Read("SELECT ';'; SELECT na",21)!.Statement.Trim()=="SELECT na","completion isolates statement without splitting literal semicolons");
Check(SqlCompletionContext.Keywords(DatabaseKind.PostgreSQL).Contains("ILIKE")&&!SqlCompletionContext.Keywords(DatabaseKind.MySQL).Contains("ILIKE"),"completion keywords follow selected dialect");
var folder=Path.Combine(Path.GetTempPath(),"DbPilot-checks-"+Guid.NewGuid().ToString("N"));
var store=new WorkspaceStore(folder);
var secret="sensitive-password-中文-🚀";
var profile=new ConnectionProfile{Password=secret,Name="测试",Group="本地开发"};
await store.SaveConnectionsAsync([profile]);
var disk=await File.ReadAllTextAsync(Path.Combine(folder,"connections.json"));
Check(!disk.Contains("sensitive-password"),"password absent from persisted JSON");
Check(profile.Password==secret,"saving does not mutate live credentials");
Check((await store.LoadConnectionsAsync())[0].Password==secret,"AES-GCM round trip preserves Unicode");
var firstCipher=JsonSerializer.Deserialize<List<ConnectionProfile>>(disk)![0].Password;
await store.SaveConnectionsAsync([profile]);
var records=JsonSerializer.Deserialize<List<ConnectionProfile>>(await File.ReadAllTextAsync(Path.Combine(folder,"connections.json")))!;
Check(firstCipher!=records[0].Password,"each save uses a fresh nonce");
var bytes=Convert.FromBase64String(records[0].Password);bytes[^1]^=1;records[0].Password=Convert.ToBase64String(bytes);
await File.WriteAllTextAsync(Path.Combine(folder,"connections.json"),JsonSerializer.Serialize(records));
bool rejected=false;try{await store.LoadConnectionsAsync();}catch(CryptographicException){rejected=true;}
Check(rejected,"tampered ciphertext is rejected");
foreach(var kind in Enum.GetValues<DatabaseKind>()){
    await using var provider=DbProviderFactory.Create(new ConnectionProfile{Kind=kind});
    var table=new DbObject("inventory","public","order details","表");
    var sql=provider.PageSql(table,"price > 10",2,50);
    Check(sql.Contains("100")&&sql.Contains("50")&&sql.Contains("price > 10"),kind+" pagination and filter");
    bool invalid=false;try{provider.PageSql(table,"",0,100000);}catch(ArgumentOutOfRangeException){invalid=true;}
    Check(invalid,kind+" rejects unbounded page size");
    Check(provider.Quote("a`\" ]").Length>6,kind+" quotes identifiers");
}
Check(SqlRowLimit.Apply("select * from huge_table",DatabaseKind.MySQL,500).EndsWith("LIMIT 501"),"MySQL execution cap appends LIMIT");
Check(SqlRowLimit.Apply("SELECT * FROM t LIMIT 10",DatabaseKind.MySQL,500)=="SELECT * FROM t LIMIT 10","explicit MySQL LIMIT is preserved");
Check(SqlRowLimit.Apply("SELECT * FROM t; -- note",DatabaseKind.MySQL,100)=="SELECT * FROM t LIMIT 101; -- note","cap is inserted before terminator and trailing comment");
Check(SqlRowLimit.Apply("SELECT * FROM t -- note",DatabaseKind.MySQL,100)=="SELECT * FROM t LIMIT 101 -- note","cap is inserted before a trailing line comment");
Check(SqlRowLimit.Apply("SELECT * FROM t /* note */",DatabaseKind.MySQL,100)=="SELECT * FROM t LIMIT 101 /* note */","cap is inserted before a trailing block comment");
Check(SqlRowLimit.Apply("SELECT * FROM t FOR UPDATE",DatabaseKind.MySQL,100)=="SELECT * FROM t FOR UPDATE","locking select is left untouched");
Check(SqlRowLimit.Apply("SELECT * FROM t INTO OUTFILE '/tmp/x'",DatabaseKind.MySQL,100)=="SELECT * FROM t INTO OUTFILE '/tmp/x'","INTO select is left untouched");
Check(SqlRowLimit.Apply("UPDATE t SET a=1",DatabaseKind.MySQL,100)=="UPDATE t SET a=1","DML is left untouched");
Check(SqlRowLimit.Apply("WITH c AS (SELECT * FROM t) SELECT * FROM c",DatabaseKind.PostgreSQL,100).EndsWith("LIMIT 101"),"PostgreSQL CTE select is capped");
Check(SqlRowLimit.Apply("WITH c AS (SELECT * FROM t) DELETE FROM c",DatabaseKind.PostgreSQL,100).EndsWith("DELETE FROM c"),"PostgreSQL CTE delete is left untouched");
Check(SqlRowLimit.Apply("SELECT * FROM t FOR UPDATE",DatabaseKind.PostgreSQL,100)=="SELECT * FROM t FOR UPDATE","PostgreSQL locking select is left untouched");
Check(SqlRowLimit.Apply("SELECT * FROM t OFFSET 10",DatabaseKind.PostgreSQL,100)=="SELECT * FROM t OFFSET 10","explicit PostgreSQL OFFSET is preserved");
Check(SqlRowLimit.Apply("SELECT * FROM huge_table",DatabaseKind.SqlServer,500)=="SELECT * FROM huge_table ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 501 ROWS ONLY","SQL Server cap adds FETCH with a stable order");
Check(SqlRowLimit.Apply("SELECT * FROM huge_table ORDER BY id",DatabaseKind.SqlServer,500)=="SELECT * FROM huge_table ORDER BY id OFFSET 0 ROWS FETCH NEXT 501 ROWS ONLY","SQL Server cap preserves ORDER BY");
Check(SqlRowLimit.Apply("SELECT TOP 10 * FROM t",DatabaseKind.SqlServer,500)=="SELECT TOP 10 * FROM t","existing SQL Server TOP is preserved");
Check(SqlRowLimit.Apply("SELECT 1; SELECT 2",DatabaseKind.SqlServer,500)=="SELECT 1; SELECT 2","multi-statement SQL Server batch is left untouched");
Check(SqlRowLimit.Apply("SELECT * FROM (SELECT * FROM t LIMIT 5) x",DatabaseKind.PostgreSQL,100).EndsWith("LIMIT 101"),"nested LIMIT does not block the outer cap");
Check(SqlRowLimit.Apply("SELECT 'limit into order by for update' AS note FROM t",DatabaseKind.PostgreSQL,100).EndsWith("LIMIT 101"),"keywords inside literals do not block the cap");
Check(SqlRowLimit.Apply("SELECT \"order\", \"limit\" FROM t",DatabaseKind.PostgreSQL,100).EndsWith("LIMIT 101"),"quoted identifiers do not block the cap");
Check(SqlRowLimit.Apply("EXPLAIN SELECT * FROM t",DatabaseKind.PostgreSQL,100)=="EXPLAIN SELECT * FROM t","EXPLAIN is left untouched");
Check(SqlRowLimit.Apply("SELECT * FROM t",DatabaseKind.TDengine,100).EndsWith("LIMIT 101"),"TDengine execution cap appends LIMIT");
Check(SqlRowLimit.Apply("SELECT * FROM t WHERE s='it''s limit'",DatabaseKind.MySQL,100).EndsWith("LIMIT 101"),"escaped quotes keep the parser in sync");
Check(SqlRowLimit.Apply("SELECT $tag$; limit $tag$ AS x",DatabaseKind.PostgreSQL,100).EndsWith("LIMIT 101"),"dollar-quoted body keeps the parser in sync");
Check(SqlRowLimit.Apply("SELECT * FROM t;;",DatabaseKind.MySQL,100).EndsWith("LIMIT 101;;"),"trailing empty statements are preserved");
Check(SqlRowLimit.Apply("SELECT 1--2",DatabaseKind.MySQL,100)=="SELECT 1--2 LIMIT 101","MySQL double dash without whitespace is not a comment");
Check(SqlRowLimit.Apply("SELECT 1",DatabaseKind.MySQL,0)=="SELECT 1","non-positive cap is ignored");
Check(SqlRowLimit.Apply(";WITH c AS (SELECT * FROM t) SELECT * FROM c",DatabaseKind.SqlServer,100).StartsWith(";WITH c")&&SqlRowLimit.Apply(";WITH c AS (SELECT * FROM t) SELECT * FROM c",DatabaseKind.SqlServer,100).EndsWith("FETCH NEXT 101 ROWS ONLY"),"leading semicolon before a CTE is still capped");
var formatted=SqlTools.Format("select 'select from', \"from\", `select` from t -- select\nwhere x = 'it''s from';");
var ddlFormatted=SqlTools.FormatDdl("CREATE TABLE t (id int, amount decimal(12,3), note varchar(80) DEFAULT 'a,b (value)');");
Check(ddlFormatted.Contains($"{Environment.NewLine}    id int,")&&ddlFormatted.Contains("decimal(12, 3)")&&ddlFormatted.Contains("'a,b (value)'"),"DDL formatting separates columns and preserves nested types and literals");
foreach(var newline in new[]{"\n","\r\n","\r"}){
    var commentDdl=SqlTools.FormatDdl($"CREATE TABLE t (id int, -- keep comment{newline} name text);");
    Check(commentDdl.Contains($"-- keep comment{Environment.NewLine}    name text"),
        $"DDL formatting preserves line-comment boundaries for {JsonSerializer.Serialize(newline)} input");
}
Check(formatted==string.Join(Environment.NewLine,["SELECT 'select from',","    \"from\",","    `select`","FROM t -- select","WHERE x = 'it''s from';"]),"formatter lays out clauses and preserves literals, identifiers and comments");
Check(SqlTools.Format(formatted)==formatted,"formatter is idempotent");
var layoutFormatted=SqlTools.Format("select a, b from t left join u on u.id = t.id where a between 1 and 5 and b = 2 order by a;");
Check(layoutFormatted==string.Join(Environment.NewLine,["SELECT a,","    b","FROM t","LEFT JOIN u","    ON u.id = t.id","WHERE a BETWEEN 1 AND 5","    AND b = 2","ORDER BY a;"]),"formatter breaks lists, joins and conditions");
Check(SqlTools.Format("select $tag$select from t$tag$ from t")==string.Join(Environment.NewLine,["SELECT $tag$select from t$tag$","FROM t"]),"formatter leaves dollar-quoted bodies untouched");
Check(SqlTools.Format("DELIMITER //\nselect 1;\nDELIMITER ;")==string.Join(Environment.NewLine,["DELIMITER //","SELECT 1;","DELIMITER ;"]),"formatter keeps delimiter commands on their own line");
Check(SqlTools.CsvCell("a,\"b\"\nc")=="\"a,\"\"b\"\"\nc\"","CSV handles quotes, commas and newlines");
Check(SqlTools.Display(null)=="NULL","NULL rendering");
Check(SqlTools.Display(new byte[]{0,255})=="0x00FF","binary preview");
var typed=ResultTable.Create(new QueryResult(["n","empty"],[new object?[]{10,null},new object?[]{2,"text"}],0,false));
typed.DefaultView.Sort="c0 ASC";
Check((int)typed.DefaultView[0][0]==2,"result grid sorts numbers numerically");
Check(typed.Rows[0].IsNull(1),"result grid preserves database NULL");
await store.SaveSettingsAsync(new AppSettings{Dark=true,FontSize=17});
Check((await store.LoadSettingsAsync()).FontSize==17,"settings persistence");
Check(ShortcutCatalog.Actions.Select(a=>a.Id).Distinct().Count()==ShortcutCatalog.Actions.Count,"shortcut action ids are unique");
Check(ShortcutCatalog.Actions.Select(a=>a.Title).Distinct().Count()==ShortcutCatalog.Actions.Count,"shortcut action titles are unique");
Check(ShortcutCatalog.Actions.All(a=>(a.WindowsDefault is null)==(a.MacDefault is null)),"shortcut defaults pair across platforms");
foreach(var mac in new[]{false,true}){
    var defaults=ShortcutCatalog.Actions.Select(a=>ShortcutCatalog.DefaultFor(a,mac)).Where(text=>text is not null).ToArray();
    Check(defaults.All(text=>ShortcutCatalog.Parse(text) is not null),(mac?"macOS":"Windows")+" shortcut defaults parse");
    Check(defaults.Select(text=>ShortcutCatalog.Canonical(ShortcutCatalog.Parse(text!)!)).Distinct().Count()==defaults.Length,(mac?"macOS":"Windows")+" shortcut defaults are unique");
}
var resolvedSettings=new AppSettings();
Check(ShortcutCatalog.Same(ShortcutCatalog.EffectiveText(resolvedSettings,"query.execute"),ShortcutCatalog.DefaultFor("query.execute")),"shortcut resolution falls back to the platform default");
resolvedSettings.Shortcuts["query.execute"]="Ctrl+Alt+9";
Check(ShortcutCatalog.Same(ShortcutCatalog.EffectiveText(resolvedSettings,"query.execute"),"Ctrl+Alt+9"),"shortcut resolution uses a custom combination");
resolvedSettings.Shortcuts["query.execute"]="not-a-key";
Check(ShortcutCatalog.Same(ShortcutCatalog.EffectiveText(resolvedSettings,"query.execute"),ShortcutCatalog.DefaultFor("query.execute")),"invalid custom shortcut falls back to the default");
resolvedSettings.Shortcuts["query.new"]="";
Check(ShortcutCatalog.EffectiveGesture(resolvedSettings,"query.new") is null,"empty custom shortcut disables a binding");
var bindingMap=ShortcutCatalog.Actions.ToDictionary(a=>a.Id,a=>ShortcutCatalog.DefaultFor(a,false));
Check(ShortcutCatalog.Conflict(bindingMap,"view.sidebar","Ctrl+R")=="执行 SQL","shortcut conflicts report the existing action");
Check(ShortcutCatalog.Conflict(bindingMap,"view.sidebar","F12") is null,"unused shortcut has no conflict");
var legacyJson="{\"ExecuteGesture\":\"Ctrl+Alt+Enter\",\"CompletionGesture\":\"Alt+OemQuestion\"}";
var migratedSettings=new AppSettings();ShortcutCatalog.MigrateLegacy(migratedSettings,legacyJson);
Check(migratedSettings.Shortcuts.GetValueOrDefault("query.execute")=="Ctrl+Alt+Enter"&&migratedSettings.Shortcuts.GetValueOrDefault("query.complete")=="Alt+OemQuestion","legacy gesture settings migrate to the shortcut catalog");
var defaultLegacy=new AppSettings();ShortcutCatalog.MigrateLegacy(defaultLegacy,"{\"ExecuteGesture\":\"Ctrl+Enter\",\"CompletionGesture\":\"Ctrl+Space\"}");
Check(defaultLegacy.Shortcuts.Count==0,"legacy defaults are not persisted as overrides");
var existingLegacy=new AppSettings{Shortcuts=new(){{"query.execute","Ctrl+Alt+Q"}}};ShortcutCatalog.MigrateLegacy(existingLegacy,legacyJson);
Check(existingLegacy.Shortcuts["query.execute"]=="Ctrl+Alt+Q","new shortcut settings take precedence over legacy values");
var legacyFolder=Path.Combine(folder,"legacy-settings");Directory.CreateDirectory(legacyFolder);
await File.WriteAllTextAsync(Path.Combine(legacyFolder,"settings.json"),legacyJson);
var migratedWorkspace=await new WorkspaceStore(legacyFolder).LoadSettingsAsync();
Check(migratedWorkspace.Shortcuts.GetValueOrDefault("query.execute")=="Ctrl+Alt+Enter","workspace loads and migrates legacy shortcut settings");
var shortcutSettings=new AppSettings();shortcutSettings.Shortcuts["query.execute"]="Ctrl+Alt+9";shortcutSettings.Shortcuts["query.new"]="";
await store.SaveSettingsAsync(shortcutSettings);
var storedShortcutSettings=await store.LoadSettingsAsync();
Check(storedShortcutSettings.Shortcuts.GetValueOrDefault("query.execute")=="Ctrl+Alt+9"&&storedShortcutSettings.Shortcuts.GetValueOrDefault("query.new")=="","shortcut overrides persist through settings.json");
await store.SaveGroupsAsync(["生产","测试"]);
Check((await store.LoadGroupsAsync()).SequenceEqual(new[]{"生产","测试"}),"connection group persistence");
await store.SaveHistoryAsync([new(DateTimeOffset.UtcNow,profile.Id,profile.Name,"SELECT 'private-literal';",7,1,"warehouse")]);
Check(!File.ReadAllText(Path.Combine(folder,"history.json")).Contains("private-literal"),"query history is encrypted at rest");
var history=await store.LoadHistoryAsync();
Check(history[0].Sql=="SELECT 'private-literal';" && history[0].Database=="warehouse","query history preserves SQL and database context");
var tdProfile=new ConnectionProfile{Kind=DatabaseKind.TDengine,Host="localhost",Port=6041,Database="meters",Username="root",Password="taosdata"};
var handler=new StubHttpHandler(async (request,ct)=>{
    Check(request.RequestUri!.AbsolutePath=="/rest/sql/meters","TDengine routes to selected database");
    Check(request.Headers.Authorization?.Scheme=="Basic","TDengine sends authentication header");
    Check((await request.Content!.ReadAsStringAsync(ct)).Contains("SELECT"),"TDengine posts SQL body");
    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"code\":0,\"column_meta\":[[\"ts\",9,8],[\"value\",6,4]],\"data\":[[\"2026-01-01\",12],[null,13]],\"rows\":2}")};
});
await using(var td=new TDengineProvider(tdProfile,handler)){
    var result=await td.QueryAsync("SELECT * FROM meters LIMIT 2",1,CancellationToken.None);
    Check(result.Columns.Count==2 && result.Rows.Count==1 && result.Truncated,"TDengine parses metadata and enforces display limit");
}
await using(var td=new TDengineProvider(tdProfile,new StubHttpHandler((_,_)=>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"code\":0,\"column_meta\":[[\"a\",4,4],[\"b\",4,4]],\"data\":[{\"b\":null,\"a\":42}]}")})))){
    var result=await td.QueryAsync("SELECT 42",1,CancellationToken.None);Check(result.Rows[0][0]?.ToString()=="42"&&result.Rows[0][1] is null,"TDengine object rows follow metadata column order and preserve NULL");
}
await using(var td=new TDengineProvider(tdProfile,new StubHttpHandler((_,_)=>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"code\":123,\"desc\":\"syntax error\"}")})))){
    bool failed=false;try{await td.QueryAsync("bad SQL",1,CancellationToken.None);}catch(InvalidOperationException ex){failed=ex.Message.Contains("syntax error");}Check(failed,"TDengine surfaces server errors");
}
string executedSql="";var editorSql="SELECT * FROM meters";
await using(var td=new TDengineProvider(tdProfile,new StubHttpHandler(async (request,ct)=>{
    executedSql=await request.Content!.ReadAsStringAsync(ct);
    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"code\":0,\"column_meta\":[[\"ts\",9,8]],\"data\":[[\"2026-01-01\"]]}")};
}))){
    var capped=await ScriptRunner.RunAsync(td,new StringReader(editorSql),DatabaseKind.TDengine,false,500,null,CancellationToken.None,capReadOnlyQueries:true);
    Check(capped.Count==1&&capped[0].Error is null&&executedSql.Trim()=="SELECT * FROM meters LIMIT 501","query window execution applies the cap");
    Check(editorSql=="SELECT * FROM meters","query window text is not rewritten");
    await ScriptRunner.RunAsync(td,new StringReader(editorSql),DatabaseKind.TDengine,false,500,null,CancellationToken.None,capReadOnlyQueries:false);
    Check(executedSql.Trim()=="SELECT * FROM meters","non-query scripts keep their statements untouched");
}
await using(var td=new TDengineProvider(tdProfile,new StubHttpHandler(async (_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return new System.Net.Http.HttpResponseMessage();}))){
    using var cancel=new CancellationTokenSource(30);bool cancelled=false;try{await td.QueryAsync("SELECT 1",1,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"TDengine query honors cancellation");
}
Environment.SetEnvironmentVariable("DBPILOT_DATA_DIR",Path.Combine(folder,"ui"));
await EditingChecks.Run(Check);
await EditingViewModelChecks.Run(Check);
await AdvancedChecks.Run(Check,folder);
await DatabaseFeatureChecks.Run(Check);
await LargeFieldChecks.Run(Check);
AppBuilder.Configure<DbPilot.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
var window=new MainWindow();
window.Show();
Dispatcher.UIThread.RunJobs();
var mainVm=(DbPilot.ViewModels.MainViewModel)window.DataContext!;
Check(window.FindControl<TextEditor>("SqlEditor") is not null,"main window creates SQL editor");
Check(mainVm.Tabs.Count==0&&!window.FindControl<Border>("EditorPane")!.IsVisible&&!window.FindControl<Grid>("ConnectionBar")!.IsVisible&&!window.FindControl<Grid>("QueryBar")!.IsVisible&&!window.FindControl<Grid>("ResultsPane")!.IsVisible&&window.FindControl<Grid>("StartPanel")!.IsVisible,"fresh window starts on a clean empty workspace");
mainVm.NewQuery();Dispatcher.UIThread.RunJobs();
Check(window.FindControl<Border>("EditorPane")!.IsVisible&&window.FindControl<Grid>("ConnectionBar")!.IsVisible&&window.FindControl<Grid>("QueryBar")!.IsVisible&&!window.FindControl<Grid>("StartPanel")!.IsVisible,"creating a query reveals the editor and hides the empty workspace");
Check(window.FindControl<TextEditor>("SqlEditor")!.Text.Contains("SELECT 1"),"new SQL tab populates editor");
Check(window.FindControl<TextEditor>("SqlEditor")!.SyntaxHighlighting is not null,"SQL syntax highlighting is configured");
var formatEditor=window.FindControl<TextEditor>("SqlEditor")!;
formatEditor.Text="select id, name from items where id = 1";Dispatcher.UIThread.RunJobs();
typeof(MainWindow).GetMethod("FormatSql",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,[null,new Avalonia.Interactivity.RoutedEventArgs()]);
Dispatcher.UIThread.RunJobs();
var formattedEditorSql=string.Join(Environment.NewLine,["SELECT id,","    name","FROM items","WHERE id = 1"]);
Check(formatEditor.Text==formattedEditorSql,"format action rewrites the editor SQL");
Check(mainVm.Tab?.Sql==formattedEditorSql,"format action updates the query tab text");
Check(window.FindControl<DataGrid>("Results") is not null,"main window creates result grid");
Check(window.FindControl<TreeView>("ObjectTree") is not null,"main window creates object tree");
Check(!window.FindControl<Button>("CommitEditsButton")!.IsEnabled,"arbitrary SQL result has no enabled commit action");
var buildMenu=typeof(MainWindow).GetMethod("BuildTreeContextMenu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
string[] MenuFor(ObjectNode node){buildMenu.Invoke(window,[node]);return window.FindControl<TreeView>("ObjectTree")!.ContextMenu!.ItemsSource!.OfType<MenuItem>().Select(m=>m.Header?.ToString()??"").ToArray();}
var connectionMenu=MenuFor(new(){Profile=profile,IsConnection=true});
Check(connectionMenu.Contains("打开连接")&&connectionMenu.Contains("编辑连接")&&!connectionMenu.Contains("删除表"),"connection context menu contains only connection actions");
var databaseMenu=MenuFor(new(){Profile=profile,Database="sandbox"});
Check(databaseMenu.Contains("查看表列表")&&databaseMenu.Contains("新建表")&&!databaseMenu.Contains("编辑连接"),"database context menu contains database actions");
var tableMenu=MenuFor(new(){Profile=profile,Database="sandbox",Object=new("sandbox","public","items","表")});
Check(tableMenu.Contains("查看表结构与建表语句")&&tableMenu.Contains("删除表")&&!tableMenu.Contains("关闭连接"),"table context menu contains table actions");
var tree=window.FindControl<TreeView>("ObjectTree")!;
var treeProfile=new ConnectionProfile{Name="tree-probe",Kind=DatabaseKind.MySQL};
var treeConnectionNode=new ObjectNode{Label=treeProfile.Name,Profile=treeProfile,IsConnection=true,Kind=ObjectNodeKind.Connection,Loaded=true};
var treeTableNode=new ObjectNode{Label="items",Profile=treeProfile,Database="sandbox",Object=new("sandbox","","items","表"),Kind=ObjectNodeKind.Table,Loaded=true};
treeTableNode.Children.Add(new(){Label="展开查看字段",Profile=treeProfile,Kind=ObjectNodeKind.Hint,Loaded=true});
treeConnectionNode.Children.Add(treeTableNode);
mainVm.Connections.Add(treeProfile);mainVm.Connection=treeProfile;mainVm.Disconnect();mainVm.Nodes.Add(treeConnectionNode);
window.UpdateLayout();Dispatcher.UIThread.RunJobs();
var connectionItem=tree.GetVisualDescendants().OfType<TreeViewItem>().First(i=>ReferenceEquals(i.DataContext,treeConnectionNode));
connectionItem.IsExpanded=true;window.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();
var tableItem=connectionItem.GetVisualDescendants().OfType<TreeViewItem>().First(i=>ReferenceEquals(i.DataContext,treeTableNode));
var tableLabel=tableItem.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text=="items");
var tablePoint=tableLabel.TranslatePoint(new Point(tableLabel.Bounds.Width/2,tableLabel.Bounds.Height/2),window)!.Value;
window.MouseDown(tablePoint,Avalonia.Input.MouseButton.Left);window.MouseUp(tablePoint,Avalonia.Input.MouseButton.Left);Dispatcher.UIThread.RunJobs();
Check(mainVm.Status.Contains("请先打开连接再浏览数据"),"single click on a table node opens its data instead of only expanding fields");
mainVm.Log("idle");
var chevron=tableItem.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(t=>t.Name=="PART_ExpandCollapseChevron");
Check(chevron.Bounds.Width>=16,"table chevron keeps a comfortable hit target");
var chevronPoint=chevron.TranslatePoint(new Point(chevron.Bounds.Width/2,chevron.Bounds.Height/2),window)!.Value;
window.MouseDown(chevronPoint,Avalonia.Input.MouseButton.Left);window.MouseUp(chevronPoint,Avalonia.Input.MouseButton.Left);Dispatcher.UIThread.RunJobs();
Check(mainVm.Status=="idle","clicking the table chevron toggles fields without opening data");
treeTableNode.Children.Clear();mainVm.Nodes.Remove(treeConnectionNode);mainVm.Connections.Remove(treeProfile);mainVm.Connection=null;Dispatcher.UIThread.RunJobs();
CompletionChecks.Run(window,mainVm,Check);
DatabaseFeatureChecks.RunUi(window,Check);
var uiResult=new QueryResult(["id","description","quantity","happened"],[new object?[]{1,"Editable row",10,new DateTime(2026,10,18,12,0,0)},new object?[]{2,"Zebra row",20,new DateTime(2026,10,19,8,30,0)}],0,false,[typeof(int),typeof(string),typeof(int),typeof(DateTime)]);
mainVm.Tab!.Result=uiResult;
mainVm.Tab.Edits=new TableEditSession(uiResult,[new("id","int",false,true,"","",IsGenerated:true),new("description","text",true,false,"",""),new("quantity","int",false,false,"",""),new("happened","datetime",true,false,"","")]);
mainVm.SelectTab(mainVm.Tab);Dispatcher.UIThread.RunJobs();
Check(!window.FindControl<DataGrid>("Results")!.IsReadOnly && window.FindControl<Button>("CommitEditsButton")!.IsEnabled,"table edit session enables grid and transaction actions");
Check(window.FindControl<DataGrid>("Results")!.Columns[0].IsReadOnly && !window.FindControl<DataGrid>("Results")!.Columns[1].IsReadOnly,"grid keeps generated fields read-only while allowing normal columns");
mainVm.Tab.Edits.Data.Rows[0][1]="Uncommitted change";Dispatcher.UIThread.RunJobs();
Check(window.FindControl<TextBlock>("EditStatus")!.Text!.Contains("1"),"edit toolbar displays pending row count");
var retainedTab=mainVm.Tab;mainVm.NewQuery();mainVm.Tab=retainedTab;Dispatcher.UIThread.RunJobs();
Check(ReferenceEquals(((ResultRows)window.FindControl<DataGrid>("Results")!.ItemsSource!).Table,retainedTab.Edits.Data) && retainedTab.Edits.PendingCount==1,"switching query tabs preserves the same edit session");
using(var rendered=window.CaptureRenderedFrame()){
    Check(window.FindControl<DataGrid>("Results")!.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Uncommitted change"),"result-grid binding renders actual cell values");
    Check(window.FindControl<DataGrid>("Results")!.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="2026-10-18 12:00:00"),"result-grid formats date/time values as yyyy-MM-dd HH:mm:ss");
}
var editGrid=window.FindControl<DataGrid>("Results")!;
var displayedRows=(ResultRows)editGrid.ItemsSource!;
editGrid.SelectedItem=displayedRows[0];editGrid.CurrentColumn=editGrid.Columns[1];editGrid.Focus();
Check(editGrid.BeginEdit(),"result grid can enter a real cell editing control");Dispatcher.UIThread.RunJobs();
var cellEditor=editGrid.GetVisualDescendants().OfType<TextBox>().First(t=>t.IsVisible);
cellEditor.Text="Changed through grid editor";
Check(editGrid.CommitEdit(DataGridEditingUnit.Cell,true) && editGrid.CommitEdit(DataGridEditingUnit.Row,true),"result grid commits its editor binding");
Check(retainedTab.Edits.Data.Rows[0][1].Equals("Changed through grid editor"),"two-way grid editing updates the staged DataRow");
editGrid.CurrentColumn=editGrid.Columns[2];Check(editGrid.BeginEdit(),"typed numeric cell can enter editing");Dispatcher.UIThread.RunJobs();
editGrid.GetVisualDescendants().OfType<TextBox>().First(t=>t.IsVisible).Text="42";
Check(editGrid.CommitEdit(DataGridEditingUnit.Cell,true) && editGrid.CommitEdit(DataGridEditingUnit.Row,true) && retainedTab.Edits.Data.Rows[0][2] is int quantity && quantity==42,"numeric cell editing retains integer parameter type");
editGrid.CurrentColumn=editGrid.Columns[3];Check(editGrid.BeginEdit(),"date cell can enter editing");Dispatcher.UIThread.RunJobs();
var dateEditor=editGrid.GetVisualDescendants().OfType<TextBox>().First(t=>t.IsVisible);
Check(dateEditor.Text=="2026-10-18 12:00:00","date editor uses yyyy-MM-dd HH:mm:ss");
dateEditor.Text="2026-10-20 09:15:30";
Check(editGrid.CommitEdit(DataGridEditingUnit.Cell,true) && editGrid.CommitEdit(DataGridEditingUnit.Row,true) && retainedTab.Edits.Data.Rows[0][3] is DateTime happened && happened==new DateTime(2026,10,20,9,15,30),"date cell editing parses the display format back into DateTime");
var sortHeader=editGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h=>h.Content?.ToString()=="description");
var headerPoint=sortHeader.TranslatePoint(new Point(sortHeader.Bounds.Width/2,sortHeader.Bounds.Height/2),window)!.Value;
for(int click=0;click<2;click++){window.MouseDown(headerPoint,Avalonia.Input.MouseButton.Left);window.MouseUp(headerPoint,Avalonia.Input.MouseButton.Left);Dispatcher.UIThread.RunJobs();}
Check((int)displayedRows[0].Row[0]==2,"column-header sorting reorders displayed row adapters");
LargeFieldChecks.RunUi(window,mainVm,retainedTab,Check);
var previewPath=Environment.GetEnvironmentVariable("DBPILOT_PREVIEW");
if(!string.IsNullOrEmpty(previewPath)) {
    using var frame=window.CaptureRenderedFrame();
    Check(frame is not null,"Skia renders main window");frame!.Save(previewPath);
}
var darkPreview=Environment.GetEnvironmentVariable("DBPILOT_DARK_PREVIEW");
if(!string.IsNullOrEmpty(darkPreview)){
    var toggle=typeof(MainWindow).GetMethod("ToggleTheme",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    toggle.Invoke(window,[null,new Avalonia.Interactivity.RoutedEventArgs()]);Dispatcher.UIThread.RunJobs();
    using(var frame=window.CaptureRenderedFrame()){frame!.Save(darkPreview);}
    toggle.Invoke(window,[null,new Avalonia.Interactivity.RoutedEventArgs()]);Dispatcher.UIThread.RunJobs();
}
var dialog=new ConnectionDialog();dialog.Show(window);Dispatcher.UIThread.RunJobs();
Check(dialog.IsVisible,"connection dialog opens without XAML or binding exceptions");
var toolsWindow=new DatabaseToolsWindow([profile],profile,(_,_)=>{});toolsWindow.Show(window);Dispatcher.UIThread.RunJobs();
var toolTabs=toolsWindow.GetVisualDescendants().OfType<TabControl>().First();
for(int i=0;i<toolTabs.ItemCount;i++){toolTabs.SelectedIndex=i;Dispatcher.UIThread.RunJobs();}
Check(toolTabs.ItemCount>=10&&toolsWindow.IsVisible,"database tools window constructs every feature tab without database access");
var toolsPreview=Environment.GetEnvironmentVariable("DBPILOT_TOOLS_PREVIEW");
if(!string.IsNullOrEmpty(toolsPreview)){toolTabs.SelectedIndex=0;Dispatcher.UIThread.RunJobs();using var frame=toolsWindow.CaptureRenderedFrame();frame!.Save(toolsPreview);}
toolsWindow.Close();
await using(var provider=DbProviderFactory.Create(new(){Kind=DatabaseKind.PostgreSQL})){
    var designer=new ObjectDesignerWindow(provider,DatabaseKind.PostgreSQL,"sandbox",null);designer.Show(window);Dispatcher.UIThread.RunJobs();
    designer.GetVisualDescendants().OfType<Button>().First(b=>b.Content?.ToString()=="生成 SQL").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
    Check(designer.GetVisualDescendants().OfType<TextBox>().Any(t=>t.Text?.Contains("CREATE TABLE")==true),"object designer generates SQL through the real UI");designer.Close();
    var builder=new QueryBuilderWindow(provider,DatabaseKind.PostgreSQL,new("sandbox","public","items","表"),[new("id","integer",false,true,"",""),new("label","text",true,false,"","")]);builder.Show(window);Dispatcher.UIThread.RunJobs();
    builder.GetVisualDescendants().OfType<Button>().First(b=>b.Content?.ToString()=="生成查询").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
    Check(builder.GetVisualDescendants().OfType<TextBox>().Any(t=>t.Text?.Contains("LIMIT 500")==true&&t.Text.Contains("\"label\"")),"query builder generates quoted projection and bounded query through UI");builder.Close();
}
var visualization=new VisualizationWindow();visualization.ShowChart(new QueryResult(["ts","value"],[new object?[]{"2026-01-01T00:00:00Z",1},new object?[]{"2026-01-01T00:01:00Z",null},new object?[]{"2026-01-01T00:02:00Z",3}],0,false));visualization.Show(window);Dispatcher.UIThread.RunJobs();
Check(visualization.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("2 个有效数据点")==true),"chart renders only real numeric values and handles NULL gaps");visualization.Close();
var er=new VisualizationWindow();er.ShowEr([(new("sandbox","public","items","表"),new ColumnInfo[]{new("id","integer",false,true,"","")})],[]);er.Show(window);Dispatcher.UIThread.RunJobs();
Check(er.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="public.items"),"ER diagram renders actual table metadata");er.Close();
dialog.Close();
var applySettings=typeof(MainWindow).GetMethod("ApplySettings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
applySettings.Invoke(window,[]);Dispatcher.UIThread.RunJobs();
var fileNewQuery=window.FindControl<MenuItem>("FileNewQuery")!;
var queryNewDefault=ShortcutCatalog.Parse(ShortcutCatalog.DefaultFor("query.new")??throw new InvalidOperationException())!;
Check(fileNewQuery.InputGesture is {} menuGesture&&ShortcutCatalog.Canonical(menuGesture)==ShortcutCatalog.Canonical(queryNewDefault),"menu labels show the effective shortcut");
var sidebar=window.FindControl<Border>("SidebarPanel")!;
var sidebarGesture=ShortcutCatalog.Parse(ShortcutCatalog.DefaultFor("view.sidebar")??throw new InvalidOperationException())!;
bool sidebarInitial=sidebar.IsVisible;
Press(window,sidebarGesture.Key,(RawInputModifiers)sidebarGesture.KeyModifiers);Dispatcher.UIThread.RunJobs();
Check(sidebar.IsVisible!=sidebarInitial,"default shortcut dispatches through the main window");
Press(window,sidebarGesture.Key,(RawInputModifiers)sidebarGesture.KeyModifiers);Dispatcher.UIThread.RunJobs();
Check(sidebar.IsVisible==sidebarInitial,"default shortcut toggles the workspace back");
mainVm.Settings.Shortcuts["view.sidebar"]="F12";
applySettings.Invoke(window,[]);Dispatcher.UIThread.RunJobs();
Press(window,Key.F12,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
bool customSidebar=sidebar.IsVisible;
Check(customSidebar!=sidebarInitial,"custom shortcut replaces the default binding");
Press(window,sidebarGesture.Key,(RawInputModifiers)sidebarGesture.KeyModifiers);Dispatcher.UIThread.RunJobs();
Check(sidebar.IsVisible==customSidebar,"overridden default shortcut no longer fires");
Press(window,Key.F12,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
Check(sidebar.IsVisible==sidebarInitial,"custom shortcut toggles the workspace back");
mainVm.Settings.Shortcuts.Remove("view.sidebar");
mainVm.Settings.Shortcuts["query.new"]="Ctrl+Alt+F12";
applySettings.Invoke(window,[]);Dispatcher.UIThread.RunJobs();
Check(fileNewQuery.InputGesture is {} refreshedMenu&&ShortcutCatalog.Canonical(refreshedMenu)==ShortcutCatalog.Canonical(ShortcutCatalog.Parse("Ctrl+Alt+F12")!),"menu labels follow a customized shortcut");
mainVm.Settings.Shortcuts.Remove("query.new");
applySettings.Invoke(window,[]);Dispatcher.UIThread.RunJobs();
var settingsToSave=new AppSettings();bool settingsSaved=false;
var settingsWindow=new SettingsWindow(settingsToSave,()=>{settingsSaved=true;return Task.CompletedTask;});
settingsWindow.Show(window);settingsWindow.Activate();Dispatcher.UIThread.RunJobs();
settingsWindow.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex=1;Dispatcher.UIThread.RunJobs();
var gestureButtons=(Dictionary<string,Button>)typeof(SettingsWindow).GetField("_gestureButtons",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(settingsWindow)!;
var shortcutBindings=(Dictionary<string,string?>)typeof(SettingsWindow).GetField("_bindings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(settingsWindow)!;
Check(gestureButtons.Count==ShortcutCatalog.Actions.Count&&shortcutBindings.Count==ShortcutCatalog.Actions.Count,"settings window lists every configurable shortcut");
gestureButtons["view.sidebar"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
Press(settingsWindow,Key.F5,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
Check(ShortcutCatalog.Same(shortcutBindings["view.sidebar"],ShortcutCatalog.DefaultFor("view.sidebar")),"settings rejects a conflicting shortcut");
Check(settingsWindow.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("已被")==true),"settings explains the shortcut conflict");
Press(settingsWindow,Key.F12,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
Check(shortcutBindings["view.sidebar"]=="F12","settings captures a new shortcut");
settingsWindow.GetVisualDescendants().OfType<Button>().First(b=>b.Content?.ToString()=="保存设置").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
Check(settingsSaved&&settingsWindow.Saved&&!settingsWindow.IsVisible,"settings window saves through the provided callback");
Check(settingsToSave.Shortcuts.Count==1&&settingsToSave.Shortcuts.GetValueOrDefault("view.sidebar")=="F12","settings persist only non-default shortcut overrides");
ShortcutChecks.Run(window,mainVm,Check);
retainedTab.Edits.Revert();foreach(var tab in mainVm.Tabs)tab.SavedSql=tab.Sql;window.Close();Dispatcher.UIThread.RunJobs();
Console.WriteLine($"All {count} checks passed. Temporary test data: {folder}");

sealed class StubHttpHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request,cancellationToken);
}
