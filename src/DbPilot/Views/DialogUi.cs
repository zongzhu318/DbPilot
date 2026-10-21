using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DbPilot.Views;

internal static class DialogUi
{
    public static Border Header(string title,string? subtitle=null)
    {
        var stack=new StackPanel{Spacing=4};
        stack.Children.Add(new TextBlock{Text=title,Classes={"dialogTitle"}});
        if(!string.IsNullOrWhiteSpace(subtitle))stack.Children.Add(new TextBlock{Text=subtitle,Classes={"dialogSubtitle"}});
        return new Border{Classes={"dialogHeader"},Child=stack};
    }

    public static Border Footer(Control? left,params Control[] actions)
    {
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        if(left is not null){left.VerticalAlignment=VerticalAlignment.Center;left.Margin=new Thickness(0,0,16,0);grid.Children.Add(left);}
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        foreach(var action in actions)buttons.Children.Add(action);
        Grid.SetColumn(buttons,1);grid.Children.Add(buttons);
        return new Border{Classes={"dialogFooter"},Child=grid};
    }

    public static Grid Shell(string title,string? subtitle,Control body,Control footer)
    {
        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,*,Auto")};
        var header=Header(title,subtitle);root.Children.Add(header);
        Grid.SetRow(body,1);root.Children.Add(body);
        Grid.SetRow(footer,2);root.Children.Add(footer);
        return root;
    }

    public static Control Field(string label,Control control,double labelWidth=150,double controlWidth=0)
    {
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions($"{labelWidth:0},*"),Margin=new Thickness(0,0,0,12)};
        grid.Children.Add(new TextBlock{Text=label,Classes={"fieldLabel"},Margin=new Thickness(0,0,16,0)});
        if(controlWidth>0){control.Width=controlWidth;control.HorizontalAlignment=HorizontalAlignment.Left;}
        Grid.SetColumn(control,1);grid.Children.Add(control);
        return grid;
    }

    public static Control Option(CheckBox option,double labelWidth=150)
    {
        option.Margin=new Thickness(0,0,0,6);
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions($"{labelWidth:0},*")};
        Grid.SetColumn(option,1);grid.Children.Add(option);
        return grid;
    }

    public static Border Divider()=>new(){Classes={"dialogDivider"}};

    public static TextBlock Status()=>new(){Classes={"hint"},VerticalAlignment=VerticalAlignment.Center};

    public static Button Primary(string text,Action click,double minWidth=0)
    {
        var button=new Button{Content=text,MinWidth=minWidth};
        button.Classes.Add("primary");
        button.Click+=(_,_)=>click();
        return button;
    }

    public static Button Secondary(string text,Action click,double minWidth=0)
    {
        var button=new Button{Content=text,MinWidth=minWidth};
        button.Click+=(_,_)=>click();
        return button;
    }

    public static Button Danger(string text,Action click)
    {
        var button=new Button{Content=text};
        button.Classes.Add("danger");
        button.Click+=(_,_)=>click();
        return button;
    }

    public static void Info(TextBlock status,string text)
    {
        status.Text=text;
        status.Classes.Remove("error");
    }

    public static void Error(TextBlock status,string text)
    {
        status.Text=text;
        if(!status.Classes.Contains("error"))status.Classes.Add("error");
    }

    public static Window Dialog(string title,double width,double? height=null,double minWidth=0,double minHeight=0)
    {
        var window=new Window{Title=title,Width=width,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        if(height is {} h)window.Height=h;
        if(minWidth>0)window.MinWidth=minWidth;
        if(minHeight>0)window.MinHeight=minHeight;
        window.Classes.Add("dialog");
        return window;
    }

    public static async Task<bool> ConfirmAsync(Window owner,string message,string confirmText="确认",string title="确认操作")
    {
        var dialog=Dialog(title,480);
        dialog.SizeToContent=SizeToContent.Height;
        dialog.CanResize=false;
        var body=new Border{Padding=new Thickness(24,20,24,12),Child=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap,LineHeight=20}};
        var cancel=Secondary("取消",()=>dialog.Close(false),88);
        Button confirm=confirmText.Contains("删除")||confirmText.Contains("放弃")?Danger(confirmText,()=>dialog.Close(true)):Primary(confirmText,()=>dialog.Close(true),88);
        dialog.Content=Shell(title,null,body,Footer(null,cancel,confirm));
        return await dialog.ShowDialog<bool>(owner);
    }
}
