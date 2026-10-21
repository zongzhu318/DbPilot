using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DbPilot.Models;
using DbPilot.Services;

namespace DbPilot.Views;

public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<Task> _save;
    private readonly bool _macOs=OperatingSystem.IsMacOS();
    private readonly Dictionary<string,string?> _bindings=new();
    private readonly Dictionary<string,Button> _gestureButtons=new();
    private readonly Dictionary<string,Button> _clearButtons=new();
    private readonly Dictionary<string,Button> _restoreButtons=new();
    private readonly TextBox _fontFamily;
    private readonly TextBox _fontSize;
    private readonly CheckBox _wordWrap=new(){Content="自动换行"};
    private readonly CheckBox _lineNumbers=new(){Content="显示行号"};
    private readonly CheckBox _continueOnError=new(){Content="脚本遇错后继续执行后续语句"};
    private readonly CheckBox _persistLogs=new(){Content="将任务日志保存到本地"};
    private readonly StackPanel _shortcutRows=new(){Spacing=0,Margin=new Thickness(0,0,0,28)};
    private readonly TextBlock _status=DialogUi.Status();
    private readonly Button _saveButton=new(){Content="保存设置",MinWidth=104};
    private string? _captureId;
    private bool _saving;
    private const string EditorIcon="M3,17.25V21h3.75L17.81,9.94l-3.75,-3.75L3,17.25zM20.71,7.04c0.39,-0.39 0.39,-1.02 0,-1.41l-2.34,-2.34c-0.39,-0.39 -1.02,-0.39 -1.41,0l-1.83,1.83 3.75,3.75 1.83,-1.83z";
    private const string ShortcutIcon="M20,5H4c-1.1,0 -1.99,0.9 -1.99,2L2,17c0,1.1 0.9,2 2,2h16c1.1,0 2,-0.9 2,-2V7c0,-1.1 -0.9,-2 -2,-2zM11,8h2v2h-2V8zM11,11h2v2h-2v-2zM8,8h2v2H8V8zM8,11h2v2H8v-2zM7,13H5v-2h2v2zM7,10H5V8h2v2zM16,17H8v-2h8v2zM16,13h-2v-2h2v2zM16,10h-2V8h2v2zM19,13h-2v-2h2v2zM19,10h-2V8h2v2z";

    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings settings,Func<Task> save)
    {
        _settings=settings;_save=save;
        _saveButton.Classes.Add("primary");
        foreach(var action in ShortcutCatalog.Actions)_bindings[action.Id]=ShortcutCatalog.EffectiveText(settings,action.Id);
        _fontFamily=new TextBox{Text=settings.FontFamily,Watermark="字体名称或逗号分隔的字体列表"};
        _fontSize=new TextBox{Text=settings.FontSize.ToString("0.#")};
        _wordWrap.IsChecked=settings.WordWrap;_lineNumbers.IsChecked=settings.LineNumbers;
        _continueOnError.IsChecked=settings.ContinueOnScriptError;_persistLogs.IsChecked=settings.PersistLogs;

        Title=$"设置 · {AppInfo.DisplayName}";Width=920;Height=720;MinWidth=780;MinHeight=580;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        var tabs=new TabControl{Classes={"nav"},ItemsSource=new[]
        {
            NavTab("编辑器",EditorIcon,BuildEditorTab()),
            NavTab("快捷键",ShortcutIcon,BuildShortcutTab())
        }};
        var cancel=DialogUi.Secondary("取消",Close,88);
        _saveButton.Click+=SaveSettings;
        var footer=DialogUi.Footer(_status,cancel,_saveButton);
        RefreshShortcutRows();
        var root=new Grid{RowDefinitions=new RowDefinitions("*,Auto")};
        root.Children.Add(tabs);
        Grid.SetRow(footer,1);root.Children.Add(footer);
        Content=root;
        AddHandler(KeyDownEvent,CaptureKeyDown,RoutingStrategies.Tunnel);
        DialogUi.EnableEscapeToClose(this);
        Closed+=(_,_)=>_captureId=null;
    }

    private Control BuildEditorTab()
    {
        var panel=new StackPanel{Width=640,Spacing=0,Margin=new Thickness(0,28,0,24),HorizontalAlignment=HorizontalAlignment.Center};
        panel.Children.Add(new TextBlock{Text="这些设置会应用到所有 SQL 查询页。",Classes={"hint"},Margin=new Thickness(0,0,0,24)});
        var form=new Grid{ColumnDefinitions=new ColumnDefinitions("140,*"),RowDefinitions=new RowDefinitions("Auto,Auto")};
        AddFormRow(form,0,"字体",_fontFamily,500);
        AddFormRow(form,1,"字号（10–30）",_fontSize,110);
        panel.Children.Add(form);
        panel.Children.Add(new Border{Classes={"dialogDivider"},Margin=new Thickness(0,10,0,22)});
        panel.Children.Add(new TextBlock{Text="行为",Classes={"sectionTitle"},Margin=new Thickness(0,0,0,14)});
        AddOptionRow(panel,_wordWrap);
        AddOptionRow(panel,_lineNumbers);
        AddOptionRow(panel,_continueOnError);
        AddOptionRow(panel,_persistLogs);
        return new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }

    private Control BuildShortcutTab()
    {
        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,*,Auto"),Margin=new Thickness(32,22,32,0)};
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        var hint=new TextBlock{Text="点击组合键后按下新按键即可修改，按 Esc 取消；需要 Ctrl/⌘、Alt 或 F1–F24 等功能键。",Classes={"hint"},MaxWidth=680,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        header.Children.Add(hint);
        var resetAll=new Button{Content="全部恢复默认",FontSize=12};
        resetAll.Click+=(_,_)=>{
            foreach(var action in ShortcutCatalog.Actions)_bindings[action.Id]=ShortcutCatalog.DefaultFor(action,_macOs);
            StopCapture();RefreshShortcutRows();SetStatus("已恢复全部默认快捷键，保存后生效。");
        };
        Grid.SetColumn(resetAll,1);header.Children.Add(resetAll);
        root.Children.Add(header);

        var columns=new Grid{ColumnDefinitions=new ColumnDefinitions("*,110,300"),Margin=new Thickness(8,18,8,8)};
        columns.Children.Add(new TextBlock{Text="命令",Classes={"hint"}});
        var scope=new TextBlock{Text="范围",Classes={"hint"}};Grid.SetColumn(scope,1);columns.Children.Add(scope);
        var keys=new TextBlock{Text="快捷键",Classes={"hint"},HorizontalAlignment=HorizontalAlignment.Left};Grid.SetColumn(keys,2);columns.Children.Add(keys);
        Grid.SetRow(columns,1);root.Children.Add(columns);

        foreach(var group in ShortcutCatalog.Actions.GroupBy(a=>a.Category))
        {
            _shortcutRows.Children.Add(new TextBlock{Text=group.Key,FontSize=11,FontWeight=FontWeight.SemiBold,Classes={"hint"},Margin=new Thickness(8,14,0,4)});
            foreach(var action in group)_shortcutRows.Children.Add(BuildShortcutRow(action));
        }
        var scroll=new ScrollViewer{Content=_shortcutRows,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Grid.SetRow(scroll,2);root.Children.Add(scroll);
        var fade=new Border{Classes={"scrollFade"},Margin=new Thickness(0,0,14,0)};
        Grid.SetRow(fade,2);root.Children.Add(fade);
        var hintBottom=new TextBlock{Text="改动会写入本地设置；冲突的组合不会被接受。",Classes={"hint"},Margin=new Thickness(8,10,0,14)};
        Grid.SetRow(hintBottom,3);root.Children.Add(hintBottom);
        return root;
    }

    private static TabItem NavTab(string title,string iconData,Control content)
    {
        var icon=new PathIcon{Data=Geometry.Parse(iconData),Width=18,Height=18,Classes={"navIcon"}};
        var label=new TextBlock{Text=title,FontSize=12,Classes={"navLabel"}};
        var header=new StackPanel{Spacing=5,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        header.Children.Add(icon);header.Children.Add(label);
        return new TabItem{Header=header,Content=content,Classes={"nav"}};
    }

    private Control BuildShortcutRow(ShortcutAction action)
    {
        var gesture=new Button{Width=190,FontSize=11,FontFamily=new FontFamily("Cascadia Code,Consolas,monospace"),HorizontalContentAlignment=HorizontalAlignment.Center,Padding=new Thickness(6,2),MinHeight=30};
        var clear=new Button{Content="清除",FontSize=11,Padding=new Thickness(8,2),MinWidth=48,MinHeight=30};
        var restore=new Button{Content="默认",FontSize=11,Padding=new Thickness(8,2),MinWidth=48,MinHeight=30};
        var defaultText=ShortcutCatalog.Describe(ShortcutCatalog.DefaultFor(action,_macOs),_macOs);
        ToolTip.SetTip(gesture,"点击后按下新的快捷键");
        ToolTip.SetTip(restore,"默认："+defaultText);
        gesture.Click+=(_,_)=>BeginCapture(action);
        clear.Click+=(_,_)=>{
            _bindings[action.Id]=null;StopCapture();RefreshShortcutRows();
            SetStatus($"已清除「{action.Title}」的快捷键，保存后生效。");
        };
        restore.Click+=(_,_)=>{
            var conflict=ShortcutCatalog.Conflict(_bindings,action.Id,ShortcutCatalog.DefaultFor(action,_macOs));
            if(conflict is not null){SetStatus($"默认组合已被「{conflict}」使用，请先修改该项。",true);return;}
            _bindings[action.Id]=ShortcutCatalog.DefaultFor(action,_macOs);StopCapture();RefreshShortcutRows();
            SetStatus($"「{action.Title}」已恢复默认快捷键。");
        };
        _gestureButtons[action.Id]=gesture;_clearButtons[action.Id]=clear;_restoreButtons[action.Id]=restore;

        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,110,300"),MinHeight=42};
        grid.Children.Add(new TextBlock{Text=action.Title,FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});
        var scope=new TextBlock{Text=ShortcutCatalog.ScopeLabel(action.Scope),FontSize=11,Classes={"hint"},VerticalAlignment=VerticalAlignment.Center};
        Grid.SetColumn(scope,1);grid.Children.Add(scope);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        buttons.Children.Add(gesture);buttons.Children.Add(clear);buttons.Children.Add(restore);
        Grid.SetColumn(buttons,2);grid.Children.Add(buttons);
        return new Border{Classes={"listRow"},Child=grid};
    }

    private void BeginCapture(ShortcutAction action)
    {
        _captureId=action.Id;SetStatus($"正在录入「{action.Title}」：请按下新的快捷键，Esc 取消。");
        RefreshShortcutRows();
        _gestureButtons[action.Id].Focus();
    }

    private void StopCapture()=>_captureId=null;

    private void SetStatus(string text,bool error=false)
    {
        if(error)DialogUi.Error(_status,text);else DialogUi.Info(_status,text);
    }

    private void RefreshShortcutRows()
    {
        foreach(var action in ShortcutCatalog.Actions)
        {
            var effective=_bindings.GetValueOrDefault(action.Id);
            bool capturing=_captureId==action.Id;
            var button=_gestureButtons[action.Id];
            button.Content=capturing?"请按下快捷键…":ShortcutCatalog.Describe(effective,_macOs);
            SetClass(button,"primary",capturing);
            _clearButtons[action.Id].IsEnabled=effective is not null;
            var restore=_restoreButtons[action.Id];
            restore.IsEnabled=!ShortcutCatalog.Same(effective,ShortcutCatalog.DefaultFor(action,_macOs));
        }
    }

    private static void SetClass(Button button,string name,bool value)
    {
        if(value){if(!button.Classes.Contains(name))button.Classes.Add(name);}
        else button.Classes.Remove(name);
    }

    private void CaptureKeyDown(object? sender,KeyEventArgs e)
    {
        if(_captureId is not {} id)return;
        if(e.Key==Key.Escape){e.Handled=true;StopCapture();RefreshShortcutRows();SetStatus("已取消快捷键录入。");return;}
        if(IsModifierKey(e.Key)){e.Handled=true;return;}
        e.Handled=true;
        var modifiers=e.KeyModifiers&(KeyModifiers.Control|KeyModifiers.Alt|KeyModifiers.Shift|KeyModifiers.Meta);
        bool strong=modifiers.HasFlag(KeyModifiers.Control)||modifiers.HasFlag(KeyModifiers.Alt)||modifiers.HasFlag(KeyModifiers.Meta);
        if(!strong&&!IsFunctionKey(e.Key)){SetStatus("请同时按下 Ctrl/⌘、Alt，或使用 F1–F24 等功能键。",true);return;}
        var text=ShortcutCatalog.Canonical(e.Key,modifiers);
        if(ShortcutCatalog.Conflict(_bindings,id,text) is {} conflict){SetStatus($"该组合已被「{conflict}」使用，请换一个组合或先清除冲突项。",true);return;}
        var action=ShortcutCatalog.Find(id);
        _bindings[id]=text;StopCapture();RefreshShortcutRows();
        SetStatus($"「{action?.Title}」已设置为 {ShortcutCatalog.Describe(text,_macOs)}，保存后生效。");
    }

    private static bool IsModifierKey(Key key)=>key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static bool IsFunctionKey(Key key)=>(int)key>=(int)Key.F1&&(int)key<=(int)Key.F24;

    private async void SaveSettings(object? sender,RoutedEventArgs e)
    {
        if(_saving)return;
        if(string.IsNullOrWhiteSpace(_fontFamily.Text)){SetStatus("字体不能为空。",true);return;}
        if(!double.TryParse(_fontSize.Text,out var fontSize)||fontSize is <10 or >30){SetStatus("字号必须在 10–30 之间。",true);return;}
        var seen=new Dictionary<string,string>();
        foreach(var action in ShortcutCatalog.Actions)
        {
            var text=_bindings.GetValueOrDefault(action.Id);
            if(string.IsNullOrWhiteSpace(text))continue;
            if(ShortcutCatalog.Parse(text) is not {} gesture){SetStatus($"「{action.Title}」的快捷键无效，请重新录入。",true);return;}
            var canonical=ShortcutCatalog.Canonical(gesture);
            if(seen.TryGetValue(canonical,out var title)){SetStatus($"快捷键 {ShortcutCatalog.Describe(gesture,_macOs)} 同时用于「{title}」和「{action.Title}」，请修改其中一项。",true);return;}
            seen[canonical]=action.Title;
        }
        var shortcuts=new Dictionary<string,string>();
        foreach(var action in ShortcutCatalog.Actions)
        {
            var effective=_bindings.GetValueOrDefault(action.Id);
            var def=ShortcutCatalog.DefaultFor(action,_macOs);
            if(effective is null){if(def is not null)shortcuts[action.Id]="";continue;}
            if(ShortcutCatalog.Same(effective,def))continue;
            shortcuts[action.Id]=ShortcutCatalog.Parse(effective) is {} gesture?ShortcutCatalog.Canonical(gesture):effective;
        }
        _saving=true;_saveButton.IsEnabled=false;
        try
        {
            _settings.FontFamily=_fontFamily.Text.Trim();
            _settings.FontSize=fontSize;
            _settings.WordWrap=_wordWrap.IsChecked==true;
            _settings.LineNumbers=_lineNumbers.IsChecked==true;
            _settings.ContinueOnScriptError=_continueOnError.IsChecked==true;
            _settings.PersistLogs=_persistLogs.IsChecked==true;
            _settings.Shortcuts=shortcuts;
            await _save();
            Saved=true;Close();
        }
        catch(Exception ex){SetStatus("保存失败："+ex.Message,true);}
        finally{_saving=false;_saveButton.IsEnabled=true;}
    }

    private static void AddFormRow(Grid grid,int row,string label,Control control,double width)
    {
        control.Width=width;control.HorizontalAlignment=HorizontalAlignment.Left;
        control.Margin=new Thickness(0,0,0,14);
        var text=new TextBlock{Text=label,Classes={"fieldLabel"},Margin=new Thickness(0,0,20,14)};
        Grid.SetRow(text,row);Grid.SetColumn(text,0);grid.Children.Add(text);
        Grid.SetRow(control,row);Grid.SetColumn(control,1);grid.Children.Add(control);
    }

    private static void AddOptionRow(StackPanel panel,CheckBox option)
    {
        option.Margin=new Thickness(140,0,0,12);
        panel.Children.Add(option);
    }
}
