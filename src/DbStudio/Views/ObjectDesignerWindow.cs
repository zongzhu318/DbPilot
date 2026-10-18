using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;

namespace DbStudio.Views;
public sealed class ObjectDesignerWindow : Window
{
    public ObjectDesignerWindow(IDbProvider provider,DatabaseKind kind,string database,DbObject? selected,Func<string,Task<bool>>? execute=null,bool createOnly=false)
    {
        Title=(createOnly?"新建表":"对象设计器")+" · "+database;Width=980;Height=650;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var actions=new[]{"创建表","创建视图","创建索引","新增字段","修改字段","删除字段","删除索引","删除表","删除视图"}.ToList();
        if(kind==DatabaseKind.TDengine)actions.AddRange(["创建超级表","创建子表","新增标签","删除标签","设置 TTL"]);
        if(createOnly)actions=actions.Where(a=>a is "创建表" or "创建超级表" or "创建子表").ToList();
        var action=new ComboBox{ItemsSource=actions,SelectedIndex=0,Width=150};
        var schema=new TextBox{Text=selected?.Schema??(kind==DatabaseKind.PostgreSQL?"public":kind==DatabaseKind.SqlServer?"dbo":""),Watermark="Schema",Width=130};
        var name=new TextBox{Text=selected?.Name??"new_table",Watermark="对象名",Width=230};
        var extra=new TextBox{Watermark="索引名 / 超级表名 / TTL 天数",Width=260};
        var expression=new TextBox{Watermark="视图 SELECT / 子表 TAGS 值表达式（不含括号）",AcceptsReturn=true,Height=80};
        var fields=new ObservableCollection<FieldDefinition>{new(){Name=kind==DatabaseKind.TDengine?"ts":"id",Type=kind==DatabaseKind.TDengine?"TIMESTAMP":"INT",Nullable=false,PrimaryKey=kind!=DatabaseKind.TDengine}};
        var grid=new DataGrid{ItemsSource=fields,AutoGenerateColumns=false,CanUserResizeColumns=true};
        foreach(var (label,path) in new[]{("名称","Name"),("类型","Type"),("默认表达式","Default")})grid.Columns.Add(new DataGridTextColumn{Header=label,Binding=new Binding(path){Mode=BindingMode.TwoWay},Width=new DataGridLength(190)});
        foreach(var (label,path) in new[]{("允许 NULL","Nullable"),("主键","PrimaryKey"),("标签","Tag")})grid.Columns.Add(new DataGridCheckBoxColumn{Header=label,Binding=new Binding(path){Mode=BindingMode.TwoWay}});
        if(kind!=DatabaseKind.TDengine)grid.Columns.Add(new DataGridCheckBoxColumn{Header="自增",Binding=new Binding("Identity"){Mode=BindingMode.TwoWay}});
        var preview=new TextBox{AcceptsReturn=true,IsReadOnly=true,FontFamily=new("Consolas,monospace")};var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var panel=new Grid{RowDefinitions=new("Auto,Auto,*,Auto,2*,Auto"),Margin=new(16)};
        var head=Row(action,schema,name,extra);var add=new Button{Content="新增字段"};add.Click+=(_,_)=>fields.Add(new());
        var remove=new Button{Content="移除所选字段"};remove.Click+=(_,_)=>{if(grid.SelectedItem is FieldDefinition f)fields.Remove(f);};
        var load=new Button{Content="载入现有字段"};load.Click+=async(_,_)=>{try{if(selected is null)return;var metadata=await provider.ColumnsAsync(selected,CancellationToken.None);fields.Clear();foreach(var c in metadata)fields.Add(new(){Name=c.Name,Type=c.Type,Nullable=c.Nullable,PrimaryKey=c.PrimaryKey,Tag=c.IsTag,Default=c.Default});status.Text="已载入字段。计算列与自增属性请通过对象定义查看。";}catch(Exception ex){status.Text=ex.Message;}};
        var generate=new Button{Content="生成 SQL"};var use=new Button{Content="采用 SQL"};use.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(preview.Text))Close(preview.Text);};
        generate.Click+=(_,_)=>{try{
            preview.Text="";
            if(!grid.CommitEdit(DataGridEditingUnit.Cell,true)||!grid.CommitEdit(DataGridEditingUnit.Row,true))throw new InvalidOperationException("请检查字段输入。");
            var table=new DbObject(database,schema.Text?.Trim()??"",Required(name.Text),action.SelectedItem?.ToString()=="创建超级表"?"超级表":"表");
            var cols=fields.Select(f=>new ColumnInfo(Required(f.Name),SchemaTools.ValidType(f.Type),f.PrimaryKey?false:f.Nullable,f.PrimaryKey,f.Default,"",f.Tag,IsGenerated:f.Identity,HasDefault:f.Default.Length>0,IsIdentity:f.Identity)).ToArray();
            if(cols.Select(c=>c.Name).Distinct().Count()!=cols.Length)throw new ArgumentException("字段名重复。");
            string q=provider.Qualified(table);var first=cols.FirstOrDefault();var indexes=string.Join(", ",cols.Select(c=>provider.Quote(c.Name)));
            var operation=action.SelectedItem?.ToString();
            preview.Text=operation switch{
                "创建表" or "创建超级表"=>SchemaTools.CreateTable(provider,kind,table,cols),
                "创建视图"=>$"CREATE VIEW {q} AS\n{Required(expression.Text)};",
                "创建索引"=>cols.Length==0?throw new ArgumentException("请选择索引字段。"): $"CREATE INDEX {provider.Quote(Required(extra.Text))} ON {q} ({indexes});",
                "新增字段"=>string.Join("\n",cols.Select(c=>$"ALTER TABLE {q} ADD COLUMN {SchemaTools.ColumnSql(provider,kind,c)};".Replace("ADD COLUMN",kind==DatabaseKind.SqlServer?"ADD":"ADD COLUMN"))),
                "修改字段"=>string.Join("\n",cols.Select(c=>kind switch{
                    DatabaseKind.MySQL=>$"ALTER TABLE {q} MODIFY COLUMN {SchemaTools.ColumnSql(provider,kind,c)};",
                    DatabaseKind.PostgreSQL=>$"ALTER TABLE {q} ALTER COLUMN {provider.Quote(c.Name)} TYPE {c.Type};\nALTER TABLE {q} ALTER COLUMN {provider.Quote(c.Name)} {(c.Nullable?"DROP":"SET")} NOT NULL;\nALTER TABLE {q} ALTER COLUMN {provider.Quote(c.Name)} {(c.HasDefault?"SET DEFAULT "+c.Default:"DROP DEFAULT")};",
                    DatabaseKind.SqlServer=>$"ALTER TABLE {q} ALTER COLUMN {provider.Quote(c.Name)} {c.Type} {(c.Nullable?"NULL":"NOT NULL")};\nGO\n"+SchemaTools.SqlServerDefault(provider,table,c),
                    _=>$"ALTER TABLE {q} MODIFY COLUMN {provider.Quote(c.Name)} {c.Type};"})),
                "删除字段"=>string.Join("\n",cols.Select(c=>$"ALTER TABLE {q} DROP COLUMN {provider.Quote(c.Name)};")),
                "删除索引"=>kind is DatabaseKind.MySQL or DatabaseKind.SqlServer?$"DROP INDEX {provider.Quote(Required(extra.Text))} ON {q};":$"DROP INDEX {(string.IsNullOrWhiteSpace(table.Schema)?"":provider.Quote(table.Schema)+".")}{provider.Quote(Required(extra.Text))};",
                "删除表"=>$"DROP TABLE {q};","删除视图"=>$"DROP VIEW {q};",
                "新增标签"=>string.Join("\n",cols.Select(c=>$"ALTER STABLE {q} ADD TAG {provider.Quote(c.Name)} {c.Type};")),
                "删除标签"=>string.Join("\n",cols.Select(c=>$"ALTER STABLE {q} DROP TAG {provider.Quote(c.Name)};")),
                "创建子表"=>$"CREATE TABLE {q} USING {provider.Qualified(table with{Name=Required(extra.Text)})} TAGS ({Required(expression.Text)});",
                "设置 TTL"=>int.TryParse(extra.Text,out var ttl)&&ttl>=0?$"ALTER TABLE {q} TTL {ttl};":throw new ArgumentException("TTL 必须为非负整数。"),
                _=>throw new ArgumentException("请选择操作。")};
            status.Text=operation?.StartsWith("删除")==true?"此操作会删除对象或数据。请在 SQL 编辑器中审阅后执行。":"SQL 已生成；类型转换、默认值和数据库版本需符合目标服务器要求。";
        }catch(Exception ex){status.Text=ex.Message;}};
        var buttons=Row(add,remove,load,generate,use);
        if(execute is not null){
            use.IsVisible=false;load.IsVisible=!createOnly;
            var create=new Button{Content="创建"};create.Classes.Add("primary");buttons.Children.Add(create);
            create.Click+=async(_,_)=>{generate.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));if(string.IsNullOrWhiteSpace(preview.Text))return;
                panel.IsEnabled=false;try{if(await execute(preview.Text))Close((string?)null);else status.Text="创建失败，请查看主窗口日志并修正后重试。";}catch(Exception ex){status.Text=ex.Message;}finally{panel.IsEnabled=true;}};
        }
        Grid.SetRow(expression,1);Grid.SetRow(grid,2);Grid.SetRow(buttons,3);Grid.SetRow(preview,4);Grid.SetRow(status,5);
        foreach(var c in new Control[]{head,expression,grid,buttons,preview,status}){c.Margin=new(0,5);panel.Children.Add(c);}Content=panel;
    }
    private static string Required(string? value)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("请填写对象、字段或表达式。"):value.Trim();
    private static StackPanel Row(params Control[] controls){var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(var c in controls)row.Children.Add(c);return row;}
}
