using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DbStudio.Models;
using DbStudio.Providers;

namespace DbStudio.Views;
public sealed class QueryBuilderWindow : Window
{
    public QueryBuilderWindow(IDbProvider provider,DatabaseKind kind,DbObject table,IReadOnlyList<ColumnInfo> columns)
    {
        Title="查询构建器 · "+table;Width=750;Height=620;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Spacing=12,Margin=new(20)};var fields=new WrapPanel();var checks=new List<CheckBox>();
        foreach(var column in columns){var check=new CheckBox{Content=column.Name,IsChecked=true,Margin=new(0,0,18,0)};checks.Add(check);fields.Children.Add(check);}
        var filterField=new ComboBox{ItemsSource=columns.Select(c=>c.Name).ToArray(),SelectedIndex=0,Width=170};
        var op=new ComboBox{ItemsSource=new[]{"无条件","=","<>",">",">=","<","<=","LIKE","IS NULL","IS NOT NULL"},SelectedIndex=0,Width=140};
        var value=new TextBox{Watermark="条件值（按所选字段类型转换）",Width=300};
        var order=new ComboBox{ItemsSource=new[]{"不排序"}.Concat(columns.Select(c=>c.Name)).ToArray(),SelectedIndex=0,Width=170};var descending=new CheckBox{Content="降序"};
        var limit=new NumericUpDown{Minimum=1,Maximum=100000,Value=500,Width=130};
        var preview=new TextBox{AcceptsReturn=true,IsReadOnly=true,Height=200,FontFamily=new("Consolas,monospace")};var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var generate=new Button{Content="生成查询"};generate.Click+=(_,_)=>{try{
            var names=checks.Select((c,i)=>(c,i)).Where(p=>p.c.IsChecked==true).Select(p=>provider.Quote(columns[p.i].Name)).ToArray();if(names.Length==0)throw new ArgumentException("至少选择一个字段。");
            var column=columns[filterField.SelectedIndex];var operation=op.SelectedItem?.ToString();
            string where=operation=="无条件"?"":" WHERE "+provider.Quote(column.Name)+" "+operation+(operation is "IS NULL" or "IS NOT NULL"?"":" "+Services.DataValues.Literal(Services.DataValues.ForColumn(value.Text??"",column,kind),kind));
            string sort=order.SelectedIndex<=0?"":" ORDER BY "+provider.Quote(columns[order.SelectedIndex-1].Name)+(descending.IsChecked==true?" DESC":" ASC");int count=(int)(limit.Value??500);
            preview.Text=$"SELECT {(kind==DatabaseKind.SqlServer?$"TOP ({count}) ":"")}{string.Join(", ",names)}\nFROM {provider.Qualified(table)}{where}{sort}"+(kind==DatabaseKind.SqlServer?";":$" LIMIT {count};");status.Text="查询仅生成 SQL，载入编辑器后执行。";
        }catch(Exception ex){status.Text=ex.Message;}};
        var use=new Button{Content="采用 SQL"};use.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(preview.Text))Close(preview.Text);};
        foreach(var c in new Control[]{new TextBlock{Text="选择输出字段"},fields,new TextBlock{Text="筛选条件"},Row(filterField,op,value),Row(new TextBlock{Text="排序",VerticalAlignment=VerticalAlignment.Center},order,descending,new TextBlock{Text="行数上限",VerticalAlignment=VerticalAlignment.Center},limit),Row(generate,use),preview,status})panel.Children.Add(c);
        Content=new ScrollViewer{Content=panel};
    }
    private static StackPanel Row(params Control[] controls){var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(var c in controls)row.Children.Add(c);return row;}
}
