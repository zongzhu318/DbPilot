using Avalonia.Threading;
namespace DbPilot.Views;
internal sealed class UiProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)
    {
        if(Dispatcher.UIThread.CheckAccess())report(value);else Dispatcher.UIThread.Post(()=>report(value));
    }
}
