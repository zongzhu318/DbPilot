using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;
using DbPilot.ViewModels;

namespace DbPilot.Views;

// Each operation owns its provider and cancellation token; no connection is shared with the editor.
public sealed class DatabaseToolsWindow : Window
{
    private readonly ComboBox _source=new(){MinWidth=220},_target=new(){MinWidth=220},_tables=new(){MinWidth=220},_targetTables=new(){MinWidth=220};
    private readonly TextBox _database=new(){Watermark="源数据库",MinWidth=150},_targetDatabase=new(){Watermark="目标数据库",MinWidth=150};
    private readonly TextBox _preview=new(){AcceptsReturn=true,FontFamily=new("Consolas,monospace"),TextWrapping=Avalonia.Media.TextWrapping.NoWrap};
    private readonly TextBlock _status=new(){Text="选择连接与数据库，然后加载对象。",TextWrapping=Avalonia.Media.TextWrapping.Wrap};
    private readonly StackPanel _actions=new(){Orientation=Orientation.Horizontal,Spacing=7};
    private readonly Action<string,ConnectionProfile> _openSql;
    private CancellationTokenSource? _operation;
    private bool _closing;
    public DatabaseToolsWindow(IEnumerable<ConnectionProfile> profiles,ConnectionProfile? selected,Action<string,ConnectionProfile> openSql)
    {
        Title="数据库工具 · 导入导出 / 结构比较 / 数据迁移";Width=1150;Height=760;MinWidth=900;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _openSql=openSql;var connections=profiles.ToArray();_source.ItemsSource=connections;_target.ItemsSource=connections;
        _source.SelectionChanged+=(_,_)=>{_database.Text=(_source.SelectedItem as ConnectionProfile)?.Database;_tables.ItemsSource=null;};
        _target.SelectionChanged+=(_,_)=>{_targetDatabase.Text=(_target.SelectedItem as ConnectionProfile)?.Database;_targetTables.ItemsSource=null;};
        _source.SelectedItem=selected??connections.FirstOrDefault();_target.SelectedItem=connections.FirstOrDefault();
        var root=new Grid{RowDefinitions=new("Auto,Auto,Auto,*,Auto"),Margin=new(16)};
        var sourceRow=Row(new TextBlock{Text="源",VerticalAlignment=VerticalAlignment.Center},_source,_database,Button("加载对象",()=>Load(false)),_tables);
        var targetRow=Row(new TextBlock{Text="目标",VerticalAlignment=VerticalAlignment.Center},_target,_targetDatabase,Button("加载对象",()=>Load(true)),_targetTables);
        Grid.SetRow(targetRow,1);sourceRow.Margin=new(0,0,0,8);targetRow.Margin=new(0,0,0,12);
        var tabs=new TabControl();Grid.SetRow(tabs,2);
        tabs.ItemsSource=new[]{
            Tab("对象定义",Button("读取原始定义",Definition)),
            Tab("对象设计",Button("打开字段与索引设计器",Design)),
            Tab("查询构建",Button("选择字段、条件和排序",BuildQuery)),
            Tab("ER 图",Button("读取当前数据库关系图",ErDiagram)),
            Tab("TDengine",Button("检测版本与流 / 订阅元数据",TdMetadata)),
            Tab("整表导出",Row(Button("CSV",()=>Export("csv")),Button("JSON",()=>Export("json")),new TextBlock{Text="导出所有行；取消时保留已有文件。",VerticalAlignment=VerticalAlignment.Center})),
            Tab("CSV 导入",Row(Button("选择 CSV 并映射字段",Import),new TextBlock{Text="首行为字段名，\\N 表示 NULL；关系数据库整批事务提交。",VerticalAlignment=VerticalAlignment.Center})),
            Tab("结构比较",Row(Button("比较源表 → 目标表",Compare),new TextBlock{Text="生成字段差异 SQL；请在编辑器中审阅后执行。",VerticalAlignment=VerticalAlignment.Center})),
            Tab("数据迁移",Row(Button("迁移到已有目标表",Migrate),new TextBlock{Text="按同名可写字段追加；不清空目标表。",VerticalAlignment=VerticalAlignment.Center})),
            Tab("逻辑备份",Button("备份当前数据库的基础表结构与数据",Backup)),
            Tab("脚本恢复",Row(Button("执行 SQL 文件",Restore),new TextBlock{Text="按批次执行，遇错停止；已完成批次不会自动回滚。",VerticalAlignment=VerticalAlignment.Center}))
        };
        Grid.SetRow(_preview,3);_preview.Margin=new(0,12);var footer=new Grid{ColumnDefinitions=new("*,Auto")};footer.Children.Add(_status);Grid.SetColumn(_actions,1);footer.Children.Add(_actions);Grid.SetRow(footer,4);
        _actions.Children.Add(Button("载入 SQL 编辑器",()=>{_openSql(_preview.Text??"",_previewProfile??Profile(false));return Task.CompletedTask;}));
        var cancel=new Button{Content="取消任务"};cancel.Click+=(_,_)=>_operation?.Cancel();_actions.Children.Add(cancel);
        root.Children.Add(sourceRow);root.Children.Add(targetRow);root.Children.Add(tabs);root.Children.Add(_preview);root.Children.Add(footer);Content=root;
        Closing+=(_,e)=>{if(_operation is not null){e.Cancel=true;_closing=true;_operation.Cancel();_status.Text="正在取消任务，请等待数据库释放资源…";}};
    }
    private static StackPanel Row(params Control[] controls){var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(var c in controls)row.Children.Add(c);return row;}
    private static TabItem Tab(string name,Control content)=>new(){Header=name,Content=content};
    private Button Button(string text,Func<Task> action){var button=new Button{Content=text};button.Click+=async(_,_)=>{try{await action();}catch(Exception ex){_status.Text=ex.Message;}};return button;}
    private ConnectionProfile Profile(bool target)
    {
        var connection=(target?_target:_source).SelectedItem as ConnectionProfile??throw new InvalidOperationException("请选择连接。");
        var database=(target?_targetDatabase:_database).Text?.Trim();if(string.IsNullOrWhiteSpace(database))throw new InvalidOperationException("请输入数据库名称。");
        return MainViewModel.CloneForDatabase(connection,database);
    }
    private DbObject Table(bool target)=>(target?_targetTables:_tables).SelectedItem as DbObject??throw new InvalidOperationException("请先加载并选择对象。");
    private async Task Run(Func<CancellationToken,Task> work)
    {
        if(_operation is not null)throw new InvalidOperationException("已有任务执行中。");
        using var source=new CancellationTokenSource();_operation=source;_status.Text="正在执行…";
        try{await work(source.Token);}
        catch(OperationCanceledException){_status.Text="操作已取消。";}
        catch(Exception ex){_status.Text="失败："+ex.Message;}
        finally{_operation=null;if(_closing)Close();}
    }
    private Task Load(bool target)=>Run(async ct=>{
        var profile=Profile(target);await using var provider=DbProviderFactory.Create(profile);var objects=await provider.ObjectsAsync(profile.Database,ct);
        var picker=target?_targetTables:_tables;picker.ItemsSource=objects;picker.SelectedIndex=objects.Count>0?0:-1;_status.Text=$"已加载 {objects.Count} 个对象。";
    });
    private Task Definition()=>Run(async ct=>{var profile=Profile(false);await using var provider=DbProviderFactory.Create(profile);_preview.Text=await provider.DefinitionAsync(Table(false),ct);_previewProfile=profile;_status.Text="对象定义已载入。";});
    private async Task Export(string format)
    {
        if(_operation is not null)return;var profile=Profile(false);var table=Table(false);
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title="导出整表",SuggestedFileName=table.Name+"."+format,DefaultExtension=format});if(file is null)return;
        var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本地文件。");
        await Run(async ct=>{
            await using var provider=DbProviderFactory.Create(profile);
            long count=0;await AtomicFile.WriteAsync(path,async stream=>count=await DataTransfer.ExportAsync(provider,profile,table,stream,format,new UiProgress<TransferProgress>(p=>_status.Text=$"{p.Stage} · {p.Rows} 行"),ct),ct);
            _status.Text=$"已导出 {count} 行到 {path}";
        });
    }
    private async Task Import()
    {
        if(_operation is not null)return;var profile=Profile(false);var table=Table(false);
        var files=await StorageProvider.OpenFilePickerAsync(new(){Title="导入 CSV",AllowMultiple=false,FileTypeFilter=[new("CSV"){Patterns=["*.csv"]}]});if(files.Count==0)return;
        await Run(async ct=>{
            await using var provider=DbProviderFactory.Create(profile);var columns=(await provider.ColumnsAsync(table,ct)).Where(c=>!c.IsGenerated&&!c.IsTag).ToArray();
            await using var sample=await files[0].OpenReadAsync();using var sampleReader=new StreamReader(sample);
            await using var enumerator=CsvData.ReadAsync(sampleReader,ct).GetAsyncEnumerator(ct);if(!await enumerator.MoveNextAsync())throw new FormatException("CSV 文件为空。");
            var header=enumerator.Current;
            var mapping=await MapColumns(header,columns);ct.ThrowIfCancellationRequested();if(mapping is null){_status.Text="已取消导入。";return;}
            var selected=mapping.Select(m=>columns[m.Target]).ToArray();
            if(!await Confirm($"向 {profile.Name} / {table} 追加 CSV 数据？\n映射 {mapping.Length} 个字段。"))return;
            await using var input=await files[0].OpenReadAsync();using var reader=new StreamReader(input);
            var rows=CsvData.MapAsync(reader,true,mapping.Select(m=>m.Source).ToArray(),selected,profile.Kind,ct);
            var count=await provider.InsertRowsAsync(table,selected.Select(c=>c.Name).ToArray(),rows,new UiProgress<TransferProgress>(p=>_status.Text=$"{p.Stage} · {p.Rows} 行"),ct);
            _status.Text=$"已提交 {count} 行。";
        });
    }
    private async Task<(int Source,int Target)[]?> MapColumns(string[] header,ColumnInfo[] columns)
    {
        var window=new Window{Title="CSV 字段映射 · 首行为标题",Width=650,Height=540,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var rows=new StackPanel{Spacing=10,Margin=new(16)};var pickers=new List<ComboBox>();
        foreach(var name in header){var picker=new ComboBox{ItemsSource=new[]{"（跳过）"}.Concat(columns.Select(c=>c.Name)).ToArray(),SelectedIndex=Array.FindIndex(columns,c=>c.Name==name)+1,Width=270};pickers.Add(picker);rows.Children.Add(Row(new TextBlock{Text=name,Width=240,TextWrapping=Avalonia.Media.TextWrapping.Wrap},picker));}
        var error=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};rows.Children.Add(error);
        var okay=new Button{Content="确认映射"};okay.Click+=(_,_)=>{
            var mapping=pickers.Select((p,i)=>(Source:i,Target:p.SelectedIndex-1)).Where(m=>m.Target>=0).ToArray();
            if(mapping.Length==0||mapping.Select(m=>m.Target).Distinct().Count()!=mapping.Length){error.Text="请选择至少一个字段，同一目标字段只能映射一次。";return;}
            window.Close(mapping);
        };rows.Children.Add(okay);window.Content=new ScrollViewer{Content=rows};return await window.ShowDialog<(int Source,int Target)[]?>(this);
    }
    private Task Compare()=>Run(async ct=>{
        var sourceProfile=Profile(false);var targetProfile=Profile(true);var sourceTable=Table(false);var targetTable=Table(true);
        await using var source=DbProviderFactory.Create(sourceProfile);await using var target=DbProviderFactory.Create(targetProfile);
        var columns=await source.ColumnsAsync(sourceTable,ct);var destination=await target.ColumnsAsync(targetTable,ct);
        var mapped=columns.Select(c=>c with{Type=SchemaTools.MapType(c.Type,sourceProfile.Kind,targetProfile.Kind)}).ToArray();
        var primaryKey=(await target.IndexesAsync(targetTable,ct)).FirstOrDefault(i=>i.Kind=="主键")?.Name;
        var differences=SchemaTools.Compare(target,targetProfile.Kind,targetTable,mapped,destination,primaryKey);
        _preview.Text=string.Join("\n\n",differences.Select(d=>$"-- {d.Kind} · {d.Object} · {d.Detail}"+(d.Destructive?" · 可能丢失数据":"")+"\n"+d.Sql));
        if(differences.Count==0)_preview.Text="-- 字段定义一致。";
        _status.Text=$"发现 {differences.Count} 项字段差异。生成 SQL 的执行连接应选择目标连接。";
        _previewProfile=targetProfile;
    });
    private ConnectionProfile? _previewProfile;
    private Task TdMetadata()=>Run(async ct=>{
        var profile=Profile(false);if(profile.Kind!=DatabaseKind.TDengine)throw new InvalidOperationException("请选择 TDengine 连接。");
        await using var provider=DbProviderFactory.Create(profile);var text=new System.Text.StringBuilder();
        foreach(var sql in new[]{"SELECT SERVER_VERSION()","SHOW DATABASES","SHOW STREAMS","SHOW TOPICS","SHOW SUBSCRIPTIONS"}){
            ct.ThrowIfCancellationRequested();text.AppendLine("-- "+sql);
            try{var result=await provider.QueryAsync(sql,500,ct);text.AppendLine(string.Join(" | ",result.Columns));foreach(var row in result.Rows)text.AppendLine(string.Join(" | ",row.Select(SqlTools.Display)));if(result.Truncated)text.AppendLine("（仅显示前 500 行）");}
            catch(OperationCanceledException){throw;}catch(Exception ex){text.AppendLine("服务器未提供此项元数据："+ex.Message);}
            text.AppendLine();_preview.Text=text.ToString();
        }
        _previewProfile=profile;_status.Text="版本和能力检测完成。流、主题及订阅语法由服务器版本与权限决定。";
    });
    private async Task ErDiagram()
    {
        if(_operation is not null)return;
        var tables=new List<(DbObject Table,IReadOnlyList<ColumnInfo> Columns)>();IReadOnlyList<ForeignKeyInfo> keys=[];bool completed=false;
        await Run(async ct=>{var profile=Profile(false);await using var provider=DbProviderFactory.Create(profile);var objects=(await provider.ObjectsAsync(profile.Database,ct)).Where(o=>o.Kind is "表" or "普通表" or "超级表").ToArray();
            if(objects.Length>100)throw new InvalidOperationException("关系图最多展示 100 张表，请按数据库拆分查看。");
            foreach(var table in objects){ct.ThrowIfCancellationRequested();tables.Add((table,await provider.ColumnsAsync(table,ct)));_status.Text=$"读取表结构 · {tables.Count}/{objects.Length}";}
            keys=await provider.ForeignKeysAsync(profile.Database,ct);
            completed=true;
        });
        if(!completed||tables.Count==0)return;var window=new VisualizationWindow();window.ShowEr(tables,keys);await window.ShowDialog(this);
    }
    private async Task Design()
    {
        if(_operation is not null)return;var profile=Profile(false);await using var provider=DbProviderFactory.Create(profile);
        var selected=_tables.SelectedItem as DbObject;
        var sql=await new ObjectDesignerWindow(provider,profile.Kind,profile.Database,selected).ShowDialog<string?>(this);
        if(sql is not null){_preview.Text=sql;_previewProfile=profile;_status.Text="设计 SQL 已生成，请载入编辑器审阅后执行。";}
    }
    private async Task BuildQuery()
    {
        if(_operation is not null)return;var profile=Profile(false);var table=Table(false);IReadOnlyList<ColumnInfo> columns=[];
        await Run(async ct=>{await using var provider=DbProviderFactory.Create(profile);columns=await provider.ColumnsAsync(table,ct);});
        if(columns.Count==0)return;
        await using var builder=DbProviderFactory.Create(profile);
        var sql=await new QueryBuilderWindow(builder,profile.Kind,table,columns).ShowDialog<string?>(this);
        if(sql is not null){_preview.Text=sql;_previewProfile=profile;_status.Text="查询 SQL 已生成。";}
    }
    private async Task Migrate()
    {
        if(_operation is not null)return;var sourceProfile=Profile(false);var targetProfile=Profile(true);var sourceTable=Table(false);var targetTable=Table(true);
        if(sourceProfile.Kind==targetProfile.Kind&&sourceProfile.Host==targetProfile.Host&&sourceProfile.Port==targetProfile.Port&&sourceTable==targetTable)throw new InvalidOperationException("源表与目标表不能相同。");
        if(!await Confirm($"从 {sourceProfile.Name}/{sourceTable} 向 {targetProfile.Name}/{targetTable} 追加数据？\n目标表中的原有数据会保留。"))return;
        await Run(async ct=>{await using var source=DbProviderFactory.Create(sourceProfile);await using var target=DbProviderFactory.Create(targetProfile);
            var count=await DataTransfer.MigrateAsync(source,sourceProfile,sourceTable,target,targetProfile,targetTable,new UiProgress<TransferProgress>(p=>_status.Text=$"迁移中 · {p.Rows} 行"),ct);_status.Text=$"迁移完成 · {count} 行。";
        });
    }
    private async Task Restore()
    {
        if(_operation is not null)return;var profile=Profile(false);
        var files=await StorageProvider.OpenFilePickerAsync(new(){Title="执行 SQL 恢复脚本",AllowMultiple=false,FileTypeFilter=[new("SQL"){Patterns=["*.sql"]}]});if(files.Count==0)return;
        if(!await Confirm($"在 {profile.Name}/{profile.Database} 执行 {files[0].Name}？\n脚本可包含修改、删除及显式数据库名称。已完成批次不会自动回滚。"))return;
        await Run(async ct=>{
            await using var provider=DbProviderFactory.Create(profile);await using var stream=await files[0].OpenReadAsync();using var reader=new StreamReader(stream);
            var outcomes=await ScriptRunner.RunAsync(provider,reader,profile.Kind,false,100,new UiProgress<TransferProgress>(p=>_status.Text=$"批次 {p.Rows} · {p.Stage}"),ct);
            _preview.Text=string.Join("\n",outcomes.Select(o=>$"批次 {o.Batch} · 第 {o.Line} 行 · {o.Error??"成功"}"));_status.Text=outcomes.Any(o=>o.Error is not null)?"恢复遇错停止，请检查批次记录。":"脚本执行完成。";
        });
    }
    private async Task Backup()
    {
        if(_operation is not null)return;var profile=Profile(false);
        if(profile.Kind==DatabaseKind.TDengine)throw new NotSupportedException("TDengine 请使用逐表导出与 SHOW CREATE 定义；子表标签绑定不能用关系型逻辑备份还原。");
        if(!await Confirm("备份字段、计算列、主键、外键和表数据，不含视图、索引、触发器、用户与权限。\n这不是服务器完整备份；并发写入期间各表不保证同一时间快照。\nMySQL 脚本保留源数据库名称，恢复前请审阅目标名称。"))return;
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title="保存基础逻辑备份",SuggestedFileName=profile.Database+"-logical.sql",DefaultExtension="sql"});if(file is null)return;
        var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本地文件。");
        await Run(async ct=>{await using var provider=DbProviderFactory.Create(profile);var tables=(await provider.ObjectsAsync(profile.Database,ct)).Where(t=>t.Kind=="表").ToArray();
            await AtomicFile.WriteAsync(path,stream=>DataTransfer.BackupAsync(provider,profile,tables,stream,new UiProgress<TransferProgress>(p=>_status.Text=$"{p.Stage} · {p.Rows} 行"),ct),ct);_status.Text=$"已备份 {tables.Length} 张表。";
        });
    }
    private Task<bool> Confirm(string text)
    {
        var window=new Window{Title="确认数据库操作",Width=540,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new(20),Spacing=18};panel.Children.Add(new TextBlock{Text=text,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        var okay=new Button{Content="确认执行"};okay.Click+=(_,_)=>window.Close(true);var cancel=new Button{Content="取消"};cancel.Click+=(_,_)=>window.Close(false);
        panel.Children.Add(Row(okay,cancel));window.Content=panel;return window.ShowDialog<bool>(this);
    }
}
