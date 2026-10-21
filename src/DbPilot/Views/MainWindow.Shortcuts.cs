using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbPilot.Models;
using DbPilot.Services;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private readonly Dictionary<string,ShortcutAction> _shortcutLookup=new();
    private readonly HashSet<string> _reportedShortcutConflicts=[];

    internal KeyGesture? ShortcutGesture(string actionId)=>ShortcutCatalog.EffectiveGesture(_vm.Settings,actionId);

    // Native menu accelerators fire regardless of focus, so scoped actions stay on the window router.
    internal KeyGesture? NativeShortcutGesture(string actionId)=>
        ShortcutCatalog.Find(actionId)?.Scope==ShortcutScope.Global?ShortcutGesture(actionId):null;

    internal void RefreshShortcuts()
    {
        _shortcutLookup.Clear();
        foreach(var (action,gesture) in ShortcutCatalog.ResolveAll(_vm.Settings))
        {
            var key=ShortcutCatalog.Canonical(gesture);
            if(_shortcutLookup.TryGetValue(key,out var existing))
            {
                if(_reportedShortcutConflicts.Add(key))
                    _vm.Log($"快捷键冲突：{ShortcutCatalog.Describe(gesture)} 同时用于「{existing.Title}」和「{action.Title}」，已保留前者。","Warn");
                continue;
            }
            _shortcutLookup[key]=action;
        }
    }

    private void ApplyShortcutLabels()
    {
        SetGesture(FileNewConnection,"connection.new");SetGesture(FileOpenConnection,"connection.open");SetGesture(FileCloseConnection,"connection.close");
        SetGesture(FileEditConnection,"connection.edit");SetGesture(FileCopyConnection,"connection.copy");SetGesture(FileDeleteConnection,"connection.delete");
        SetGesture(FileNewQuery,"query.new");SetGesture(FileOpenScript,"query.open");SetGesture(FileSaveScript,"query.save");
        SetGesture(FileHistory,"query.history");SetGesture(FileBackup,"connection.backup");
        SetGesture(QueryRunMenuItem,"query.execute");SetGesture(QueryRunSelectionMenuItem,"query.executeSelection");SetGesture(QueryStopMenuItem,"query.stop");
        SetGesture(QueryFormatMenuItem,"query.format");SetGesture(QueryCompletionMenuItem,"query.complete");SetGesture(QueryExportMenuItem,"query.export");
        SetGesture(ViewSidebarMenuItem,"view.sidebar");SetGesture(ViewEditorMenuItem,"view.editor");SetGesture(ViewLogsMenuItem,"view.logs");
        SetGesture(ViewFocusMenuItem,"view.focus");SetGesture(ViewThemeMenuItem,"view.theme");
        SetGesture(ToolsCreateDatabase,"database.create");SetGesture(ToolsCreateTable,"table.create");SetGesture(ToolsTemplates,"query.templates");
        SetGesture(ToolsExplain,"query.explain");SetGesture(ToolsChart,"tools.chart");SetGesture(ToolsDatabase,"tools.database");SetGesture(ToolsSettings,"tools.settings");
        SetGesture(HelpUpdate,"tools.update");SetGesture(HelpAbout,"tools.about");
        SetGesture(ExecuteEditorSqlMenu,"query.execute");SetGesture(ExecuteSelectionEditorSqlMenu,"query.executeSelection");SetGesture(FormatEditorSqlMenu,"query.format");

        ToolTip.SetTip(NewQueryButton,ShortcutTip("新建查询","query.new"));
        ToolTip.SetTip(ShowTablesButton,ShortcutTip("浏览当前数据库的表","view.catalog"));
        ToolTip.SetTip(ExecuteSqlButton,ShortcutTip("执行编辑器中的全部 SQL","query.execute"));
        ToolTip.SetTip(StopSqlButton,ShortcutTip("终止当前任务","query.stop"));
        ToolTip.SetTip(FormatSqlButton,ShortcutTip("格式化 SQL","query.format"));
        ToolTip.SetTip(SaveScriptButton,ShortcutTip("保存 SQL","query.save"));
        ToolTip.SetTip(ExportResultButton,ShortcutTip("导出查询结果","query.export"));
        ToolTip.SetTip(SidebarToggleButton,ShortcutTip("显示或隐藏连接侧栏","view.sidebar"));
        ToolTip.SetTip(EditorToggleButton,ShortcutTip("展开或收起 SQL 编辑器","view.editor"));
        ToolTip.SetTip(FocusModeButton,ShortcutTip("隐藏侧栏、SQL 编辑器与日志，将空间留给表数据","view.focus"));
    }

    private void SetGesture(MenuItem item,string actionId)=>item.InputGesture=ShortcutGesture(actionId);
    private string ShortcutTip(string title,string actionId)
    {
        var text=ShortcutCatalog.Describe(ShortcutCatalog.EffectiveText(_vm.Settings,actionId));
        return text=="未设置"?title:$"{title}（{text}）";
    }

    private async void WorkspaceKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.None||IsModifierKey(e.Key))return;
        if(!_shortcutLookup.TryGetValue(ShortcutCatalog.Canonical(e.Key,e.KeyModifiers),out var action))return;
        if(!ShortcutApplies(action.Scope))return;
        e.Handled=true;
        try{await RunShortcutAsync(action.Id);}
        catch(Exception ex){_vm.Log("快捷键执行失败："+ex.Message,"Error");}
    }

    private bool ShortcutApplies(ShortcutScope scope)=>scope switch
    {
        ShortcutScope.Editor=>SqlEditor.IsKeyboardFocusWithin,
        ShortcutScope.Tree=>ObjectTree.IsKeyboardFocusWithin,
        ShortcutScope.Grid=>Results.IsKeyboardFocusWithin,
        ShortcutScope.Browse=>_vm.Tab?.Table is not null,
        _=>true
    };

    private static bool IsModifierKey(Key key)=>key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private async Task RunShortcutAsync(string id)
    {
        var args=new RoutedEventArgs();
        switch(id)
        {
            case "connection.new":NewConnection(this,args);break;
            case "connection.open":ConnectDatabase(this,args);break;
            case "connection.close":DisconnectDatabase(this,args);break;
            case "connection.edit":SelectTreeConnection();EditConnection(this,args);break;
            case "connection.copy":SelectTreeConnection();CopyConnection(this,args);break;
            case "connection.delete":SelectTreeConnection();DeleteConnection(this,args);break;
            case "database.create":CreateDatabase(this,args);break;
            case "table.create":CreateTable(this,args);break;
            case "connection.backup":WorkspaceBackup(this,args);break;
            case "query.new":NewQuery(this,args);break;
            case "query.open":OpenScript(this,args);break;
            case "query.save":SaveScript(this,args);break;
            case "query.close":CloseTab(this,args);break;
            case "query.execute":await Run();break;
            case "query.executeSelection":await Run(selectionOnly:true);break;
            case "query.stop":StopSql(this,args);break;
            case "query.format":FormatSql(this,args);break;
            case "query.complete":await ShowCompletionFromShortcut();break;
            case "query.history":ShowHistory(this,args);break;
            case "query.templates":ShowTemplates(this,args);break;
            case "query.explain":ExplainSql(this,args);break;
            case "query.export":ExportResult(this,args);break;
            case "view.sidebar":ToggleSidebar(this,args);break;
            case "view.editor":ToggleEditor(this,args);break;
            case "view.logs":ToggleLogs(this,args);break;
            case "view.focus":ToggleFocusMode(this,args);break;
            case "view.theme":ToggleTheme(this,args);break;
            case "view.catalog":ShowCatalog(this,args);break;
            case "view.refresh":await RefreshCurrent();break;
            case "tabs.next":SelectRelativeTab(1);break;
            case "tabs.previous":SelectRelativeTab(-1);break;
            case "data.filter":FocusWhereFilter();break;
            case "data.previousPage":PreviousPage(this,args);break;
            case "data.nextPage":NextPage(this,args);break;
            case "data.viewCell":ViewCell(this,args);break;
            case "data.copyRow":CopyRow(this,args);break;
            case "data.addRow":AddDataRow(this,args);break;
            case "data.deleteRow":DeleteDataRow(this,args);break;
            case "data.setNull":SetCellNull(this,args);break;
            case "data.commit":CommitDataEdits(this,args);break;
            case "data.revert":RevertDataEdits(this,args);break;
            case "tools.chart":OpenChart(this,args);break;
            case "tools.database":OpenDatabaseTools(this,args);break;
            case "tools.settings":ShowSettings(this,args);break;
            case "tools.update":CheckForUpdates(this,args);break;
            case "tools.about":await App.ShowAbout(this);break;
        }
    }

    private void SelectTreeConnection()
    {
        if(ObjectTree.SelectedItem is ObjectNode node)_vm.Connection=node.Profile;
    }

    private async void ShowCompletionMenu(object? sender,RoutedEventArgs e)=>await ShowCompletionFromShortcut();
    private async Task ShowCompletionFromShortcut()
    {
        if(!_vm.HasTab)return;
        if(!EditorPane.IsVisible)SetEditorVisible(true);
        SqlEditor.Focus();
        await ShowCompletion();
    }

    private void SelectRelativeTab(int delta)
    {
        if(_vm.Tabs.Count<2||_vm.Tab is not {} current)return;
        int index=_vm.Tabs.IndexOf(current);
        _vm.Tab=_vm.Tabs[(index+delta+_vm.Tabs.Count)%_vm.Tabs.Count];
    }

    private async Task RefreshCurrent()
    {
        if(TableCatalogPanel.IsVisible){await ShowTableCatalog();return;}
        if(_vm.Tab?.Table is not null){await Browse(_vm.Tab.Page);return;}
        if(_vm.Connection is not null){_vm.RebuildTree();await RefreshDatabasePicker(true);}
    }

    private void FocusWhereFilter()
    {
        if(_vm.Tab?.Table is null)return;
        WhereFilter.Focus();WhereFilter.SelectAll();
    }
}
