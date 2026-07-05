using System.IO;
using System.Text.Json;
using VideoThumbnailBrowser.Models;

namespace VideoThumbnailBrowser.Services;

/// <summary>
/// アプリ設定をexeと同じフォルダのJSONファイルに保存・復元する。
/// </summary>
public class AppSettings
{
    public List<WatchedFolder> Folders { get; set; } = new();
    public int ThumbnailsPerVideo { get; set; } = 4;
    public int ThumbnailDisplayWidth { get; set; } = 200;
    public int PageSize { get; set; } = 20;
    public string SortKey { get; set; } = "FileName";
    public bool SortAscending { get; set; } = true;

    /// <summary>動画用の起動ソフト（最大3つ）。</summary>
    public List<ExternalApp> VideoApps { get; set; } = new();

    /// <summary>書庫用の起動ソフト（最大3つ）。</summary>
    public List<ExternalApp> ArchiveApps { get; set; } = new();

    /// <summary>exeと同階層のデータディレクトリ。</summary>
    public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;

    private static string SettingsPath => Path.Combine(AppDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
