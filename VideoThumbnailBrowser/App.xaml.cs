using System.Windows;

namespace VideoThumbnailBrowser;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// アプリ全体のキャンセルトークン。
    /// 終了時にCancelを呼ぶことでバックグラウンドのFFmpeg/7zプロセスを全部Killする。
    /// </summary>
    public static CancellationTokenSource AppCts { get; } = new();

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppCts.Cancel(); } catch { }
        base.OnExit(e);
    }
}
