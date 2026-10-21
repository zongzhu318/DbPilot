using Avalonia.Interactivity;

namespace DbPilot.Views;
public sealed partial class MainWindow
{
    internal void ExecuteMenuAction(string action)
    {
        if(OwnedWindows.Any(window=>window.IsVisible))return;
        var args=new RoutedEventArgs();
        switch(action)
        {
            case "NewConnection":NewConnection(this,args);break;
            case "OpenConnection":ConnectDatabase(this,args);break;
            case "CloseConnection":DisconnectDatabase(this,args);break;
            case "EditConnection":EditConnection(this,args);break;
            case "CopyConnection":CopyConnection(this,args);break;
            case "DeleteConnection":DeleteConnection(this,args);break;
            case "NewQuery":NewQuery(this,args);break;
            case "OpenScript":OpenScript(this,args);break;
            case "SaveScript":SaveScript(this,args);break;
            case "CloseTab":CloseTab(this,args);break;
            case "RunSql":RunSql(this,args);break;
            case "RunSelectionSql":RunSelectionSql(this,args);break;
            case "StopSql":StopSql(this,args);break;
            case "FormatSql":FormatSql(this,args);break;
            case "ShowCompletionMenu":ShowCompletionMenu(this,args);break;
            case "ExportResult":ExportResult(this,args);break;
            case "ShowHistory":ShowHistory(this,args);break;
            case "WorkspaceBackup":WorkspaceBackup(this,args);break;
            case "ToggleSidebar":ToggleSidebar(this,args);break;
            case "ToggleEditor":ToggleEditor(this,args);break;
            case "ToggleLogs":ToggleLogs(this,args);break;
            case "ToggleFocusMode":ToggleFocusMode(this,args);break;
            case "ToggleTheme":ToggleTheme(this,args);break;
            case "ShowCatalog":ShowCatalog(this,args);break;
            case "RefreshCurrent":_=RefreshCurrent();break;
            case "CreateDatabase":CreateDatabase(this,args);break;
            case "CreateTable":CreateTable(this,args);break;
            case "ShowTemplates":ShowTemplates(this,args);break;
            case "ExplainSql":ExplainSql(this,args);break;
            case "OpenChart":OpenChart(this,args);break;
            case "OpenDatabaseTools":OpenDatabaseTools(this,args);break;
            case "ShowSettings":ShowSettings(this,args);break;
            case "CheckForUpdates":CheckForUpdates(this,args);break;
        }
    }
    private async void ShowAbout(object? sender,RoutedEventArgs e)=>await App.ShowAbout(this);
}
