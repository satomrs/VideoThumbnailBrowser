using VideoThumbnailBrowser.Models;

namespace VideoThumbnailBrowser.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private int _thumbnailsPerVideo;
    public int ThumbnailsPerVideo
    {
        get => _thumbnailsPerVideo;
        set => SetField(ref _thumbnailsPerVideo, Math.Clamp(value, 1, 30));
    }

    private int _thumbnailDisplayWidth;
    public int ThumbnailDisplayWidth
    {
        get => _thumbnailDisplayWidth;
        set => SetField(ref _thumbnailDisplayWidth, Math.Clamp(value, 80, 480));
    }

    private int _pageSize;
    public int PageSize
    {
        get => _pageSize;
        set => SetField(ref _pageSize, Math.Clamp(value, 5, 200));
    }

    public List<ExternalApp> VideoApps { get; }
    public List<ExternalApp> ArchiveApps { get; }

    public SettingsViewModel(int thumbnailsPerVideo, int thumbnailDisplayWidth,
        List<ExternalApp> videoApps, List<ExternalApp> archiveApps, int pageSize = 20)
    {
        _thumbnailsPerVideo = thumbnailsPerVideo;
        _thumbnailDisplayWidth = thumbnailDisplayWidth;
        _pageSize = pageSize;
        VideoApps = PadToThree(videoApps, "動画");
        ArchiveApps = PadToThree(archiveApps, "書庫");
    }

    private static List<ExternalApp> PadToThree(List<ExternalApp> source, string prefix)
    {
        var result = source.Select(a => new ExternalApp
            { Name = a.Name, ExePath = a.ExePath, Arguments = a.Arguments }).ToList();
        for (var i = result.Count + 1; i <= 3; i++)
            result.Add(new ExternalApp { Name = $"{prefix}ソフト{i}", ExePath = "" });
        return result.Take(3).ToList();
    }

    public List<ExternalApp> GetValidVideoApps() =>
        VideoApps.Where(a => !string.IsNullOrWhiteSpace(a.ExePath)).ToList();

    public List<ExternalApp> GetValidArchiveApps() =>
        ArchiveApps.Where(a => !string.IsNullOrWhiteSpace(a.ExePath)).ToList();
}
