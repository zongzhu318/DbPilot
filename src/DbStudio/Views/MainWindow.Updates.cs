using Avalonia.Interactivity;
using DbStudio.Services;

namespace DbStudio.Views;
public sealed partial class MainWindow
{
    private ApplicationUpdates? _updates;
    private bool CanInstallUpdate() => !_vm.Busy && FlushGridEdits() &&
        !_vm.Tabs.Any(t => t.IsDirty || t.Edits is {} edits && (edits.PendingCount > 0 || edits.CommitUncertain));
    private void InitializeUpdates()
    {
        _updates = new ApplicationUpdates(CanInstallUpdate, Close);
        _updates.Start();
    }
    private void CheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (!CanInstallUpdate()) { _vm.Log("请先停止任务、保存 SQL 并提交或撤销数据改动，再安装更新。", "Warn"); return; }
        if (_updates is null) { _vm.Log("自动更新尚未初始化。", "Warn"); return; }
        if (_updates.Error is {} error) { _vm.Log(error, "Warn"); return; }
        _updates.Check();
    }
}
