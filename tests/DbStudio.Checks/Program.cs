using System.Security.Cryptography;
using System.Text.Json;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using DbStudio.Views;
using DbStudio.Checks;
using Avalonia.VisualTree;

if(args.Contains("--mysql-live")){Environment.ExitCode=await MySqlLiveChecks.Run();return;}
if(args.Contains("--pgsql-live")){Environment.ExitCode=await PostgreSqlLiveChecks.Run();return;}
if(args.Contains("--tdengine-live")){Environment.ExitCode=await TDengineLiveChecks.Run();return;}
if(args.Contains("--catalog-live")){Environment.ExitCode=await CatalogLiveChecks.Run();return;}
if(args.Contains("--sqlserver-live")){Environment.ExitCode=await SqlServerLiveChecks.Run();return;}

var count=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAILED: "+name);Console.WriteLine("PASS "+name);count++;}
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
var folder=Path.Combine(Path.GetTempPath(),"DbStudio-checks-"+Guid.NewGuid().ToString("N"));
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
var formatted=SqlTools.Format("select 'select from', \"from\", `select` from t -- select\nwhere x = 'it''s from';");
var ddlFormatted=SqlTools.FormatDdl("CREATE TABLE t (id int, amount decimal(12,3), note varchar(80) DEFAULT 'a,b (value)');");
Check(ddlFormatted.Contains("\n    id int,")&&ddlFormatted.Contains("decimal(12,3)")&&ddlFormatted.Contains("'a,b (value)'"),"DDL formatting separates columns and preserves nested types and literals");
Check(SqlTools.FormatDdl("CREATE TABLE t (id int, -- keep comment\n name text);").Contains("-- keep comment\n"),"DDL formatting preserves line-comment boundaries");
Check(formatted.Contains("SELECT 'select from', \"from\", `select` FROM t -- select"),"formatter preserves literals, identifiers and comments");
Check(SqlTools.CsvCell("a,\"b\"\nc")=="\"a,\"\"b\"\"\nc\"","CSV handles quotes, commas and newlines");
Check(SqlTools.Display(null)=="NULL","NULL rendering");
Check(SqlTools.Display(new byte[]{0,255})=="0x00FF","binary preview");
var typed=ResultTable.Create(new QueryResult(["n","empty"],[new object?[]{10,null},new object?[]{2,"text"}],0,false));
typed.DefaultView.Sort="c0 ASC";
Check((int)typed.DefaultView[0][0]==2,"result grid sorts numbers numerically");
Check(typed.Rows[0].IsNull(1),"result grid preserves database NULL");
await store.SaveSettingsAsync(new AppSettings{Dark=true,FontSize=17});
Check((await store.LoadSettingsAsync()).FontSize==17,"settings persistence");
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
await using(var td=new TDengineProvider(tdProfile,new StubHttpHandler(async (_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return new System.Net.Http.HttpResponseMessage();}))){
    using var cancel=new CancellationTokenSource(30);bool cancelled=false;try{await td.QueryAsync("SELECT 1",1,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"TDengine query honors cancellation");
}
Environment.SetEnvironmentVariable("DBSTUDIO_DATA_DIR",Path.Combine(folder,"ui"));
await EditingChecks.Run(Check);
await EditingViewModelChecks.Run(Check);
await AdvancedChecks.Run(Check,folder);
await DatabaseFeatureChecks.Run(Check);
AppBuilder.Configure<DbStudio.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
var window=new MainWindow();
window.Show();
Dispatcher.UIThread.RunJobs();
Check(window.FindControl<TextEditor>("SqlEditor") is not null,"main window creates SQL editor");
Check(window.FindControl<TextEditor>("SqlEditor")!.Text.Contains("SELECT 1"),"initial SQL tab populates editor");
Check(window.FindControl<TextEditor>("SqlEditor")!.SyntaxHighlighting is not null,"SQL syntax highlighting is configured");
Check(window.FindControl<DataGrid>("Results") is not null,"main window creates result grid");
Check(window.FindControl<TreeView>("ObjectTree") is not null,"main window creates object tree");
Check(!window.FindControl<Button>("CommitEditsButton")!.IsEnabled,"arbitrary SQL result has no enabled commit action");
var mainVm=(DbStudio.ViewModels.MainViewModel)window.DataContext!;
var buildMenu=typeof(MainWindow).GetMethod("BuildTreeContextMenu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
string[] MenuFor(ObjectNode node){buildMenu.Invoke(window,[node]);return window.FindControl<TreeView>("ObjectTree")!.ContextMenu!.ItemsSource!.OfType<MenuItem>().Select(m=>m.Header?.ToString()??"").ToArray();}
var connectionMenu=MenuFor(new(){Profile=profile,IsConnection=true});
Check(connectionMenu.Contains("打开连接")&&connectionMenu.Contains("编辑连接")&&!connectionMenu.Contains("删除表"),"connection context menu contains only connection actions");
var databaseMenu=MenuFor(new(){Profile=profile,Database="sandbox"});
Check(databaseMenu.Contains("查看表列表")&&databaseMenu.Contains("新建表")&&!databaseMenu.Contains("编辑连接"),"database context menu contains database actions");
var tableMenu=MenuFor(new(){Profile=profile,Database="sandbox",Object=new("sandbox","public","items","表")});
Check(tableMenu.Contains("查看表结构与建表语句")&&tableMenu.Contains("删除表")&&!tableMenu.Contains("关闭连接"),"table context menu contains table actions");
CompletionChecks.Run(window,mainVm,Check);
DatabaseFeatureChecks.RunUi(window,Check);
var uiResult=new QueryResult(["id","description","quantity"],[new object?[]{1,"Editable row",10},new object?[]{2,"Zebra row",20}],0,false,[typeof(int),typeof(string),typeof(int)]);
mainVm.Tab!.Result=uiResult;
mainVm.Tab.Edits=new TableEditSession(uiResult,[new("id","int",false,true,"","",IsGenerated:true),new("description","text",true,false,"",""),new("quantity","int",false,false,"","")]);
mainVm.SelectTab(mainVm.Tab);Dispatcher.UIThread.RunJobs();
Check(!window.FindControl<DataGrid>("Results")!.IsReadOnly && window.FindControl<Button>("CommitEditsButton")!.IsEnabled,"table edit session enables grid and transaction actions");
Check(window.FindControl<DataGrid>("Results")!.Columns[0].IsReadOnly && !window.FindControl<DataGrid>("Results")!.Columns[1].IsReadOnly,"grid keeps generated fields read-only while allowing normal columns");
mainVm.Tab.Edits.Data.Rows[0][1]="Uncommitted change";Dispatcher.UIThread.RunJobs();
Check(window.FindControl<TextBlock>("EditStatus")!.Text!.Contains("1"),"edit toolbar displays pending row count");
var retainedTab=mainVm.Tab;mainVm.NewQuery();mainVm.Tab=retainedTab;Dispatcher.UIThread.RunJobs();
Check(ReferenceEquals(((ResultRows)window.FindControl<DataGrid>("Results")!.ItemsSource!).Table,retainedTab.Edits.Data) && retainedTab.Edits.PendingCount==1,"switching query tabs preserves the same edit session");
using(var rendered=window.CaptureRenderedFrame()){
    Check(window.FindControl<DataGrid>("Results")!.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Uncommitted change"),"result-grid binding renders actual cell values");
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
var sortHeader=editGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h=>h.Content?.ToString()=="description");
var headerPoint=sortHeader.TranslatePoint(new Point(sortHeader.Bounds.Width/2,sortHeader.Bounds.Height/2),window)!.Value;
for(int click=0;click<2;click++){window.MouseDown(headerPoint,Avalonia.Input.MouseButton.Left);window.MouseUp(headerPoint,Avalonia.Input.MouseButton.Left);Dispatcher.UIThread.RunJobs();}
Check((int)displayedRows[0].Row[0]==2,"column-header sorting reorders displayed row adapters");
var previewPath=Environment.GetEnvironmentVariable("DBSTUDIO_PREVIEW");
if(!string.IsNullOrEmpty(previewPath)) {
    using var frame=window.CaptureRenderedFrame();
    Check(frame is not null,"Skia renders main window");frame!.Save(previewPath);
}
var darkPreview=Environment.GetEnvironmentVariable("DBSTUDIO_DARK_PREVIEW");
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
var toolsPreview=Environment.GetEnvironmentVariable("DBSTUDIO_TOOLS_PREVIEW");
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
dialog.Close();retainedTab.Edits.Revert();foreach(var tab in mainVm.Tabs)tab.SavedSql=tab.Sql;window.Close();Dispatcher.UIThread.RunJobs();
Console.WriteLine($"All {count} checks passed. Temporary test data: {folder}");

sealed class StubHttpHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request,cancellationToken);
}
