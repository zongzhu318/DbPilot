using Avalonia.Controls;
using Avalonia.Data;

namespace DbStudio.Views;

// NULL is a display label, never an editor value to write back to the DataRow.
internal sealed class ResultTextColumn : DataGridTextColumn
{
    public int ColumnIndex { get; init; }
    protected override Control GenerateElement(DataGridCell cell,object dataItem)
    {
        var element=base.GenerateElement(cell,dataItem);
        if(element is TextBlock label)
            label.Bind(TextBlock.TextProperty,new Binding("["+ColumnIndex+"]"){Mode=BindingMode.OneWay,TargetNullValue="NULL"});
        return element;
    }
}
