using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbPilot.Services;

namespace DbPilot.Views;

// NULL is a display label, never an editor value to write back to the DataRow.
internal sealed class ResultTextColumn : DataGridTextColumn
{
    public int ColumnIndex { get; init; }
    public IValueConverter? DisplayConverter { get; init; }
    public bool Previewed { get; init; }
    protected override Control GenerateElement(DataGridCell cell,object dataItem)
    {
        var element=base.GenerateElement(cell,dataItem);
        if(element is TextBlock label)
        {
            label.MaxLines=1;label.TextTrimming=TextTrimming.CharacterEllipsis;
            label.Bind(TextBlock.TextProperty,new Binding("["+ColumnIndex+"]"){Mode=BindingMode.OneWay,TargetNullValue="NULL",Converter=DisplayConverter});
            if(Previewed)ToolTip.SetTip(label,$"大字段仅加载前 {LargeColumns.PreviewLength} 字符；使用“字段详情”或“复制”获取完整内容。");
        }
        return element;
    }
}
