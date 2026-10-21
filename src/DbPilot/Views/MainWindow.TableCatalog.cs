using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbPilot.Models;
using DbPilot.Providers;

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
        var open=new MenuItem{Header="打开数据",IsEnabled=available};
        open.Click+=async(_,_)=>{if(_vm.Busy||_vm.IsClosed(profile))return;CloseCatalog();await _vm.OpenTableAsync(new ObjectNode{Profile=profile,Database=table.Database,Object=table,Label=table.Name},Size);};
        var properties=new MenuItem{Header="属性 · 表结构与建表语句",IsEnabled=available};
        properties.Click+=async(_,_)=>await ShowTableStructure(profile,table);
        var rename=new MenuItem{Header="重命名",IsEnabled=available&&profile.Kind!=DatabaseKind.TDengine&&table.Kind!="视图"};
        rename.Click+=async(_,_)=>await RenameTableAsync(profile,table);
        var copy=new MenuItem{Header="复制表名"};copy.Click+=async(_,_)=>{if(Clipboard is not null)await Clipboard.SetTextAsync(table.ToString());};
        var menu=TableCatalogGrid.ContextMenu!;
        menu.ItemsSource=new Control[]{open,properties,rename,new Separator(),copy};
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
        TableCatalogSummary.Text=_catalogError??(_catalogLoading?"正在加载表列表…":_catalogTables.Count==0?"此数据库暂无表或视图。":count==0?"没有匹配的表，请修改或清空筛选条件。":$"显示 {count} / {_catalogTables.Count} 个表与视图 · 双击或按 Enter 打开，右键查看属性");
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
            var objects=await provider.ObjectsAsync(profile.Database,timeout.Token);
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
    private async Task OpenCatalogSelection()
    {
        if(_vm.Busy||_catalogLoading||TableCatalogGrid.SelectedItem is not DbObject table || _catalogProfile is null)return;
        if(_vm.IsClosed(_catalogProfile)){_vm.Log("请先打开连接。");return;}
        if(!FlushGridEdits())return;
        CloseCatalog();
        await _vm.OpenTableAsync(new ObjectNode{Profile=_catalogProfile,Database=table.Database,Object=table,Label=table.Name},Size);
    }
}
