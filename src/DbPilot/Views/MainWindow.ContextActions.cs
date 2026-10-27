using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private static readonly (string Name,string Value)[] ConnectionColors=[("默认",""),("蓝色","#3E7FBF"),("绿色","#2E9E63"),("橙色","#D9822B"),("紫色","#8A63D2"),("红色","#D9534F"),("青色","#12A4A4"),("灰色","#7A828C")];
    private static bool IsTableObject(DbObject obj)=>obj.Kind is "表" or "普通表" or "子表" or "超级表";
    private static ConnectionProfile MenuProfile(ObjectNode node)=>MainViewModel.CloneForDatabase(node.Profile,node.Database??node.Profile.Database);
    private bool TryBeginTreeAction()
    {
        if(_vm.Busy){_vm.Log("请先等待当前任务完成。");return false;}
        return FlushGridEdits();
    }
    private async Task RefreshTreeNodeAsync(ObjectNode node)
    {
        try{
            await RefreshObjects(node.Profile,node.Database);
            if(TableCatalogPanel.IsVisible)await ShowTableCatalog();
        }catch(Exception ex){_vm.Log("刷新失败："+ex.Message,"Warn");}
    }
    private async Task ToggleFavoriteAsync(ObjectNode node)
    {
        if(!TryBeginTreeAction())return;
        node.Profile.Favorite=!node.Profile.Favorite;
        await _vm.SaveConnectionPreferencesAsync(node.Profile);
        _vm.Log((node.Profile.Favorite?"已添加星标 · ":"已取消星标 · ")+node.Profile.Name);
    }
    private async Task SetConnectionColorAsync(ObjectNode node,string color)
    {
        if(!TryBeginTreeAction())return;
        node.Profile.Color=color;
        await _vm.SaveConnectionPreferencesAsync(node.Profile);
        _vm.Log("已更新连接颜色 · "+node.Profile.Name);
    }
    private async Task MoveConnectionGroupAsync(ObjectNode node,string group)
    {
        if(!TryBeginTreeAction())return;
        node.Profile.Group=group;
        if(group.Length>0&&!_vm.Groups.Contains(group)){_vm.Groups.Add(group);await _vm.Store.SaveGroupsAsync(_vm.Groups);}
        await _vm.SaveConnectionPreferencesAsync(node.Profile);
        _vm.Log("已移动到"+(group.Length==0?"未分组":"组 · "+group));
    }
    private MenuItem BuildColorMenu(ObjectNode node)
    {
        var children=new List<Control>();
        foreach(var (name,value) in ConnectionColors){
            var label=node.Profile.Color==value?"✓ "+name:name;
            var item=new MenuItem{Header=label};
            item.Click+=async(_,_)=>await SetConnectionColorAsync(node,value);
            children.Add(item);
        }
        return new MenuItem{Header="颜色",ItemsSource=children};
    }
    private MenuItem BuildGroupMenu(ObjectNode node)
    {
        var groups=_vm.Groups.Concat(_vm.Connections.Select(c=>c.Group)).Where(g=>!string.IsNullOrWhiteSpace(g)).Distinct().ToArray();
        var children=new List<Control>{new MenuItem{Header=node.Profile.Group.Length==0?"✓ 未分组":"未分组"}};
        ((MenuItem)children[0]).Click+=async(_,_)=>await MoveConnectionGroupAsync(node,"");
        foreach(var group in groups){
            var item=new MenuItem{Header=node.Profile.Group==group?"✓ "+group:group};
            item.Click+=async(_,_)=>await MoveConnectionGroupAsync(node,group);
            children.Add(item);
        }
        return new MenuItem{Header="管理组",ItemsSource=children};
    }
    private MenuItem BuildDumpMenu(ObjectNode node,bool enabled)
    {
        var children=new List<Control>();
        foreach(var (label,structure,data) in new[]{("结构 + 数据",true,true),("仅结构",true,false),("仅数据",false,true)}){
            var item=new MenuItem{Header=label,IsEnabled=enabled};
            item.Click+=async(_,_)=>await DumpSqlFileAsync(node,structure,data);
            children.Add(item);
        }
        return new MenuItem{Header="转储 SQL 文件",IsEnabled=enabled,ItemsSource=children};
    }
    private MenuItem? BuildMaintenanceMenu(ObjectNode node,DbObject table,bool enabled)
    {
        (string Label,string Action)[] actions=node.Profile.Kind switch{
            DatabaseKind.MySQL=>[("分析表","analyze"),("检查表","check"),("优化表","optimize"),("修复表","repair")],
            DatabaseKind.PostgreSQL=>[("清理 (VACUUM)","vacuum"),("分析 (ANALYZE)","analyze")],
            DatabaseKind.SqlServer=>[("更新统计信息","statistics")],
            _=>[]};
        if(actions.Length==0)return null;
        var children=new List<Control>();
        foreach(var (label,action) in actions){
            var item=new MenuItem{Header=label,IsEnabled=enabled};
            item.Click+=async(_,_)=>await RunTableMaintenanceAsync(node,table,action);
            children.Add(item);
        }
        return new MenuItem{Header="维护",IsEnabled=enabled,ItemsSource=children};
    }
    private async Task ShowConnectionInfoAsync(ObjectNode node)
    {
        var p=node.Profile;
        string Masked(string value)=>string.IsNullOrEmpty(value)?"未设置":"已设置";
        var body=new StackPanel{Margin=new Thickness(24,18),Spacing=6};
        void Row(string label,string value)
        {
            var grid=new Grid{ColumnDefinitions=new("120,*")};
            grid.Children.Add(new TextBlock{Text=label,Classes={"fieldLabel"}});
            var text=new TextBlock{Text=value,TextWrapping=TextWrapping.Wrap};
            Grid.SetColumn(text,1);grid.Children.Add(text);body.Children.Add(grid);
        }
        Row("连接类型",p.Kind.ToString());
        Row("主机",$"{p.Host}:{p.Port}");
        Row("默认数据库",string.IsNullOrWhiteSpace(p.Database)?"未设置":p.Database);
        Row("用户名",string.IsNullOrWhiteSpace(p.Username)?"未设置":p.Username);
        Row("密码",Masked(p.Password));
        Row("SSL",p.Ssl?"已启用":"未启用");
        Row("Windows 身份验证",p.IntegratedSecurity?"已启用":"未启用");
        Row("连接超时",$"{p.Timeout} 秒");
        Row("时区",p.Timezone);
        Row("连接池",p.UsePooling?$"{p.MinimumPoolSize}–{p.MaximumPoolSize}":"未启用");
        if(p.Ssh.Enabled){
            Row("SSH 隧道",$"{p.Ssh.Username}@{p.Ssh.Host}:{p.Ssh.Port}");
            Row("SSH 认证",!string.IsNullOrWhiteSpace(p.Ssh.PrivateKeyPath)?"私钥":"密码");
        }
        Row("状态",_vm.IsOpen(p)?"已连接":_vm.IsClosed(p)?"已断开":"尚未连接");
        var dialog=DialogUi.Dialog("连接信息 · "+p.Name,620,520);
        dialog.CanResize=false;
        dialog.Content=DialogUi.Shell("连接信息",p.Name,new Border{Padding=new Thickness(0),Child=body},DialogUi.Footer(null,DialogUi.Secondary("关闭",()=>dialog.Close(),88)));
        await dialog.ShowDialog(this);
    }
    private async Task OpenCommandLineAsync(ConnectionProfile profile)
    {
        try{
            await using var provider=DbProviderFactory.Create(profile);
            var dialog=DialogUi.Dialog("命令列界面 · "+profile.Name,900,620);
            var output=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,FontFamily=new("Cascadia Code,Consolas,monospace"),Height=430,VerticalContentAlignment=VerticalAlignment.Top};
            var input=new TextBox{Watermark="输入 SQL，按 Enter 执行；↑ / ↓ 浏览历史",FontFamily=new("Cascadia Code,Consolas,monospace"),Margin=new Thickness(0,8,0,0)};
            var status=DialogUi.Status();
            var history=new List<string>();int historyIndex=0;bool running=false;CancellationTokenSource? current=null;
            void Append(string text){output.Text=string.IsNullOrEmpty(output.Text)?text:output.Text+"\n"+text;output.CaretIndex=output.Text?.Length??0;}
            async Task Execute()
            {
                var sql=input.Text?.Trim();
                if(string.IsNullOrWhiteSpace(sql)||running)return;
                running=true;input.Text="";history.Add(sql);historyIndex=history.Count;Append("> "+sql);status.Text="正在执行…";
                current=new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try{
                    var results=await provider.QueryMultipleAsync(sql,500,current.Token);
                    if(results.Count==0)Append("（无结果）");
                    long rows=0;
                    foreach(var result in results){
                        rows+=result.Rows.Count;
                        if(result.Columns.Count==0){Append($"受影响 {result.Affected} 行");continue;}
                        var header=string.Join(" | ",result.Columns);Append(header);Append(new string('-',Math.Min(80,header.Length)));
                        foreach(var row in result.Rows)Append(string.Join(" | ",row.Select(SqlTools.Display)));
                        if(result.Truncated)Append("（仅显示前 500 行）");
                    }
                    status.Text=$"完成 · {rows} 行";
                }catch(OperationCanceledException){Append("已取消。");status.Text="已取消";}
                catch(Exception ex){Append("错误："+ex.Message);status.Text="执行失败";}
                finally{running=false;current.Dispose();current=null;}
            }
            input.KeyDown+=(_,e)=>{
                if(e.Key==Key.Enter){e.Handled=true;_=Execute();}
                else if(e.Key==Key.Up&&history.Count>0){e.Handled=true;historyIndex=Math.Max(0,historyIndex-1);input.Text=history[historyIndex];input.CaretIndex=input.Text.Length;}
                else if(e.Key==Key.Down&&history.Count>0){e.Handled=true;historyIndex=Math.Min(history.Count,historyIndex+1);input.Text=historyIndex>=history.Count?"":history[historyIndex];input.CaretIndex=input.Text.Length;}
            };
            var run=DialogUi.Primary("执行",()=>_=Execute());
            var cancel=DialogUi.Secondary("取消任务",()=>current?.Cancel());
            var clear=DialogUi.Secondary("清空",()=>{output.Text="";status.Text="";});
            var close=DialogUi.Secondary("关闭",()=>dialog.Close(),88);
            var body=new Grid{RowDefinitions=new("*,Auto"),Margin=new Thickness(24,16)};
            body.Children.Add(output);Grid.SetRow(input,1);body.Children.Add(input);
            dialog.Content=DialogUi.Shell("命令列界面",$"{profile.Kind} · {profile.Database}",body,DialogUi.Footer(status,clear,cancel,run,close));
            dialog.Opened+=(_,_)=>input.Focus();
            dialog.Closed+=(_,_)=>current?.Cancel();
            await dialog.ShowDialog(this);
        }catch(Exception ex){_vm.Log("命令列界面打开失败："+ex.Message,"Error");}
    }
    private async Task DumpSqlFileAsync(ObjectNode node,bool structure,bool data)
    {
        if(!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(string.IsNullOrWhiteSpace(profile.Database)){_vm.Log("请先选择数据库。");return;}
        if(profile.Kind==DatabaseKind.TDengine){_vm.Log("TDengine 请使用导出向导逐表导出。","Warn");return;}
        try{
            var label=structure&&data?"结构 + 数据":structure?"仅结构":"仅数据";
            var baseName=node.Object?.Name??profile.Database;
            var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title="转储 SQL 文件 · "+label,SuggestedFileName=$"{baseName}-{(structure&&data?"full":structure?"schema":"data")}.sql",DefaultExtension="sql",FileTypeChoices=[new FilePickerFileType("SQL"){Patterns=["*.sql"]}]});
            if(file is null)return;
            var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本地文件。");
            await _vm.Guard(async ct=>{
                await using var provider=DbProviderFactory.Create(profile);
                IReadOnlyList<DbObject> tables=node.Object is {} obj?[obj]:(await provider.ObjectsAsync(profile.Database,ct)).Where(IsTableObject).OrderBy(t=>t.Schema).ThenBy(t=>t.Name).ToArray();
                if(tables.Count==0)throw new InvalidOperationException("当前数据库没有可转储的基础表。");
                await AtomicFile.WriteAsync(path,stream=>DataTransfer.DumpAsync(provider,profile,tables,stream,structure,data,new UiProgress<TransferProgress>(p=>_vm.Status=$"{p.Stage} · {p.Rows} 行"),ct),ct);
                _vm.Log($"已转储 {tables.Count} 张表的{label}到 {path}");
            });
        }catch(Exception ex){_vm.Log(ex.Message,"Error");}
    }
    private async Task RunSqlFileAsync(ObjectNode node)
    {
        if(!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(string.IsNullOrWhiteSpace(profile.Database)){_vm.Log("请先选择数据库。");return;}
        try{
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="运行 SQL 文件",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("SQL"){Patterns=["*.sql"]}]});
            if(files.Count==0)return;
            if(!await Confirm($"在 {profile.Name}/{profile.Database} 执行 {files[0].Name}？\n脚本可包含修改、删除及显式数据库名称。已完成批次不会自动回滚。","执行脚本"))return;
            await _vm.Guard(async ct=>{
                await using var provider=DbProviderFactory.Create(profile);
                await using var stream=await files[0].OpenReadAsync();using var reader=new StreamReader(stream);
                var outcomes=await ScriptRunner.RunAsync(provider,reader,profile.Kind,_vm.Settings.ContinueOnScriptError,100,new UiProgress<TransferProgress>(p=>_vm.Status=$"批次 {p.Rows} · {p.Stage}"),ct);
                var failed=outcomes.Where(o=>o.Error is not null).ToArray();
                foreach(var failure in failed.Take(5))_vm.Log($"第 {failure.Line} 行执行失败：{failure.Error}","Error");
                _vm.Log(failed.Length==0?$"SQL 文件执行完成 · {outcomes.Count} 个批次":$"SQL 文件执行结束 · {failed.Length} 个批次失败",failed.Length==0?"Info":"Warn");
            });
        }catch(Exception ex){_vm.Log(ex.Message,"Error");}
    }
    private async Task CloseDatabaseAsync(ObjectNode node)
    {
        if(node.Database is not {} database)return;
        if(!TryBeginTreeAction())return;
        try{
            if(_vm.Connection?.Id==node.Profile.Id&&_vm.CurrentProfile?.Database==database){
                var other=_databaseLists.GetValueOrDefault(node.Profile.Id,[]).FirstOrDefault(d=>d!=database);
                _vm.SelectDatabase(other??"");
                await RefreshDatabasePicker();
            }
        }catch(Exception ex){_vm.Log(ex.Message,"Warn");}
        node.Loaded=false;node.Children.Clear();
        node.Children.Add(new ObjectNode{Label="双击加载对象",Profile=node.Profile,Kind=ObjectNodeKind.Hint,Loaded=true});
        _vm.Log("已关闭数据库 "+database);
    }
    private async Task EditDatabaseAsync(ObjectNode node)
    {
        if(node.Database is not {} database)return;
        if(!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        if(profile.Kind==DatabaseKind.TDengine){_vm.Log("TDengine 数据库属性暂不支持编辑。","Warn");return;}
        try{
            await using var provider=DbProviderFactory.Create(profile);
            if(profile.Kind==DatabaseKind.MySQL){
                string charset="utf8mb4",collation="utf8mb4_unicode_ci";
                try{
                    var current=await provider.QueryAsync($"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME={DataValues.String(database,profile.Kind)}",1,CancellationToken.None);
                    if(current.Rows.Count>0&&current.Rows[0].Length>=2){charset=Convert.ToString(current.Rows[0][0])??charset;collation=Convert.ToString(current.Rows[0][1])??collation;}
                }catch{}
                var charsetBox=new TextBox{Text=charset};var collationBox=new TextBox{Text=collation};var preview=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,FontFamily=new("Cascadia Code,Consolas,monospace"),Height=88};
                var status=DialogUi.Status();
                void Update(){try{preview.Text=DatabaseDdl.AlterDatabaseCharset(provider,database,charsetBox.Text??"",collationBox.Text??"");status.Text="";}catch(Exception ex){preview.Text="";status.Text=ex.Message;}}
                charsetBox.TextChanged+=(_,_)=>Update();collationBox.TextChanged+=(_,_)=>Update();Update();
                var dialog=DialogUi.Dialog("编辑数据库 · "+database,620,460);dialog.CanResize=false;
                var apply=DialogUi.Primary("应用",()=>{},88);
                apply.Click+=async(_,_)=>{
                    try{
                        var sql=DatabaseDdl.AlterDatabaseCharset(provider,database,charsetBox.Text??"",collationBox.Text??"");
                        apply.IsEnabled=false;
                        if(await _vm.ExecuteDdlAsync(profile,sql)){dialog.Close();await RefreshObjects(profile);_vm.Log("数据库字符集已更新 · "+database);}
                        else status.Text=_vm.Status;
                    }catch(Exception ex){status.Text=ex.Message;}finally{apply.IsEnabled=true;}
                };
                var body=new StackPanel{Margin=new Thickness(24,18)};
                body.Children.Add(new TextBlock{Text="修改数据库默认字符集与排序规则；只影响之后新建的表。",Classes={"hint"},Margin=new Thickness(0,0,0,14)});
                body.Children.Add(DialogUi.Field("字符集",charsetBox));body.Children.Add(DialogUi.Field("排序规则",collationBox));
                body.Children.Add(new TextBlock{Text="预览",Classes={"hint"},Margin=new Thickness(0,4,0,6)});body.Children.Add(preview);
                dialog.Content=DialogUi.Shell("编辑数据库",profile.Name+" / "+database,new Border{Child=body},DialogUi.Footer(status,DialogUi.Secondary("取消",()=>dialog.Close(),88),apply));
                await dialog.ShowDialog(this);
            }else{
                var nameBox=new TextBox{Text=database};var preview=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,FontFamily=new("Cascadia Code,Consolas,monospace"),Height=70};
                var status=DialogUi.Status();
                void Update(){try{preview.Text=DatabaseDdl.RenameDatabase(provider,profile.Kind,database,nameBox.Text??"");status.Text="";}catch(Exception ex){preview.Text="";status.Text=ex.Message;}}
                nameBox.TextChanged+=(_,_)=>Update();Update();
                var dialog=DialogUi.Dialog("编辑数据库 · "+database,620,420);dialog.CanResize=false;
                var apply=DialogUi.Primary("重命名",()=>{},88);
                apply.Click+=async(_,_)=>{
                    try{
                        var newName=DatabaseDdl.Name(nameBox.Text);
                        var sql=DatabaseDdl.RenameDatabase(provider,profile.Kind,database,newName);
                        apply.IsEnabled=false;
                        if(await _vm.ExecuteDdlAsync(profile,sql)){
                            dialog.Close();_databaseLists.Remove(profile.Id);
                            if(_vm.CurrentProfile?.Database==database)try{_vm.SelectDatabase(newName);}catch(Exception ex){_vm.Log(ex.Message,"Warn");}
                            await RefreshObjects(profile);await RefreshDatabasePicker(true);_vm.Log("数据库已重命名 · "+newName);
                        }else status.Text=_vm.Status;
                    }catch(Exception ex){status.Text=ex.Message;}finally{apply.IsEnabled=true;}
                };
                var body=new StackPanel{Margin=new Thickness(24,18)};
                body.Children.Add(new TextBlock{Text="重命名数据库可能需要先断开其他会话；已有查询页仍指向旧名称。",Classes={"hint"},Margin=new Thickness(0,0,0,14)});
                body.Children.Add(DialogUi.Field("新名称",nameBox));
                body.Children.Add(new TextBlock{Text="预览",Classes={"hint"},Margin=new Thickness(0,4,0,6)});body.Children.Add(preview);
                dialog.Content=DialogUi.Shell("编辑数据库",profile.Name+" / "+database,new Border{Child=body},DialogUi.Footer(status,DialogUi.Secondary("取消",()=>dialog.Close(),88),apply));
                await dialog.ShowDialog(this);
            }
        }catch(Exception ex){_vm.Log("编辑数据库失败："+ex.Message,"Error");}
    }
    private async Task DropDatabaseAsync(ObjectNode node)
    {
        if(node.Database is not {} database)return;
        if(!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        if(profile.Kind==DatabaseKind.MySQL&&database is "information_schema" or "mysql" or "performance_schema" or "sys"){_vm.Log("系统数据库不能删除。","Warn");return;}
        if(profile.Kind==DatabaseKind.PostgreSQL&&database is "postgres" or "template0" or "template1"){_vm.Log("模板与默认数据库不能删除。","Warn");return;}
        try{
            if(!await Confirm($"永久删除数据库 {database} 及其中全部对象与数据？此操作不可撤销。","永久删除数据库"))return;
            await using var provider=DbProviderFactory.Create(profile);
            if(await _vm.ExecuteDdlAsync(profile,DatabaseDdl.DropDatabase(provider,database))){
                _databaseLists.Remove(profile.Id);
                if(_vm.CurrentProfile?.Database==database)try{_vm.SelectDatabase("");}catch(Exception ex){_vm.Log(ex.Message,"Warn");}
                await RefreshObjects(profile);await RefreshDatabasePicker(true);_vm.Log("数据库已删除 · "+database);
            }
        }catch(Exception ex){_vm.Log("删除数据库失败："+ex.Message,"Error");}
    }
    private async Task CreateTableForContextAsync(ObjectNode node)
    {
        if(!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)||string.IsNullOrWhiteSpace(profile.Database)){_vm.Log("请先打开连接并选择数据库。");return;}
        try{await CreateTableAsync(profile);}catch(Exception ex){_vm.Log(ex.Message,"Error");}
    }
    private async Task DesignTableAsync(ObjectNode node)
    {
        if(node.Object is not {} table||!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        await OpenDesigner(profile,table);
    }
    private async Task ClearTableAsync(ObjectNode node,bool truncate)
    {
        if(node.Object is not {} table||!IsTableObject(table)||!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        try{
            _vm.EnsureObjectHasNoDrafts(profile,table);
            if(!await Confirm(truncate?$"截断 {table}？将删除全部行并重置自增计数，此操作不可撤销。":$"清空 {table} 的全部行（DELETE）？此操作不可撤销。",truncate?"截断表":"清空表"))return;
            _vm.EnsureObjectHasNoDrafts(profile,table);
            await using var provider=DbProviderFactory.Create(profile);
            var sql=truncate?DatabaseDdl.TruncateTable(provider,profile.Kind,table):DatabaseDdl.DeleteAll(provider,table);
            if(await _vm.ExecuteDdlAsync(profile,sql)){
                _vm.InvalidateObject(profile,table);await RefreshObjects(profile,table.Database);
                if(TableCatalogPanel.IsVisible)await ShowTableCatalog();
                _vm.Log((truncate?"已截断表 · ":"已清空表 · ")+table);
            }
        }catch(Exception ex){_vm.Log(ex.Message,"Error");}
    }
    private async Task CopyTableAsync(ObjectNode node,bool withData)
    {
        if(node.Object is not {} table||!IsTableObject(table)||!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        if(profile.Kind==DatabaseKind.TDengine){_vm.Log("TDengine 请使用导出与导入向导复制表。","Warn");return;}
        try{
            var dialog=DialogUi.Dialog("复制表 · "+table,560,340);dialog.CanResize=false;
            var name=new TextBox{Text=table.Name+"_copy"};var status=DialogUi.Status();
            var hint=profile.Kind==DatabaseKind.SqlServer
                ?"SQL Server 复制的表不包含主键、默认值与索引，请在设计器中补齐。"
                :withData?"将复制表结构、索引与全部数据；大表可能耗时。":"将复制表结构与索引，不复制数据。";
            var copy=DialogUi.Primary("复制",()=>{},88);
            copy.Click+=async(_,_)=>{
                try{
                    var newName=DatabaseDdl.Name(name.Text);
                    if(newName==table.Name)throw new ArgumentException("新表名与原表名相同。");
                    await using var provider=DbProviderFactory.Create(profile);
                    var target=table with{Name=newName};
                    var sql=DatabaseDdl.CopyTable(provider,profile.Kind,table,target,withData);
                    copy.IsEnabled=false;
                    if(await _vm.ExecuteDdlAsync(profile,sql)){dialog.Close();await RefreshObjects(profile,table.Database);if(TableCatalogPanel.IsVisible)await ShowTableCatalog();_vm.Log($"已复制表 · {table} → {target}");}
                    else status.Text=_vm.Status;
                }catch(Exception ex){status.Text=ex.Message;}finally{copy.IsEnabled=true;}
            };
            var body=new StackPanel{Margin=new Thickness(24,18)};
            body.Children.Add(new TextBlock{Text=hint,Classes={"hint"},TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
            body.Children.Add(DialogUi.Field("新表名",name));
            dialog.Content=DialogUi.Shell("复制表",profile.Name+" / "+table,new Border{Child=body},DialogUi.Footer(status,DialogUi.Secondary("取消",()=>dialog.Close(),88),copy));
            await dialog.ShowDialog(this);
        }catch(Exception ex){_vm.Log("复制表失败："+ex.Message,"Error");}
    }
    private async Task RunTableMaintenanceAsync(ObjectNode node,DbObject table,string action)
    {
        if(!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        try{
            await using var provider=DbProviderFactory.Create(profile);
            var sql=DatabaseDdl.Maintenance(provider,profile.Kind,table,action)??throw new NotSupportedException("当前数据库不支持此维护操作。");
            if(await _vm.ExecuteDdlAsync(profile,sql))_vm.Log("表维护完成 · "+table);
        }catch(Exception ex){_vm.Log("表维护失败："+ex.Message,"Error");}
    }
    private async Task SetPermissionsAsync(ObjectNode node)
    {
        if(node.Object is not {} table||!TryBeginTreeAction())return;
        var profile=MenuProfile(node);
        if(_vm.Busy||_vm.IsClosed(profile)){_vm.Log("请先打开连接并等待当前任务完成。");return;}
        try{
            await using var provider=DbProviderFactory.Create(profile);
            var dialog=DialogUi.Dialog("设置权限 · "+table,760,590);
            var principal=new TextBox{Watermark=profile.Kind switch{DatabaseKind.MySQL=>"user@host",DatabaseKind.PostgreSQL=>"角色名",_=>"登录名"}};
            var boxes=new[]{"SELECT","INSERT","UPDATE","DELETE","ALTER","CREATE","DROP"}.Select(p=>new CheckBox{Content=p,IsChecked=p=="SELECT"}).ToArray();
            var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontFamily=new("Cascadia Code,Consolas,monospace"),Height=170};
            var status=DialogUi.Status();
            string Target()
            {
                var name=principal.Text?.Trim()??"";
                if(name.Length==0)throw new InvalidOperationException("请填写用户或角色名称。");
                if(profile.Kind==DatabaseKind.MySQL){
                    var at=name.LastIndexOf('@');var user=at<0?name:name[..at];var host=at<0?"%":name[(at+1)..];
                    return $"'{user.Replace("'","''")}'@'{host.Replace("'","''")}'";
                }
                return provider.Quote(name);
            }
            string Build(bool revoke)
            {
                var privileges=boxes.Where(b=>b.IsChecked==true).Select(b=>b.Content?.ToString()).Where(p=>!string.IsNullOrWhiteSpace(p)).ToArray();
                if(privileges.Length==0)throw new InvalidOperationException("请至少选择一项权限。");
                var on=profile.Kind==DatabaseKind.PostgreSQL?"TABLE "+provider.Qualified(table):provider.Qualified(table);
                return revoke?$"REVOKE {string.Join(", ",privileges)} ON {on} FROM {Target()};":$"GRANT {string.Join(", ",privileges)} ON {on} TO {Target()};";
            }
            void Update(){try{preview.Text=Build(false);status.Text="";}catch(Exception ex){preview.Text="";status.Text=ex.Message;}}
            principal.TextChanged+=(_,_)=>Update();
            foreach(var box in boxes)box.IsCheckedChanged+=(_,_)=>Update();
            var grant=DialogUi.Primary("授权 SQL",()=>{try{dialog.Close(Build(false));}catch(Exception ex){DialogUi.Error(status,ex.Message);}},88);
            var revoke=DialogUi.Secondary("撤销 SQL",()=>{try{dialog.Close(Build(true));}catch(Exception ex){DialogUi.Error(status,ex.Message);}},88);
            var body=new StackPanel{Margin=new Thickness(24,18)};
            body.Children.Add(new TextBlock{Text="选择用户与权限后生成 GRANT / REVOKE 语句，并在编辑器中审阅后执行。",Classes={"hint"},TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
            body.Children.Add(DialogUi.Field("用户 / 角色",principal));
            var privilegeGrid=new UniformGrid{Columns=4,Margin=new Thickness(150,0,0,12)};
            foreach(var box in boxes)privilegeGrid.Children.Add(box);
            body.Children.Add(privilegeGrid);
            body.Children.Add(new TextBlock{Text="预览",Classes={"hint"},Margin=new Thickness(0,0,0,6)});body.Children.Add(preview);
            dialog.Content=DialogUi.Shell("设置权限",profile.Name+" / "+table,new Border{Child=body},DialogUi.Footer(status,DialogUi.Secondary("取消",()=>dialog.Close(),88),revoke,grant));
            var sql=await dialog.ShowDialog<string?>(this);
            if(!string.IsNullOrWhiteSpace(sql)){_vm.NewQuery(sql,profile);_vm.Log("权限 SQL 已载入编辑器，请审阅后执行。");}
        }catch(Exception ex){_vm.Log("设置权限失败："+ex.Message,"Error");}
    }
}
