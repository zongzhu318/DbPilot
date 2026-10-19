using System.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DbStudio.Services;

namespace DbStudio.Views;

public sealed partial class MainWindow
{
    private async void ViewCell(object? sender,RoutedEventArgs e)
    {
        if(!FlushGridEdits()||Results.SelectedItem is not ResultRow selected||Results.CurrentColumn is not {} column)return;
        int index=Results.Columns.IndexOf(column);var value=selected[index];var tab=_vm.Tab;var session=tab?.Edits;
        bool writable=!_vm.Busy&&session is {CommitUncertain:false}&&session.CanEditColumn(index);
        var window=new Window{Title="字段详情 · "+tab?.Result?.Columns[index],Width=850,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid{RowDefinitions=new("*,Auto"),Margin=new Avalonia.Thickness(16)};
        var text=new TextBox{Text=value is byte[] b?Convert.ToHexString(b):value is null or DBNull?"":Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture),AcceptsReturn=true,TextWrapping=Avalonia.Media.TextWrapping.Wrap,IsReadOnly=!writable,FontFamily=new("Consolas,monospace")};
        var footer=new StackPanel{Spacing=8,Margin=new Avalonia.Thickness(0,12,0,0)};Grid.SetRow(footer,1);var status=new TextBlock{Text=writable?"修改仅暂存到当前页，仍需提交变更。":"当前字段只读。",TextWrapping=Avalonia.Media.TextWrapping.Wrap};footer.Children.Add(status);
        if(writable){var save=new Button{Content="暂存修改"};save.Click+=(_,_)=>{try{if(!ReferenceEquals(tab,_vm.Tab)||session!.CommitUncertain||_vm.Busy)throw new InvalidOperationException("编辑会话已变化，请关闭后重新打开。");selected.Row[index]=Services.DataValues.ForColumn(text.Text??"",session.Columns[index],tab!.Connection!.Kind)??DBNull.Value;window.Close();}catch(Exception ex){status.Text=ex.Message;}};footer.Children.Add(save);}
        grid.Children.Add(text);grid.Children.Add(footer);window.Content=grid;await window.ShowDialog(this);
    }
    private TableEditSession? _shownEdits;
    private void ObserveEditSession(TableEditSession? session)
    {
        if(_shownEdits is not null)_shownEdits.Changed-=UpdateEditControls;
        _shownEdits=session;
        if(_shownEdits is not null)_shownEdits.Changed+=UpdateEditControls;
    }
    private void UpdateEditControls()
    {
        var session=_vm.Tab?.Edits;EditToolbar.IsVisible=session is not null;bool writable=session is {CommitUncertain:false} && !_vm.Busy && (_vm.Tab?.Connection is not {} connection || !_vm.IsClosed(connection));
        Results.IsReadOnly=!writable;Results.IsEnabled=!_vm.Busy;
        QueryTabs.IsEnabled=!_vm.Busy;ConnectionPicker.IsEnabled=!_vm.Busy;
        bool browse=!_vm.Busy && _vm.Tab?.Table is not null && _vm.Tab.Connection is {} profile && !_vm.IsClosed(profile);
        WhereFilter.IsEnabled=browse;ApplyFilterButton.IsEnabled=browse;
        PreviousPageButton.IsEnabled=browse&&_vm.Tab!.Page>0;
        NextPageButton.IsEnabled=browse&&(_vm.Tab!.Result?.Rows.Count??0)>=_vm.Tab.PageSize;
        ExecuteSqlButton.IsEnabled=!_vm.Busy&&_vm.CurrentProfile is {} active&&!_vm.IsClosed(active);StopSqlButton.IsEnabled=_vm.Busy;
        AddRowButton.IsEnabled=writable;DeleteRowButton.IsEnabled=writable;NullCellButton.IsEnabled=writable;
        CommitEditsButton.IsEnabled=writable;RevertEditsButton.IsEnabled=session is not null && !_vm.Busy;
        EditStatus.Text=_vm.Tab?.Connection is {} closed&&_vm.IsClosed(closed)?"连接已关闭 · 本地改动已保留，重新连接后可继续":session is null ? "只读 · 从对象树打开有主键的关系表可编辑" : session.CommitUncertain ? "提交结果未知 · 请核对数据库后重新加载" : $"待提交 {session.PendingCount} 行 · 双击单元格编辑";
    }
    private bool FlushGridEdits()
    {
        if(Results is null)return true;
        if(!Results.CommitEdit(DataGridEditingUnit.Cell,true) || !Results.CommitEdit(DataGridEditingUnit.Row,true)){
            _vm.Log("单元格数据无效，请修正输入或按 Esc 取消编辑。");return false;
        }
        return true;
    }
    private void AddDataRow(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy || !FlushGridEdits() || _vm.Tab?.Edits is not {} session)return;
        try{var row=session.AddRow();Results.SelectedItem=_resultRows?.FirstOrDefault(v=>ReferenceEquals(v.Row,row));}
        catch(Exception ex){_vm.Log("无法新增行："+ex.Message);}
    }
    private async void DeleteDataRow(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy || !FlushGridEdits() || _vm.Tab?.Edits is not {} session || Results.SelectedItem is not ResultRow selected)return;
        if(!await Confirm("将选中行标记为删除？点击“提交变更”后才会修改数据库。","标记删除"))return;
        try{session.DeleteRow(selected.Row);}catch(Exception ex){_vm.Log("无法删除行："+ex.Message);}
    }
    private void SetCellNull(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy || !FlushGridEdits() || _vm.Tab?.Edits is not {} session || Results.SelectedItem is not ResultRow selected || Results.CurrentColumn is not {} column)return;
        try{session.SetNull(selected.Row,Results.Columns.IndexOf(column));}catch(Exception ex){_vm.Log(ex.Message);}
    }
    private async void CommitDataEdits(object? sender,RoutedEventArgs e)
    {
        if(!FlushGridEdits() || _vm.Busy || _vm.Tab?.Edits is not {} session)return;
        int count;
        try{count=session.GetChanges().Count;}catch(Exception ex){_vm.Log(ex.Message);return;}
        if(count==0){_vm.Log("没有需要提交的数据改动。");return;}
        if(!await Confirm($"将 {count} 行新增、修改或删除通过一个事务提交到数据库？","提交事务"))return;
        await _vm.CommitEditsAsync();UpdateEditControls();
    }
    private async void RevertDataEdits(object? sender,RoutedEventArgs e)
    {
        if(_vm.Busy)return;
        if(_vm.Tab?.Edits?.CommitUncertain==true){
            if(await Confirm("上次事务可能已提交。请先核对数据库；丢弃本地会话不会撤销服务器上的修改。确定丢弃并重新加载？","丢弃并重新加载")){
                _vm.DiscardUncertainSession();await Browse(_vm.Tab?.Page??0);
            }
            return;
        }
        Results.CancelEdit();_vm.RevertEdits();UpdateEditControls();
    }
}
