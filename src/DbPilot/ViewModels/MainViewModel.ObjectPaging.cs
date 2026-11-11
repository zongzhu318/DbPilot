using DbPilot.Models;
using DbPilot.Providers;

namespace DbPilot.ViewModels;
public sealed partial class MainViewModel
{
    public const int ObjectPageSize=200;
    private static readonly TimeSpan ObjectPageTimeout=TimeSpan.FromSeconds(15);
    private readonly Dictionary<ObjectNode,LazyCategoryState> _lazyStates=[];
    private int _pagingGeneration;
    private sealed class LazyCategoryState
    {
        public CancellationTokenSource Cancellation { get; }=new();
        public HashSet<string> Names { get; }=new(StringComparer.OrdinalIgnoreCase);
        public int Offset { get; set; }
        public int Generation { get; set; }
    }
    private async Task BuildObjectSkeletonAsync(ObjectNode node,IPagedObjectProvider provider,CancellationToken ct)
    {
        var database=node.Database!;
        var stables=await provider.StablesAsync(database,ct);
        node.Children.Clear();
        if(stables.Count>0)
        {
            var stableCategory=new ObjectNode{Label="超级表",Profile=node.Profile,Database=database,Kind=ObjectNodeKind.Category,Loaded=true};
            foreach(var stable in stables)
            {
                var item=new ObjectNode{Label=stable,Profile=node.Profile,Database=database,Object=new DbObject(database,"",stable,"超级表"),Kind=ObjectNodeKind.Table};
                item.Children.Add(new(){Label="展开查看字段",Profile=node.Profile,Kind=ObjectNodeKind.Hint,Loaded=true});
                stableCategory.Children.Add(item);
            }
            node.Children.Add(stableCategory);
        }
        node.Children.Add(CreateLazyCategory(node,database,null,"普通表"));
        foreach(var stable in stables)node.Children.Add(CreateLazyCategory(node,database,stable,"子表 / "+stable));
    }
    private static ObjectNode CreateLazyCategory(ObjectNode parent,string database,string? stable,string label)
    {
        var category=new ObjectNode{Label=label,BaseLabel=label,Profile=parent.Profile,Database=database,Kind=ObjectNodeKind.Category,IsLazyCategory=true,ParentStable=stable};
        category.Children.Add(new(){Label=stable is null?"展开加载普通表…":"展开加载子表…",Profile=parent.Profile,Kind=ObjectNodeKind.Hint,Loaded=true});
        return category;
    }
    public async Task EnsureLazyCategoryAsync(ObjectNode category)
    {
        if(category is not {IsLazyCategory:true} || category.Loaded || category.IsLoading)return;
        await LoadObjectPageAsync(category,0,true);
    }
    public async Task LoadMoreObjectPageAsync(ObjectNode category)
    {
        if(category is not {IsLazyCategory:true} || category.IsLoading || !category.HasMore)return;
        await LoadObjectPageAsync(category,null,false);
    }
    public async Task ReloadLazyCategoryAsync(ObjectNode category)
    {
        if(category is not {IsLazyCategory:true})return;
        await LoadObjectPageAsync(category,0,true);
    }
    private async Task LoadObjectPageAsync(ObjectNode category,int? offset,bool reset)
    {
        LazyCategoryState state;
        if(reset)
        {
            if(_lazyStates.Remove(category,out var previous)){previous.Generation++;previous.Cancellation.Cancel();}
            state=new LazyCategoryState{Generation=++_pagingGeneration};
            _lazyStates[category]=state;
            category.Children.Clear();
            category.LoadedCount=0;category.TotalCount=null;category.HasMore=false;category.Loaded=false;
        }
        else
        {
            if(category.IsLoading)return;
            state=GetLazyState(category);
        }
        var generation=state.Generation;
        var pageOffset=offset??state.Offset;
        category.IsLoading=true;
        RemoveCategoryHints(category,child=>child.IsLoadMoreHint||child.Label=="正在加载…"||child.Label=="加载失败，点击重试");
        category.Children.Add(new(){Label="正在加载…",Profile=category.Profile,Database=category.Database,Kind=ObjectNodeKind.Hint,Loaded=true});
        try
        {
            var profile=CloneForDatabase(category.Profile,category.Database!);
            await using var provider=_createProvider(profile);
            if(provider is not IPagedObjectProvider paged)throw new InvalidOperationException("当前连接不支持分页对象浏览。");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(state.Cancellation.Token);
            timeout.CancelAfter(ObjectPageTimeout);
            var page=await paged.TablesPageAsync(category.Database!,category.ParentStable,null,pageOffset,ObjectPageSize,timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if(!IsCurrent(category,state,generation))return;
            RemoveCategoryHints(category,child=>child.Label=="正在加载…");
            var kind=category.ParentStable is null?"普通表":"子表";
            foreach(var name in page.Names)
            {
                if(!state.Names.Add(name))continue;
                var item=new ObjectNode{Label=name,Profile=category.Profile,Database=category.Database,Object=new DbObject(category.Database!,"",name,kind,Parent:category.ParentStable??""),Kind=ObjectNodeKind.Table};
                item.Children.Add(new(){Label="展开查看字段",Profile=category.Profile,Kind=ObjectNodeKind.Hint,Loaded=true});
                category.Children.Add(item);
            }
            state.Offset=pageOffset+page.Names.Count;
            category.HasMore=page.HasMore;
            category.LoadedCount=state.Names.Count;
            category.Loaded=true;
            if(page.HasMore)category.Children.Add(CreateLoadMoreHint(category));
            Log($"已加载 {category.BaseLabel} · {category.LoadedCount} 条");
            _=LoadCategoryTotalAsync(category,state,generation);
        }
        catch(OperationCanceledException) when(state.Cancellation.IsCancellationRequested||!IsCurrent(category,state,generation)){}
        catch(Exception ex)
        {
            if(!IsCurrent(category,state,generation))return;
            RemoveCategoryHints(category,child=>child.Label=="正在加载…");
            category.HasMore=true;
            category.Children.Add(new(){Label="加载失败，点击重试",Profile=category.Profile,Database=category.Database,Kind=ObjectNodeKind.Hint,Loaded=true,IsLoadMoreHint=true,LoadMoreOf=category});
            Log($"子表分页加载失败：{ex.Message}","Warn");
        }
        finally
        {
            if(IsCurrent(category,state,generation))category.IsLoading=false;
        }
    }
    private async Task LoadCategoryTotalAsync(ObjectNode category,LazyCategoryState state,int generation)
    {
        long total;
        try
        {
            var profile=CloneForDatabase(category.Profile,category.Database!);
            await using var provider=_createProvider(profile);
            if(provider is not IPagedObjectProvider paged)return;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(state.Cancellation.Token);
            timeout.CancelAfter(ObjectPageTimeout);
            total=await paged.CountTablesAsync(category.Database!,category.ParentStable,null,timeout.Token);
        }
        catch(OperationCanceledException){return;}
        catch(Exception ex){Log($"子表总数读取失败：{ex.Message}","Warn");return;}
        if(!IsCurrent(category,state,generation))return;
        category.TotalCount=total;
    }
    public async Task<ObjectNode?> LocateSubtableAsync(ObjectNode category,DbObject target)
    {
        if(category is not {IsLazyCategory:true})return null;
        var existing=FindCategoryObject(category,target.Name);
        if(existing is not null)return existing;
        try
        {
            var profile=CloneForDatabase(category.Profile,category.Database!);
            await using var provider=_createProvider(profile);
            if(provider is not IPagedObjectProvider paged)return null;
            using var timeout=new CancellationTokenSource(ObjectPageTimeout);
            var rank=await paged.TableRankAsync(category.Database!,category.ParentStable,target.Name,timeout.Token);
            var offset=checked((int)(rank/ObjectPageSize*ObjectPageSize));
            await LoadObjectPageAsync(category,offset,true);
            var found=FindCategoryObject(category,target.Name);
            if(found is not null)return found;
            var names=category.Children.Where(child=>child.Object is not null).Select(child=>child.Object!.Name).ToArray();
            Log($"定位 {target.Name} 未命中：排名 {rank}，页偏移 {offset}，本页范围 {names.FirstOrDefault()??"-"} ~ {names.LastOrDefault()??"-"}。","Warn");
            await LoadObjectPageAsync(category,0,true);
            return null;
        }
        catch(Exception ex){Log("定位子表失败："+ex.Message,"Error");return null;}
    }
    public void CancelObjectPaging(Guid? profileId=null)
    {
        foreach(var pair in _lazyStates.ToArray())
        {
            if(profileId is not null && pair.Key.Profile.Id!=profileId)continue;
            pair.Value.Generation++;pair.Value.Cancellation.Cancel();_lazyStates.Remove(pair.Key);
        }
    }
    private LazyCategoryState GetLazyState(ObjectNode category)
    {
        if(_lazyStates.TryGetValue(category,out var state))return state;
        state=new LazyCategoryState{Generation=++_pagingGeneration};
        _lazyStates[category]=state;
        return state;
    }
    private bool IsCurrent(ObjectNode category,LazyCategoryState state,int generation)
        =>_lazyStates.TryGetValue(category,out var current)&&ReferenceEquals(current,state)&&state.Generation==generation;
    private static ObjectNode? FindCategoryObject(ObjectNode category,string name)
        =>category.Children.FirstOrDefault(child=>child.Object is {} obj&&obj.Name.Equals(name,StringComparison.Ordinal));
    private static ObjectNode CreateLoadMoreHint(ObjectNode category)
        =>new(){Label="继续加载…",Profile=category.Profile,Database=category.Database,Kind=ObjectNodeKind.Hint,Loaded=true,IsLoadMoreHint=true,LoadMoreOf=category};
    private static void RemoveCategoryHints(ObjectNode category,Func<ObjectNode,bool> predicate)
    {
        for(int index=category.Children.Count-1;index>=0;index--)
        {
            var child=category.Children[index];
            if(child.IsHint&&predicate(child))category.Children.RemoveAt(index);
        }
    }
}
