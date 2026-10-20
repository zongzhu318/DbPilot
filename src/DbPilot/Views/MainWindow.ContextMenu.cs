using Avalonia.Controls;
using Avalonia.Interactivity;
using DbPilot.Models;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private bool TryUseTreeContext(ObjectNode node)
    {
        if(_vm.Busy){_vm.Log("请先等待当前任务完成。");return false;}
        if(!FlushGridEdits())return false;
        var current=_vm.CurrentProfile;
        bool different=current?.Id!=node.Profile.Id || node.Database is not null&&current.Database!=node.Database;
        if(different){
            // Navigation gets a separate query; existing SQL, results and staged edits stay in their tab.
            _vm.NewQuery(profile:DbPilot.ViewModels.MainViewModel.CloneForDatabase(node.Profile,node.Database));
            _vm.Connection=node.Profile;
        }
        return true;
    }
    private void BuildTreeContextMenu(ObjectNode node)
    {
        var items=new List<Control>();
        void Add(string text,EventHandler<RoutedEventArgs> action,bool enabled=true){var item=new MenuItem{Header=text,IsEnabled=enabled};item.Click+=(sender,e)=>{if(TryUseTreeContext(node))action(sender,e);};items.Add(item);}
        void Separator()=>items.Add(new Separator());
        bool idle=!_vm.Busy,open=_vm.IsOpen(node.Profile);
        if(node.IsConnection){
            Add(open?"关闭连接":"打开连接",open?DisconnectDatabase:ConnectDatabase,idle);
            Separator();Add("编辑连接",EditConnection,idle);Add("复制连接",CopyConnection,idle);Add("删除连接",DeleteConnection,idle);
            Separator();Add("新建数据库",CreateDatabase,idle&&open);
        }else if(node.Database is not null&&node.Object is null){
            Add("查看表列表",async(_,_)=>await ShowTableCatalog(),idle&&open);
            Add("打开 / 加载对象",OpenNodeMenu,idle&&open);
            Add("新建表",CreateTable,idle&&open);
            Add("新建查询",NewQuery,idle&&open);
        }else if(node.Object is {} obj){
            bool table=obj.Kind is "表" or "普通表" or "子表" or "超级表";
            bool browsable=table||obj.Kind=="视图";
            Add(browsable?"打开数据":"打开对象",OpenNodeMenu,idle&&open);
            if(browsable){
                Add("查看表结构与建表语句",ViewStructure,idle&&open);
                Separator();Add("生成 SELECT",GenerateSelect,idle&&open);
                if(table){Add("生成 INSERT 模板",GenerateInsert,idle&&open);Add("生成 CREATE TABLE 模板",GenerateCreate,idle&&open);Add("生成 CREATE INDEX 模板",GenerateIndex,idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine);
                    Separator();Add("重命名表",RenameTable,idle&&open&&node.Profile.Kind!=DatabaseKind.TDengine);Add("删除表",DropTable,idle&&open);}
            }
            Separator();Add("复制对象名称",CopyObjectName);
        }else Add("加载",OpenNodeMenu,idle&&open);
        ObjectTree.ContextMenu!.ItemsSource=items;
    }
}
