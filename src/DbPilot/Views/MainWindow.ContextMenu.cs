using Avalonia.Controls;
using Avalonia.Interactivity;
using DbPilot.Models;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private bool TryUseTreeContext(ObjectNode node,bool navigate=true)
    {
        if(_vm.Busy){_vm.Log("请先等待当前任务完成。");return false;}
        if(!FlushGridEdits())return false;
        if(!navigate){_vm.Connection=node.Profile;return true;}
        var current=_vm.CurrentProfile;
        bool different=current?.Id!=node.Profile.Id || node.Database is not null&&current.Database!=node.Database;
        if(different){
            // Navigation gets a separate query; existing SQL, results and staged edits stay in their tab.
            _vm.NewQuery(profile:DbPilot.ViewModels.MainViewModel.CloneForDatabase(node.Profile,node.Database));
            _vm.Connection=node.Profile;
        }
        return true;
    }
    private MenuItem BuildReloadMenu(ObjectNode node,bool enabled)
    {
        var objects=new MenuItem{Header="重新加载对象",IsEnabled=enabled};
        objects.Click+=async(_,_)=>await RefreshTreeNodeAsync(node);
        var databases=new MenuItem{Header="重新加载数据库列表",IsEnabled=enabled};
        databases.Click+=async(_,_)=>{
            _databaseLists.Remove(node.Profile.Id);
            if(_vm.Connection?.Id==node.Profile.Id)await RefreshDatabasePicker(true);
            _vm.Log("数据库列表已重新加载。");
        };
        return new MenuItem{Header="重载",IsEnabled=enabled,ItemsSource=new Control[]{objects,databases}};
    }
    private MenuItem BuildCopyTableMenu(ObjectNode node,bool enabled)
    {
        var children=new List<Control>();
        foreach(var (label,withData) in new[]{("仅结构",false),("结构和数据",true)}){
            var item=new MenuItem{Header=label,IsEnabled=enabled};
            item.Click+=async(_,_)=>await CopyTableAsync(node,withData);
            children.Add(item);
        }
        return new MenuItem{Header="复制表",IsEnabled=enabled,ItemsSource=children};
    }
    private void BuildTreeContextMenu(ObjectNode node)
    {
        var items=new List<Control>();
        void Add(string text,EventHandler<RoutedEventArgs> action,bool enabled=true,bool switchContext=false){
            var item=new MenuItem{Header=text,IsEnabled=enabled};
            item.Click+=(sender,e)=>{
                if(switchContext){if(!TryUseTreeContext(node))return;}
                else if(!TryBeginTreeAction())return;
                action(sender,e);
            };
            items.Add(item);
        }
        void AddTask(string text,Func<Task> action,bool enabled=true){
            var item=new MenuItem{Header=text,IsEnabled=enabled};
            item.Click+=async(_,_)=>{if(TryBeginTreeAction())await action();};
            items.Add(item);
        }
        void Separator()=>items.Add(new Separator());
        bool idle=!_vm.Busy,open=_vm.IsOpen(node.Profile);
        bool databaseReady=!string.IsNullOrWhiteSpace(node.Database??node.Profile.Database);
        bool dumpable=idle&&open&&databaseReady&&node.Profile.Kind!=DatabaseKind.TDengine;
        if(node.IsConnection){
            Add(open?"关闭连接":"打开连接",open?DisconnectDatabase:ConnectDatabase,idle,true);
            Add("编辑连接",EditConnection,idle,true);
            Add("新建连接",NewConnection,idle);
            Add("删除连接",DeleteConnection,idle,true);
            Add("复制连接",CopyConnection,idle,true);
            Separator();
            Add("新建数据库",CreateDatabase,idle&&open,true);
            Add("新建查询",(_,_)=>_vm.NewQuery(profile:MenuProfile(node)),idle&&open);
            AddTask("命令列界面",()=>OpenCommandLineAsync(MenuProfile(node)),idle&&open);
            AddTask("运行 SQL 文件",()=>RunSqlFileAsync(node),idle&&open&&databaseReady);
            Separator();
            items.Add(BuildReloadMenu(node,idle&&open));
            AddTask(node.Profile.Favorite?"取消星标":"添加星标",()=>ToggleFavoriteAsync(node),idle);
            items.Add(BuildColorMenu(node));
            items.Add(BuildGroupMenu(node));
            AddTask("连接信息",()=>ShowConnectionInfoAsync(node));
            AddTask("刷新",()=>RefreshTreeNodeAsync(node),idle&&open);
        }else if(node.IsDatabase){
            AddTask("关闭数据库",()=>CloseDatabaseAsync(node),idle&&open);
            AddTask("编辑数据库",()=>EditDatabaseAsync(node),idle&&open);
            Add("新建数据库",CreateDatabase,idle&&open,true);
            AddTask("删除数据库",()=>DropDatabaseAsync(node),idle&&open);
            Add("新建查询",(_,_)=>_vm.NewQuery(profile:MenuProfile(node)),idle&&open);
            AddTask("命令列界面",()=>OpenCommandLineAsync(MenuProfile(node)),idle&&open);
            AddTask("运行 SQL 文件",()=>RunSqlFileAsync(node),idle&&open);
            items.Add(BuildDumpMenu(node,dumpable));
            Separator();
            Add("打开 / 加载对象",OpenNodeMenu,idle&&open);
            Add("查看表列表",async(_,_)=>await ShowTableCatalog(),idle&&open,true);
            AddTask("新建表",()=>CreateTableForContextAsync(node),idle&&open);
            AddTask("导入向导",()=>OpenDatabaseToolsTab(6,node),idle&&open);
            AddTask("导出向导",()=>OpenDatabaseToolsTab(5,node),idle&&open);
            Add("复制数据库名称",async(_,_)=>{if(Clipboard is not null&&node.Database is {} name)await Clipboard.SetTextAsync(name);});
            AddTask("刷新",()=>RefreshTreeNodeAsync(node),idle&&open);
        }else if(node.IsCategory){
            if(node.Label.Contains("表",StringComparison.OrdinalIgnoreCase)){
                AddTask("新建表",()=>CreateTableForContextAsync(node),idle&&open);
                AddTask("导入向导",()=>OpenDatabaseToolsTab(6,node),idle&&open);
                AddTask("导出向导",()=>OpenDatabaseToolsTab(5,node),idle&&open);
                Separator();
            }
            Add("新建查询",(_,_)=>_vm.NewQuery(profile:MenuProfile(node)),idle&&open);
            AddTask("刷新",()=>RefreshTreeNodeAsync(node),idle&&open);
        }else if(node.Object is {} obj){
            bool table=IsTableObject(obj);
            bool browsable=table||obj.Kind=="视图";
            Add(browsable?"打开数据":"打开对象",OpenNodeMenu,idle&&open);
            if(browsable){
                if(table)AddTask("设计表",()=>DesignTableAsync(node),idle&&open);
                Add("查看表结构与建表语句",ViewStructure,idle&&open);
                Separator();
                if(table)AddTask("新建表",()=>CreateTableForContextAsync(node),idle&&open);
                AddTask("删除表",()=>DropTableAsync(MenuProfile(node),obj),idle&&open);
                if(table){
                    AddTask("清空表",()=>ClearTableAsync(node,false),idle&&open);
                    AddTask("截断表",()=>ClearTableAsync(node,true),idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine);
                    items.Add(BuildCopyTableMenu(node,idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine));
                    AddTask("设置权限",()=>SetPermissionsAsync(node),idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine);
                }
                Separator();
                AddTask("导入向导",()=>OpenDatabaseToolsTab(6,node),idle&&open&&table);
                AddTask("导出向导",()=>OpenDatabaseToolsTab(5,node),idle&&open);
                if(table){
                    items.Add(BuildDumpMenu(node,dumpable));
                    if(BuildMaintenanceMenu(node,obj,idle&&open) is {} maintenance)items.Add(maintenance);
                }
                Separator();
                Add("生成 SELECT",GenerateSelect,idle&&open);
                if(table){
                    Add("生成 INSERT 模板",GenerateInsert,idle&&open);
                    Add("生成 CREATE TABLE 模板",GenerateCreate,idle&&open);
                    Add("生成 CREATE INDEX 模板",GenerateIndex,idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine);
                    AddTask("重命名表",()=>RenameTableAsync(MenuProfile(node),obj),idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine);
                }
            }
            Separator();
            Add("复制对象名称",CopyObjectName);
            AddTask("刷新",()=>RefreshTreeNodeAsync(node),idle&&open);
        }else Add("加载",OpenNodeMenu,idle&&open);
        ObjectTree.ContextMenu!.ItemsSource=items;
    }
}
