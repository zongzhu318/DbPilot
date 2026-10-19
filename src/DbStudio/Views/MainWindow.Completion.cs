using AvaloniaEdit.CodeCompletion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;

namespace DbStudio.Views;
public sealed partial class MainWindow
{
    private CancellationTokenSource? _completionRequest;
    private readonly Dictionary<string,(DateTime Time,IReadOnlyList<DbObject> Objects)> _objectSuggestions=new();
    private readonly Dictionary<string,(DateTime Time,IReadOnlyList<ColumnInfo> Columns)> _columnSuggestions=new();
    private void InitializeCompletion()
    {
        SqlEditor.TextArea.TextEntered+=async(_,e)=>{
            if(_loading||string.IsNullOrEmpty(e.Text))return;
            if(e.Text.Any(c=>SqlCompletionContext.IsIdentifier(c)||c is '.' or ',' or '('||char.IsWhiteSpace(c)))await ShowCompletion(true);
        };
        SqlEditor.TextArea.TextEntering+=(_,e)=>{
            if(_completion is null||string.IsNullOrEmpty(e.Text))return;
            if(e.Text=="."){_completion.Close();return;}
            if(!SqlCompletionContext.IsIdentifier(e.Text[0]))_completion.CompletionList.RequestInsertion(e);
        };
        SqlEditor.TextChanged+=(_,_)=>{_completionRequest?.Cancel();};
        SqlEditor.TextArea.Caret.PositionChanged+=(_,_)=>_completionRequest?.Cancel();
        Closed+=(_,_)=>CancelCompletion();
    }
    private void CancelCompletion(){_completionRequest?.Cancel();_completion?.Close();}
    private async Task ShowCompletion(bool automatic=false)
    {
        if(_completion is not null)return;
        string sql=SqlEditor.Text;int caret=SqlEditor.CaretOffset;var tab=_vm.Tab;
        var context=SqlCompletionContext.Read(sql,caret);if(context is null)return;
        bool contextual=context.Prefix.Length==0&&(context.TablePosition||context.ColumnPosition)&&caret>0&&(char.IsWhiteSpace(sql[caret-1])||sql[caret-1] is ',' or '(');
        if(automatic&&context.Prefix.Length<2&&context.Qualifier.Length==0&&!contextual)return;
        _completionRequest?.Cancel();using var request=new CancellationTokenSource();_completionRequest=request;
        var ct=request.Token;
        try{
            if(automatic)await Task.Delay(250,ct);
            var profile=tab?.Connection??_vm.Connection;var kind=profile?.Kind??DatabaseKind.MySQL;
            var words=new Dictionary<string,(string Description,string Insert)>(StringComparer.OrdinalIgnoreCase);
            void Add(string name,string description,string? insert=null)=>words.TryAdd(name,(description,insert??name));
            if(context.Qualifier.Length==0&&!context.TablePosition)foreach(var word in SqlCompletionContext.Keywords(kind))Add(word,"SQL 关键字 / 函数");
            if(profile is not null&&!_vm.IsClosed(profile)&&!string.IsNullOrWhiteSpace(profile.Database)){
                // Separate background metadata requests from query execution and its Busy/cancellation state.
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(4));
                try{
                    await using var provider=DbProviderFactory.Create(profile);
                    var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile)));
                    if(!_objectSuggestions.TryGetValue(key,out var cached)||DateTime.UtcNow-cached.Time>TimeSpan.FromSeconds(60)){
                        cached=(DateTime.UtcNow,await provider.ObjectsAsync(profile.Database,deadline.Token));
                        if(_objectSuggestions.Count>20){_objectSuggestions.Clear();_columnSuggestions.Clear();}_objectSuggestions[key]=cached;
                    }
                    string Insert(string name)=>name.All(SqlCompletionContext.IsIdentifier)&&!char.IsDigit(name[0])&&name==name.ToLowerInvariant()&&!SqlCompletionContext.Keywords(kind).Contains(name,StringComparer.OrdinalIgnoreCase)?name:provider.Quote(name);
                    var objects=cached.Objects.Where(o=>o.Kind is "表" or "视图" or "普通表" or "子表" or "超级表").ToArray();
                    var refs=SqlReferences.Read(context.Statement);
                    if(context.Qualifier.Length==0){
                        if(!context.ColumnPosition)foreach(var obj in objects)Add(obj.Name,$"{obj.Kind} · {obj.Schema}",Insert(obj.Name));
                        if(!context.TablePosition)foreach(var reference in refs)Add(reference.Alias,"表别名");
                    }else{
                        foreach(var obj in objects.Where(o=>o.Schema.Equals(context.Qualifier,StringComparison.OrdinalIgnoreCase)||o.Database.Equals(context.Qualifier,StringComparison.OrdinalIgnoreCase)))Add(obj.Name,obj.Kind,Insert(obj.Name));
                    }
                    if(!context.TablePosition){
                        var targets=new List<(DbObject Table,string Alias)>();
                        foreach(var reference in refs){
                            if(context.Qualifier.Length>0&&!reference.Alias.Equals(context.Qualifier,StringComparison.OrdinalIgnoreCase)&&!reference.Name.Equals(context.Qualifier,StringComparison.OrdinalIgnoreCase))continue;
                            var matches=objects.Where(o=>o.Name.Equals(reference.Name,StringComparison.OrdinalIgnoreCase)&&(reference.Schema.Length==0||o.Schema.Equals(reference.Schema,StringComparison.OrdinalIgnoreCase)||o.Database.Equals(reference.Schema,StringComparison.OrdinalIgnoreCase))).ToArray();
                            if(matches.Length==1)targets.Add((matches[0],reference.Alias));
                        }
                        if(targets.Count==0&&tab?.Table is {} table&&(context.Qualifier.Length==0||context.Qualifier.Equals(table.Name,StringComparison.OrdinalIgnoreCase)))targets.Add((table,table.Name));
                        foreach(var target in targets.Take(12)){
                            string columnKey=key+"|"+target.Table.Schema+"|"+target.Table.Name;
                            if(!_columnSuggestions.TryGetValue(columnKey,out var columns)||DateTime.UtcNow-columns.Time>TimeSpan.FromSeconds(60)){
                                columns=(DateTime.UtcNow,await provider.ColumnsAsync(target.Table,deadline.Token));_columnSuggestions[columnKey]=columns;
                            }
                            foreach(var column in columns.Columns)Add(column.Name,$"{target.Alias} · {column.Type}{(column.IsTag?" · 标签":"")} · {column.Comment}",Insert(column.Name));
                        }
                    }
                }catch(OperationCanceledException) when(!ct.IsCancellationRequested){}catch(Exception ex) when(!ct.IsCancellationRequested){if(!automatic)_vm.Log("补全元数据暂不可用："+ex.Message);}
            }
            ct.ThrowIfCancellationRequested();
            if(_vm.Tab!=tab||(tab?.Connection??_vm.Connection)!=profile||SqlEditor.CaretOffset!=caret||SqlEditor.Text!=sql||_completion is not null||automatic&&!SqlEditor.IsKeyboardFocusWithin)return;
            var entries=words.Where(p=>p.Key.StartsWith(context.Prefix,StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p.Key).Take(300).ToArray();if(entries.Length==0)return;
            var window=new CompletionWindow(SqlEditor.TextArea){StartOffset=context.Start};_completion=window;
            // Bind directly to the completion item: the bundled theme's nested Content binding renders blank rows.
            window.CompletionList.ListBox.ItemTemplate=new FuncDataTemplate<ICompletionData>((item,_)=>new TextBlock{
                Text=item?.Text,FontSize=12,Margin=new Thickness(8,3),VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center
            });
            foreach(var entry in entries)window.CompletionList.CompletionData.Add(new SqlCompletionData(entry.Key,entry.Value.Description,entry.Value.Insert));
            window.Closed+=(_,_)=>{if(_completion==window)_completion=null;};window.Show();window.CompletionList.SelectItem(context.Prefix);
        }catch(OperationCanceledException){}
        finally{if(_completionRequest==request)_completionRequest=null;}
    }
}
