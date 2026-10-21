using System.Text.Json;
using Avalonia.Input;
using DbPilot.Models;

namespace DbPilot.Services;

public enum ShortcutScope { Global, Editor, Tree, Grid, Browse }

public sealed record ShortcutAction(string Id,string Category,string Title,string? WindowsDefault,string? MacDefault,ShortcutScope Scope=ShortcutScope.Global);

public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new("connection.new","连接","新建连接","Ctrl+Shift+N","Meta+Shift+N"),
        new("connection.open","连接","打开连接","Ctrl+Shift+O","Meta+Shift+O"),
        new("connection.close","连接","关闭连接","Ctrl+Shift+D","Meta+Shift+D"),
        new("connection.edit","连接","编辑连接","Ctrl+E","Meta+E",ShortcutScope.Tree),
        new("connection.copy","连接","复制连接","Ctrl+Shift+C","Meta+Shift+C",ShortcutScope.Tree),
        new("connection.delete","连接","删除连接",null,null,ShortcutScope.Tree),
        new("database.create","连接","新建数据库","Ctrl+Alt+N","Meta+Alt+N"),
        new("table.create","连接","新建表","Ctrl+N","Meta+N"),
        new("connection.backup","连接","配置备份",null,null),

        new("query.new","查询","新建查询","Ctrl+Q","Meta+Y"),
        new("query.open","查询","打开脚本","Ctrl+O","Meta+O"),
        new("query.save","查询","保存脚本","Ctrl+S","Meta+S"),
        new("query.close","查询","关闭查询","Ctrl+W","Meta+W"),
        new("query.execute","查询","执行 SQL","Ctrl+R","Meta+R"),
        new("query.executeSelection","查询","执行选中 SQL","Ctrl+Shift+R","Meta+Shift+R",ShortcutScope.Editor),
        new("query.stop","查询","终止任务","Ctrl+T","Meta+T"),
        new("query.format","查询","格式化 SQL","Ctrl+Shift+F","Meta+Shift+F"),
        new("query.complete","查询","SQL 补全","Ctrl+Space","Ctrl+Space",ShortcutScope.Editor),
        new("query.history","查询","查询历史","Ctrl+L","Meta+L"),
        new("query.templates","查询","SQL 模板","Ctrl+Alt+T","Meta+Alt+T"),
        new("query.explain","查询","执行计划","Ctrl+Shift+P","Meta+Shift+P"),
        new("query.export","查询","导出结果","Ctrl+Shift+E","Meta+Shift+E"),

        new("view.sidebar","视图","显示 / 隐藏侧栏","F6","F6"),
        new("view.editor","视图","展开 / 收起 SQL 编辑器","Ctrl+Alt+E","Meta+Alt+E"),
        new("view.logs","视图","显示 / 隐藏日志","Ctrl+Alt+L","Meta+Alt+L"),
        new("view.focus","视图","专注模式","F11","F11"),
        new("view.theme","视图","切换主题","Ctrl+Alt+D","Meta+Alt+D"),
        new("view.catalog","视图","表列表","F7","F7"),
        new("view.refresh","视图","刷新","F5","F5"),
        new("tabs.next","视图","下一个查询标签","Ctrl+Tab","Ctrl+Tab"),
        new("tabs.previous","视图","上一个查询标签","Ctrl+Shift+Tab","Ctrl+Shift+Tab"),

        new("data.filter","数据表","筛选条件","Ctrl+F","Meta+F",ShortcutScope.Browse),
        new("data.previousPage","数据表","上一页","Ctrl+PageUp","Ctrl+PageUp",ShortcutScope.Browse),
        new("data.nextPage","数据表","下一页","Ctrl+PageDown","Ctrl+PageDown",ShortcutScope.Browse),
        new("data.viewCell","数据表","字段详情","Alt+Enter","Alt+Enter",ShortcutScope.Grid),
        new("data.copyRow","数据表","复制行","Ctrl+D","Meta+D",ShortcutScope.Grid),
        new("data.addRow","数据表","新增行","Ctrl+Shift+A","Meta+Shift+A",ShortcutScope.Grid),
        new("data.deleteRow","数据表","删除选中行","Ctrl+Delete","Meta+Back",ShortcutScope.Grid),
        new("data.setNull","数据表","设为 NULL","Ctrl+D0","Meta+D0",ShortcutScope.Grid),
        new("data.commit","数据表","提交变更","Ctrl+Enter","Meta+Enter",ShortcutScope.Grid),
        new("data.revert","数据表","撤销变更","Ctrl+Shift+Z","Meta+Shift+Z",ShortcutScope.Grid),

        new("tools.chart","工具","图表","Ctrl+Alt+G","Meta+Alt+G"),
        new("tools.database","工具","数据库工具","Ctrl+Alt+B","Meta+Alt+B"),
        new("tools.settings","工具","设置","Ctrl+OemComma","Meta+OemComma"),
        new("tools.update","工具","检查更新",null,null),
        new("tools.about","工具","关于 DbPilot",null,null)
    ];

    private static readonly Dictionary<string,ShortcutAction> ById=Actions.ToDictionary(a=>a.Id);
    public static ShortcutAction? Find(string id)=>ById.GetValueOrDefault(id);
    public static string? DefaultFor(ShortcutAction action,bool macOs)=>macOs?action.MacDefault:action.WindowsDefault;
    public static string? DefaultFor(string id)=>ById.TryGetValue(id,out var action)?DefaultFor(action,OperatingSystem.IsMacOS()):null;
    public static string? DefaultFor(string id,bool macOs)=>ById.TryGetValue(id,out var action)?DefaultFor(action,macOs):null;

    public static string? EffectiveText(AppSettings settings,string id)
    {
        if(settings.Shortcuts.TryGetValue(id,out var custom))
        {
            if(string.IsNullOrWhiteSpace(custom))return null;
            if(Parse(custom) is not null)return custom.Trim();
        }
        return DefaultFor(id);
    }

    public static KeyGesture? EffectiveGesture(AppSettings settings,string id)=>Parse(EffectiveText(settings,id));

    public static IReadOnlyList<(ShortcutAction Action,KeyGesture Gesture)> ResolveAll(AppSettings settings)
    {
        var resolved=new List<(ShortcutAction,KeyGesture)>();
        foreach(var action in Actions)
            if(EffectiveGesture(settings,action.Id) is {} gesture)resolved.Add((action,gesture));
        return resolved;
    }

    public static KeyGesture? Parse(string? text)
    {
        if(string.IsNullOrWhiteSpace(text))return null;
        try{
            var gesture=KeyGesture.Parse(text);
            return gesture.Key==Key.None?null:gesture;
        }catch{return null;}
    }

    public static string Canonical(KeyGesture gesture)=>Canonical(gesture.Key,gesture.KeyModifiers);

    public static string Canonical(Key key,KeyModifiers modifiers)
    {
        var parts=new List<string>(5);
        if(modifiers.HasFlag(KeyModifiers.Meta))parts.Add("Meta");
        if(modifiers.HasFlag(KeyModifiers.Control))parts.Add("Control");
        if(modifiers.HasFlag(KeyModifiers.Alt))parts.Add("Alt");
        if(modifiers.HasFlag(KeyModifiers.Shift))parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+",parts);
    }

    public static bool Same(string? left,string? right)
    {
        if(string.IsNullOrWhiteSpace(left)||string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left)&&string.IsNullOrWhiteSpace(right);
        var leftGesture=Parse(left);var rightGesture=Parse(right);
        if(leftGesture is null||rightGesture is null)return string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
        return Canonical(leftGesture)==Canonical(rightGesture);
    }

    public static string? Conflict(IReadOnlyDictionary<string,string?> bindings,string actionId,string? gestureText)
    {
        var candidate=Parse(gestureText);
        if(candidate is null)return null;
        var canonical=Canonical(candidate);
        foreach(var action in Actions)
        {
            if(action.Id==actionId)continue;
            if(Parse(bindings.GetValueOrDefault(action.Id)) is {} other&&Canonical(other)==canonical)return action.Title;
        }
        return null;
    }

    public static string Describe(string? text,bool? macOs=null)
    {
        if(string.IsNullOrWhiteSpace(text))return "未设置";
        return Parse(text) is {} gesture?Describe(gesture,macOs??OperatingSystem.IsMacOS()):"无效快捷键";
    }

    public static string Describe(KeyGesture gesture)=>Describe(gesture,OperatingSystem.IsMacOS());

    public static string Describe(KeyGesture gesture,bool macOs)
    {
        var key=KeyLabel(gesture.Key,macOs);
        if(macOs)
        {
            var prefix="";
            if(gesture.KeyModifiers.HasFlag(KeyModifiers.Control))prefix+="⌃";
            if(gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))prefix+="⌥";
            if(gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))prefix+="⇧";
            if(gesture.KeyModifiers.HasFlag(KeyModifiers.Meta))prefix+="⌘";
            return prefix+key;
        }
        var parts=new List<string>(5);
        if(gesture.KeyModifiers.HasFlag(KeyModifiers.Control))parts.Add("Ctrl");
        if(gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))parts.Add("Alt");
        if(gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))parts.Add("Shift");
        if(gesture.KeyModifiers.HasFlag(KeyModifiers.Meta))parts.Add("Win");
        parts.Add(key);
        return string.Join("+",parts);
    }

    public static string ScopeLabel(ShortcutScope scope)=>scope switch
    {
        ShortcutScope.Editor=>"SQL 编辑器",
        ShortcutScope.Tree=>"连接树",
        ShortcutScope.Grid=>"数据表格",
        ShortcutScope.Browse=>"表浏览",
        _=>"全局"
    };

    public static void MigrateLegacy(AppSettings settings,string json)
    {
        if(string.IsNullOrWhiteSpace(json))return;
        settings.Shortcuts??=new();
        try
        {
            using var document=JsonDocument.Parse(json);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return;
            Migrate(root,"ExecuteGesture","query.execute","Ctrl+Enter");
            Migrate(root,"CompletionGesture","query.complete","Ctrl+Space");
        }
        catch(JsonException){}

        void Migrate(JsonElement root,string property,string actionId,string oldDefault)
        {
            if(settings.Shortcuts.ContainsKey(actionId))return;
            if(!root.TryGetProperty(property,out var element)||element.ValueKind!=JsonValueKind.String)return;
            var value=element.GetString();
            if(string.IsNullOrWhiteSpace(value)||Same(value,oldDefault)||Parse(value) is null)return;
            settings.Shortcuts[actionId]=value;
        }
    }

    private static string KeyLabel(Key key,bool macOs)=>key switch
    {
        Key.D0 or Key.NumPad0=>"0",
        Key.D1 or Key.NumPad1=>"1",
        Key.D2 or Key.NumPad2=>"2",
        Key.D3 or Key.NumPad3=>"3",
        Key.D4 or Key.NumPad4=>"4",
        Key.D5 or Key.NumPad5=>"5",
        Key.D6 or Key.NumPad6=>"6",
        Key.D7 or Key.NumPad7=>"7",
        Key.D8 or Key.NumPad8=>"8",
        Key.D9 or Key.NumPad9=>"9",
        Key.OemComma=>",",
        Key.OemPeriod=>".",
        Key.OemPlus=>"+",
        Key.OemMinus=>"-",
        Key.OemQuestion=>"/",
        Key.OemSemicolon=>";",
        Key.OemQuotes=>"'",
        Key.OemOpenBrackets=>"[",
        Key.OemCloseBrackets=>"]",
        Key.OemPipe=>"\\",
        Key.OemTilde=>"`",
        Key.Space=>"Space",
        Key.Return=>macOs?"↩":"Enter",
        Key.Tab=>macOs?"⇥":"Tab",
        Key.Back=>macOs?"⌫":"Backspace",
        Key.Delete=>macOs?"⌦":"Delete",
        Key.Escape=>macOs?"⎋":"Esc",
        Key.PageUp=>macOs?"⇞":"PageUp",
        Key.PageDown=>macOs?"⇟":"PageDown",
        Key.Left=>macOs?"←":"Left",
        Key.Right=>macOs?"→":"Right",
        Key.Up=>macOs?"↑":"Up",
        Key.Down=>macOs?"↓":"Down",
        _=>key.ToString()
    };
}
