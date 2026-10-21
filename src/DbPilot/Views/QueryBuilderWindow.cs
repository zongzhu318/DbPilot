using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DbPilot.Models;
using DbPilot.Providers;

namespace DbPilot.Views;
public sealed class QueryBuilderWindow : Window
{
    public QueryBuilderWindow(IDbProvider provider,DatabaseKind kind,DbObject table,IReadOnlyList<ColumnInfo> columns)
    {
        Title="查询构建器 · "+table;Width=780;Height=660;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        DialogUi.EnableEscapeToClose(this);
        var panel=new StackPanel{Spacing=10,Margin=new Thickness(24,18,24,18)};var fields=new WrapPanel();var checks=new List<CheckBox>();
        foreach(var column in columns){var check=new CheckBox{Content=column.Name,IsChecked=true,Margin=new Thickness(0,0,18,4)};checks.Add(check);fields.Children.Add(check);}
        var filterField=new ComboBox{ItemsSource=columns.Select(c=>c.Name).ToArray(),SelectedIndex=0,Width=170};
        var op=new ComboBox{ItemsSource=new[]{"无条件","=","<>",">",">=","<","<=","LIKE","IS NULL","IS NOT NULL"},SelectedIndex=0,Width=140};
        var value=new TextBox{Watermark="条件值（按所选字段类型转换）",MinWidth=260};
        var order=new ComboBox{ItemsSource=new[]{"不排序"}.Concat(columns.Select(c=>c.Name)).ToArray(),SelectedIndex=0,Width=170};var descending=new CheckBox{Content="降序"};
        var limit=new NumericUpDown{Minimum=1,Maximum=100000,Value=500,Width=130};
        var preview=new TextBox{AcceptsReturn=true,IsReadOnly=true,Height=200,FontFamily=new("Consolas,monospace")};
        var status=DialogUi.Status();
        void Generate(){try{
            var names=checks.Select((c,i)=>(c,i)).Where(p=>p.c.IsChecked==true).Select(p=>provider.Quote(columns[p.i].Name)).ToArray();if(names.Length==0)throw new ArgumentException("至少选择一个字段。");
            var column=columns[filterField.SelectedIndex];var operation=op.SelectedItem?.ToString();
            string where=operation=="无条件"?"":" WHERE "+provider.Quote(column.Name)+" "+operation+(operation is "IS NULL" or "IS NOT NULL"?"":" "+Services.DataValues.Literal(Services.DataValues.ForColumn(value.Text??"",column,kind),kind));
            string sort=order.SelectedIndex<=0?"":" ORDER BY "+provider.Quote(columns[order.SelectedIndex-1].Name)+(descending.IsChecked==true?" DESC":" ASC");int count=(int)(limit.Value??500);
            preview.Text=$"SELECT {(kind==DatabaseKind.SqlServer?$"TOP ({count}) ":"")}{string.Join(", ",names)}\nFROM {provider.Qualified(table)}{where}{sort}"+(kind==DatabaseKind.SqlServer?";":$" LIMIT {count};");DialogUi.Info(status,"查询仅生成 SQL，载入编辑器后执行。");
        }catch(Exception ex){DialogUi.Error(status,ex.Message);}}

        panel.Children.Add(Section("输出字段","取消勾选以排除字段"));
        panel.Children.Add(fields);
        panel.Children.Add(Section("筛选条件"));
        var filterRow=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,Auto,*")};
        filterField.Margin=new Thickness(0,0,8,0);filterRow.Children.Add(filterField);
        op.Margin=new Thickness(0,0,8,0);Grid.SetColumn(op,1);filterRow.Children.Add(op);
        Grid.SetColumn(value,2);filterRow.Children.Add(value);
        panel.Children.Add(filterRow);
        panel.Children.Add(Section("排序与行数"));
        var limitLabel=new TextBlock{Text="行数上限",Classes={"hint"},VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};
        panel.Children.Add(Row(order,descending,limitLabel,limit));
        panel.Children.Add(Section("SQL 预览"));
        panel.Children.Add(preview);
        var generate=DialogUi.Secondary("生成查询",Generate,96);
        var use=DialogUi.Primary("采用 SQL",()=>{if(!string.IsNullOrWhiteSpace(preview.Text))Close(preview.Text);},96);
        Content=DialogUi.Shell("查询构建器",table.ToString(),new ScrollViewer{Content=panel},DialogUi.Footer(status,generate,use));
    }
    private static Control Section(string title,string? hint=null)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,Margin=new Thickness(0,4,0,0)};
        row.Children.Add(new TextBlock{Text=title,Classes={"sectionTitle"}});
        if(hint is not null)row.Children.Add(new TextBlock{Text=hint,Classes={"hint"},VerticalAlignment=VerticalAlignment.Center});
        return row;
    }
    private static StackPanel Row(params Control[] controls){var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(var c in controls)row.Children.Add(c);return row;}
}
