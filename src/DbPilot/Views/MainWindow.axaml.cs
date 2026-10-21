using System.Data;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.CodeCompletion;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;

namespace DbPilot.Views;
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm=new();
    private QueryTab? _displayedTab;
    private DataTable? _resultData;
    private ResultRows? _resultRows;
    private string? _sortMember;
    private bool _sortAscending;
    private bool _loading;
    private bool _closingSaved;
    private bool _closingCheck;
    private Point _dragStart;
    private QueryTab? _dragTab;
    private CompletionWindow? _completion;
    public MainWindow()
    {
        InitializeComponent();DataContext=_vm;InitializeCompletion();InitializeSqlContextMenu();
        if(OperatingSystem.IsMacOS())AppMenuBar.IsVisible=false;
        AddHandler(KeyDownEvent,WorkspaceKeyDown,Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TableCatalogGrid.AddHandler(PointerPressedEvent,CatalogPointerPressed,RoutingStrategies.Tunnel,handledEventsToo:true);
        TableCatalogGrid.AddHandler(ContextRequestedEvent,CatalogContextRequested,RoutingStrategies.Tunnel,handledEventsToo:true);
        ObjectTree.ContextMenu!.Opening+=(_,_)=>{if(ObjectTree.SelectedItem is ObjectNode selected)BuildTreeContextMenu(selected);};
        ObjectTree.ContainerPrepared+=(_,e)=>PrepareTreeItem(e.Container as TreeViewItem);
        SqlEditor.SyntaxHighlighting=SqlHighlighting.Create(DatabaseKind.MySQL,false);
        SqlEditor.TextChanged+=(_,_)=>{if(!_loading && _displayedTab is not null)_displayedTab.Sql=SqlEditor.Text;};
        _vm.ResultChanged+=ShowResult;
        _vm.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(MainViewModel.Busy))UpdateEditControls();if(e.PropertyName is nameof(MainViewModel.Busy) or nameof(MainViewModel.ConnectionStatus))UpdateConnectionControls();if(e.PropertyName==nameof(MainViewModel.HasTab))UpdateWorkspaceState();};
        Closed+=(_,_)=>{_databaseRequest?.Cancel();_catalogCancellation?.Cancel();};
        Results.RowEditEnded+=(_,_)=>UpdateEditControls();
        Results.Sorting+=(_,e)=>{
            e.Handled=true;if(!FlushGridEdits() || _resultData is null)return;
            _sortAscending=_sortMember!=e.Column.SortMemberPath || !_sortAscending;_sortMember=e.Column.SortMemberPath;
            _resultData.DefaultView.Sort=_sortMember+(_sortAscending?" ASC":" DESC");
            if(_vm.Tab?.Result is {} result)for(int i=0;i<Results.Columns.Count;i++)Results.Columns[i].Header=result.Columns[i]+(Results.Columns[i]==e.Column?(_sortAscending?" ↑":" ↓"):"");
        };
        UpdateEditControls();
        UpdateWorkspaceState();
        var expansion=TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>(async(item,args)=>{
            if(item.IsExpanded&&item.DataContext is ObjectNode{Loaded:false} node&&item.GetVisualAncestors().Contains(ObjectTree)&&!_vm.Busy)await _vm.ExpandAsync(node);
        });
        Closed+=(_,_)=>expansion.Dispose();
        QueryTabs.AddHandler(DragDrop.DragOverEvent,TabsDragOver);
        QueryTabs.AddHandler(DragDrop.DropEvent,TabsDrop);
        var heartbeat=new Avalonia.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(60)};
        heartbeat.Tick+=async(_,_)=>await _vm.HeartbeatAsync();Closed+=(_,_)=>{heartbeat.Stop();_updates?.Dispose();};
        Opened+=async (_,_)=>{await _vm.InitializeAsync();ApplySettings();UpdateWorkspaceState();heartbeat.Start();InitializeUpdates();};
        Closing+=async (_,e)=>{
            if(_closingSaved)return;e.Cancel=true;_vm.Cancel();
            if(_vm.Busy){_vm.Log("正在取消当前任务，请等待完成后再关闭窗口。");return;}
            if(_closingCheck)return;_closingCheck=true;
            bool valid=FlushGridEdits();
            if((!valid || _vm.Tabs.Any(t=>t.IsDirty || t.Edits is {} edits && (edits.PendingCount>0 || edits.CommitUncertain))) && !await Confirm("有尚未保存的 SQL 或未提交的数据改动。确定关闭并放弃本地改动？结果未知的事务仍需核对数据库。","放弃本地改动")){_closingCheck=false;return;}
            try{_vm.Settings.Width=Width;_vm.Settings.Height=Height;CaptureWorkspaceLayout();await _vm.Store.SaveSettingsAsync(_vm.Settings);}
            catch(Exception ex){_vm.Log("布局保存失败："+ex.Message);}
            _closingSaved=true;Close();
        };
    }
    private void ApplySettings()
    {
        RequestedThemeVariant=_vm.Settings.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Width=Math.Clamp(_vm.Settings.Width,1040,3840);Height=Math.Clamp(_vm.Settings.Height,680,2160);
        _sidebarWidthBeforeCollapse=Math.Clamp(_vm.Settings.SidebarWidth,180,500);
        WorkspaceGrid.ColumnDefinitions[0].Width=new GridLength(SidebarPanel.IsVisible?_sidebarWidthBeforeCollapse:0);
        SqlEditor.FontSize=Math.Clamp(_vm.Settings.FontSize,10,30);SqlEditor.WordWrap=_vm.Settings.WordWrap;SqlEditor.ShowLineNumbers=_vm.Settings.LineNumbers;
        SqlEditor.FontFamily=new Avalonia.Media.FontFamily(_vm.Settings.FontFamily);
        EditorWorkspaceGrid.RowDefinitions[2].Height=new GridLength(Math.Clamp(_vm.Settings.EditorRatio,.2,5),GridUnitType.Star);
        EditorWorkspaceGrid.RowDefinitions[4].Height=new GridLength(1,GridUnitType.Star);
        _expandedEditorHeight=EditorWorkspaceGrid.RowDefinitions[2].Height;_expandedResultsHeight=EditorWorkspaceGrid.RowDefinitions[4].Height;
        if(!EditorPane.IsVisible){EditorWorkspaceGrid.RowDefinitions[2].Height=new GridLength(0);EditorWorkspaceGrid.RowDefinitions[3].Height=new GridLength(0);}
        try{KeyGesture.Parse(_vm.Settings.ExecuteGesture);KeyGesture.Parse(_vm.Settings.CompletionGesture);}catch{_vm.Settings.ExecuteGesture="Ctrl+Enter";_vm.Settings.CompletionGesture="Ctrl+Space";}
        SqlEditor.Foreground=Avalonia.Media.Brush.Parse(_vm.Settings.Dark?"#F8F8F2":"#1E1E1E");
        SqlEditor.LineNumbersForeground=Avalonia.Media.Brush.Parse(_vm.Settings.Dark?"#6272A4":"#2B91AF");
        SqlEditor.SyntaxHighlighting=SqlHighlighting.Create(_vm.Connection?.Kind??DatabaseKind.MySQL,_vm.Settings.Dark);
    }
    private int Size => int.Parse((PageSize.SelectedItem as ComboBoxItem)?.Content?.ToString()??"100");
    private async void NewConnection(object? sender,RoutedEventArgs e)
    { var profile=await new ConnectionDialog().ShowDialog<ConnectionProfile?>(this);if(profile is not null)await _vm.SaveProfileAsync(profile); }
    private async void EditConnection(object? sender,RoutedEventArgs e)
    { if(_vm.Connection is not {} current){_vm.Log("请先选择连接。");return;}var p=await new ConnectionDialog(current).ShowDialog<ConnectionProfile?>(this);if(p is not null)await _vm.SaveProfileAsync(p,current.Id); }
    private async void CopyConnection(object? sender,RoutedEventArgs e)
    {if(_vm.Connection is not {} current)return;var p=MainViewModel.CloneForDatabase(current,null);p.Id=Guid.NewGuid();p.Name+=" 副本";await _vm.SaveProfileAsync(p);}
    private async void DeleteConnection(object? sender,RoutedEventArgs e)
    {if(_vm.Connection is {} p && await Confirm($"删除连接“{p.Name}”？数据库中的数据不会被删除。"))await _vm.DeleteProfileAsync(p);}
    private void NewQuery(object? sender,RoutedEventArgs e){if(_vm.Busy)return;CloseCatalog();if(FlushGridEdits())_vm.NewQuery();}
    private async void CloseTab(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy){_vm.Log("请先终止当前查询。");return;}
        bool valid=FlushGridEdits();
        if((!valid || _vm.Tab?.IsDirty==true || _vm.Tab?.Edits is {} edits && (edits.PendingCount>0 || edits.CommitUncertain)) && !await Confirm("该查询存在未保存脚本或未提交数据。确定放弃本地改动？","放弃本地改动"))return;
        if(_vm.Tab is {} tab)_vm.Tabs.Remove(tab);_vm.Tab=_vm.Tabs.LastOrDefault();
    }
    private void ChangeTab(object? sender,SelectionChangedEventArgs e)
    {
        if(SqlEditor is null)return;
        CloseCatalog();
        if(!FlushGridEdits()){_vm.Tab=_displayedTab;return;}
        CancelCompletion();_displayedTab=QueryTabs.SelectedItem as QueryTab;_loading=true;SqlEditor.Text=_displayedTab?.Sql??"";_loading=false;
        _vm.SelectTab(_displayedTab);PageLabel.Text=$"第 {(_displayedTab?.Page??0)+1} 页";
        WhereFilter.Text=_displayedTab?.Filter??"";
        _=RefreshDatabasePicker();
    }
    private void ChangeConnection(object? sender,SelectionChangedEventArgs e)
    {
        if(_vm.Connection is null)return;
        CloseCatalog();
        SqlEditor.SyntaxHighlighting=SqlHighlighting.Create(_vm.Connection.Kind,_vm.Settings.Dark);
        if(_displayedTab is not null && _displayedTab.Connection?.Id!=_vm.Connection.Id){
            if(!FlushGridEdits() || _displayedTab.Edits is {} edits && (edits.PendingCount>0 || edits.CommitUncertain)){
                _vm.Log("请先提交或撤销当前页的数据改动，再切换连接。");_vm.Connection=_vm.Connections.FirstOrDefault(p=>p.Id==_displayedTab.Connection?.Id);return;
            }
            _displayedTab.Connection=_vm.DefaultProfile(_vm.Connection);_displayedTab.Table=null;_displayedTab.Edits=null;_displayedTab.Result=null;_displayedTab.ResultSets=[];ShowResult(null);
        }
        CancelCompletion();_=RefreshDatabasePicker();
    }
    private async void RunSql(object? sender,RoutedEventArgs e)=>await Run();
    private async void ExplainSql(object? sender,RoutedEventArgs e)
    {
        var sql=string.IsNullOrWhiteSpace(SqlEditor.SelectedText)?SqlEditor.Text:SqlEditor.SelectedText;if(!string.IsNullOrWhiteSpace(sql)&&FlushGridEdits())await _vm.ExplainAsync(sql);
    }
    private async void ConnectDatabase(object? sender,RoutedEventArgs e){await _vm.ConnectAsync();await RefreshDatabasePicker(true);}
    private void DisconnectDatabase(object? sender,RoutedEventArgs e){CloseCatalog();_databaseRequest?.Cancel();CancelCompletion();_vm.Disconnect();UpdateConnectionControls();}
    private async void OpenChart(object? sender,RoutedEventArgs e)
    {
        if(_vm.Tab?.Result is not {Columns.Count:>0} result){_vm.Log("请先执行查询。");return;}
        var window=new VisualizationWindow();window.ShowChart(result);await window.ShowDialog(this);
    }
    private async void OpenDatabaseTools(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy)return;
        await new DatabaseToolsWindow(_vm.Connections,_vm.Connection,(sql,profile)=>_vm.NewQuery(sql,profile)).ShowDialog(this);
    }
    private async Task Run()
    {
        CloseCatalog();
        if(!EditorPane.IsVisible)SetEditorVisible(true);
        var sql=string.IsNullOrWhiteSpace(SqlEditor.SelectedText)?SqlEditor.Text:SqlEditor.SelectedText;
        if(_vm.Busy){ReportSqlProblem("已有任务运行，请等待或终止当前任务。");return;}
        if(string.IsNullOrWhiteSpace(sql)){ReportSqlProblem("请输入或选中要执行的 SQL。");return;}
        var profile=_vm.CurrentProfile;
        if(profile is null){ReportSqlProblem("请先在上方选择连接，再执行 SQL。");return;}
        if(_vm.IsClosed(profile)){ReportSqlProblem("该连接已关闭，请先打开连接再执行 SQL。");return;}
        if(!FlushGridEdits()){TaskLogPanel.IsVisible=true;TaskLogPanel.IsExpanded=true;return;}
        if(_vm.Tab?.Edits is {} edits&&(edits.PendingCount>0||edits.CommitUncertain)){
            ReportSqlProblem("请先提交或撤销数据改动；提交结果未知时，请先核对数据库并重新加载。");return;
        }
        if(_vm.Tab is null)_vm.NewQuery(SqlEditor.Text,profile);
        if(SqlTools.RequiresConfirmation(sql)&&!await Confirm("SQL 包含删除对象或结构的操作，可能永久删除数据。确定执行？","执行删除"))return;
        int baseLine=string.IsNullOrWhiteSpace(SqlEditor.SelectedText)?0:SqlEditor.Document.GetLineByOffset(SqlEditor.SelectionStart).LineNumber-1;
        await _vm.ExecuteAsync(sql);
        if(_vm.LastScriptErrorLine is not null||_vm.Status.StartsWith("操作失败：")){TaskLogPanel.IsVisible=true;TaskLogPanel.IsExpanded=true;}
        if(_vm.LastScriptErrorLine is {} line){line=Math.Clamp(line+baseLine,1,SqlEditor.Document.LineCount);SqlEditor.CaretOffset=SqlEditor.Document.GetLineByNumber(line).Offset;SqlEditor.ScrollToLine(line);}
    }
    private void WorkspaceKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.F&&(e.KeyModifiers&(KeyModifiers.Control|KeyModifiers.Meta))!=0&&e.KeyModifiers.HasFlag(KeyModifiers.Shift)){e.Handled=true;ToggleFocusMode(sender,new RoutedEventArgs());return;}
        if((e.KeyModifiers&(KeyModifiers.Control|KeyModifiers.Meta))==0 || (e.KeyModifiers&(KeyModifiers.Alt|KeyModifiers.Shift))!=0)return;
        if(e.Key==Key.S){e.Handled=true;SaveScript(sender,new RoutedEventArgs());}
        else if(e.Key==Key.N){e.Handled=true;NewQuery(sender,new RoutedEventArgs());}
        else if(e.Key==Key.W){e.Handled=true;CloseTab(sender,new RoutedEventArgs());}
    }
    private async void EditorKeyDown(object? sender,KeyEventArgs e)
    {
        if(KeyGesture.Parse(_vm.Settings.ExecuteGesture).Matches(e)){e.Handled=true;await Run();return;}
        if(KeyGesture.Parse(_vm.Settings.CompletionGesture).Matches(e)){e.Handled=true;await ShowCompletion();}
    }
    private void StopSql(object? sender,RoutedEventArgs e)=>_vm.Cancel();
    private void ToggleTheme(object? sender,RoutedEventArgs e){_vm.Settings.Dark=!_vm.Settings.Dark;ApplySettings();}
    private async void ClearLogs(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy)return;_vm.Logs.Clear();try{await _vm.Store.SaveLogsAsync([]);}catch(Exception ex){_vm.Log("清空日志文件失败："+ex.Message,"Warn");}
    }
    private async void OpenNode(object? sender,TappedEventArgs e)=>await OpenSelected();
    private async void OpenNodeMenu(object? sender,RoutedEventArgs e)=>await OpenSelected();
    private async Task OpenSelected()
    {
        if(_vm.Busy || ObjectTree.SelectedItem is not ObjectNode node)return;
        if(node.Object is null&&!TryUseTreeContext(node))return;
        if(node.Object is null&&node.Database is null&&_vm.IsClosed(node.Profile)){_vm.Connection=node.Profile;await _vm.ConnectAsync();}
        if(node.Database is {} database&&node.Object is null){try{if(!FlushGridEdits())return;_vm.Connection=node.Profile;if(_vm.CurrentProfile?.Database!=database)_vm.SelectDatabase(database);}catch(Exception ex){_vm.Log(ex.Message);return;}}
        if(node.Object is not null){CloseCatalog();await _vm.OpenTableAsync(node,Size);}else await _vm.ExpandAsync(node);
        await RefreshDatabasePicker();
        if(node.Database is not null && node.Object is null)await ShowTableCatalog();
    }
    private void RefreshTree(object? sender,RoutedEventArgs e)=>_vm.RebuildTree();
    private void PrepareTreeItem(TreeViewItem? item)
    {
        if(item is null)return;
        item.Loaded+=(_,_)=>CompactTreeItem(item);
        item.ContainerPrepared+=(_,e)=>PrepareTreeItem(e.Container as TreeViewItem);
    }
    private static void CompactTreeItem(TreeViewItem item)
    {
        foreach(var part in item.GetVisualDescendants())
            if(part is Grid{Name:"PART_Header"} header)header.Margin=new Thickness(item.Level*8,0,0,0);
            else if(part is Panel{Name:"PART_ExpandCollapseChevronContainer"} chevron)chevron.Margin=new Thickness(0,0,4,0);
    }
    private void FilterObjects(object? sender,TextChangedEventArgs e)
    {
        if(ObjectTree is null)return;
        var text=ObjectFilter.Text??"";
        // Filter the existing tree without discarding loaded metadata.
        ApplyTreeFilter(_vm.Nodes,text);
    }
    private static bool ApplyTreeFilter(IEnumerable<ObjectNode> nodes,string text,bool ancestorMatch=false)
    {
        bool any=false;
        foreach(var item in nodes) {
            bool match=ancestorMatch||item.Label.Contains(text,StringComparison.OrdinalIgnoreCase);
            bool child=ApplyTreeFilter(item.Children,text,match);item.Visible=string.IsNullOrEmpty(text)||match||child;any|=item.Visible;
        }
        return any;
    }
    private async void FilterKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter){e.Handled=true;await Browse(0);}
        else if(e.Key==Key.Escape){e.Handled=true;WhereFilter.Text="";await Browse(0);}
    }
    private async void ApplyFilter(object? sender,RoutedEventArgs e)=>await Browse(0);
    private async void PreviousPage(object? sender,RoutedEventArgs e)=>await Browse(Math.Max(0,(_vm.Tab?.Page??0)-1));
    private async void NextPage(object? sender,RoutedEventArgs e)=>await Browse((_vm.Tab?.Page??0)+1);
    private async Task Browse(int page){if(!FlushGridEdits())return;await _vm.BrowseAsync(WhereFilter.Text??"",page,Size);_loading=true;SqlEditor.Text=_vm.Tab?.Sql??"";_loading=false;PageLabel.Text=$"第 {(_vm.Tab?.Page??0)+1} 页";}
    private void ShowResult(QueryResult? result)
    {
        _loadingResults=true;
        ResultPicker.ItemsSource=_vm.Tab?.ResultSets.Select((r,i)=>$"结果 {i+1} · {r.Rows.Count} 行").ToArray();
        ResultPicker.SelectedIndex=_vm.Tab?.ResultIndex??-1;
        _loadingResults=false;
        ObserveEditSession(_vm.Tab?.Edits);
        Results.ItemsSource=null;
        _resultRows?.Dispose();_resultRows=null;_sortMember=null;
        Results.Columns.Clear();_resultData=new DataTable();
        if(result is null){Results.ItemsSource=null;RowCount.Text="0 行";UpdateEditControls();return;}
        _resultData=_vm.Tab?.Edits?.Data??ResultTable.Create(result);
        for(int i=0;i<result.Columns.Count;i++){
            Results.Columns.Add(new ResultTextColumn{ColumnIndex=i,Header=result.Columns[i],Binding=new Binding("["+i+"]"){Mode=BindingMode.TwoWay},IsReadOnly=_vm.Tab?.Edits is not {} edits || !edits.CanEditColumn(i),CanUserSort=true,SortMemberPath="c"+i,Width=new DataGridLength(150)});
        }
        _resultRows=new ResultRows(_resultData.DefaultView);Results.ItemsSource=_resultRows;RowCount.Text=$"{result.Rows.Count} 行"+(result.Truncated?"（已截断）":"");
        UpdateEditControls();
    }
    private bool _loadingResults;
    private void ChangeResult(object? sender,SelectionChangedEventArgs e)
    {
        if(!_loadingResults && FlushGridEdits())_vm.SelectResult(ResultPicker.SelectedIndex);
    }
    private async void CopyCell(object? sender,RoutedEventArgs e)
    {
        if(Results.SelectedItem is ResultRow row && Results.CurrentColumn is {} column && Clipboard is not null)
            await Clipboard.SetTextAsync(SqlTools.Display(row[Results.Columns.IndexOf(column)]));
    }
    private async void CopyRow(object? sender,RoutedEventArgs e)
    {if(Results.SelectedItem is ResultRow row && Clipboard is not null)await Clipboard.SetTextAsync(string.Join("\t",row.Row.ItemArray.Select(SqlTools.Display)));}
    private async void CopyObjectName(object? sender,RoutedEventArgs e)
    {if(ObjectTree.SelectedItem is ObjectNode{Object:{} obj} && Clipboard is not null)await Clipboard.SetTextAsync(obj.ToString());}
    private async void ViewStructure(object? sender,RoutedEventArgs e)
    {
        if(ObjectTree.SelectedItem is not ObjectNode{Object:{} obj} node)return;
        await ShowTableStructure(node.Profile,obj);
    }
    private async Task ShowTableStructure(ConnectionProfile profile,DbObject obj)
    {
        await _vm.Guard(async ct=>{
            await using var provider=DbProviderFactory.Create(MainViewModel.CloneForDatabase(profile,obj.Database));
            var columns=await provider.ColumnsAsync(obj,ct);var indexes=await provider.IndexesAsync(obj,ct);
            var grid=new DataGrid{ItemsSource=columns,AutoGenerateColumns=true,IsReadOnly=true,Margin=new Thickness(10)};
            var indexGrid=new DataGrid{ItemsSource=indexes,AutoGenerateColumns=true,IsReadOnly=true,Margin=new Thickness(10)};
            var definition=SqlTools.FormatDdl(await provider.DefinitionAsync(obj,ct));
            var ddl=new TextBox{Text=definition,IsReadOnly=true,AcceptsReturn=true,TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontFamily="Cascadia Code,Consolas,monospace",Margin=new Thickness(10)};
            var tabs=new TabControl{ItemsSource=new[]{new TabItem{Header="字段与标签",Content=grid},new TabItem{Header="索引",Content=indexGrid},new TabItem{Header="建表语句",Content=ddl}}};
            await new Window{Title=$"表结构 · {obj}",Width=950,Height=550,Content=tabs,WindowStartupLocation=WindowStartupLocation.CenterOwner}.ShowDialog(this);
        });
    }
    private async void GenerateSelect(object? sender,RoutedEventArgs e)=>await Generate("select");
    private async void GenerateInsert(object? sender,RoutedEventArgs e)=>await Generate("insert");
    private async void GenerateCreate(object? sender,RoutedEventArgs e)=>await Generate("create");
    private async void GenerateIndex(object? sender,RoutedEventArgs e)=>await Generate("index");
    private async Task Generate(string kind)
    {
        if(ObjectTree.SelectedItem is not ObjectNode{Object:{} obj} node)return;
        await _vm.Guard(async ct=>{
            var profile=MainViewModel.CloneForDatabase(node.Profile,node.Database);await using var provider=DbProviderFactory.Create(profile);
            var columns=await provider.ColumnsAsync(obj,ct);
            var sql=kind switch{
                "select"=>provider.PageSql(obj,"",0,Size),
                "insert"=>SqlTools.InsertTemplate(provider,obj,columns),
                "index"=>profile.Kind==DatabaseKind.TDengine ? "-- TDengine 索引语法取决于版本与标签类型，请查阅服务器版本文档。" : $"CREATE INDEX {provider.Quote("idx_"+obj.Name)} ON {provider.Qualified(obj)} ({provider.Quote(columns.First().Name)});",
                _=>SqlTools.CreateTemplate(provider,obj,columns)};
            _vm.NewQuery(sql,profile);
        });
    }
    private async void OpenScript(object? sender,RoutedEventArgs e)
    {
        await _vm.Guard(async ct=>{
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="打开 SQL 脚本",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("SQL"){Patterns=["*.sql"]}]});
            if(files.Count==0)return;await using var stream=await files[0].OpenReadAsync();using var reader=new StreamReader(stream);
            _vm.NewQuery(await reader.ReadToEndAsync(ct));_vm.Tab!.FilePath=files[0].TryGetLocalPath();_vm.Tab.SavedSql=_vm.Tab.Sql;
        });
    }
    private async void SaveScript(object? sender,RoutedEventArgs e)
    {
        await _vm.Guard(async ct=>{
            var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title="保存 SQL 脚本",SuggestedFileName="query.sql",DefaultExtension="sql"});
            if(file is null)return;await using var stream=await file.OpenWriteAsync();stream.SetLength(0);await using var writer=new StreamWriter(stream);await writer.WriteAsync(SqlEditor.Text.AsMemory(),ct);await writer.FlushAsync(ct);if(_vm.Tab is {} tab)tab.SavedSql=SqlEditor.Text;_vm.Log("脚本已保存。");
        });
    }
    private async void ExportResult(object? sender,RoutedEventArgs e)
    {
        if(_vm.Tab?.Result is not {} result){_vm.Log("当前没有可导出的查询结果。");return;}
        await _vm.Guard(async ct=>{
            var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title="导出当前结果（CSV / JSON / SQL）",SuggestedFileName="result.csv",FileTypeChoices=[new FilePickerFileType("CSV"){Patterns=["*.csv"]},new FilePickerFileType("JSON"){Patterns=["*.json"]},new FilePickerFileType("SQL INSERT"){Patterns=["*.sql"]}]});
            if(file is null)return;var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本地文件。");
            string format=Path.GetExtension(path).ToLowerInvariant() switch{".json"=>"json",".sql"=>"sql",_=>"csv"};
            var profile=_vm.Tab?.Connection??_vm.Connection??new();DbObject? target=null;
            if(format=="sql"){target=await ChooseExportTarget(profile);if(target is null)return;}
            await using var provider=DbProviderFactory.Create(profile);
            await AtomicFile.WriteAsync(path,stream=>ResultExport.WriteAsync(result,stream,format,provider,profile.Kind,target,ct),ct);
            _vm.Log($"已导出当前结果 {result.Rows.Count} 行"+(result.Truncated?"（不包含被截断的数据）":""));
        });
    }
    private Task<DbObject?> ChooseExportTarget(ConnectionProfile profile)
    {
        var window=new Window{Title="SQL 导出目标表",Width=470,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(20),Spacing=12};var name=new TextBox{Text=_vm.Tab?.Table?.Name??"exported_result",Watermark="目标表名"};var schema=new TextBox{Text=_vm.Tab?.Table?.Schema??(profile.Kind==DatabaseKind.PostgreSQL?"public":profile.Kind==DatabaseKind.SqlServer?"dbo":""),Watermark="Schema"};
        panel.Children.Add(new TextBlock{Text="仅导出当前结果；请填写 INSERT 的目标表。自增与计算列需要在执行脚本前调整。",TextWrapping=Avalonia.Media.TextWrapping.Wrap});panel.Children.Add(schema);panel.Children.Add(name);
        var okay=new Button{Content="导出"};okay.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(name.Text))window.Close(new DbObject(profile.Database,schema.Text?.Trim()??"",name.Text.Trim(),"表"));};panel.Children.Add(okay);window.Content=panel;return window.ShowDialog<DbObject?>(this);
    }
    private async Task<bool> Confirm(string text,string action="确认")
    {
        var window=new Window{Title="确认操作",Width=450,Height=190,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(22),Spacing=18};panel.Children.Add(new TextBlock{Text=text,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="取消"};var ok=new Button{Content=action};cancel.Click+=(_,_)=>window.Close(false);ok.Click+=(_,_)=>window.Close(true);buttons.Children.Add(cancel);buttons.Children.Add(ok);panel.Children.Add(buttons);window.Content=panel;return await window.ShowDialog<bool>(this);
    }
    private async void ShowSettings(object? sender,RoutedEventArgs e)
    {
        var window=new Window{Title="编辑器设置",Width=560,Height=650,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(24),Spacing=18};
        var size=new NumericUpDown{Minimum=10,Maximum=30,Value=(decimal)_vm.Settings.FontSize};var wrap=new CheckBox{Content="自动换行",IsChecked=_vm.Settings.WordWrap};var lines=new CheckBox{Content="显示行号",IsChecked=_vm.Settings.LineNumbers};
        panel.Children.Add(new TextBlock{Text="编辑器字号"});panel.Children.Add(size);panel.Children.Add(wrap);panel.Children.Add(lines);
        var font=new TextBox{Text=_vm.Settings.FontFamily};var execute=new TextBox{Text=_vm.Settings.ExecuteGesture};var completion=new TextBox{Text=_vm.Settings.CompletionGesture};var keepGoing=new CheckBox{Content="SQL 脚本批次失败后继续执行（此前批次不会回滚）",IsChecked=_vm.Settings.ContinueOnScriptError};var persistLogs=new CheckBox{Content="加密保存最近 500 条日志",IsChecked=_vm.Settings.PersistLogs};
        panel.Children.Add(new TextBlock{Text="字体"});panel.Children.Add(font);panel.Children.Add(new TextBlock{Text="执行快捷键"});panel.Children.Add(execute);panel.Children.Add(new TextBlock{Text="补全快捷键"});panel.Children.Add(completion);panel.Children.Add(keepGoing);panel.Children.Add(persistLogs);var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var save=new Button{Content="保存设置"};save.Click+=async(_,_)=>{try{
            var run=KeyGesture.Parse(execute.Text??"");var complete=KeyGesture.Parse(completion.Text??"");if(run.Key==Key.None||complete.Key==Key.None||run.ToString()==complete.ToString())throw new ArgumentException("请设置两个不同的有效快捷键。");
            if(string.IsNullOrWhiteSpace(font.Text))throw new ArgumentException("请填写字体。");
            _vm.Settings.FontSize=(double)(size.Value??14);_vm.Settings.WordWrap=wrap.IsChecked==true;_vm.Settings.LineNumbers=lines.IsChecked==true;_vm.Settings.FontFamily=font.Text;_vm.Settings.ExecuteGesture=execute.Text!;_vm.Settings.CompletionGesture=completion.Text!;_vm.Settings.ContinueOnScriptError=keepGoing.IsChecked==true;
            _vm.Settings.PersistLogs=persistLogs.IsChecked==true;if(!_vm.Settings.PersistLogs)await _vm.Store.SaveLogsAsync([]);
            await _vm.Store.SaveSettingsAsync(_vm.Settings);ApplySettings();window.Close();
        }catch(Exception ex){status.Text=ex.Message;}};panel.Children.Add(save);panel.Children.Add(status);window.Content=new ScrollViewer{Content=panel};await window.ShowDialog(this);
    }
    private static T? ItemAt<T>(object? source) where T:class
    {
        if(source is not Control control)return null;
        return control.DataContext as T ?? control.GetVisualAncestors().OfType<Control>().Select(c=>c.DataContext).OfType<T>().FirstOrDefault();
    }
    private void TreePointerPressed(object? sender,PointerPressedEventArgs e)
    {
        var node=ItemAt<ObjectNode>(e.Source);
        if(e.GetCurrentPoint(ObjectTree).Properties.IsRightButtonPressed){
            if(node is null){ObjectTree.ContextMenu!.ItemsSource=Array.Empty<Control>();return;}
            ObjectTree.SelectedItem=node;BuildTreeContextMenu(node);
        }
    }
    private void TabsPointerPressed(object? sender,PointerPressedEventArgs e)
    {_dragStart=e.GetPosition(QueryTabs);_dragTab=ItemAt<QueryTab>(e.Source);if(_dragTab is not null&&e.GetCurrentPoint(QueryTabs).Properties.IsLeftButtonPressed)CloseCatalog();}
    private async void TabsPointerMoved(object? sender,PointerEventArgs e)
    {
        if(_dragTab is not {} tab || !e.GetCurrentPoint(QueryTabs).Properties.IsLeftButtonPressed || Distance(e.GetPosition(QueryTabs),_dragStart)<8)return;
        _dragTab=null;var data=new DataObject();data.Set("DbPilot.Tab",_vm.Tabs.IndexOf(tab));await DragDrop.DoDragDrop(e,data,DragDropEffects.Move);
    }
    private void TabsDragOver(object? sender,DragEventArgs e)
    {e.DragEffects=e.Data.Contains("DbPilot.Tab") ? DragDropEffects.Move : DragDropEffects.None;e.Handled=true;}
    private void TabsDrop(object? sender,DragEventArgs e)
    {
        if(e.Data.Get("DbPilot.Tab") is int from && from>=0 && from<_vm.Tabs.Count && ItemAt<QueryTab>(e.Source) is {} target){var tab=_vm.Tabs[from];var to=_vm.Tabs.IndexOf(target);_vm.Tabs.Move(from,to);_vm.Tab=tab;}
        e.Handled=true;
    }
    private static double Distance(Point a,Point b)=>Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y);
    private async void ShowHistory(object? sender,RoutedEventArgs e)
    {
        var window=new Window{Title="查询历史 · 最近 200 条成功查询",Width=900,Height=620,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid{RowDefinitions=new RowDefinitions("Auto,2*,3*,Auto"),Margin=new Thickness(18)};
        var search=new TextBox{Watermark="搜索连接名称或 SQL…",Margin=new Thickness(0,0,0,10)};
        var list=new ListBox{ItemsSource=_vm.History};Grid.SetRow(list,1);
        var sql=new TextBox{AcceptsReturn=true,IsReadOnly=true,TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontFamily=new Avalonia.Media.FontFamily("Consolas"),Margin=new Thickness(0,10)};Grid.SetRow(sql,2);
        list.SelectionChanged+=(_,_)=>sql.Text=(list.SelectedItem as QueryHistoryEntry)?.Sql??"";
        search.TextChanged+=(_,_)=>list.ItemsSource=_vm.History.Where(h=>h.Sql.Contains(search.Text??"",StringComparison.OrdinalIgnoreCase)||h.ConnectionName.Contains(search.Text??"",StringComparison.OrdinalIgnoreCase)).ToArray();
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};Grid.SetRow(row,3);
        var open=new Button{Content="载入新查询页"};open.Click+=(_,_)=>{if(list.SelectedItem is QueryHistoryEntry h){var profile=_vm.Connections.FirstOrDefault(p=>p.Id==h.ConnectionId);if(profile is null){_vm.Log("原连接已删除，请重新配置连接后再载入。");return;}_vm.NewQuery(h.Sql,MainViewModel.CloneForDatabase(profile,h.Database));window.Close();}};
        var clear=new Button{Content="清空历史"};clear.Click+=async (_,_)=>await _vm.Guard(async ct=>{await _vm.Store.SaveHistoryAsync([]);_vm.History.Clear();sql.Text="";list.ItemsSource=_vm.History;});
        row.Children.Add(open);row.Children.Add(clear);grid.Children.Add(search);grid.Children.Add(list);grid.Children.Add(sql);grid.Children.Add(row);window.Content=grid;await window.ShowDialog(this);
    }
    private async void ShowTemplates(object? sender,RoutedEventArgs e)
    {
        var templates=new Dictionary<string,string>{
            ["基础查询"]="SELECT * FROM table_name WHERE condition;",
            ["分组统计"]="SELECT category, COUNT(*) AS total\nFROM table_name\nGROUP BY category\nORDER BY total DESC;",
            ["执行计划"]=_vm.Connection?.Kind==DatabaseKind.SqlServer ? "SET SHOWPLAN_TEXT ON;\nGO\n-- 请在服务器支持的批处理中运行待分析 SQL" : "EXPLAIN "+SqlEditor.Text,
            ["TDengine 最近一小时"]="SELECT * FROM database_name.stable_name\nWHERE ts >= NOW - 1h\nORDER BY ts DESC LIMIT 100;",
            ["TDengine 分钟聚合"]="SELECT _wstart, AVG(value) AS average_value\nFROM database_name.stable_name\nWHERE ts >= NOW - 24h\nINTERVAL(1m);",
            ["TDengine 创建超级表"]="CREATE STABLE database_name.meters (ts TIMESTAMP, current FLOAT, voltage INT)\nTAGS (location BINARY(64), group_id INT);",
            ["TDengine 创建子表"]="CREATE TABLE database_name.meter_001 USING database_name.meters TAGS ('Shanghai', 1);"
        };
        List<SqlTemplate> custom;
        try{custom=await _vm.Store.LoadTemplatesAsync();}catch(Exception ex){_vm.Log("模板读取失败："+ex.Message);return;}
        var builtin=templates.Keys.ToHashSet();foreach(var t in custom)if(!builtin.Contains(t.Name))templates[t.Name]=t.Sql;
        var window=new Window{Title="SQL 模板",Width=760,Height=590,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid{RowDefinitions=new RowDefinitions("Auto,*,Auto,Auto"),Margin=new Thickness(18)};
        var picker=new ComboBox{ItemsSource=templates.Keys,HorizontalAlignment=HorizontalAlignment.Stretch};var text=new TextBox{AcceptsReturn=true,TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontFamily=new Avalonia.Media.FontFamily("Consolas"),Margin=new Thickness(0,14)};Grid.SetRow(text,1);
        picker.SelectionChanged+=(_,_)=>text.Text=templates[picker.SelectedItem!.ToString()!];picker.SelectedIndex=0;
        var name=new TextBox{Watermark="自定义模板名称",Width=260};var save=new Button{Content="保存模板"};var delete=new Button{Content="删除自定义模板"};
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};actions.Children.Add(name);actions.Children.Add(save);actions.Children.Add(delete);Grid.SetRow(actions,2);
        var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        picker.SelectionChanged+=(_,_)=>name.Text=picker.SelectedItem?.ToString();
        save.Click+=async(_,_)=>{try{var key=name.Text?.Trim();if(string.IsNullOrEmpty(key)||builtin.Contains(key))throw new ArgumentException("请使用新的自定义名称，内置模板不能覆盖。");var item=new SqlTemplate(key,text.Text??"");var next=custom.Where(t=>t.Name!=key).Append(item).ToList();await _vm.Store.SaveTemplatesAsync(next);custom=next;templates[key]=item.Sql;picker.ItemsSource=templates.Keys.ToArray();picker.SelectedItem=key;status.Text="自定义模板已加密保存。";}catch(Exception ex){status.Text=ex.Message;}};
        delete.Click+=async(_,_)=>{try{var key=picker.SelectedItem?.ToString();if(key is null||builtin.Contains(key))return;var next=custom.Where(t=>t.Name!=key).ToList();await _vm.Store.SaveTemplatesAsync(next);custom=next;templates.Remove(key);picker.ItemsSource=templates.Keys.ToArray();picker.SelectedIndex=0;status.Text="自定义模板已删除。";}catch(Exception ex){status.Text=ex.Message;}};
        var use=new Button{Content="在新查询页中打开"};use.Click+=(_,_)=>{_vm.NewQuery(text.Text);window.Close();};var footer=new StackPanel{Spacing=8};footer.Children.Add(use);footer.Children.Add(status);Grid.SetRow(footer,3);
        grid.Children.Add(picker);grid.Children.Add(text);grid.Children.Add(actions);grid.Children.Add(footer);window.Content=grid;await window.ShowDialog(this);
    }
}
