using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbPilot.Models;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private const double ScrollLoadThreshold=48;
    private void TreeScrolled(object? sender,ScrollChangedEventArgs e)
    {
        var scroll=e.Source as ScrollViewer??ObjectTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if(scroll is null||scroll.Extent.Height<=0)return;
        if(scroll.Extent.Height-(scroll.Offset.Y+scroll.Viewport.Height)>ScrollLoadThreshold)return;
        if(LastExpandedLazyCategory(_vm.Nodes) is {} category)_=LoadMoreCategoryAsync(category);
    }
    private async Task LoadMoreCategoryAsync(ObjectNode category)
    {
        try{await _vm.LoadMoreObjectPageAsync(category);}
        catch(Exception ex){_vm.Log("加载更多子表失败："+ex.Message,"Warn");}
    }
    private static ObjectNode? LastExpandedLazyCategory(IEnumerable<ObjectNode> nodes)
    {
        ObjectNode? found=null;
        foreach(var node in nodes)
        {
            if(node.IsLazyCategory&&node.IsExpanded&&node.HasMore&&!node.IsLoading)found=node;
            if(LastExpandedLazyCategory(node.Children) is {} child)found=child;
        }
        return found;
    }
    private async void SearchSubtables(object? sender,RoutedEventArgs e)
    {
        if(ObjectTree.SelectedItem is not ObjectNode{IsLazyCategory:true,ParentStable:not null} category)return;
        try
        {
            var target=await new SubtableSearchWindow(category.Profile,category.Database!,category.ParentStable).ShowDialog<DbObject?>(this);
            if(target is null)return;
            var located=await _vm.LocateSubtableAsync(category,target);
            if(located is null){_vm.Log($"未能定位子表 {target.Name}，请重新搜索。","Warn");return;}
            await ExpandAndSelectAsync(located);
        }
        catch(Exception ex){_vm.Log("搜索子表失败："+ex.Message,"Error");}
    }
    private async void ReloadSubtables(object? sender,RoutedEventArgs e)
    {
        if(ObjectTree.SelectedItem is not ObjectNode{IsLazyCategory:true} category)return;
        try{await _vm.ReloadLazyCategoryAsync(category);}
        catch(Exception ex){_vm.Log("重新加载子表失败："+ex.Message,"Error");}
    }
    private async Task ExpandAndSelectAsync(ObjectNode target)
    {
        var path=PathTo(_vm.Nodes,target,[]);
        if(path is null)return;
        foreach(var node in path)
        {
            if(ReferenceEquals(node,target))break;
            if(!node.Loaded&&!_vm.Busy)
            {
                if(node.IsLazyCategory)await _vm.EnsureLazyCategoryAsync(node);
                else await _vm.ExpandAsync(node);
            }
            await ExpandTreeNodeAsync(node);
        }
        ObjectTree.SelectedItem=target;
        await ExpandTreeNodeAsync(target);
        ObjectTree.UpdateLayout();
        FindTreeItem(target)?.BringIntoView();
    }
    private static List<ObjectNode>? PathTo(IEnumerable<ObjectNode> nodes,ObjectNode target,List<ObjectNode> path)
    {
        foreach(var node in nodes)
        {
            path.Add(node);
            if(ReferenceEquals(node,target))return path;
            if(PathTo(node.Children,target,path) is not null)return path;
            path.RemoveAt(path.Count-1);
        }
        return null;
    }
    private async Task ExpandTreeNodeAsync(ObjectNode node)
    {
        await Task.Yield();
        ObjectTree.UpdateLayout();
        if(FindTreeItem(node) is {} item)item.IsExpanded=true;
    }
    private TreeViewItem? FindTreeItem(ObjectNode node)
        =>ObjectTree.GetVisualDescendants().OfType<TreeViewItem>().FirstOrDefault(item=>ReferenceEquals(item.DataContext,node));
}
