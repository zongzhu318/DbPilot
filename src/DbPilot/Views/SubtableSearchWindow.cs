using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.ViewModels;

namespace DbPilot.Views;

public sealed class SubtableSearchWindow : Window
{
    private const int ResultLimit=100;
    private readonly ConnectionProfile _profile;
    private readonly string _database,_stable;
    private readonly TextBox _keyword=new(){Watermark="输入子表名称片段，自动搜索"};
    private readonly ListBox _results=new(){Classes={"flat"}};
    private readonly TextBlock _status=DialogUi.Status();
    private readonly Button _locate;
    private CancellationTokenSource? _request;
    private int _requestId;
    public SubtableSearchWindow(ConnectionProfile profile,string database,string stable)
    {
        _profile=MainViewModel.CloneForDatabase(profile,database);
        _database=database;_stable=stable;
        Title="搜索子表 · "+stable;Width=640;Height=560;MinWidth=520;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");DialogUi.EnableEscapeToClose(this);
        _status.Text="输入子表名称片段开始搜索。";
        _locate=DialogUi.Primary("定位",Choose,88);
        var hint=new TextBlock{Text="仅在该超级表范围内按名称模糊匹配，最多显示 100 条。",Classes={"hint"},Margin=new Thickness(0,6,0,10)};
        var body=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,*"),Margin=new Thickness(24,16,24,16)};
        body.Children.Add(_keyword);
        Grid.SetRow(hint,1);body.Children.Add(hint);
        Grid.SetRow(_results,2);body.Children.Add(_results);
        Content=DialogUi.Shell("搜索子表","超级表 "+stable+" · 服务端模糊搜索",body,DialogUi.Footer(_status,DialogUi.Secondary("取消",()=>Close(null),88),_locate));
        _keyword.TextChanged+=(_,_)=>QueueSearch();
        _keyword.KeyDown+=KeywordKeyDown;
        _results.KeyDown+=ResultKeyDown;
        _results.SelectionChanged+=(_,_)=>_locate.IsEnabled=_results.SelectedItem is string;
        _results.DoubleTapped+=(_,_)=>Choose();
        Opened+=(_,_)=>_keyword.Focus();
        Closed+=(_,_)=>_request?.Cancel();
    }
    private void QueueSearch()
    {
        _request?.Cancel();
        var text=_keyword.Text?.Trim()??"";
        _results.ItemsSource=null;_locate.IsEnabled=false;
        if(text.Length==0){_status.Text="输入子表名称片段开始搜索。";return;}
        var request=new CancellationTokenSource();_request=request;
        _=SearchAsync(text,++_requestId,request);
    }
    private async Task SearchAsync(string text,int requestId,CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(300,request.Token);
            await using var provider=DbProviderFactory.Create(_profile);
            if(provider is not IPagedObjectProvider paged)throw new InvalidOperationException("当前连接不支持服务端分页搜索。");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var page=await paged.TablesPageAsync(_database,_stable,text,0,ResultLimit,timeout.Token);
            request.Token.ThrowIfCancellationRequested();
            if(requestId!=_requestId)return;
            var items=page.Names.ToArray();
            _results.ItemsSource=items;
            if(items.Length==0)_status.Text="没有匹配的子表。";
            else if(page.HasMore)_status.Text=$"匹配结果超过 {ResultLimit} 条，仅显示前 {ResultLimit} 条，请细化关键词。";
            else _status.Text=$"找到 {items.Length} 张匹配子表，双击或按 Enter 定位。";
            if(items.Length>0)_results.SelectedIndex=0;
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(requestId==_requestId)DialogUi.Error(_status,"搜索失败："+ex.Message);}
        finally{if(ReferenceEquals(_request,request))_request=null;request.Dispose();}
    }
    private void KeywordKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter){e.Handled=true;Choose();}
        else if(e.Key==Key.Down&&_results.ItemCount>0){e.Handled=true;_results.Focus();_results.SelectedIndex=Math.Min(_results.SelectedIndex+1,_results.ItemCount-1);}
    }
    private void ResultKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter){e.Handled=true;Choose();}
    }
    private void Choose()
    {
        var name=_results.SelectedItem as string??_results.ItemsSource?.Cast<string>().FirstOrDefault();
        if(name is null){DialogUi.Error(_status,"请先选择匹配的子表。");return;}
        Close(new DbObject(_database,"",name,"子表",Parent:_stable));
    }
}
