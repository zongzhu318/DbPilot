using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private bool _changingWorkspaceMode;
    private bool _catalogHasLoaded;
    private void SetWorkspaceMode(bool catalog)
    {
        _changingWorkspaceMode=true;
        WorkspaceModeTabs.SelectedIndex=catalog?0:1;
        _changingWorkspaceMode=false;
        TableCatalogPanel.IsVisible=catalog;
        StartPanel.IsVisible=!catalog&&!_vm.HasTab;
    }
    private async void ChangeWorkspaceMode(object? sender,SelectionChangedEventArgs e)
    {
        if(_changingWorkspaceMode||TableCatalogPanel is null)return;
        if(WorkspaceModeTabs.SelectedIndex!=0){CloseCatalog();return;}
        if(!FlushGridEdits()){SetWorkspaceMode(false);return;}
        var profile=_vm.CurrentProfile;
        if(profile is null||string.IsNullOrWhiteSpace(profile.Database)||_vm.IsClosed(profile)){
            SetWorkspaceMode(false);_vm.Log("请先打开连接并选择数据库。");return;
        }
        if(_catalogHasLoaded&&_catalogError is null&&_catalogProfile?.Id==profile.Id&&_catalogProfile.Database==profile.Database){SetWorkspaceMode(true);return;}
        await ShowTableCatalog();
    }
    private DbObject? _catalogPointerTarget;
    private void CatalogPointerPressed(object? sender,PointerPressedEventArgs e)
    {
        if(!e.GetCurrentPoint(TableCatalogGrid).Properties.IsRightButtonPressed)return;
        _catalogPointerTarget=ItemAt<DbObject>(e.Source);
        if(_catalogPointerTarget is {} table)TableCatalogGrid.SelectedItem=table;
    }
    private void CatalogContextRequested(object? sender,ContextRequestedEventArgs e)
    {
        // DataGrid cells consume pointer events; receive the context gesture in the tunnel.
        e.Handled=true;
        var table=e.TryGetPosition(TableCatalogGrid,out _)?_catalogPointerTarget:TableCatalogGrid.SelectedItem as DbObject;
        if(table is null || _catalogProfile is null || _catalogLoading)return;
        TableCatalogGrid.SelectedItem=table;
        var profile=_catalogProfile;
        bool available=!_vm.Busy&&!_vm.IsClosed(profile);
        bool isTable=IsTableObject(table);bool truncatable=available&&profile.Kind!=DatabaseKind.TDengine&&isTable;
        var node=new ObjectNode{Profile=profile,Database=table.Database,Object=table,Label=table.Name,Loaded=true,Kind=table.Kind=="视图"?ObjectNodeKind.View:ObjectNodeKind.Table};
        var open=new MenuItem{Header=isTable?"打开表":"打开视图",IsEnabled=available};
        open.Click+=async(_,_)=>{if(_vm.Busy||_vm.IsClosed(profile))return;CloseCatalog();await _vm.OpenTableAsync(node,Size);};
        var properties=new MenuItem{Header="属性 · 表结构与建表语句",IsEnabled=available};
        properties.Click+=async(_,_)=>await ShowTableStructure(profile,table);
        var design=new MenuItem{Header="设计表",IsEnabled=available&&isTable};
        design.Click+=async(_,_)=>{await OpenDesigner(profile,table);};
        var newTable=new MenuItem{Header="新建表",IsEnabled=available};
        newTable.Click+=async(_,_)=>await CreateTableForContextAsync(node);
        var rename=new MenuItem{Header="重命名",IsEnabled=available&&profile.Kind!=DatabaseKind.TDengine&&isTable};
        rename.Click+=async(_,_)=>await RenameTableAsync(profile,table);
        var delete=new MenuItem{Header="删除表",IsEnabled=available&&isTable};
        delete.Click+=async(_,_)=>await DropTableAsync(profile,table);
        var clear=new MenuItem{Header="清空表",IsEnabled=truncatable};
        clear.Click+=async(_,_)=>await ClearTableAsync(node,false);
        var truncate=new MenuItem{Header="截断表",IsEnabled=truncatable};
        truncate.Click+=async(_,_)=>await ClearTableAsync(node,true);
        var copyTable=BuildCopyTableMenu(node,truncatable);
        var permissions=new MenuItem{Header="设置权限",IsEnabled=truncatable};
        permissions.Click+=async(_,_)=>await SetPermissionsAsync(node);
        var import=new MenuItem{Header="导入向导…",IsEnabled=available&&isTable};
        import.Click+=async(_,_)=>await OpenDatabaseToolsTab(6,node);
        var export=new MenuItem{Header="导出向导…",IsEnabled=available};
        export.Click+=async(_,_)=>await OpenDatabaseToolsTab(5,node);
        var dump=BuildDumpMenu(node,available&&isTable&&profile.Kind!=DatabaseKind.TDengine);
        var maintenance=BuildMaintenanceMenu(node,table,available&&isTable);
        var copy=new MenuItem{Header="复制表名"};copy.Click+=async(_,_)=>{if(Clipboard is not null)await Clipboard.SetTextAsync(table.ToString());};
        var refresh=new MenuItem{Header="刷新",IsEnabled=available};
        refresh.Click+=async(_,_)=>await ShowTableCatalog();
        var menu=TableCatalogGrid.ContextMenu!;
        var commands=new List<Control>{open,design,newTable,properties,rename,delete,new Separator(),clear,truncate,copyTable,permissions,new Separator(),import,export,dump};
        if(maintenance is not null)commands.Add(maintenance);
        commands.Add(new Separator());commands.Add(copy);commands.Add(refresh);
        menu.ItemsSource=commands;
        menu.Open(TableCatalogGrid);
    }
    private IReadOnlyList<DbObject> _catalogTables=[];
    private ConnectionProfile? _catalogProfile;
    private int _catalogRequest;
    private CancellationTokenSource? _catalogCancellation;
    private bool _catalogLoading;
    private string? _catalogError;
    private void CloseCatalog(){++_catalogRequest;_catalogCancellation?.Cancel();_catalogLoading=false;_catalogError=null;TableCatalogGrid.IsEnabled=true;SetWorkspaceMode(false);}
    private void UpdateCatalogSummary()
    {
        var count=TableCatalogGrid.ItemsSource?.Cast<DbObject>().Count()??0;
        var hint=_catalogProfile?.Kind==DatabaseKind.TDengine?" · TDengine 子表请在对象树中按超级表查看":"";
        TableCatalogSummary.Text=(_catalogError??(_catalogLoading?"正在加载表列表…":_catalogTables.Count==0?"此数据库暂无表或视图。":count==0?"没有匹配的表，请修改或清空筛选条件。":$"显示 {count} / {_catalogTables.Count} 个表与视图 · 双击或按 Enter 打开，右键查看属性"))+hint;
    }
    private async Task ShowTableCatalog()
    {
        var profile=_vm.CurrentProfile;
        if(profile is null || string.IsNullOrWhiteSpace(profile.Database) || _vm.IsClosed(profile))return;
        _catalogCancellation?.Cancel();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _catalogCancellation=timeout;
        var request=++_catalogRequest;
        bool sameContext=_catalogProfile?.Id==profile.Id&&_catalogProfile.Database==profile.Database;
        _catalogProfile=profile;_catalogLoading=true;_catalogError=null;
        if(!sameContext){_catalogHasLoaded=false;_catalogTables=[];TableCatalogFilter.Text="";}
        TableCatalogTitle.Text=$"{profile.Name} / {profile.Database}";
        TableCatalogGrid.IsEnabled=false;SetWorkspaceMode(true);ApplyCatalogFilter();
        try{
            await using var provider=DbProviderFactory.Create(profile);
            var objects=await provider.ObjectsLiteAsync(profile.Database,timeout.Token);
            var current=_vm.CurrentProfile;
            if(request!=_catalogRequest || _vm.IsClosed(profile)||current?.Id!=profile.Id||current.Database!=profile.Database)return;
            var selected=(TableCatalogGrid.SelectedItem as DbObject)?.ToString();
            _catalogHasLoaded=true;
            _catalogTables=objects.Where(o=>o.Kind is "表" or "视图" or "普通表" or "子表" or "超级表").OrderBy(o=>o.Schema).ThenBy(o=>o.Name).ToArray();
            ApplyCatalogFilter();
            TableCatalogGrid.SelectedItem=_catalogTables.FirstOrDefault(o=>o.ToString()==selected);
        }catch(OperationCanceledException){if(request==_catalogRequest){_catalogError="加载超时，请点击刷新重试。";_vm.Log(_catalogError,"Warn");}}
        catch(Exception ex){if(request==_catalogRequest){_catalogError="表列表加载失败，请点击刷新重试："+ex.Message;_vm.Log(_catalogError,"Warn");}}
        finally{
            if(request==_catalogRequest){_catalogLoading=false;TableCatalogGrid.IsEnabled=true;UpdateCatalogSummary();}
            if(ReferenceEquals(_catalogCancellation,timeout))_catalogCancellation=null;
        }
    }
    private void ApplyCatalogFilter(){var text=TableCatalogFilter.Text?.Trim()??"";TableCatalogGrid.ItemsSource=_catalogTables.Where(o=>$"{o.Name} {o.Schema} {o.Kind}".Contains(text,StringComparison.OrdinalIgnoreCase)).ToArray();UpdateCatalogSummary();}
    private void FilterTableCatalog(object? sender,TextChangedEventArgs e){if(TableCatalogGrid is not null&&TableCatalogSummary is not null)ApplyCatalogFilter();}
    private async void ReloadTableCatalog(object? sender,RoutedEventArgs e)=>await ShowTableCatalog();
    private void HideTableCatalog(object? sender,RoutedEventArgs e){CloseCatalog();SqlEditor.Focus();}
    private async void ShowCatalog(object? sender,RoutedEventArgs e)=>await ShowTableCatalog();
    private void ClearCatalogFilter(object? sender,RoutedEventArgs e){TableCatalogFilter.Text="";TableCatalogFilter.Focus();}
    private async void CatalogKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){e.Handled=true;HideTableCatalog(sender,new RoutedEventArgs());}
        else if(e.Key==Key.Enter && sender==TableCatalogGrid){e.Handled=true;await OpenCatalogSelection();}
        else if(e.Key==Key.F5){e.Handled=true;await ShowTableCatalog();}
    }
    private async void OpenCatalogTable(object? sender,TappedEventArgs e)
        =>await OpenCatalogSelection();
    private async void OpenCatalogSelection(object? sender,RoutedEventArgs e)=>await OpenCatalogSelection();
    private async void OpenCatalogDesign(object? sender,RoutedEventArgs e)
    {
        if(TableCatalogGrid.SelectedItem is not DbObject table||_catalogProfile is null){_vm.Log("请先选择数据表。");return;}
        if(_vm.Busy||_vm.IsClosed(_catalogProfile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        await OpenDesigner(_catalogProfile,table);
    }
    private async Task OpenDesigner(ConnectionProfile profile,DbObject table)
    {
        try{
            await using var provider=DbProviderFactory.Create(profile);
            await new ObjectDesignerWindow(provider,profile.Kind,table.Database,table,async sql=>{
                if(!await _vm.ExecuteDdlAsync(profile,sql))return false;
                _vm.InvalidateObject(profile,table);await RefreshObjects(profile,table.Database);
                if(TableCatalogPanel.IsVisible)await ShowTableCatalog();
                return true;
            }).ShowDialog<string?>(this);
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
    private async void DropCatalogSelection(object? sender,RoutedEventArgs e)
    {
        if(TableCatalogGrid.SelectedItem is not DbObject table||_catalogProfile is null){_vm.Log("请先选择数据表。");return;}
        var profile=_catalogProfile;
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        if(!FlushGridEdits())return;
        try{
            _vm.EnsureObjectHasNoDrafts(profile,table);
            if(!await Confirm($"永久删除 {table.Database}.{table.Schema}.{table.Name} 及其全部数据？此操作不可撤销。","永久删除"))return;
            await using var provider=DbProviderFactory.Create(profile);
            if(await _vm.ExecuteDdlAsync(profile,DatabaseDdl.DropTable(provider,table))){
                _vm.InvalidateObject(profile,table);await RefreshObjects(profile,table.Database);
                if(TableCatalogPanel.IsVisible)await ShowTableCatalog();
            }
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
    private async void OpenCatalogImport(object? sender,RoutedEventArgs e)=>await OpenDatabaseToolsTab(6);
    private async void OpenCatalogExport(object? sender,RoutedEventArgs e)=>await OpenDatabaseToolsTab(5);
    private async Task OpenDatabaseToolsTab(int index)
    {
        if(_vm.Busy)return;
        await new DatabaseToolsWindow(_vm.Connections,_vm.CurrentProfile??_vm.Connection,(sql,profile)=>_vm.NewQuery(sql,profile),index).ShowDialog(this);
    }
    private async Task OpenDatabaseToolsTab(int index,ObjectNode node)
    {
        if(_vm.Busy)return;
        await new DatabaseToolsWindow(_vm.Connections,MenuProfile(node),(sql,profile)=>_vm.NewQuery(sql,profile),index).ShowDialog(this);
    }
    private async Task OpenCatalogSelection()
    {
        if(_vm.Busy||_catalogLoading||TableCatalogGrid.SelectedItem is not DbObject table || _catalogProfile is null)return;
        if(_vm.IsClosed(_catalogProfile)){_vm.Log("请先打开连接。");return;}
        if(!FlushGridEdits())return;
        CloseCatalog();
        await _vm.OpenTableAsync(new ObjectNode{Profile=_catalogProfile,Database=table.Database,Object=table,Label=table.Name},Size);
    }
}
