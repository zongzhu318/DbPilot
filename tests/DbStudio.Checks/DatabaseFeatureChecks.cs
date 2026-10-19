using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;
using DbStudio.ViewModels;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbStudio.Views;

namespace DbStudio.Checks;
internal static class DatabaseFeatureChecks
{
    public static void RunUi(MainWindow window,Action<bool,string> check)
    {
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        var sidebar=window.FindControl<Grid>("SidebarPanel")!;
        var workspace=window.FindControl<Grid>("WorkspaceGrid")!;
        var editor=window.FindControl<Border>("EditorPane")!;
        var logs=window.FindControl<Expander>("TaskLogPanel")!;
        check(!logs.IsExpanded,"task logs default to collapsed to leave space for data");
        var originalSidebar=workspace.ColumnDefinitions[0].ActualWidth;
        var focus=typeof(MainWindow).GetMethod("ToggleFocusMode",flags)!;
        focus.Invoke(window,[null,new Avalonia.Interactivity.RoutedEventArgs()]);Dispatcher.UIThread.RunJobs();
        check(!sidebar.IsVisible&&!editor.IsVisible&&!logs.IsVisible&&workspace.ColumnDefinitions[0].Width.Value==0,"focus mode gives sidebar editor and log space to the workspace");
        typeof(MainWindow).GetMethod("CaptureWorkspaceLayout",flags)!.Invoke(window,null);
        var layoutVm=(MainViewModel)window.DataContext!;
        check(layoutVm.Settings.SidebarWidth>=180&&layoutVm.Settings.EditorRatio>0,"saving in focus mode preserves expanded layout dimensions");
        focus.Invoke(window,[null,new Avalonia.Interactivity.RoutedEventArgs()]);Dispatcher.UIThread.RunJobs();
        check(sidebar.IsVisible&&editor.IsVisible&&logs.IsVisible&&Math.Abs(workspace.ColumnDefinitions[0].Width.Value-originalSidebar)<1,"leaving focus mode restores previous panel visibility and sidebar width");
        var vm=(MainViewModel)window.DataContext!;var originalConnection=vm.Connection;var originalTab=vm.Tab;var profile=new ConnectionProfile{Name="选择数据库测试",Kind=DatabaseKind.PostgreSQL,Database="sales"};
        var cached=(Dictionary<Guid,IReadOnlyList<string>>)typeof(MainWindow).GetField("_databaseLists",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        cached[profile.Id]=["sales","warehouse"];vm.Connections.Add(profile);vm.Connection=profile;vm.NewQuery(profile:profile);Dispatcher.UIThread.RunJobs();
        var picker=window.FindControl<ComboBox>("DatabasePicker")!;
        check(picker.SelectedItem?.ToString()=="sales"&&picker.Items.Count==2,"main database selector displays selected connection databases");picker.SelectedItem="warehouse";Dispatcher.UIThread.RunJobs();
        check(vm.Tab?.Connection?.Database=="warehouse"&&profile.Database=="sales","actual database picker changes only active query context");
        var catalog=window.FindControl<Border>("TableCatalogPanel")!;
        catalog.IsVisible=true;
        typeof(MainWindow).GetMethod("CloseCatalog",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,null);
        check(!catalog.IsVisible,"leaving catalog hides its overlay immediately");
        var catalogField=typeof(MainWindow).GetField("_catalogTables",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        catalogField.SetValue(window,new DbObject[]{new("warehouse","public","orders","表"),new("warehouse","audit","events","表")});
        var filter=window.FindControl<TextBox>("TableCatalogFilter")!;filter.Text=" audit ";Dispatcher.UIThread.RunJobs();
        var catalogGrid=window.FindControl<DataGrid>("TableCatalogGrid")!;
        check(catalogGrid.ItemsSource!.Cast<DbObject>().Single().Name=="events","catalog filter trims input and searches schemas");
        filter.Text="missing_table";Dispatcher.UIThread.RunJobs();
        check(window.FindControl<TextBlock>("TableCatalogSummary")!.Text!.Contains("没有匹配"),"catalog reports no matches instead of a blank workspace");
        filter.Text="";Dispatcher.UIThread.RunJobs();
        check(catalogGrid.ItemsSource!.Cast<DbObject>().Count()==2,"clearing catalog search restores all objects");
        typeof(MainWindow).GetField("_catalogProfile",flags)!.SetValue(window,MainViewModel.CloneForDatabase(profile,"warehouse"));
        typeof(MainWindow).GetField("_catalogHasLoaded",flags)!.SetValue(window,true);
        var workspaceModes=window.FindControl<TabStrip>("WorkspaceModeTabs")!;
        var retainedSql=vm.Tab!.Sql;var retainedTabCount=vm.Tabs.Count;
        workspaceModes.SelectedIndex=0;Dispatcher.UIThread.RunJobs();
        check(catalog.IsVisible,"data tables workspace tab displays the catalog without replacing query tabs");
        filter.Text="audit";Dispatcher.UIThread.RunJobs();workspaceModes.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
        check(!catalog.IsVisible&&vm.Tab.Sql==retainedSql&&vm.Tabs.Count==retainedTabCount,"switching to SQL preserves the existing query and tabs");
        workspaceModes.SelectedIndex=0;Dispatcher.UIThread.RunJobs();
        check(catalog.IsVisible&&filter.Text=="audit"&&catalogGrid.ItemsSource!.Cast<DbObject>().Single().Name=="events","returning to data tables preserves the catalog filter and cached objects");
        filter.Text="";Dispatcher.UIThread.RunJobs();
        catalog.IsVisible=true;window.UpdateLayout();Dispatcher.UIThread.RunJobs();
        var targetRow=catalogGrid.GetVisualDescendants().OfType<DataGridRow>().First(r=>r.DataContext is DbObject{Name:"events"});
        var targetPoint=targetRow.TranslatePoint(new Point(30,targetRow.Bounds.Height/2),window)!.Value;
        catalogGrid.SelectedItem=catalogGrid.ItemsSource!.Cast<DbObject>().First(o=>o.Name=="orders");
        window.MouseDown(targetPoint,MouseButton.Right);window.MouseUp(targetPoint,MouseButton.Right);Dispatcher.UIThread.RunJobs();
        var menu=catalogGrid.ContextMenu!;
        check(menu.IsOpen&&catalogGrid.SelectedItem is DbObject{Name:"events"},"right click on a catalog row opens its menu and selects the clicked table");
        var renameItem=menu.ItemsSource!.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="重命名");
        check(renameItem.IsEnabled&&menu.ItemsSource!.OfType<MenuItem>().Any(m=>m.Header?.ToString()?.StartsWith("属性")==true),"catalog row menu offers enabled rename and structure properties");
        menu.Close();renameItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));Dispatcher.UIThread.RunJobs();
        var renameDialog=window.OwnedWindows.FirstOrDefault(w=>w.Title=="重命名表 · events");
        check(renameDialog is not null,"catalog rename action targets the right-clicked table instead of the tree selection");renameDialog?.Close();Dispatcher.UIThread.RunJobs();
        var blankPoint=catalogGrid.TranslatePoint(new Point(30,catalogGrid.Bounds.Height-8),window)!.Value;
        window.MouseDown(blankPoint,MouseButton.Right);window.MouseUp(blankPoint,MouseButton.Right);Dispatcher.UIThread.RunJobs();
        check(!menu.IsOpen,"catalog blank-space right click does not act on a previously selected table");
        catalogGrid.SelectedItem=catalogGrid.ItemsSource!.Cast<DbObject>().First(o=>o.Name=="orders");
        catalogGrid.RaiseEvent(new ContextRequestedEventArgs{RoutedEvent=Control.ContextRequestedEvent});Dispatcher.UIThread.RunJobs();
        check(menu.IsOpen,"keyboard context request opens menu for the selected catalog table");menu.Close();
        typeof(MainWindow).GetMethod("CloseCatalog",flags)!.Invoke(window,null);
        var retainedTab=vm.Tab!;retainedTab.Sql="SELECT 'keep this draft'";
        var activate=typeof(MainWindow).GetMethod("TryUseTreeContext",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        activate.Invoke(window,[new ObjectNode{Profile=profile,Database="sales"}]);Dispatcher.UIThread.RunJobs();
        check(!ReferenceEquals(vm.Tab,retainedTab)&&retainedTab.Sql=="SELECT 'keep this draft'"&&retainedTab.Connection?.Database=="warehouse"&&vm.CurrentProfile?.Database=="sales","tree navigation preserves prior query and database in its original tab");
        vm.Tabs.Remove(retainedTab);
        vm.Tabs.Remove(vm.Tab!);vm.Connection=originalConnection;vm.Tab=originalTab;vm.Connections.Remove(profile);cached.Remove(profile.Id);Dispatcher.UIThread.RunJobs();
        var provider=DbProviderFactory.Create(new(){Kind=DatabaseKind.PostgreSQL});string? executed=null;
        var designer=new ObjectDesignerWindow(provider,DatabaseKind.PostgreSQL,"sales",null,sql=>{executed=sql;return Task.FromResult(false);},true);designer.Show(window);Dispatcher.UIThread.RunJobs();
        try{
            var grid=designer.GetVisualDescendants().OfType<DataGrid>().Single();var fields=(System.Collections.ObjectModel.ObservableCollection<FieldDefinition>)grid.ItemsSource!;fields[0].Identity=true;fields.Add(new(){Name="name",Type="VARCHAR(80)",Nullable=false});
            designer.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="创建").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
            check(executed?.Contains("GENERATED BY DEFAULT AS IDENTITY")==true&&executed.Contains("\"name\" VARCHAR(80) NOT NULL")&&executed.Contains("PRIMARY KEY"),"visual create-table form executes current fields including identity and primary key");
        }finally{designer.Close();provider.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
    public static async Task Run(Action<bool,string> check)
    {
        int calls=0;var profile=new ConnectionProfile{Kind=DatabaseKind.PostgreSQL,Database="initial"};
        var vm=new MainViewModel(p=>{calls++;return new Probe(p);});vm.Settings.PersistLogs=false;vm.Connections.Add(profile);vm.Connection=profile;vm.NewQuery();var first=vm.Tab!;
        vm.SelectDatabase("sales");check(first.Connection?.Database=="sales"&&profile.Database=="initial","database selection clones tab context without changing saved profile");
        vm.NewQuery();vm.SelectDatabase("warehouse");check(first.Connection?.Database=="sales"&&vm.Tab?.Connection?.Database=="warehouse","query tabs retain independent database selection");
        vm.SelectTab(first);check(vm.CurrentProfile?.Database=="sales","returning to a query restores its database context");
        first.Table=new("sales","public","items","表");first.Result=new(["id","name"],[new object?[]{1,"before"}],0,false,[typeof(int),typeof(string)]);first.Edits=new(first.Result,[new("id","int",false,true,"",""),new("name","text",true,false,"","")]);first.Edits.Data.Rows[0][1]="draft";
        bool blocked=false;try{vm.SelectDatabase("warehouse");}catch(InvalidOperationException){blocked=true;}check(blocked&&first.Connection?.Database=="sales"&&first.Edits.PendingCount==1,"database switching preserves and protects staged edits");
        blocked=false;try{vm.EnsureObjectHasNoDrafts(profile,first.Table);}catch(InvalidOperationException){blocked=true;}check(blocked,"table DDL refuses an object with unsaved row edits");first.Edits.Revert();
        vm.Disconnect();int before=calls;blocked=false;try{await vm.ReadDatabasesAsync(profile,default);}catch(InvalidOperationException){blocked=true;}check(blocked&&calls==before&&!await vm.ExecuteDdlAsync(profile,"CREATE TABLE t(id int)"),"closed connection blocks metadata and DDL before provider access");
        await vm.ConnectAsync();vm.RebuildTree();var retainedGroup=vm.Nodes[0];var retainedConnection=retainedGroup.Children[0];
        vm.Disconnect();check(ReferenceEquals(vm.Nodes[0],retainedGroup)&&ReferenceEquals(vm.Nodes[0].Children[0],retainedConnection)&&retainedConnection.Label.Contains("已关闭"),"disconnect updates only its node without rebuilding other tree branches");
        await vm.ConnectAsync();check(vm.IsOpen(profile)&&(await vm.ReadDatabasesAsync(profile,default)).Contains("sales"),"open connection restores metadata access");
        var tabsBeforeOpen=vm.Tabs.Count;
        await vm.OpenTableAsync(new(){Profile=profile,Database="sales",Object=first.Table},100);
        check(vm.Tabs.Count==tabsBeforeOpen&&ReferenceEquals(vm.Tab,first),"reopening a table reuses its tab and preserves local state");
        vm.SelectDatabase("warehouse");vm.NewQuery();check(vm.Tab?.Connection?.Database=="warehouse","new query inherits the selected database");
        foreach(var kind in Enum.GetValues<DatabaseKind>()){
            await using var provider=DbProviderFactory.Create(new(){Kind=kind});var sql=DatabaseDdl.CreateDatabase(provider,kind,"odd ; name");check(sql.Contains(provider.Quote("odd ; name")),kind+" create database quotes identifier");
            var table=new DbObject("db",kind==DatabaseKind.PostgreSQL?"public":"dbo","items","表");
            if(kind!=DatabaseKind.TDengine)check(DatabaseDdl.RenameTable(provider,kind,table,"new ' ; name").Contains(kind==DatabaseKind.SqlServer?"new '' ; name":provider.Quote("new ' ; name")),kind+" rename table preserves quoted names");
        }
        string[] tableSql=["SELECT * FROM ","SELECT * FROM users JOIN ","UPDATE ","INSERT INTO ","SELECT * FROM users, "];
        foreach(var sql in tableSql)check(SqlCompletionContext.Read(sql,sql.Length) is {TablePosition:true},"table suggestions after "+sql);
        foreach(var sql in new[]{"SELECT ","SELECT * FROM users WHERE ","UPDATE users SET ","SELECT * FROM users ORDER BY "})check(SqlCompletionContext.Read(sql,sql.Length) is {ColumnPosition:true},"column suggestions after "+sql);
    }
    private sealed class Probe(ConnectionProfile profile):IDbProvider
    {
        public Task TestAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>>(["sales","warehouse"]);
        public Task<IReadOnlyList<DbObject>> ObjectsAsync(string db,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DbObject>>([]);
        public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ColumnInfo>>([]);
        public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>Task.FromResult<IReadOnlyList<IndexInfo>>([]);
        public Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct)=>Task.FromResult(new QueryResult(["database"],[new object?[]{profile.Database}],0,false));
        public string Quote(string name)=>"\""+name.Replace("\"","\"\"")+"\"";
        public string Qualified(DbObject table)=>Quote(table.Schema)+"."+Quote(table.Name);
        public string PageSql(DbObject table,string filter,int page,int size)=>"SELECT * FROM "+Qualified(table);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
