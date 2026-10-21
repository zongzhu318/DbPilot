using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;

namespace DbPilot.Services;

/// <summary>Native Sparkle/WinSparkle is shipped alongside release builds only.</summary>
internal sealed class ApplicationUpdates : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CanShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Shutdown();
    private readonly CanShutdown _canShutdown;
    private readonly Shutdown _shutdown;
    private bool _started;
    public string? Error { get; private set; }

    public ApplicationUpdates(Func<bool> canShutdown, Action shutdown)
    {
        _canShutdown = () =>
        {
            try
            {
                return Dispatcher.UIThread.CheckAccess()
                    ? (canShutdown() ? 1 : 0)
                    : Dispatcher.UIThread.InvokeAsync(() => canShutdown() ? 1 : 0).GetAwaiter().GetResult();
            }
            catch { return 0; } // Never allow managed exceptions to cross a native callback boundary.
        };
        _shutdown = () => Dispatcher.UIThread.Post(shutdown);
    }

    public void Start()
    {
        try
        {
            if (AppInfo.IsDevelopment)
            {
                Error = "当前运行的是开发版，不提供自动更新。请使用正式安装版本。";
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                var bridge = Path.Combine(AppContext.BaseDirectory, "libDbPilotUpdater.dylib");
                if (!File.Exists(bridge)) { Error = "当前运行的是开发版，请安装正式发布的 DMG 版本。"; return; }
                MacStart(_canShutdown);
            }
            else if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(AppContext.BaseDirectory, "updates.json");
                if (!File.Exists(path)) { Error = "当前运行的是开发版，请安装正式发布的 EXE 版本。"; return; }
                var config = JsonSerializer.Deserialize<UpdateConfiguration>(File.ReadAllText(path))!;
                var uri = new Uri(config.FeedUrl);
                if (uri.Scheme != "https") throw new InvalidDataException("更新源必须使用 HTTPS。");
                WinSetAppDetails(AppInfo.DisplayName, AppInfo.DisplayName, typeof(ApplicationUpdates).Assembly.GetName().Version!.ToString(3));
                WinSetFeed(config.FeedUrl);
                if (WinSetPublicKey(config.PublicKey) == 0) throw new InvalidDataException("更新公钥格式无效。");
                WinSetCanShutdown(_canShutdown);
                WinSetShutdown(_shutdown);
                WinSetAutomaticChecks(1);
                WinInit();
            }
            else { Error = "此平台暂不支持自动更新。"; return; }
            _started = true;
        }
        catch (Exception ex) { Error = "初始化自动更新失败：" + ex.Message; }
    }

    public void Check()
    {
        if (!_started) return;
        if (OperatingSystem.IsMacOS()) MacCheck();
        else WinCheck();
    }

    public void Dispose()
    {
        if (_started && OperatingSystem.IsWindows()) WinCleanup();
        _started = false;
        GC.KeepAlive(_canShutdown);
        GC.KeepAlive(_shutdown);
    }

    private sealed record UpdateConfiguration(string FeedUrl, string PublicKey);
    [DllImport("libDbPilotUpdater.dylib", EntryPoint = "dbpilot_updater_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MacStart(CanShutdown callback);
    [DllImport("libDbPilotUpdater.dylib", EntryPoint = "dbpilot_updater_check", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MacCheck();
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_set_app_details", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void WinSetAppDetails(string company, string app, string version);
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_set_appcast_url", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinSetFeed([MarshalAs(UnmanagedType.LPUTF8Str)] string url);
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_set_eddsa_public_key", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WinSetPublicKey([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_set_can_shutdown_callback", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinSetCanShutdown(CanShutdown callback);
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_set_shutdown_request_callback", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinSetShutdown(Shutdown callback);
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_set_automatic_check_for_updates", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinSetAutomaticChecks(int enabled);
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinInit();
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_cleanup", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinCleanup();
    [DllImport("WinSparkle.dll", EntryPoint = "win_sparkle_check_update_with_ui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WinCheck();
}
