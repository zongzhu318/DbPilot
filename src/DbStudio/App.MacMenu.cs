using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace DbStudio;
public sealed partial class App
{
    private void ConfigureMacMenu(Window mainWindow)
    {
        if(!OperatingSystem.IsMacOS())return;
        var menu=new NativeMenu();
        var about=new NativeMenuItem("关于 DbStudio");
        about.Click+=async(_,_)=>{
            var version=typeof(App).Assembly.GetName().Version?.ToString(3);
            var dialog=new Window{Title="关于 DbStudio",Width=360,Height=180,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var panel=new StackPanel{Margin=new Avalonia.Thickness(24),Spacing=12};
            panel.Children.Add(new TextBlock{Text="DbStudio",FontSize=22});
            panel.Children.Add(new TextBlock{Text=$"版本 {version} · 数据库工作台"});
            var close=new Button{Content="确定",HorizontalAlignment=HorizontalAlignment.Right};close.Click+=(_,_)=>dialog.Close();panel.Children.Add(close);
            dialog.Content=panel;await dialog.ShowDialog(mainWindow);
        };
        menu.Items.Add(about);menu.Items.Add(new NativeMenuItemSeparator());
        AddAction(menu,"隐藏 DbStudio","Meta+H",()=>SendAppAction("hide:"));
        AddAction(menu,"隐藏其他应用","Meta+Alt+H",()=>SendAppAction("hideOtherApplications:"));
        AddAction(menu,"显示全部",null,()=>SendAppAction("unhideAllApplications:"));
        menu.Items.Add(new NativeMenuItemSeparator());
        AddAction(menu,"退出 DbStudio","Meta+Q",mainWindow.Close);
        NativeMenu.SetMenu(this,menu);
    }
    private static void AddAction(NativeMenu menu,string title,string? gesture,Action action)
    {
        var item=new NativeMenuItem(title);
        if(gesture is not null)item.Gesture=KeyGesture.Parse(gesture);
        item.Click+=(_,_)=>action();menu.Items.Add(item);
    }
    private static void SendAppAction(string selector)
    {
        var app=Send(GetClass("NSApplication"),GetSelector("sharedApplication"));
        SendWithArgument(app,GetSelector(selector),IntPtr.Zero);
    }
    [DllImport("/usr/lib/libobjc.A.dylib",EntryPoint="objc_getClass")]
    private static extern IntPtr GetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib",EntryPoint="sel_registerName")]
    private static extern IntPtr GetSelector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib",EntryPoint="objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver,IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib",EntryPoint="objc_msgSend")]
    private static extern void SendWithArgument(IntPtr receiver,IntPtr selector,IntPtr argument);
}
