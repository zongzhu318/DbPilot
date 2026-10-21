using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private bool _loadingDatabases;
    private CancellationTokenSource? _databaseRequest;
    private readonly Dictionary<Guid,IReadOnlyList<string>> _databaseLists=new();
    private void UpdateConnectionControls()
    {
        if(DatabasePicker is null)return;var p=_vm.Connection;
        DatabasePicker.IsEnabled=p is not null&&!_vm.IsClosed(p)&&!_vm.Busy;
        FileOpenConnection.IsEnabled=p is not null&&!_vm.IsOpen(p)&&!_vm.Busy;
        FileCloseConnection.IsEnabled=p is not null&&_vm.IsOpen(p);
        ShowTablesButton.IsEnabled=p is not null&&!_vm.IsClosed(p)&&!_vm.Busy&&!string.IsNullOrWhiteSpace(_vm.CurrentProfile?.Database);
        if(p is not null&&_vm.IsClosed(p)){_databaseRequest?.Cancel();CancelCompletion();}
        if(Results is not null)UpdateEditControls();
    }
    private async Task RefreshDatabasePicker(bool reload=false)
    {
        if(DatabasePicker is null)return;
        _databaseRequest?.Cancel();using var request=new CancellationTokenSource(TimeSpan.FromSeconds(8));_databaseRequest=request;
        var p=_vm.Connection;
        try{
            UpdateConnectionControls();
            _loadingDatabases=true;
            var current=_vm.CurrentProfile?.Database??"";
            DatabasePicker.ItemsSource=p is not null&&_databaseLists.TryGetValue(p.Id,out var existing)?existing:current.Length>0?new[]{current}:Array.Empty<string>();DatabasePicker.SelectedItem=current;_loadingDatabases=false;
            if(p is null||_vm.IsClosed(p))return;
            if(reload||!_databaseLists.ContainsKey(p.Id)){
                var names=await _vm.ReadDatabasesAsync(p,request.Token);request.Token.ThrowIfCancellationRequested();_databaseLists[p.Id]=names;
            }
            if(_vm.Connection?.Id!=p.Id)return;
            _loadingDatabases=true;DatabasePicker.ItemsSource=_databaseLists[p.Id];DatabasePicker.SelectedItem=_vm.CurrentProfile?.Database;_loadingDatabases=false;UpdateConnectionControls();
        }catch(OperationCanceledException){}catch(Exception ex){_vm.Log("数据库列表加载失败："+ex.Message,"Warn");}
        finally{_loadingDatabases=false;if(_databaseRequest==request)_databaseRequest=null;}
    }
    private async void ChangeDatabase(object? sender,SelectionChangedEventArgs e)
    {
        if(_loadingDatabases||DatabasePicker.SelectedItem is not string database||database==_vm.CurrentProfile?.Database)return;
        try{if(!FlushGridEdits())throw new InvalidOperationException("请完成当前单元格输入。");_vm.SelectDatabase(database);CancelCompletion();_objectSuggestions.Clear();_columnSuggestions.Clear();WhereFilter.Text="";PageLabel.Text="第 1 页";await ShowTableCatalog();}
        catch(Exception ex){_vm.Log(ex.Message);_loadingDatabases=true;DatabasePicker.SelectedItem=_vm.CurrentProfile?.Database;_loadingDatabases=false;}
    }
    private async void ReloadDatabases(object? sender,RoutedEventArgs e)=>await RefreshDatabasePicker(true);
    private ConnectionProfile RequireContext(bool database)
    {
        if(_vm.Busy)throw new InvalidOperationException("请等待当前任务结束。");
        var p=_vm.CurrentProfile??throw new InvalidOperationException("请先选择连接。");
        if(_vm.IsClosed(p))throw new InvalidOperationException("请先打开连接。");
        if(database&&string.IsNullOrWhiteSpace(p.Database))throw new InvalidOperationException("请先选择数据库。");return p;
    }
    private async Task RefreshObjects(ConnectionProfile profile,string? database=null)
    {
        _objectSuggestions.Clear();_columnSuggestions.Clear();
        await RefreshDatabasePicker(true);
        foreach(var node in _vm.Nodes.Where(n=>n.Profile.Id==profile.Id).ToArray()){
            node.Loaded=false;await _vm.ExpandAsync(node);
            if(database is not null&&node.Children.FirstOrDefault(n=>n.Database==database) is {} dbNode)await _vm.ExpandAsync(dbNode);
        }
    }
    private async void CreateDatabase(object? sender,RoutedEventArgs e)
    {
        try{
            var profile=RequireContext(false);await using var provider=DbProviderFactory.Create(profile);
            var dialog=DialogUi.Dialog("新建数据库 · "+profile.Name,600,400);dialog.CanResize=false;
            var name=new TextBox{Watermark="数据库名称"};var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,Height=96,FontFamily=new Avalonia.Media.FontFamily("Consolas,monospace")};var status=DialogUi.Status();
            var create=new Button{Content="创建",MinWidth=88};create.Classes.Add("primary");
            var body=new StackPanel{Margin=new Thickness(24,20)};
            body.Children.Add(new TextBlock{Text="将执行 CREATE DATABASE；名称不能为空。",Classes={"hint"},Margin=new Thickness(0,0,0,16)});
            body.Children.Add(DialogUi.Field("数据库名称",name));
            body.Children.Add(new TextBlock{Text="预览",Classes={"hint"},Margin=new Thickness(0,4,0,6)});
            body.Children.Add(preview);
            dialog.Content=DialogUi.Shell("新建数据库",profile.Name,body,DialogUi.Footer(status,DialogUi.Secondary("取消",()=>dialog.Close(),88),create));
            name.TextChanged+=(_,_)=>{try{preview.Text=DatabaseDdl.CreateDatabase(provider,profile.Kind,name.Text??"");status.Text="";}catch(Exception ex){preview.Text="";status.Text=ex.Message;}};
            create.Click+=async(_,_)=>{
                try{var database=DatabaseDdl.Name(name.Text);var sql=DatabaseDdl.CreateDatabase(provider,profile.Kind,database);create.IsEnabled=false;
                    if(await _vm.ExecuteDdlAsync(profile,sql)){dialog.Close();await RefreshObjects(profile);if(_vm.Connection?.Id==profile.Id&&FlushGridEdits()){try{_vm.SelectDatabase(database);}catch(Exception ex){_vm.Log(ex.Message);}}await RefreshDatabasePicker();}
                    else status.Text=_vm.Status;
                }catch(Exception ex){status.Text=ex.Message;}finally{create.IsEnabled=true;}
            };
            await dialog.ShowDialog(this);
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
    private async void CreateTable(object? sender,RoutedEventArgs e)
    {
        try{
            var profile=RequireContext(true);await using var provider=DbProviderFactory.Create(profile);
            await new ObjectDesignerWindow(provider,profile.Kind,profile.Database,null,async sql=>{
                if(!await _vm.ExecuteDdlAsync(profile,sql))return false;await RefreshObjects(profile,profile.Database);return true;
            },true).ShowDialog<string?>(this);
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
    private (ConnectionProfile Profile,DbObject Table) SelectedTable()
    {
        var p=RequireContext(true);
        if(ObjectTree.SelectedItem is ObjectNode{Object:{} obj} node&&node.Profile.Id==p.Id&&obj.Kind is "表" or "普通表" or "子表" or "超级表")return (DbPilot.ViewModels.MainViewModel.CloneForDatabase(node.Profile,node.Database),obj);
        if(_vm.Tab?.Table is {} table&&table.Kind is "表" or "普通表" or "子表" or "超级表")return(p,table);
        throw new InvalidOperationException("请先在对象树选择数据表。");
    }
    private async void RenameTable(object? sender,RoutedEventArgs e)
    {
        try{
            var (profile,table)=SelectedTable();await RenameTableAsync(profile,table);
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
    private async Task RenameTableAsync(ConnectionProfile profile,DbObject table)
    {
        try{
            if(_vm.Busy||_vm.IsClosed(profile))throw new InvalidOperationException("请先打开连接并等待当前任务完成。");
            if(!FlushGridEdits())return;_vm.EnsureObjectHasNoDrafts(profile,table);
            if(profile.Kind==DatabaseKind.TDengine)throw new NotSupportedException("TDengine 当前不支持表重命名。");
            await using var provider=DbProviderFactory.Create(profile);
            var dialog=DialogUi.Dialog("重命名表 · "+table.Name,560,280);dialog.CanResize=false;
            var name=new TextBox{Text=table.Name,Watermark="新表名"};var status=DialogUi.Status();var rename=new Button{Content="重命名",MinWidth=88};rename.Classes.Add("primary");
            var subtitle=string.IsNullOrWhiteSpace(table.Schema)?table.Database+"."+table.Name:table.Database+"."+table.Schema+"."+table.Name;
            var body=new StackPanel{Margin=new Thickness(24,20)};body.Children.Add(DialogUi.Field("新表名",name));
            dialog.Content=DialogUi.Shell("重命名表",subtitle,body,DialogUi.Footer(status,DialogUi.Secondary("取消",()=>dialog.Close(),88),rename));
            rename.Click+=async(_,_)=>{try{_vm.EnsureObjectHasNoDrafts(profile,table);rename.IsEnabled=false;if(await _vm.ExecuteDdlAsync(profile,DatabaseDdl.RenameTable(provider,profile.Kind,table,name.Text??""))){_vm.InvalidateObject(profile,table);dialog.Close();await RefreshObjects(profile,table.Database);if(TableCatalogPanel.IsVisible)await ShowTableCatalog();}else status.Text=_vm.Status;}catch(Exception ex){status.Text=ex.Message;}finally{rename.IsEnabled=true;}};await dialog.ShowDialog(this);
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
    private async void DropTable(object? sender,RoutedEventArgs e)
    {
        try{
            var (profile,table)=SelectedTable();if(!FlushGridEdits())return;_vm.EnsureObjectHasNoDrafts(profile,table);
            if(!await Confirm($"永久删除 {table.Database}.{table.Schema}.{table.Name} 及其全部数据？此操作不可撤销。"+(table.Kind=="超级表"?"该超级表的所有子表也会受影响。":""),"永久删除"))return;
            _vm.EnsureObjectHasNoDrafts(profile,table);await using var provider=DbProviderFactory.Create(profile);
            if(await _vm.ExecuteDdlAsync(profile,DatabaseDdl.DropTable(provider,table))){_vm.InvalidateObject(profile,table);await RefreshObjects(profile,table.Database);}
        }catch(Exception ex){_vm.Log(ex.Message);}
    }
}
