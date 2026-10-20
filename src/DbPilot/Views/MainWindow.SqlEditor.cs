using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbPilot.Services;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private (int Start,int Length)? _sqlMenuSelection;
    private void InitializeSqlContextMenu()
    {
        SqlEditor.AddHandler(PointerPressedEvent,(_,e)=>{
            if(e.GetCurrentPoint(SqlEditor).Properties.IsRightButtonPressed)
                _sqlMenuSelection=(SqlEditor.SelectionStart,SqlEditor.SelectionLength);
        },RoutingStrategies.Tunnel,handledEventsToo:true);
        SqlEditor.AddHandler(ContextRequestedEvent,(sender,e)=>{
            if(e.TryGetPosition(SqlEditor,out _)&&_sqlMenuSelection is {} selected)
                SqlEditor.Select(selected.Start,selected.Length);
            _sqlMenuSelection=null;
            e.Handled=true;
            UpdateSqlContextMenu();
            SqlEditor.ContextMenu!.Open(SqlEditor);
        },RoutingStrategies.Tunnel,handledEventsToo:true);
        SqlEditor.ContextMenu!.Opening+=(_,_)=>UpdateSqlContextMenu();
    }
    private void UpdateSqlContextMenu()
    {
            bool selected=!string.IsNullOrWhiteSpace(SqlEditor.SelectedText);
            var sql=selected?SqlEditor.SelectedText:SqlEditor.Text;
            ExecuteEditorSqlMenu.Header=selected?"执行选中 SQL":"执行 SQL";
            FormatEditorSqlMenu.Header=selected?"格式化选中 SQL":"格式化 SQL";
            ExecuteEditorSqlMenu.IsEnabled=!_vm.Busy&&!string.IsNullOrWhiteSpace(sql)&&_vm.CurrentProfile is {} p&&!_vm.IsClosed(p);
            FormatEditorSqlMenu.IsEnabled=!string.IsNullOrWhiteSpace(sql);
    }
    private void ReportSqlProblem(string message)
    {
        _vm.Log(message,"Warn");TaskLogPanel.IsVisible=true;TaskLogPanel.IsExpanded=true;
    }
    private void FormatSql(object? sender,RoutedEventArgs e)
    {
        bool selected=SqlEditor.SelectionLength>0;
        int start=selected?SqlEditor.SelectionStart:0;
        int length=selected?SqlEditor.SelectionLength:SqlEditor.Document.TextLength;
        var formatted=SqlTools.Format(SqlEditor.Document.GetText(start,length));
        SqlEditor.Document.Replace(start,length,formatted);
        if(selected)SqlEditor.Select(start,formatted.Length);
    }
}
