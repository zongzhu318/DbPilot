using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DbPilot.Views;

namespace DbPilot;
public sealed partial class App
{
    private void ConfigureMacMenu(Window mainWindow)
    {
        if(!OperatingSystem.IsMacOS())return;
        var win=(MainWindow)mainWindow;
        var appMenu=new NativeMenu();
        var about=new NativeMenuItem("关于 DbPilot");about.Click+=(_,_)=>_=ShowAbout(mainWindow);appMenu.Items.Add(about);
        appMenu.Items.Add(new NativeMenuItemSeparator());
        var settings=new NativeMenuItem("设置…"){Gesture=new KeyGesture(Key.OemComma,KeyModifiers.Meta)};
        settings.Click+=(_,_)=>win.ExecuteMenuAction("ShowSettings");appMenu.Items.Add(settings);
        appMenu.Items.Add(new NativeMenuItemSeparator());
        appMenu.Items.Add(AppAction("隐藏 DbPilot","Meta+H",()=>SendAppAction("hide:")));
        appMenu.Items.Add(AppAction("隐藏其他应用","Meta+Alt+H",()=>SendAppAction("hideOtherApplications:")));
        appMenu.Items.Add(AppAction("显示全部",null,()=>SendAppAction("unhideAllApplications:")));
        appMenu.Items.Add(new NativeMenuItemSeparator());
        var quit=new NativeMenuItem("退出 DbPilot"){Gesture=new KeyGesture(Key.Q,KeyModifiers.Meta)};
        quit.Click+=(_,_)=>mainWindow.Close();appMenu.Items.Add(quit);
        NativeMenu.SetMenu(this,appMenu);

        var menu=new NativeMenu();
        menu.Items.Add(SubMenu("文件",win,
            ("新建连接","NewConnection"),
            ("打开连接","OpenConnection"),
            ("关闭连接","CloseConnection"),
            null,
            ("编辑连接","EditConnection"),
            ("复制连接","CopyConnection"),
            ("删除连接","DeleteConnection"),
            null,
            ("新建查询","NewQuery"),
            ("打开脚本","OpenScript"),
            ("保存脚本","SaveScript"),
            null,
            ("查询历史","ShowHistory"),
            ("配置备份","WorkspaceBackup")));
        menu.Items.Add(SubMenu("查看",win,
            ("显示 / 隐藏侧栏","ToggleSidebar"),
            ("展开 / 收起 SQL 编辑器","ToggleEditor"),
            ("显示 / 隐藏日志","ToggleLogs"),
            ("专注模式","ToggleFocusMode"),
            null,
            ("切换主题","ToggleTheme")));
        menu.Items.Add(SubMenu("工具",win,
            ("新建数据库","CreateDatabase"),
            ("新建表","CreateTable"),
            null,
            ("SQL 模板","ShowTemplates"),
            ("执行计划","ExplainSql"),
            ("图表","OpenChart"),
            ("数据库工具","OpenDatabaseTools")));
        menu.Items.Add(SubMenu("帮助",win,
            ("检查更新","CheckForUpdates")));

        NativeMenu.SetMenu(win,menu);
    }
    internal static async Task ShowAbout(Window mainWindow)
    {
        var version=typeof(App).Assembly.GetName().Version?.ToString(3);
        var dialog=new Window{Title="关于 DbPilot",Width=360,Height=180,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Avalonia.Thickness(24),Spacing=12};
        panel.Children.Add(new TextBlock{Text="DbPilot",FontSize=22});
        panel.Children.Add(new TextBlock{Text=$"版本 {version} · 数据库工作台"});
        var close=new Button{Content="确定",HorizontalAlignment=HorizontalAlignment.Right};close.Click+=(_,_)=>dialog.Close();panel.Children.Add(close);
        dialog.Content=panel;await dialog.ShowDialog(mainWindow);
    }
    private static NativeMenuItem AppAction(string title,string? gesture,Action action)
    {
        var item=new NativeMenuItem(title);
        if(gesture is not null)item.Gesture=KeyGesture.Parse(gesture);
        item.Click+=(_,_)=>action();return item;
    }
    private static NativeMenuItem SubMenu(string title,MainWindow win,params object?[] items)
    {
        var item=new NativeMenuItem(title);
        var sub=new NativeMenu();item.Menu=sub;
        foreach(var entry in items)
        {
            if(entry is null){sub.Items.Add(new NativeMenuItemSeparator());continue;}
            var (label,action)=((string,string))entry;
            var child=new NativeMenuItem(label);child.Click+=(_,_)=>win.ExecuteMenuAction(action);sub.Items.Add(child);
        }
        return item;
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
