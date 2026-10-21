using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbPilot.Models;
using DbPilot.Services;
using DbPilot.ViewModels;
using DbPilot.Views;

namespace DbPilot.Checks;

internal static class ShortcutChecks
{
    public static void Run(MainWindow window, MainViewModel vm, Action<bool,string> check)
    {
        var applySettings=typeof(MainWindow).GetMethod("ApplySettings",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var lookupField=typeof(MainWindow).GetField("_shortcutLookup",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var lookup=(Dictionary<string,ShortcutAction>)lookupField.GetValue(window)!;
        var connectedField=typeof(MainViewModel).GetField("_connected",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var disconnectedField=typeof(MainViewModel).GetField("_disconnected",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var connected=(HashSet<Guid>)connectedField.GetValue(vm)!;
        var disconnected=(HashSet<Guid>)disconnectedField.GetValue(vm)!;
        var editor=window.FindControl<TextEditor>("SqlEditor")!;
        var tree=window.FindControl<TreeView>("ObjectTree")!;
        var grid=window.FindControl<DataGrid>("Results")!;
        var sidebar=window.FindControl<Border>("SidebarPanel")!;
        var editorPane=window.FindControl<Border>("EditorPane")!;
        var logPanel=window.FindControl<Expander>("TaskLogPanel")!;
        var where=window.FindControl<TextBox>("WhereFilter")!;
        var queryTabs=window.FindControl<ListBox>("QueryTabs")!;
        var originalShortcuts=new Dictionary<string,string?>(vm.Settings.Shortcuts);
        var originalTab=vm.Tab;
        var originalConnection=vm.Connection;
        var originalEdits=originalTab?.Edits;
        var originalResult=originalTab?.Result;
        var originalResultSets=originalTab?.ResultSets;
        var originalResultIndex=originalTab?.ResultIndex??0;
        var originalTableState=originalTab?.Table;
        var originalTableColumns=originalTab?.TableColumns;
        var originalPreviewColumns=originalTab?.PreviewColumns;
        var originalBrowseSql=originalTab?.BrowseSql;
        var originalPage=originalTab?.Page??0;
        var originalPageSize=originalTab?.PageSize??100;
        var originalFilter=originalTab?.Filter;
        var originalTabConnection=originalTab?.Connection;

        void Pump()=>Dispatcher.UIThread.RunJobs();
        bool WaitFor(Func<bool> predicate,int timeout=5000){
            var start=Environment.TickCount64;
            while(Environment.TickCount64-start<timeout){
                Pump();
                if(predicate())return true;
                Thread.Sleep(10);
            }
            Pump();
            return predicate();
        }
        void ClearLogs()=>vm.Logs.Clear();
        bool LogHas(string text)=>vm.Logs.Any(l=>l.Contains(text,StringComparison.OrdinalIgnoreCase));
        void Apply(){applySettings.Invoke(window,[]);Pump();}
        void Defaults(){vm.Settings.Shortcuts.Clear();foreach(var pair in originalShortcuts)vm.Settings.Shortcuts[pair.Key]=pair.Value;Apply();}
        KeyGesture Gesture(string id)=>ShortcutCatalog.EffectiveGesture(vm.Settings,id)??throw new InvalidOperationException("missing gesture "+id);
        void Press(KeyGesture gesture){window.KeyPress(gesture.Key,(RawInputModifiers)gesture.KeyModifiers,PhysicalKey.None,null);Pump();}
        void PressId(string id)=>Press(Gesture(id));
        Window? Owned()=>window.OwnedWindows.LastOrDefault(w=>w.IsVisible);
        Window? WaitDialog(string? titlePrefix=null,string? typeName=null){
            Window? found=null;
            WaitFor(()=>{
                var candidate=window.OwnedWindows.LastOrDefault(w=>w.IsVisible);
                if(candidate is null)return false;
                if(titlePrefix is not null&&!candidate.Title.StartsWith(titlePrefix,StringComparison.Ordinal))return false;
                if(typeName is not null&&candidate.GetType().Name!=typeName)return false;
                found=candidate;return true;
            });
            return found;
        }
        void CloseDialog(Window? dialog=null){
            dialog??=Owned();
            dialog?.Close();
            WaitFor(()=>Owned() is null,1500);
        }
        void ClickDialogButton(Window dialog,string content){
            var button=dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.Content?.ToString()==content)
                ??throw new InvalidOperationException("missing dialog button "+content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        bool Raise(KeyGesture gesture){
            var args=new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=gesture.Key,KeyModifiers=gesture.KeyModifiers};
            window.RaiseEvent(args);Pump();return args.Handled;
        }
        bool Focus(Control control){
            control.Focus();
            return WaitFor(()=>control.IsKeyboardFocusWithin,1000);
        }
        bool FocusTree(){
            window.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Pump();
            var item=tree.GetVisualDescendants().OfType<TreeViewItem>().FirstOrDefault(i=>ReferenceEquals(i.DataContext,tree.SelectedItem));
            (item as Control ?? tree).Focus();
            return WaitFor(()=>tree.IsKeyboardFocusWithin,1000);
        }

        // Fail fast if a previous check left a modal dialog open, since every shortcut below targets the main window.
        if(Owned() is {} leftover)CloseDialog(leftover);
        window.Activate();Pump();
        var routeProfile=new ConnectionProfile{Name="快捷键路由",Kind=DatabaseKind.MySQL,Database="sandbox"};
        vm.Connections.Add(routeProfile);vm.RebuildTree();Pump();
        tree.SelectedItem=vm.Nodes.FirstOrDefault(n=>n.Profile.Id==routeProfile.Id);Pump();

        // 1. Every default binding resolves through the window lookup to its catalog action.
        Defaults();
        var defaults=ShortcutCatalog.Actions.Select(a=>(Action:a,Gesture:ShortcutCatalog.EffectiveGesture(vm.Settings,a.Id))).Where(x=>x.Gesture is not null).ToArray();
        check(lookup.Count==defaults.Length,$"shortcut lookup contains every default binding ({defaults.Length})");
        foreach(var (action,gesture) in defaults)
            check(lookup.TryGetValue(ShortcutCatalog.Canonical(gesture!),out var mapped)&&mapped.Id==action.Id,$"lookup maps {action.Id} to {ShortcutCatalog.Describe(gesture)}");

        // 2. Route every default shortcut through the real window handler. The lookup is swapped for
        //    no-op probe actions so scope gating is verified without opening dialogs or touching a database.
        var realLookup=lookup.ToArray();
        var routeTab=vm.Tab;
        var routeTable=routeTab?.Table;
        var neutral=window.FindControl<Button>("NewQueryButton")!;
        check(Focus(editor),"SQL editor accepts focus for editor-scoped shortcuts");
        check(FocusTree(),"object tree accepts focus for tree-scoped shortcuts");
        check(Focus(grid),"result grid accepts focus for grid-scoped shortcuts");
        bool ReadyForScope(ShortcutScope scope){
            switch(scope){
                case ShortcutScope.Editor:return Focus(editor);
                case ShortcutScope.Tree:return FocusTree();
                case ShortcutScope.Grid:return Focus(grid);
                case ShortcutScope.Browse:
                    if(routeTab is null)return false;
                    routeTab.Table=new DbObject("sandbox","","items","表");vm.SelectTab(routeTab);Pump();return true;
                default:neutral.Focus();Pump();return true;
            }
        }
        bool BlockedForScope(ShortcutScope scope,KeyGesture gesture){
            switch(scope){
                case ShortcutScope.Editor:Focus(grid);break;
                case ShortcutScope.Tree:Focus(editor);break;
                case ShortcutScope.Grid:Focus(editor);break;
                case ShortcutScope.Browse:
                    if(routeTab is null)return true;
                    routeTab.Table=null;vm.SelectTab(routeTab);Pump();break;
                default:return true;
            }
            return !Raise(gesture);
        }
        lookup.Clear();
        foreach(var action in ShortcutCatalog.Actions)
            if(ShortcutCatalog.EffectiveGesture(vm.Settings,action.Id) is {} defaultGesture)
                lookup[ShortcutCatalog.Canonical(defaultGesture)]=action with{Id="__probe__:"+action.Id};
        check(lookup.Count==defaults.Length,"probe lookup mirrors every default binding");
        foreach(var (action,gesture) in defaults){
            if(ReadyForScope(action.Scope))check(Raise(gesture!),$"default shortcut routes: {action.Id} ({ShortcutCatalog.Describe(gesture)})");
            else check(false,$"default shortcut scope setup: {action.Id}");
            if(action.Scope!=ShortcutScope.Global)
                check(BlockedForScope(action.Scope,gesture!),$"default shortcut respects scope: {action.Id}");
        }
        // 2b. All actions, including those without a default, can be rebound to unique combinations.
        var customKeys=new List<Key>();
        for(int i=(int)Key.F1;i<=(int)Key.F24;i++)customKeys.Add((Key)i);
        for(int i=0;i<26;i++)customKeys.Add((Key)((int)Key.A+i));
        var custom=ShortcutCatalog.Actions.Select((action,index)=>(Action:action,Text:ShortcutCatalog.Canonical(customKeys[index],KeyModifiers.Control|KeyModifiers.Alt|KeyModifiers.Shift),Gesture:new KeyGesture(customKeys[index],KeyModifiers.Control|KeyModifiers.Alt|KeyModifiers.Shift))).ToArray();
        foreach(var entry in custom)vm.Settings.Shortcuts[entry.Action.Id]=entry.Text;
        Apply();
        check(lookup.Count==ShortcutCatalog.Actions.Count,"custom bindings register every catalog action");
        foreach(var entry in custom)
            check(lookup.TryGetValue(ShortcutCatalog.Canonical(entry.Gesture),out var mapped)&&mapped.Id==entry.Action.Id,$"custom binding routes: {entry.Action.Id}");
        lookup.Clear();
        foreach(var entry in custom)lookup[ShortcutCatalog.Canonical(entry.Gesture)]=entry.Action with{Id="__probe__:"+entry.Action.Id};
        foreach(var entry in custom){
            if(ReadyForScope(entry.Action.Scope))check(Raise(entry.Gesture),$"custom shortcut routes: {entry.Action.Id}");
            else check(false,$"custom shortcut scope setup: {entry.Action.Id}");
            if(entry.Action.Scope!=ShortcutScope.Global)
                check(BlockedForScope(entry.Action.Scope,entry.Gesture),$"custom shortcut respects scope: {entry.Action.Id}");
        }
        // 2c. Clearing a binding removes it from the lookup and the keyboard route.
        foreach(var action in ShortcutCatalog.Actions)vm.Settings.Shortcuts[action.Id]="";
        Apply();
        check(lookup.Count==0,"cleared bindings leave no keyboard route");
        foreach(var entry in custom)check(!Raise(entry.Gesture),$"cleared shortcut is inert: {entry.Action.Id}");
        Defaults();
        lookup.Clear();foreach(var pair in realLookup)lookup[pair.Key]=pair.Value;
        if(routeTab is not null){routeTab.Table=routeTable;vm.SelectTab(routeTab);}Pump();
        neutral.Focus();Pump();

        // 3. Real handlers. Dialog-only paths are opened and closed; no database connection is attempted.
        // Actions that intentionally ship without a default still need a temporary binding for the keyboard path.
        foreach(var (id,key) in new[]{("connection.backup",Key.F21),("connection.delete",Key.F22),("tools.update",Key.F23),("tools.about",Key.F24)})
            vm.Settings.Shortcuts[id]=ShortcutCatalog.Canonical(new KeyGesture(key,KeyModifiers.Control|KeyModifiers.Alt|KeyModifiers.Shift));
        Apply();
        foreach(var tab in vm.Tabs)tab.Edits?.Revert();
        var originalTabs=vm.Tabs.ToList();
        var addedProfiles=new List<ConnectionProfile>();
        var createdTabs=new List<QueryTab>();
        ConnectionProfile NewProfile(string name,bool withDatabase=true){
            var profile=new ConnectionProfile{Name=name,Kind=DatabaseKind.MySQL,Database=withDatabase?"sandbox":""};
            vm.Connections.Add(profile);addedProfiles.Add(profile);return profile;
        }
        QueryTab NewTab(string sql,ConnectionProfile? profile=null){
            vm.NewQuery(sql,profile);Pump();var tab=vm.Tab!;tab.SavedSql=tab.Sql;createdTabs.Add(tab);return tab;
        }

        vm.Connection=null;
        ClearLogs();PressId("connection.new");
        var connectionDialog=WaitDialog(typeName:"ConnectionDialog");
        check(connectionDialog is not null,"action connection.new opens the connection dialog");
        CloseDialog(connectionDialog);

        vm.Connection=null;ClearLogs();PressId("connection.open");
        check(WaitFor(()=>LogHas("请选择连接")),"action connection.open reaches connection handling without a profile");
        check(WaitFor(()=>!vm.Busy),"connection.open finishes without leaving a busy task");

        ClearLogs();PressId("connection.backup");
        var backupDialog=WaitDialog("加密配置备份");
        check(backupDialog is not null,"action connection.backup opens the encrypted backup dialog");
        CloseDialog(backupDialog);

        var scaffold=NewProfile("快捷键校验");
        vm.Connection=scaffold;Pump();
        ClearLogs();PressId("database.create");
        var databaseDialog=WaitDialog("新建数据库");
        check(databaseDialog is not null,"action database.create opens the create-database dialog");
        CloseDialog(databaseDialog);

        ClearLogs();PressId("table.create");
        var designerDialog=WaitDialog(typeName:"ObjectDesignerWindow");
        check(designerDialog is not null,"action table.create opens the object designer");
        CloseDialog(designerDialog);

        var closing=NewProfile("快捷键关闭");
        connected.Add(closing.Id);disconnected.Remove(closing.Id);vm.Connection=closing;Pump();
        ClearLogs();PressId("connection.close");
        check(WaitFor(()=>LogHas("已断开")),"action connection.close disconnects the selected profile");
        disconnected.Remove(closing.Id);

        var treeProfile=NewProfile("快捷键连接树");
        vm.RebuildTree();Pump();
        var treeNode=vm.Nodes.First(n=>n.Profile.Id==treeProfile.Id);
        tree.SelectedItem=treeNode;
        check(FocusTree(),"object tree can receive keyboard focus for scoped shortcuts");
        ClearLogs();PressId("connection.edit");
        var editDialog=WaitDialog(typeName:"ConnectionDialog");
        check(editDialog is not null,"action connection.edit opens the connection dialog from the tree");
        CloseDialog(editDialog);

        int beforeCopy=vm.Connections.Count;
        FocusTree();ClearLogs();PressId("connection.copy");
        check(WaitFor(()=>vm.Connections.Count>beforeCopy),"action connection.copy saves a duplicate profile");
        var copied=vm.Connections.LastOrDefault(p=>p.Name.EndsWith(" 副本",StringComparison.Ordinal));
        check(copied is not null,"copied profile keeps the copy suffix");
        if(copied is not null)addedProfiles.Add(copied);

        tree.SelectedItem=vm.Nodes.FirstOrDefault(n=>n.Profile.Id==treeProfile.Id);Pump();
        check(FocusTree(),"tree regains focus after saving a copy");
        ClearLogs();PressId("connection.delete");
        var deleteDialog=WaitDialog("确认操作");
        check(deleteDialog is not null,"action connection.delete asks for confirmation");
        CloseDialog(deleteDialog);

        var workTab=NewTab("select 1 from t");
        workTab.Connection=null;vm.Connection=null;

        int beforeNew=vm.Tabs.Count;PressId("query.new");
        check(vm.Tabs.Count==beforeNew+1,"action query.new opens a query tab");
        if(vm.Tabs.Count>beforeNew)createdTabs.Add(vm.Tab!);

        vm.SelectTab(workTab);queryTabs.SelectedItem=workTab;Pump();
        editor.Text="select 1 from t";
        PressId("query.format");
        check(editor.Text.Contains("SELECT")&&editor.Text.Contains("FROM"),"action query.format reformats SQL in the editor");

        ClearLogs();PressId("query.execute");
        check(WaitFor(()=>LogHas("请先在上方选择连接")),"action query.execute reaches execution without a connection");

        editor.Select(0,6);Focus(editor);ClearLogs();PressId("query.executeSelection");
        check(WaitFor(()=>LogHas("请先在上方选择连接")),"action query.executeSelection executes only the selected SQL");

        editor.Text="SEL";editor.CaretOffset=3;Focus(editor);ClearLogs();PressId("query.complete");
        var completionField=typeof(MainWindow).GetField("_completion",BindingFlags.Instance|BindingFlags.NonPublic)!;
        check(WaitFor(()=>completionField.GetValue(window) is not null),"action query.complete opens the completion list");
        typeof(MainWindow).GetMethod("CancelCompletion",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[]);
        Pump();

        ClearLogs();PressId("query.history");
        var historyDialog=WaitDialog("查询历史");
        check(historyDialog is not null,"action query.history opens the history dialog");
        CloseDialog(historyDialog);

        ClearLogs();PressId("query.templates");
        var templatesDialog=WaitDialog("SQL 模板");
        check(templatesDialog is not null,"action query.templates opens the template dialog");
        CloseDialog(templatesDialog);

        ClearLogs();PressId("query.explain");
        check(WaitFor(()=>LogHas("请选择连接")),"action query.explain dispatches the plan request");

        workTab.Result=null;ClearLogs();PressId("query.export");
        check(WaitFor(()=>LogHas("当前没有可导出的查询结果")),"action query.export reports a missing result instead of failing");

        bool sidebarVisible=sidebar.IsVisible;PressId("view.sidebar");
        check(sidebar.IsVisible!=sidebarVisible,"action view.sidebar toggles the sidebar");PressId("view.sidebar");
        check(sidebar.IsVisible==sidebarVisible,"action view.sidebar toggles back");

        Focus(editor);bool editorVisible=editorPane.IsVisible;PressId("view.editor");
        check(editorPane.IsVisible!=editorVisible,"action view.editor collapses the SQL editor");PressId("view.editor");
        check(editorPane.IsVisible==editorVisible,"action view.editor expands the SQL editor again");

        bool logsExpanded=logPanel.IsExpanded;PressId("view.logs");
        check(logPanel.IsExpanded!=logsExpanded,"action view.logs toggles the log panel");PressId("view.logs");
        check(logPanel.IsExpanded==logsExpanded,"action view.logs toggles back");

        PressId("view.focus");
        check(!sidebar.IsVisible&&!editorPane.IsVisible,"action view.focus enters focus mode");
        PressId("view.focus");
        check(sidebar.IsVisible&&editorPane.IsVisible,"action view.focus restores the workspace");

        bool dark=vm.Settings.Dark;PressId("view.theme");
        check(vm.Settings.Dark!=dark,"action view.theme switches the theme");PressId("view.theme");
        check(vm.Settings.Dark==dark,"action view.theme switches back");

        vm.Connection=null;ClearLogs();PressId("view.catalog");
        check(WaitFor(()=>!vm.Busy)&&!LogHas("快捷键执行失败"),"action view.catalog is routed without a context");
        ClearLogs();PressId("view.refresh");
        check(WaitFor(()=>!vm.Busy)&&!LogHas("快捷键执行失败"),"action view.refresh is routed without a context");

        var tabA=NewTab("SELECT 1");var tabB=NewTab("SELECT 2");
        queryTabs.SelectedItem=tabA;vm.Tab=tabA;Pump();
        PressId("tabs.next");
        check(!ReferenceEquals(vm.Tab,tabA),"action tabs.next selects the next query tab");
        PressId("tabs.previous");
        check(ReferenceEquals(vm.Tab,tabA),"action tabs.previous selects the previous query tab");

        var browse=NewTab("SELECT * FROM items");
        browse.Table=new DbObject("sandbox","","items","表");
        browse.Connection=scaffold;
        vm.SelectTab(browse);Pump();
        PressId("data.filter");
        check(where.IsKeyboardFocusWithin,"action data.filter focuses the WHERE filter");

        var busyField=typeof(MainViewModel).GetField("_busy",BindingFlags.Instance|BindingFlags.NonPublic)!;
        busyField.SetValue(vm,true);ClearLogs();
        PressId("data.previousPage");
        check(WaitFor(()=>LogHas("已有任务运行")),"action data.previousPage reaches page navigation");
        ClearLogs();PressId("data.nextPage");
        check(WaitFor(()=>LogHas("已有任务运行")),"action data.nextPage reaches page navigation");
        busyField.SetValue(vm,false);Pump();
        browse.Table=null;

        var result=new QueryResult(["id","name"],[new object?[]{1,"alpha"},new object?[]{2,"beta"}],0,false,[typeof(int),typeof(string)]);
        var editTab=NewTab("SELECT id, name FROM items");
        editTab.Result=result;
        editTab.Edits=new TableEditSession(result,[new("id","int",false,true,"","",IsGenerated:true),new("name","text",true,false,"","")]);
        vm.SelectTab(editTab);Pump();
        var rows=(ResultRows)grid.ItemsSource!;
        grid.SelectedItem=rows[0];grid.CurrentColumn=grid.Columns[1];
        check(Focus(grid),"result grid can receive keyboard focus for scoped shortcuts");

        PressId("data.viewCell");
        var cellDialog=WaitDialog("字段详情");
        check(cellDialog is not null,"action data.viewCell opens the field detail dialog");
        CloseDialog(cellDialog);

        Focus(grid);grid.SelectedItem=rows[0];
        var clipboard=window.Clipboard;
        if(clipboard is not null){
            PressId("data.copyRow");
            var copiedText=WaitFor(()=>clipboard.GetTextAsync().GetAwaiter().GetResult() is {Length:>0},2000)?clipboard.GetTextAsync().GetAwaiter().GetResult():null;
            check(copiedText is not null&&copiedText.Contains("alpha",StringComparison.Ordinal),"action data.copyRow copies the selected row");
        }

        Focus(grid);grid.SelectedItem=rows[0];
        int rowCount=editTab.Edits!.Data.Rows.Count;
        PressId("data.addRow");
        check(editTab.Edits.Data.Rows.Count==rowCount+1,"action data.addRow stages a new row");
        check(editTab.Edits.Data.Rows.Cast<System.Data.DataRow>().Last().RowState==System.Data.DataRowState.Added,"new row is staged as Added");

        grid.SelectedItem=rows[1];
        Focus(grid);ClearLogs();PressId("data.deleteRow");
        var rowDeleteDialog=WaitDialog("确认操作");
        check(rowDeleteDialog is not null,"action data.deleteRow asks before marking a row deleted");
        if(rowDeleteDialog is not null){ClickDialogButton(rowDeleteDialog,"标记删除");CloseDialog(rowDeleteDialog);}
        check(WaitFor(()=>editTab.Edits.Data.Rows.Cast<System.Data.DataRow>().Any(r=>r.RowState==System.Data.DataRowState.Deleted)),"delete shortcut marks the row for deletion");

        grid.SelectedItem=rows[0];grid.CurrentColumn=grid.Columns[1];Focus(grid);
        PressId("data.setNull");
        check(rows[0].Row[1] is DBNull,"action data.setNull clears the selected cell to NULL");

        ClearLogs();PressId("data.commit");
        var commitDialog=WaitDialog("确认操作");
        check(commitDialog is not null,"action data.commit asks before committing staged edits");
        CloseDialog(commitDialog);

        editTab.Edits.SetNull(rows[0].Row,1);
        PressId("data.revert");
        check(editTab.Edits.PendingCount==0,"action data.revert discards staged edits");

        editTab.Result=result;vm.SelectTab(editTab);Pump();
        PressId("tools.chart");
        var chartDialog=WaitDialog(typeName:"VisualizationWindow");
        check(chartDialog is not null,"action tools.chart opens the chart window");
        CloseDialog(chartDialog);

        PressId("tools.database");
        var toolsDialog=WaitDialog(typeName:"DatabaseToolsWindow");
        check(toolsDialog is not null,"action tools.database opens the database tools window");
        CloseDialog(toolsDialog);

        PressId("tools.settings");
        var settingsDialog=WaitDialog(typeName:"SettingsWindow");
        check(settingsDialog is not null,"action tools.settings opens the settings window");
        CloseDialog(settingsDialog);

        ClearLogs();PressId("tools.update");
        check(WaitFor(()=>!LogHas("快捷键执行失败")&&vm.Logs.Count>0),"action tools.update reaches the update service");

        PressId("tools.about");
        var aboutDialog=WaitDialog("关于 DbPilot");
        check(aboutDialog is not null,"action tools.about opens the about dialog");
        CloseDialog(aboutDialog);

        ClearLogs();vm.Connection=null;CheckFileShortcut("query.open");
        ClearLogs();CheckFileShortcut("query.save");

        var closeTab=NewTab("SELECT 3");closeTab.Connection=null;closeTab.Edits=null;
        vm.SelectTab(closeTab);queryTabs.SelectedItem=closeTab;Pump();
        int beforeClose=vm.Tabs.Count;PressId("query.close");
        check(vm.Tabs.Count==beforeClose-1,"action query.close closes the current query tab");

        ClearLogs();PressId("query.stop");
        check(!LogHas("快捷键执行失败"),"action query.stop is routed without failing");

        void CheckFileShortcut(string id){
            PressId(id);
            WaitFor(()=>!vm.Busy,3000);
            check(!LogHas("快捷键执行失败"),$"action {id} is routed through the storage picker");
        }

        // 4. macOS native menu accelerators must mirror the catalog; scoped actions stay on the window router.
        if(OperatingSystem.IsMacOS()){
            typeof(App).GetMethod("RefreshMacMenu",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[window]);Pump();
            var items=(Dictionary<string,NativeMenuItem>)typeof(App).GetField("_macActionItems",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            check(items.Count>=30,"macOS window menu exposes the shortcut actions");
            foreach(var (actionId,item) in items){
                var action=ShortcutCatalog.Find(actionId)??throw new InvalidOperationException("unknown menu action "+actionId);
                var expected=action.Scope==ShortcutScope.Global?ShortcutCatalog.EffectiveGesture(vm.Settings,actionId):null;
                check(SameGesture(item.Gesture,expected),$"macOS menu gesture matches catalog: {actionId}");
            }
            var settingsItem=(NativeMenuItem?)typeof(App).GetField("_macSettingsItem",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null);
            check(SameGesture(settingsItem?.Gesture,ShortcutCatalog.EffectiveGesture(vm.Settings,"tools.settings")),"macOS settings menu gesture matches catalog");
        }

        // Cleanup so the remaining checks and window close see the original workspace.
        if(Owned() is {} remaining)CloseDialog(remaining);
        typeof(MainWindow).GetMethod("CancelCompletion",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[]);
        editTab.Edits.Revert();busyField.SetValue(vm,false);
        foreach(var tab in createdTabs)if(tab.Edits is not null)tab.Edits.Revert();
        foreach(var tab in createdTabs)vm.Tabs.Remove(tab);
        vm.Connections.Remove(routeProfile);
        foreach(var profile in addedProfiles)vm.Connections.Remove(profile);
        vm.RebuildTree();Pump();
        if(originalTab is not null&&vm.Tabs.Contains(originalTab)){
            originalTab.Connection=originalTabConnection;
            originalTab.Table=originalTableState;
            originalTab.TableColumns=originalTableColumns;
            originalTab.PreviewColumns=originalPreviewColumns;
            originalTab.Result=originalResult;
            originalTab.ResultSets=originalResultSets??[];
            originalTab.ResultIndex=originalResultIndex;
            originalTab.Edits=originalEdits;
            originalTab.BrowseSql=originalBrowseSql??"";
            originalTab.Page=originalPage;
            originalTab.PageSize=originalPageSize;
            originalTab.Filter=originalFilter??"";
            vm.Tab=originalTab;queryTabs.SelectedItem=originalTab;Pump();
            vm.SelectTab(originalTab);Pump();
        }
        vm.Connection=originalConnection;
        vm.Settings.Shortcuts.Clear();foreach(var pair in originalShortcuts)vm.Settings.Shortcuts[pair.Key]=pair.Value;
        Apply();
    }

    private static bool SameGesture(KeyGesture? left,KeyGesture? right)
    {
        if(left is null||right is null)return left is null&&right is null;
        return ShortcutCatalog.Canonical(left)==ShortcutCatalog.Canonical(right);
    }
}
