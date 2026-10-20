using System.Globalization;
using System.Security;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DbPilot.Models;
using DbPilot.Services;

namespace DbPilot.Views;
public sealed class VisualizationWindow : Window
{
    private readonly Canvas _canvas=new(){Background=Brushes.White};
    private readonly StringBuilder _svg=new();
    private readonly StackPanel _toolbar=new(){Orientation=Orientation.Horizontal,Spacing=10,Margin=new(12)};
    private readonly TextBlock _status=new(){Margin=new(12),TextWrapping=TextWrapping.Wrap};
    private double _width=1000,_height=600;
    public VisualizationWindow()
    {
        Width=1100;Height=750;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid{RowDefinitions=new("Auto,*,Auto")};var scroll=new ScrollViewer{Content=_canvas,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);Grid.SetRow(_status,2);
        var save=new Button{Content="导出 SVG"};save.Click+=async(_,_)=>{try{
            var file=await StorageProvider.SaveFilePickerAsync(new(){Title="导出矢量图",SuggestedFileName="diagram.svg",DefaultExtension="svg"});if(file is null)return;
            var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本地文件。");
            var text=$"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(_width)}\" height=\"{N(_height)}\" viewBox=\"0 0 {N(_width)} {N(_height)}\"><rect width=\"100%\" height=\"100%\" fill=\"white\"/>{_svg}</svg>";
            await AtomicFile.WriteAsync(path,s=>s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask(),default);_status.Text="已导出 "+path;
        }catch(Exception ex){_status.Text=ex.Message;}};_toolbar.Children.Add(save);
        root.Children.Add(_toolbar);root.Children.Add(scroll);root.Children.Add(_status);Content=root;
    }
    private static string N(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
    private void Clear(double width,double height){_width=width;_height=height;_canvas.Width=width;_canvas.Height=height;_canvas.Children.Clear();_svg.Clear();}
    private void Line(double x1,double y1,double x2,double y2,string color,double thickness=1)
    {
        _canvas.Children.Add(new Line{StartPoint=new(x1,y1),EndPoint=new(x2,y2),Stroke=Brush.Parse(color),StrokeThickness=thickness});
        _svg.Append($"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" stroke=\"{color}\" stroke-width=\"{N(thickness)}\"/>");
    }
    private void Text(double x,double y,string text,int size=12,string color="#263238")
    {
        var label=new TextBlock{Text=text,FontSize=size,Foreground=Brush.Parse(color)};Canvas.SetLeft(label,x);Canvas.SetTop(label,y);_canvas.Children.Add(label);
        _svg.Append($"<text x=\"{N(x)}\" y=\"{N(y+size)}\" font-family=\"sans-serif\" font-size=\"{size}\" fill=\"{color}\">{SecurityElement.Escape(text)}</text>");
    }
    private void Box(double x,double y,double w,double h,string color)
    {
        var rect=new Rectangle{Width=w,Height=h,Fill=Brush.Parse(color),Stroke=Brush.Parse("#617D74"),StrokeThickness=1};Canvas.SetLeft(rect,x);Canvas.SetTop(rect,y);_canvas.Children.Add(rect);
        _svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" fill=\"{color}\" stroke=\"#617D74\"/>");
    }
    public void ShowEr(IReadOnlyList<(DbObject Table,IReadOnlyList<ColumnInfo> Columns)> tables,IReadOnlyList<ForeignKeyInfo> keys)
    {
        Title="ER 关系图 · 外键与字段";int rows=(tables.Count+2)/3;double cellHeight=tables.Count==0?100:tables.Max(t=>t.Columns.Count)*22+90;Clear(1140,Math.Max(500,rows*cellHeight+30));
        var positions=new Dictionary<(string,string),Point>();
        for(int i=0;i<tables.Count;i++){double x=20+(i%3)*375,y=20+(i/3)*cellHeight;positions[(tables[i].Table.Schema,tables[i].Table.Name)]=new(x,y);}
        foreach(var key in keys){
            var from=tables.FirstOrDefault(t=>t.Table.Name==key.Table&&(t.Table.Schema==key.Schema||t.Table.Database==key.Schema));
            var to=tables.FirstOrDefault(t=>t.Table.Name==key.ReferencedTable&&(t.Table.Schema==key.ReferencedSchema||t.Table.Database==key.ReferencedSchema));
            if(from.Table is null||to.Table is null)continue;
            var a=positions[(from.Table.Schema,from.Table.Name)];var b=positions[(to.Table.Schema,to.Table.Name)];
            double y1=a.Y+52+Math.Max(0,from.Columns.ToList().FindIndex(c=>c.Name==key.Column))*22,y2=b.Y+52+Math.Max(0,to.Columns.ToList().FindIndex(c=>c.Name==key.ReferencedColumn))*22;
            Line(a.X+340,y1,b.X,y2,"#647DAD",2);Line(b.X,y2,b.X-8,y2-5,"#647DAD",2);Line(b.X,y2,b.X-8,y2+5,"#647DAD",2);
        }
        for(int i=0;i<tables.Count;i++){
            var item=tables[i];var p=positions[(item.Table.Schema,item.Table.Name)];Box(p.X,p.Y,340,item.Columns.Count*22+62,"#F5FAF7");Box(p.X,p.Y,340,32,"#D8EEE1");Text(p.X+10,p.Y+7,item.Table.ToString(),14);
            for(int c=0;c<item.Columns.Count;c++)Text(p.X+10,p.Y+42+c*22,(item.Columns[c].PrimaryKey?"PK  ":"     ")+item.Columns[c].Name+"  "+item.Columns[c].Type);
        }
        _status.Text=$"{tables.Count} 张表 · {keys.Count} 项外键字段引用；关系来自数据库元数据。";
    }
    public void ShowChart(QueryResult result)
    {
        Title="查询结果图表";var x=new ComboBox{ItemsSource=result.Columns,SelectedIndex=0,Width=180};var y=new ComboBox{ItemsSource=result.Columns,SelectedIndex=Math.Min(1,result.Columns.Count-1),Width=180};
        _toolbar.Children.Add(new TextBlock{Text="X",VerticalAlignment=VerticalAlignment.Center});_toolbar.Children.Add(x);_toolbar.Children.Add(new TextBlock{Text="Y",VerticalAlignment=VerticalAlignment.Center});_toolbar.Children.Add(y);
        var zoom=new Slider{Minimum=1,Maximum=5,Value=1,Width=130};_toolbar.Children.Add(new TextBlock{Text="横向缩放",VerticalAlignment=VerticalAlignment.Center});_toolbar.Children.Add(zoom);
        void Draw(){
            Clear(1000*zoom.Value,580);if(x.SelectedIndex<0||y.SelectedIndex<0)return;
            bool Number(object? v,out double n){n=0;return v is not null&&double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&double.IsFinite(n);}
            double? Axis(object? v){if(Number(v,out var n))return n;if(DateTimeOffset.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date))return date.ToUnixTimeMilliseconds();return null;}
            var points=result.Rows.Select((r,i)=>(X:Axis(r[x.SelectedIndex]),Y:Number(r[y.SelectedIndex],out var n)?(double?)n:null,Index:i)).ToArray();var valid=points.Where(p=>p.X.HasValue&&p.Y.HasValue).ToArray();
            if(valid.Length==0){_status.Text="所选列没有可绘制的数值或时间坐标。";return;}
            double minX=valid.Min(p=>p.X!.Value),maxX=valid.Max(p=>p.X!.Value),minY=valid.Min(p=>p.Y!.Value),maxY=valid.Max(p=>p.Y!.Value);if(minX==maxX)maxX=minX+1;if(minY==maxY){minY-=1;maxY+=1;}
            if(!double.IsFinite(maxX-minX)||!double.IsFinite(maxY-minY)||maxX==minX||maxY==minY){_status.Text="数值范围超出图表精度，请缩小查询范围。";return;}
            Point Position(double a,double b)=>new(90+(a-minX)/(maxX-minX)*(_width-140),500-(b-minY)/(maxY-minY)*440);
            for(int tick=0;tick<=5;tick++){double v=minY+(maxY-minY)*tick/5;var p=Position(minX,v);Line(90,p.Y,_width-50,p.Y,"#E0E6E2");Text(8,p.Y-8,v.ToString("G5",CultureInfo.InvariantCulture));}
            Line(90,60,90,500,"#617D74");Line(90,500,_width-50,500,"#617D74");Text(90,525,Convert.ToString(result.Rows[valid[0].Index][x.SelectedIndex],CultureInfo.InvariantCulture)??"");Text(_width-280,525,Convert.ToString(result.Rows[valid[^1].Index][x.SelectedIndex],CultureInfo.InvariantCulture)??"");
            Point? previous=null;foreach(var point in points){if(point.X is not {} a||point.Y is not {} b){previous=null;continue;}var p=Position(a,b);if(previous is {} last)Line(last.X,last.Y,p.X,p.Y,"#217B58",2);previous=p;
                var dot=new Ellipse{Width=6,Height=6,Fill=Brush.Parse("#217B58")};Canvas.SetLeft(dot,p.X-3);Canvas.SetTop(dot,p.Y-3);ToolTip.SetTip(dot,$"{result.Rows[point.Index][x.SelectedIndex]}\n{result.Columns[y.SelectedIndex]}: {b}");_canvas.Children.Add(dot);
                _svg.Append($"<circle cx=\"{N(p.X)}\" cy=\"{N(p.Y)}\" r=\"3\" fill=\"#217B58\"/>");
            }
            Text(90,15,result.Columns[y.SelectedIndex]+" / "+result.Columns[x.SelectedIndex],16);_status.Text=$"{valid.Length} 个有效数据点 · NULL 处断线 · 悬停查看值。图表使用当前查询结果"+(result.Truncated?"（已截断）。":"。");
        }
        x.SelectionChanged+=(_,_)=>Draw();y.SelectionChanged+=(_,_)=>Draw();zoom.PropertyChanged+=(_,e)=>{if(e.Property.Name==nameof(Slider.Value))Draw();};Draw();
    }
}
