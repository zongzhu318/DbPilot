using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    private double _sidebarWidthBeforeCollapse=220;
    private double _editorRatioBeforeCollapse=2.0/3;
    private GridLength _expandedEditorHeight=new(2,GridUnitType.Star);
    private GridLength _expandedResultsHeight=new(3,GridUnitType.Star);
    private (bool Sidebar,bool Editor,bool Logs)? _focusLayout;
    private bool _editorExpanded=true;
    private void SetSidebarVisible(bool visible)
    {
        if(!visible && SidebarPanel.IsVisible && WorkspaceGrid.ColumnDefinitions[0].ActualWidth>0)
            _sidebarWidthBeforeCollapse=WorkspaceGrid.ColumnDefinitions[0].ActualWidth;
        SidebarPanel.IsVisible=visible;SidebarSplitter.IsVisible=visible;
        WorkspaceGrid.ColumnDefinitions[0].Width=new GridLength(visible?_sidebarWidthBeforeCollapse:0);
        WorkspaceGrid.ColumnDefinitions[1].Width=new GridLength(visible?4:0);
    }
    private void SetEditorVisible(bool visible)
    {
        _editorExpanded=visible;
        bool effective=visible&&_vm.HasTab;
        if(!effective && EditorPane.IsVisible){
            _expandedEditorHeight=EditorWorkspaceGrid.RowDefinitions[2].Height;
            _expandedResultsHeight=EditorWorkspaceGrid.RowDefinitions[4].Height;
            _editorRatioBeforeCollapse=EditorWorkspaceGrid.RowDefinitions[2].ActualHeight/Math.Max(1,EditorWorkspaceGrid.RowDefinitions[4].ActualHeight);
        }
        EditorPane.IsVisible=effective;EditorSplitter.IsVisible=effective;
        EditorWorkspaceGrid.RowDefinitions[2].Height=effective?_expandedEditorHeight:new GridLength(0);
        EditorWorkspaceGrid.RowDefinitions[3].Height=new GridLength(effective?4:0);
        EditorWorkspaceGrid.RowDefinitions[4].Height=effective?_expandedResultsHeight:new GridLength(1,GridUnitType.Star);
        EditorToggleLabel.Text=_editorExpanded?"收起 SQL":"展开 SQL";
    }
    private void UpdateWorkspaceState()
    {
        bool hasTab=_vm.HasTab;
        ConnectionBar.IsVisible=hasTab;
        QueryBar.IsVisible=hasTab;
        ResultsPane.IsVisible=hasTab;
        StartPanel.IsVisible=!hasTab&&!TableCatalogPanel.IsVisible;
        TaskLogPanel.IsVisible=hasTab||TaskLogPanel.IsExpanded;
        SetEditorVisible(_editorExpanded);
        UpdateEditControls();
    }
    private void ClearFocusState(){_focusLayout=null;FocusModeLabel.Text="专注模式";}
    private void ToggleSidebar(object? sender,RoutedEventArgs e){ClearFocusState();SetSidebarVisible(!SidebarPanel.IsVisible);}
    private void ToggleEditor(object? sender,RoutedEventArgs e){ClearFocusState();SetEditorVisible(!_editorExpanded);}
    private void ToggleLogs(object? sender,RoutedEventArgs e){TaskLogPanel.IsVisible=true;TaskLogPanel.IsExpanded=!TaskLogPanel.IsExpanded;}
    private void ToggleFocusMode(object? sender,RoutedEventArgs e)
    {
        if(_focusLayout is {} previous){SetSidebarVisible(previous.Sidebar);SetEditorVisible(previous.Editor);TaskLogPanel.IsVisible=true;TaskLogPanel.IsExpanded=previous.Logs;ClearFocusState();}
        else{_focusLayout=(SidebarPanel.IsVisible,_editorExpanded,TaskLogPanel.IsExpanded);SetSidebarVisible(false);SetEditorVisible(false);TaskLogPanel.IsExpanded=false;TaskLogPanel.IsVisible=false;FocusModeLabel.Text="退出专注";}
    }
    private void CaptureWorkspaceLayout()
    {
        _vm.Settings.SidebarWidth=SidebarPanel.IsVisible?WorkspaceGrid.ColumnDefinitions[0].ActualWidth:_sidebarWidthBeforeCollapse;
        _vm.Settings.EditorRatio=EditorPane.IsVisible?EditorWorkspaceGrid.RowDefinitions[2].ActualHeight/Math.Max(1,EditorWorkspaceGrid.RowDefinitions[4].ActualHeight):_editorRatioBeforeCollapse;
    }
}
