using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DbPilot.Views;

namespace DbPilot;
public sealed partial class App
{
    private static NativeMenuItem? _macSettingsItem;
    private static readonly Dictionary<string,NativeMenuItem> _macActionItems = new();
    private static bool _macMenuBuilt;
    // macOS 应用菜单由 Avalonia 在 AppBuilder.AfterSetup 阶段固化，之后设置 Application 菜单不会再同步，
    // 因此必须在 Initialize 阶段安装；主窗口创建后再单独安装窗口菜单。
    private void ConfigureMacAppMenu()
    {
        NativeMenu.SetMenu(this,BuildAppMenu());
    }
    private void ConfigureMacWindowMenu(Window mainWindow)
    {
        if(!OperatingSystem.IsMacOS()||mainWindow is not MainWindow win)return;
        BuildMacWindowMenu(win);
    }
    private static void BuildMacWindowMenu(MainWindow win)
    {
        _macActionItems.Clear();
        NativeMenu.SetMenu(win,BuildWindowMenu(win));
        _macMenuBuilt=true;
    }
    internal static void RefreshMacMenu(Window mainWindow)
    {
        if(!OperatingSystem.IsMacOS()||mainWindow is not MainWindow win)return;
        if(!_macMenuBuilt){BuildMacWindowMenu(win);return;}
        if(_macSettingsItem is not null)_macSettingsItem.Gesture=win.NativeShortcutGesture("tools.settings");
        foreach(var (actionId,item) in _macActionItems)item.Gesture=win.NativeShortcutGesture(actionId);
    }
    private static NativeMenu BuildAppMenu()
    {
        var appMenu=new NativeMenu();
        var about=new NativeMenuItem($"关于 {AppInfo.DisplayName}");
        about.Click+=(_,_)=>_=ShowAbout(CurrentMainWindow());
        appMenu.Items.Add(about);
        appMenu.Items.Add(new NativeMenuItemSeparator());
        var settings=new NativeMenuItem("设置…");
        settings.Click+=(_,_)=>CurrentMainWindow()?.ExecuteMenuAction("ShowSettings");
        appMenu.Items.Add(settings);_macSettingsItem=settings;
        appMenu.Items.Add(new NativeMenuItemSeparator());
        appMenu.Items.Add(AppAction($"隐藏 {AppInfo.DisplayName}","Meta+H",()=>SendAppAction("hide:")));
        appMenu.Items.Add(AppAction("隐藏其他应用","Meta+Alt+H",()=>SendAppAction("hideOtherApplications:")));
        appMenu.Items.Add(AppAction("显示全部",null,()=>SendAppAction("unhideAllApplications:")));
        appMenu.Items.Add(new NativeMenuItemSeparator());
        var quit=new NativeMenuItem($"退出 {AppInfo.DisplayName}"){Gesture=new KeyGesture(Key.Q,KeyModifiers.Meta)};
        quit.Click+=(_,_)=>CurrentMainWindow()?.RequestQuit();appMenu.Items.Add(quit);
        return appMenu;
    }
    private static MainWindow? CurrentMainWindow()=>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
    private static NativeMenu BuildWindowMenu(MainWindow win)
    {
        var menu=new NativeMenu();
        menu.Items.Add(SubMenu("文件",win,
            ("新建连接","connection.new","NewConnection"),
            ("打开连接","connection.open","OpenConnection"),
            ("关闭连接","connection.close","CloseConnection"),
            null,
            ("编辑连接","connection.edit","EditConnection"),
            ("复制连接","connection.copy","CopyConnection"),
            ("删除连接","connection.delete","DeleteConnection"),
            null,
            ("新建查询","query.new","NewQuery"),
            ("打开脚本","query.open","OpenScript"),
            ("保存脚本","query.save","SaveScript"),
            null,
            ("查询历史","query.history","ShowHistory"),
            ("配置备份","connection.backup","WorkspaceBackup")));
        menu.Items.Add(SubMenu("查询",win,
            ("执行 SQL","query.execute","RunSql"),
            ("执行选中 SQL","query.executeSelection","RunSelectionSql"),
            ("终止任务","query.stop","StopSql"),
            ("关闭查询","query.close","CloseTab"),
            null,
            ("格式化 SQL","query.format","FormatSql"),
            ("SQL 补全","query.complete","ShowCompletionMenu"),
            null,
            ("执行计划","query.explain","ExplainSql"),
            ("导出结果","query.export","ExportResult")));
        menu.Items.Add(SubMenu("查看",win,
            ("显示 / 隐藏侧栏","view.sidebar","ToggleSidebar"),
            ("展开 / 收起 SQL 编辑器","view.editor","ToggleEditor"),
            ("显示 / 隐藏日志","view.logs","ToggleLogs"),
            ("专注模式","view.focus","ToggleFocusMode"),
            null,
            ("表列表","view.catalog","ShowCatalog"),
            ("刷新","view.refresh","RefreshCurrent"),
            ("切换主题","view.theme","ToggleTheme")));
        menu.Items.Add(SubMenu("工具",win,
            ("新建数据库","database.create","CreateDatabase"),
            ("新建表","table.create","CreateTable"),
            null,
            ("SQL 模板","query.templates","ShowTemplates"),
            ("图表","tools.chart","OpenChart"),
            ("数据库工具","tools.database","OpenDatabaseTools")));
        menu.Items.Add(SubMenu("帮助",win,
            ("检查更新","tools.update","CheckForUpdates")));
        return menu;
    }
    internal static async Task ShowAbout(Window? mainWindow)
    {
        var version=AppInfo.Version;
        var dialog=DialogUi.Dialog($"关于 {AppInfo.DisplayName}",460);
        dialog.SizeToContent=SizeToContent.Height;dialog.CanResize=false;
        var logo=new Bitmap(AssetLoader.Open(new Uri("avares://DbPilot/Assets/AppIcon.png")));
        dialog.Closed+=(_,_)=>logo.Dispose();

        var iconTile=new Border{Width=112,Height=112,CornerRadius=new CornerRadius(26),Padding=new Thickness(12),HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,18)};
        iconTile.Classes.Add("flatPanel");
        iconTile.Child=new Image{Source=logo,Stretch=Stretch.Uniform};
        var name=new TextBlock{Text=AppInfo.DisplayName,FontSize=26,FontWeight=FontWeight.SemiBold,HorizontalAlignment=HorizontalAlignment.Center};
        var tagline=new TextBlock{Text=AppInfo.IsDevelopment?"数据库工作台 · 开发版":"数据库工作台",FontSize=13,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,6,0,16)};
        tagline.Classes.Add("hint");
        var pill=new Border{CornerRadius=new CornerRadius(11),Padding=new Thickness(12,4),HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,22)};
        pill.Classes.Add("flatPanel");
        pill.Child=new TextBlock{Text=$"版本 {version}",FontSize=11.5,Classes={"hint"}};
        var description=new TextBlock{Text="轻量、快速的数据库管理与 SQL 查询工作台。",FontSize=12,MaxWidth=320,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,HorizontalAlignment=HorizontalAlignment.Center};
        description.Classes.Add("hint");
        var body=new StackPanel{Margin=new Thickness(32,36,32,30)};
        body.Children.Add(iconTile);
        body.Children.Add(name);
        body.Children.Add(tagline);
        body.Children.Add(pill);
        body.Children.Add(description);

        var copyright=new TextBlock{Text=$"© {DateTime.Now.Year} DbPilot",FontSize=11,VerticalAlignment=VerticalAlignment.Center};
        copyright.Classes.Add("hint");
        var ok=DialogUi.Primary("好",()=>dialog.Close(),96);
        ok.MinHeight=32;ok.FontSize=13;ok.FontWeight=FontWeight.Medium;
        ok.HorizontalContentAlignment=HorizontalAlignment.Center;ok.VerticalContentAlignment=VerticalAlignment.Center;

        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto")};
        root.Children.Add(body);
        var footer=DialogUi.Footer(copyright,ok);
        Grid.SetRow(footer,1);root.Children.Add(footer);
        dialog.Content=root;
        if(mainWindow is not null)await dialog.ShowDialog(mainWindow);else dialog.Show();
    }
    // macOS 程序坞不读取 Window.Icon，且 bundle 图标会被 Dock 长时间缓存，运行期显式设置才能确保显示新图标。
    internal static void ApplyMacDockIcon()
    {
        if(!OperatingSystem.IsMacOS())return;
        using var stream=AssetLoader.Open(new Uri("avares://DbPilot/Assets/AppIcon.png"));
        using var buffer=new MemoryStream();
        stream.CopyTo(buffer);
        var bytes=buffer.ToArray();
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try
        {
            var data=Send(GetClass("NSData"),GetSelector("alloc"));
            data=SendBytes(data,GetSelector("initWithBytes:length:"),pinned.AddrOfPinnedObject(),(nuint)bytes.Length);
            var image=Send(GetClass("NSImage"),GetSelector("alloc"));
            image=SendObject(image,GetSelector("initWithData:"),data);
            var app=Send(GetClass("NSApplication"),GetSelector("sharedApplication"));
            SendWithArgument(app,GetSelector("setApplicationIconImage:"),image);
            Send(image,GetSelector("release"));
            Send(data,GetSelector("release"));
        }
        finally{pinned.Free();}
    }
    private static NativeMenuItem AppAction(string title,string? gesture,Action action)
    {
        var item=new NativeMenuItem(title);
        if(gesture is not null)item.Gesture=KeyGesture.Parse(gesture);
        item.Click+=(_,_)=>action();return item;
    }
    private static NativeMenuItem SubMenu(string title,MainWindow win,params (string Label,string ActionId,string Action)?[] items)
    {
        var item=new NativeMenuItem(title);
        var sub=new NativeMenu();item.Menu=sub;
        foreach(var entry in items)
        {
            if(entry is null){sub.Items.Add(new NativeMenuItemSeparator());continue;}
            var (label,actionId,action)=entry.Value;
            var child=new NativeMenuItem(label){Gesture=win.NativeShortcutGesture(actionId)};
            child.Click+=(_,_)=>win.ExecuteMenuAction(action);sub.Items.Add(child);_macActionItems[actionId]=child;
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
    [DllImport("/usr/lib/libobjc.A.dylib",EntryPoint="objc_msgSend")]
    private static extern IntPtr SendObject(IntPtr receiver,IntPtr selector,IntPtr argument);
    [DllImport("/usr/lib/libobjc.A.dylib",EntryPoint="objc_msgSend")]
    private static extern IntPtr SendBytes(IntPtr receiver,IntPtr selector,IntPtr bytes,nuint length);
}
