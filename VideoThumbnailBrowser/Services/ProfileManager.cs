using System.IO;
using System.Text.Json;
using VideoThumbnailBrowser.Models;

namespace VideoThumbnailBrowser.Services;

/// <summary>
/// プロファイルを {AppDir}/{ProfileName}.profile のJSONファイルで管理する。
/// サムネイルは {AppDir}/Thumbnails/{ProfileName}/ に保存する。
/// </summary>
public class ProfileManager
{
    public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;

    public List<DbProfile> Profiles { get; private set; } = new();
    public string ActiveProfileName { get; private set; } = "Default";

    public DbProfile ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Name == ActiveProfileName) ?? Profiles.First();

    /// <summary>プロファイルのDBファイルパス。</summary>
    public string GetDbPath(DbProfile profile) =>
        Path.Combine(AppDir, $"{profile.DirectoryName}.db");

    /// <summary>プロファイルのサムネイルフォルダ。</summary>
    public string GetThumbnailDir(DbProfile profile) =>
        Path.Combine(AppDir, "Thumbnails", profile.DirectoryName);

    /// <summary>プロファイル設定ファイルパス（監視フォルダ等）。</summary>
    public string GetProfileSettingsPath(DbProfile profile) =>
        Path.Combine(AppDir, $"{profile.DirectoryName}.profile");

    public static ProfileManager Load()
    {
        var mgr = new ProfileManager();

        // AppDirにある *.profile ファイルを列挙してプロファイル一覧を作成
        var profileFiles = Directory.GetFiles(AppDir, "*.profile")
            .OrderBy(f => f)
            .ToList();

        // active.txt で最後に使ったプロファイルを記録
        var activeFile = Path.Combine(AppDir, "active.txt");
        if (File.Exists(activeFile))
            mgr.ActiveProfileName = File.ReadAllText(activeFile).Trim();

        foreach (var pf in profileFiles)
        {
            try
            {
                var dirName = Path.GetFileNameWithoutExtension(pf);
                var json = File.ReadAllText(pf);
                var data = JsonSerializer.Deserialize<ProfileFileData>(json);
                mgr.Profiles.Add(new DbProfile
                {
                    Name = data?.Name ?? dirName,
                    DirectoryName = dirName
                });
            }
            catch { }
        }

        // .profileが1つもなければ最初のプロファイルを作成（名前はDefault固定ではなく変更可能）
        if (mgr.Profiles.Count == 0)
        {
            var firstProfile = new DbProfile { Name = "メイン", DirectoryName = "Main" };
            mgr.Profiles.Add(firstProfile);
            mgr.ActiveProfileName = "メイン";
            mgr.SaveProfileFile(firstProfile, new AppSettings());
        }

        // ActiveProfileNameが存在しなければ先頭に戻す
        if (!mgr.Profiles.Any(p => p.Name == mgr.ActiveProfileName))
            mgr.ActiveProfileName = mgr.Profiles.First().Name;

        return mgr;
    }

    public void SaveActive()
    {
        try { File.WriteAllText(Path.Combine(AppDir, "active.txt"), ActiveProfileName); }
        catch { }
    }

    public void SaveProfileFile(DbProfile profile, AppSettings settings)
    {
        try
        {
            var data = new ProfileFileData
            {
                Name = profile.Name,
                Folders = settings.Folders,
                ThumbnailsPerVideo = settings.ThumbnailsPerVideo,
                ThumbnailDisplayWidth = settings.ThumbnailDisplayWidth,
                PageSize = settings.PageSize,
                SortKey = settings.SortKey,
                SortAscending = settings.SortAscending,
                VideoApps = settings.VideoApps,
                ArchiveApps = settings.ArchiveApps
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetProfileSettingsPath(profile), json);
        }
        catch { }
    }

    public AppSettings LoadProfileSettings(DbProfile profile)
    {
        var path = GetProfileSettingsPath(profile);
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<ProfileFileData>(json);
            if (data == null) return new AppSettings();
            return new AppSettings
            {
                Folders = data.Folders ?? new(),
                ThumbnailsPerVideo = data.ThumbnailsPerVideo > 0 ? data.ThumbnailsPerVideo : 4,
                ThumbnailDisplayWidth = data.ThumbnailDisplayWidth > 0 ? data.ThumbnailDisplayWidth : 200,
                PageSize = data.PageSize > 0 ? data.PageSize : 20,
                SortKey = data.SortKey ?? "FileName",
                SortAscending = data.SortAscending,
                VideoApps = data.VideoApps ?? new(),
                ArchiveApps = data.ArchiveApps ?? new()
            };
        }
        catch { return new AppSettings(); }
    }

    public void SwitchTo(string profileName)
    {
        if (Profiles.Any(p => p.Name == profileName))
        {
            ActiveProfileName = profileName;
            SaveActive();
        }
    }

    public DbProfile AddProfile(string name)
    {
        // ディレクトリ名はファイルシステムセーフな名前にする
        var dirName = MakeSafeName(name);
        var profile = new DbProfile { Name = name, DirectoryName = dirName };
        Profiles.Add(profile);
        Directory.CreateDirectory(GetThumbnailDir(profile));
        SaveProfileFile(profile, new AppSettings());
        return profile;
    }

    public bool DeleteProfile(string profileName)
    {
        if (Profiles.Count <= 1) return false; // 最後の1つは削除不可
        var profile = Profiles.FirstOrDefault(p => p.Name == profileName);
        if (profile == null) return false;
        Profiles.Remove(profile);
        if (ActiveProfileName == profileName)
            ActiveProfileName = Profiles.FirstOrDefault()?.Name ?? string.Empty;
        SaveActive();
        // .profileファイルは残す（データ保護のため削除しない）
        return true;
    }

    private static string MakeSafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"{safe}_{DateTime.UtcNow.Ticks % 100000}";
    }

    private class ProfileFileData
    {
        public string? Name { get; set; }
        public List<WatchedFolder>? Folders { get; set; }
        public int ThumbnailsPerVideo { get; set; } = 4;
        public int ThumbnailDisplayWidth { get; set; } = 200;
        public int PageSize { get; set; } = 20;
        public string? SortKey { get; set; }
        public bool SortAscending { get; set; } = true;
        public List<ExternalApp>? VideoApps { get; set; }
        public List<ExternalApp>? ArchiveApps { get; set; }
    }
}
