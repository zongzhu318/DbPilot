using System.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DbPilot.Models;
using DbPilot.Services;

namespace DbPilot.Views;

public sealed partial class MainWindow
{
    private async void ViewCell(object? sender,RoutedEventArgs e)
    {
        if(!FlushGridEdits()||Results.SelectedItem is not ResultRow selected||Results.CurrentColumn is not {} column)return;
        int index=Results.Columns.IndexOf(column);var tab=_vm.Tab;var session=tab?.Edits;
        if(tab?.Result is not {} result || index<0 || index>=result.Columns.Count)return;
        var value=selected[index];var raw=PreviewedRawValue(tab,selected,index);
        bool writable=!_vm.Busy&&session is {CommitUncertain:false}&&session.CanEditColumn(index);
        bool previewed=tab.PreviewColumns?.Contains(index)==true;
        var window=DialogUi.Dialog("字段详情 · "+result.Columns[index],880,620);
        var text=new TextBox{Text=DetailText(previewed?raw??value:value),AcceptsReturn=true,TextWrapping=Avalonia.Media.TextWrapping.Wrap,IsReadOnly=!writable,FontFamily=new("Consolas,monospace")};
        var body=new Border{Padding=new Avalonia.Thickness(24,16),Child=text};
        var status=DialogUi.Status();status.Text=writable?"修改仅暂存到当前页，仍需提交变更。":"当前字段只读。";
        if(previewed){
            var originals=OriginalValues(selected.Row);
            if(selected.Row.RowState==DataRowState.Added)status.Text=$"新行尚未写入数据库，仅显示前 {LargeColumns.PreviewLength} 字符预览；提交并刷新后可查看完整内容。";
            else if(raw is null or DBNull)status.Text="该字段为 NULL。";
            else{
                var reader=LargeValueReader.Create(tab,result,out var reason);
                if(reader is null)status.Text="无法读取完整内容："+reason;
                else if(reader.IsComplete(originals)){status.Text=$"该字段未超过 {LargeColumns.PreviewLength} 字符，已完整显示。";await reader.DisposeAsync();}
                else if(reader.PositionOf(index)>=0){
                    status.Text=$"当前仅显示前 {LargeColumns.PreviewLength} 字符预览，正在加载完整内容…";
                    _=LoadFullValueAsync(reader,originals,index,text,status,window);
                }else{status.Text="该字段不在大字段预览列中。";await reader.DisposeAsync();}
            }
        }
        var buttons=new List<Control>{DialogUi.Secondary("关闭",()=>window.Close(),88)};
        if(writable)buttons.Add(DialogUi.Primary("暂存修改",()=>{try{if(!ReferenceEquals(tab,_vm.Tab)||session!.CommitUncertain||_vm.Busy)throw new InvalidOperationException("编辑会话已变化，请关闭后重新打开。");selected.Row[index]=Services.DataValues.ForColumn(text.Text??"",session.Columns[index],tab!.Connection!.Kind)??DBNull.Value;window.Close();}catch(Exception ex){DialogUi.Error(status,ex.Message);}},88));
        window.Content=DialogUi.Shell("字段详情",result.Columns[index],body,DialogUi.Footer(status,buttons.ToArray()));
        await window.ShowDialog(this);
    }
    private object? PreviewedRawValue(QueryTab tab,ResultRow row,int index)
    {
        if(tab.Result is not {} result || _resultData is null)return row[index];
        int rowIndex=_resultData.Rows.IndexOf(row.Row);
        return rowIndex>=0 && rowIndex<result.Rows.Count && index<result.Rows[rowIndex].Length ? result.Rows[rowIndex][index] : row[index];
    }
    private static object?[] OriginalValues(DataRow row)
    {
        var values=new object?[row.Table.Columns.Count];
        for(int i=0;i<values.Length;i++){
            var value=row.RowState is DataRowState.Added or DataRowState.Detached ? row[i] : row[i,DataRowVersion.Original];
            values[i]=value is DBNull?null:value;
        }
        return values;
    }
    private static string DetailText(object? value)=>value switch{null or DBNull=>"",byte[] bytes=>Convert.ToHexString(bytes),_=>Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)??""};
    private static string CopyText(object? value)=>value switch{null or DBNull=>"NULL",byte[] bytes=>Convert.ToHexString(bytes),_=>SqlTools.Display(value)};
    private static async Task LoadFullValueAsync(LargeValueReader reader,object?[] rowValues,int index,TextBox text,TextBlock status,Window window)
    {
        using var cts=new CancellationTokenSource();
        void Cancel(object? _,EventArgs __)=>cts.Cancel();
        window.Closed+=Cancel;
        try{
            var values=await reader.ReadAsync(rowValues,cts.Token);
            if(cts.IsCancellationRequested)return;
            int position=reader.PositionOf(index);
            if(position<0){status.Text="该字段不在大字段预览列中。";return;}
            text.Text=DetailText(values[position]);
            status.Text="完整内容已加载。当前字段只读。";
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!cts.IsCancellationRequested)status.Text="完整内容加载失败，仍显示预览："+ex.Message;}
        finally{window.Closed-=Cancel;await reader.DisposeAsync();}
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
        bool hasTab=_vm.Tab is not null;
        ExecuteSqlButton.IsEnabled=hasTab&&!_vm.Busy;StopSqlButton.IsEnabled=_vm.Busy;
        FormatSqlButton.IsEnabled=hasTab&&!_vm.Busy;SaveScriptButton.IsEnabled=hasTab&&!_vm.Busy;ExportResultButton.IsEnabled=hasTab&&!_vm.Busy;
        EditorToggleButton.IsEnabled=hasTab;
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
