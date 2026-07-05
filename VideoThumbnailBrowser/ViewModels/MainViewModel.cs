using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using VideoThumbnailBrowser.Models;
using VideoThumbnailBrowser.Services;

namespace VideoThumbnailBrowser.ViewModels;

public class MainViewModel : ViewModelBase
{
    private AppSettings _settings;
    private readonly FolderWatcherService _watcherService = new();
    private FfmpegThumbnailGenerator _thumbGenerator;
    private ArchiveThumbnailExtractor _archiveExtractor;
    private ThumbnailCacheDb _cacheDb;
    private readonly DispatcherTimer _searchDebounceTimer;

    // プロファイル管理
    private readonly ProfileManager _profileManager;

    // ファイルパス → 既存ViewModel／キャッシュ済みVideoItem の高速参照用。
    private readonly List<VideoItemViewModel> _allItems = new();
    private readonly Dictionary<string, VideoItemViewModel> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoItem> _cacheLookup = new(StringComparer.OrdinalIgnoreCase);

    private string _ffmpegWarning = string.Empty;

    public ObservableCollection<WatchedFolder> Folders { get; } = new();
    public ObservableCollection<FolderTreeNodeViewModel> FolderTree { get; } = new();
    public ObservableCollection<RowViewModel> Rows { get; } = new();
    public ObservableCollection<DbProfile> Profiles { get; } = new();

    // 詳細パネル
    public DetailPanelViewModel DetailPanel { get; } = new();

    /// <summary>トークンバーに表示するトークン（詳細パネルで選択中の動画）。</summary>
    private System.Collections.ObjectModel.ObservableCollection<string> _currentTokens = new();
    public System.Collections.ObjectModel.ObservableCollection<string> CurrentTokens => _currentTokens;

    // 並べ替え
    private string _sortKey = "FileName";
    public string SortKey
    {
        get => _sortKey;
        set { if (SetField(ref _sortKey, value)) { _settings.SortKey = value; _profileManager.SaveProfileFile(_profileManager.ActiveProfile, _settings); RebuildRows(); } }
    }

    private bool _sortAscending = true;
    public bool SortAscending
    {
        get => _sortAscending;
        set { if (SetField(ref _sortAscending, value)) { _settings.SortAscending = value; _profileManager.SaveProfileFile(_profileManager.ActiveProfile, _settings); RebuildRows(); } }
    }

    // プロファイル表示名
    private string _activeProfileName = "Default";
    public string ActiveProfileName
    {
        get => _activeProfileName;
        set => SetField(ref _activeProfileName, value);
    }

    /// <summary>フォルダツリーで絞り込み中のフォルダパス（nullなら全件表示）。</summary>
    private string? _selectedFolderFilter;

    // ---- ページネーション ----
    private int _pageSize = 20;
    private List<VideoItemViewModel> _filteredItems = new();

    private int _currentPage = 1;
    public int CurrentPage { get => _currentPage; private set { SetField(ref _currentPage, value); NotifyPageProps(); } }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_filteredItems.Count / (double)_pageSize));
    public int TotalCount => _filteredItems.Count;
    public int PageStartIndex => _filteredItems.Count == 0 ? 0 : (_currentPage - 1) * _pageSize + 1;
    public int PageEndIndex => Math.Min(_currentPage * _pageSize, _filteredItems.Count);

    public RelayCommand PrevPageCommand { get; private set; } = null!;
    public RelayCommand NextPageCommand { get; private set; } = null!;
    public RelayCommand<int> GoToPageCommand { get; private set; } = null!;
    public RelayCommand<string> GoToPageInputCommand { get; private set; } = null!;
    public RelayCommand<string> JumpPageCommand { get; private set; } = null!;

    /// <summary>ページ番号ボタン一覧（ページネーションUIにバインド）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<int> PageNumbers { get; } = new();
    /// <summary>0=絞り込みなし、1〜5=その星以上を表示。</summary>
    private int _ratingFilter;
    public int RatingFilter
    {
        get => _ratingFilter;
        set
        {
            if (SetField(ref _ratingFilter, value))
            {
                OnPropertyChanged(nameof(RatingFilterLabel));
                OnPropertyChanged(nameof(IsAnyFilterActive));
                RebuildRows();
            }
        }
    }
    public string RatingFilterLabel => _ratingFilter == 0 ? "評価 ▼" : $"★{_ratingFilter}以上 ▼";

    // ---- タグフィルター ----
    /// <summary>選択中のタグ名セット（空なら絞り込みなし）。</summary>
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UIバインド用：全タグとその選択状態のリスト。</summary>
    public System.Collections.ObjectModel.ObservableCollection<TagFilterItem> TagFilterItems { get; } = new();

    public string TagFilterLabel
    {
        get
        {
            if (_activeTagFilters.Count == 0) return "タグ ▼";
            if (_activeTagFilters.Count == 1) return $"{_activeTagFilters.First()} ▼";
            return $"タグ ({_activeTagFilters.Count}) ▼";
        }
    }

    /// <summary>評価・タグのいずれかのフィルターが有効なときtrue。「クリア」ボタンの表示制御に使う。</summary>
    public bool IsAnyFilterActive => _ratingFilter > 0 || _activeTagFilters.Count > 0 || _kindFilter != KindFilter.All;

    // ---- 種別フィルター ----
    public enum KindFilter { All, Video, Archive }

    private KindFilter _kindFilter = KindFilter.All;
    public KindFilter CurrentKindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetField(ref _kindFilter, value))
            {
                OnPropertyChanged(nameof(KindFilterLabel));
                OnPropertyChanged(nameof(IsAnyFilterActive));
                RebuildRows();
            }
        }
    }
    public string KindFilterLabel => _kindFilter switch
    {
        KindFilter.Video   => "🎬 動画のみ ▼",
        KindFilter.Archive => "📦 書庫のみ ▼",
        _                  => "種別 ▼"
    };
    public RelayCommand<string> SetKindFilterCommand { get; private set; } = null!;

    public RelayCommand ClearRatingFilterCommand { get; private set; } = null!;
    public RelayCommand ClearTagFilterCommand { get; private set; } = null!;
    public RelayCommand RefreshTagFilterListCommand { get; private set; } = null!;

    // プロファイル
    public RelayCommand AddProfileCommand { get; private set; } = null!;
    public RelayCommand<DbProfile> SwitchProfileCommand { get; private set; } = null!;
    public RelayCommand<DbProfile> DeleteProfileCommand { get; private set; } = null!;

    // 詳細パネル
    public RelayCommand<VideoItemViewModel> ShowDetailCommand { get; private set; } = null!;
    public RelayCommand CloseDetailCommand { get; private set; } = null!;

    // 並べ替え
    public RelayCommand<string> SetSortKeyCommand { get; private set; } = null!;
    public RelayCommand ToggleSortDirectionCommand { get; private set; } = null!;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }
    }

    private int _columnCount = 4;
    public int ColumnCount
    {
        get => _columnCount;
        set
        {
            value = Math.Max(1, value);
            if (SetField(ref _columnCount, value))
                BuildRowsFromPage(); // ページリセットしない
        }
    }

    private int _thumbnailDisplayWidth;
    /// <summary>サムネイルタイルの表示幅（px）。高さはXAML側で比率固定。</summary>
    public int ThumbnailDisplayWidth
    {
        get => _thumbnailDisplayWidth;
        set
        {
            if (SetField(ref _thumbnailDisplayWidth, value))
                OnPropertyChanged(nameof(ThumbnailDisplayHeight));
        }
    }

    /// <summary>幅に対して16:10の比率で高さを自動算出する。</summary>
    public int ThumbnailDisplayHeight => (int)(_thumbnailDisplayWidth * 0.625);

    private string _statusText = "準備中...";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public RelayCommand RescanAllCommand { get; }
    public RelayCommand RescanAllWithRegenerateCommand { get; }
    public RelayCommand<WatchedFolder> RescanFolderCommand { get; }
    public RelayCommand SelectFolderNodeCommand { get; }
    public RelayCommand ClearFolderFilterCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand<VideoItemViewModel> EditTagsCommand { get; }

    // ---- 複数選択・一括タグ付与 ----
    private readonly List<VideoItemViewModel> _selectedItems = new();

    private int _selectedCount;
    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetField(ref _selectedCount, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }
    public bool HasSelection => _selectedCount > 0;

    public RelayCommand<VideoItemViewModel> ToggleSelectCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand BulkTagCommand { get; }

    public MainViewModel()
    {
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RebuildRows();
        };

        _profileManager = ProfileManager.Load();
        ActiveProfileName = _profileManager.ActiveProfileName;
        foreach (var p in _profileManager.Profiles) Profiles.Add(p);

        // プロファイルごとの設定を読み込む
        _settings = _profileManager.LoadProfileSettings(_profileManager.ActiveProfile);
        _thumbnailDisplayWidth = _settings.ThumbnailDisplayWidth;
        _sortKey = _settings.SortKey;
        _sortAscending = _settings.SortAscending;
        _pageSize = _settings.PageSize > 0 ? _settings.PageSize : 20;

        var thumbDir = _profileManager.GetThumbnailDir(_profileManager.ActiveProfile);
        _cacheDb = new ThumbnailCacheDb(_profileManager.GetDbPath(_profileManager.ActiveProfile));

        var (ffmpeg, ffprobe) = ResolveFfmpegPaths();
        if (!IsExecutableAvailable(ffmpeg) || !IsExecutableAvailable(ffprobe))
        {
            _ffmpegWarning = "（FFmpegが見つかりません。Toolsフォルダーにffmpeg.exe/ffprobe.exeを配置するかPATHに追加してください）";
        }

        _thumbGenerator = CreateThumbGenerator(ffmpeg, ffprobe, thumbDir, _settings.ThumbnailsPerVideo);
        _archiveExtractor = new ArchiveThumbnailExtractor(Path.Combine(thumbDir, "archives"));

        _watcherService.FileAdded += path => _ = HandleFileAddedAsync(path);
        _watcherService.FileRemoved += HandleFileRemoved;
        _watcherService.FileRenamed += HandleFileRenamed;

        AddFolderCommand = new RelayCommand(_ => AddFolder());
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is WatchedFolder f) RemoveFolder(f);
        });
        RescanAllCommand = new RelayCommand(_ => _ = RescanAllAsync(regenerate: false));
        RescanAllWithRegenerateCommand = new RelayCommand(_ => _ = RescanAllAsync(regenerate: true));
        RescanFolderCommand = new RelayCommand<WatchedFolder>(f =>
        {
            if (f != null) _ = ScanFolderAsync(f);
        });
        SelectFolderNodeCommand = new RelayCommand(p =>
        {
            if (p is FolderTreeNodeViewModel node) SelectFolderFilter(node.FullPath);
        });
        ClearFolderFilterCommand = new RelayCommand(_ => SelectFolderFilter(null));
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        EditTagsCommand = new RelayCommand<VideoItemViewModel>(EditTags);

        ToggleSelectCommand = new RelayCommand<VideoItemViewModel>(item => ToggleSelect(item));
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        SelectAllCommand = new RelayCommand(_ => SelectAll());
        BulkTagCommand = new RelayCommand(_ => OpenBulkTagDialog());

        ClearRatingFilterCommand = new RelayCommand(_ => { RatingFilter = 0; });
        ClearTagFilterCommand = new RelayCommand(_ => ClearTagFilter());
        RefreshTagFilterListCommand = new RelayCommand(_ => RefreshTagFilterList());
        SetKindFilterCommand = new RelayCommand<string>(k => CurrentKindFilter = k switch
        {
            "Video"   => KindFilter.Video,
            "Archive" => KindFilter.Archive,
            _         => KindFilter.All
        });

        AddProfileCommand = new RelayCommand(_ => AddProfile());
        SwitchProfileCommand = new RelayCommand<DbProfile>(p => { if (p != null) SwitchProfile(p); });
        DeleteProfileCommand = new RelayCommand<DbProfile>(p => { if (p != null) DeleteProfile(p); });

        ShowDetailCommand = new RelayCommand<VideoItemViewModel>(item =>
        {
            if (item == null) return;
            DetailPanel.Show(item);
            _currentTokens.Clear();
            foreach (var t in item.FileNameTokens) _currentTokens.Add(t);
        });
        CloseDetailCommand = new RelayCommand(_ => DetailPanel.Close());

        SetSortKeyCommand = new RelayCommand<string>(key => { if (key != null) SortKey = key; });
        ToggleSortDirectionCommand = new RelayCommand(_ => SortAscending = !SortAscending);

        PrevPageCommand = new RelayCommand(_ => { if (_currentPage > 1) { CurrentPage--; BuildRowsFromPage(); } });
        NextPageCommand = new RelayCommand(_ => { if (_currentPage < TotalPages) { CurrentPage++; BuildRowsFromPage(); } });
        GoToPageCommand = new RelayCommand<int>(p => { if (p >= 1 && p <= TotalPages) { CurrentPage = p; BuildRowsFromPage(); } });
        JumpPageCommand = new RelayCommand<string>(deltaStr =>
        {
            if (!int.TryParse(deltaStr, out var delta)) return;
            var target = Math.Clamp(_currentPage + delta, 1, TotalPages);
            if (target != _currentPage) { CurrentPage = target; BuildRowsFromPage(); }
        });

        // 文字列入力からページジャンプ
        GoToPageInputCommand = new RelayCommand<string>(input =>
        {
            if (int.TryParse(input, out var page))
                GoToPageCommand.Execute(Math.Clamp(page, 1, TotalPages));
        });

        foreach (var folder in _settings.Folders)
            Folders.Add(folder);

        _ = InitialLoadAsync();
    }

    private static FfmpegThumbnailGenerator CreateThumbGenerator(string ffmpeg, string ffprobe, string appDataDir, int thumbnailsPerVideo)
    {
        return new FfmpegThumbnailGenerator(
            ffmpeg, ffprobe,
            Path.Combine(appDataDir, "thumbnails"),
            maxConcurrency: Math.Max(1, Environment.ProcessorCount / 2))
        {
            ThumbnailWidth = 320,
            ThumbnailCount = thumbnailsPerVideo
        };
    }

    private static (string ffmpeg, string ffprobe) ResolveFfmpegPaths()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localFfmpeg = Path.Combine(baseDir, "Tools", "ffmpeg.exe");
        var localFfprobe = Path.Combine(baseDir, "Tools", "ffprobe.exe");

        if (File.Exists(localFfmpeg) && File.Exists(localFfprobe))
            return (localFfmpeg, localFfprobe);

        // 同梱されていなければPATH上のffmpeg/ffprobeに頼る。
        return ("ffmpeg.exe", "ffprobe.exe");
    }

    private static bool IsExecutableAvailable(string pathOrName)
    {
        if (Path.IsPathRooted(pathOrName))
            return File.Exists(pathOrName);

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return pathEnv.Split(Path.PathSeparator)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Any(dir =>
            {
                try { return File.Exists(Path.Combine(dir, pathOrName)); }
                catch { return false; }
            });
    }

    private async Task InitialLoadAsync()
    {
        try
        {
            StatusText = "キャッシュを読み込み中...";

            // Step1: DBキャッシュを一括ロードして即座に表示（高速）
            var cached = await Task.Run(() => _cacheDb.LoadAll());
            foreach (var kv in cached)
            {
                _cacheLookup[kv.Key] = kv.Value;
                // ファイルが実在するキャッシュ済みアイテムのみ即時追加（画像読み込みは遅延）
                if (kv.Value.ThumbnailPaths.Count > 0 && File.Exists(kv.Value.ThumbnailPaths[0]))
                    AddOrUpdateItem(kv.Value);
            }

            RebuildRows();
            RebuildFolderTree();
            UpdateSummaryStatus();
            StatusText = $"{_allItems.Count} 件をキャッシュから表示。バックグラウンドでスキャン中...";

            // Step2: バックグラウンドでフォルダをスキャン（新規・更新ファイルのみ処理）
            foreach (var folder in Folders.ToList())
            {
                _watcherService.Watch(folder);
                await ScanFolderAsync(folder);
            }

            RebuildFolderTree();
            UpdateSummaryStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"初期化エラー: {ex.Message}";
        }
    }

    /// <summary>フォルダパスを直接追加（D&D対応）。</summary>
    public void AddFolderPath(string path)
    {
        if (!Directory.Exists(path)) return;
        if (Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        var folder = new WatchedFolder { Path = path, Recursive = true };
        Folders.Add(folder);
        SaveFolderSettings();
        _watcherService.Watch(folder);
        _ = ScanFolderAsync(folder).ContinueWith(_ =>
        {
            RebuildFolderTree();
            UpdateSummaryStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "監視するフォルダを選択してください",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != DialogResult.OK) return;

        var folder = new WatchedFolder { Path = dialog.SelectedPath, Recursive = true };
        if (Folders.Any(f => string.Equals(f.Path, folder.Path, StringComparison.OrdinalIgnoreCase)))
            return;

        Folders.Add(folder);
        SaveFolderSettings();

        _watcherService.Watch(folder);
        _ = ScanFolderAsync(folder).ContinueWith(_ =>
        {
            RebuildFolderTree();
            UpdateSummaryStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RemoveFolder(WatchedFolder folder)
    {
        var result = System.Windows.MessageBox.Show(
            $"「{folder.Path}」の登録を解除しますか？\n\nキャッシュされたサムネイルも削除されます。",
            "フォルダ登録解除", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        Folders.Remove(folder);
        SaveFolderSettings();

        var toRemove = _allItems.Where(v => IsUnderFolder(v.FilePath, folder.Path)).ToList();
        foreach (var item in toRemove)
        {
            // サムネイル画像ファイルを削除
            foreach (var thumbPath in item.Model.ThumbnailPaths)
            {
                try
                {
                    if (File.Exists(thumbPath)) File.Delete(thumbPath);
                    // 親ディレクトリが空になったら削除
                    var dir = Path.GetDirectoryName(thumbPath);
                    if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFiles(dir).Any())
                        Directory.Delete(dir, recursive: false);
                }
                catch { }
            }
            RemoveItem(item.FilePath);
        }

        if (_selectedFolderFilter != null && IsUnderFolder(_selectedFolderFilter, folder.Path))
            _selectedFolderFilter = null;

        RebuildFolderTree();
        RebuildRows();
        UpdateSummaryStatus();

        _watcherService.UnwatchAll();
        foreach (var f in Folders)
            _watcherService.Watch(f);
    }

    private static bool IsUnderFolder(string filePath, string folderPath)
    {
        var full = Path.GetFullPath(filePath);
        var folder = Path.GetFullPath(folderPath);
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveFolderSettings()
    {
        _settings.Folders = Folders.ToList();
        _profileManager.SaveProfileFile(_profileManager.ActiveProfile, _settings);
    }

    private async Task ScanFolderAsync(WatchedFolder folder, bool regenerate = false)
    {
        StatusText = $"スキャン中: {folder.Path}";

        var files = await Task.Run(() => VideoScanner.EnumerateVideoFiles(folder.Path, folder.Recursive).ToList());
        if (files.Count == 0)
        {
            RebuildRows();
            return;
        }

        var processed = 0;
        var tasks = files.Select(async file =>
        {
            await ProcessFileAsync(file, forceRegenerate: regenerate);
            Interlocked.Increment(ref processed);
            if (processed % 25 == 0 || processed == files.Count)
                StatusText = $"サムネイル生成中... {processed}/{files.Count}";
        });

        await Task.WhenAll(tasks);
        RebuildRows();
    }

    private async Task RescanAllAsync(bool regenerate = false)
    {
        foreach (var folder in Folders.ToList())
            await ScanFolderAsync(folder, regenerate);

        RebuildFolderTree();
        UpdateSummaryStatus();
    }

    private async Task ProcessFileAsync(string filePath, bool forceRegenerate = false)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return;

            var isArchive = ArchiveFileTypes.IsArchiveFile(filePath);

            VideoItem? item = null;
            _cacheLookup.TryGetValue(filePath, out var cached);

            if (!forceRegenerate &&
                cached != null
                && cached.FileSize == fileInfo.Length
                && cached.LastWriteTicks == fileInfo.LastWriteTimeUtc.Ticks
                && cached.ThumbnailPaths.Count > 0
                && cached.ThumbnailPaths.All(File.Exists))
            {
                item = cached;
            }

            if (item == null)
            {
                if (isArchive)
                {
                    var coverPath = await _archiveExtractor.ExtractCoverAsync(filePath);
                    if (coverPath != null)
                    {
                        item = new VideoItem
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
                            DurationSeconds = 0,
                            ThumbnailPaths = new List<string> { coverPath },
                            Kind = Models.ItemKind.Archive
                        };
                        if (cached != null) { item.Rating = cached.Rating; item.Tags = cached.Tags; }
                        _cacheLookup[filePath] = item;
                        _cacheDb.Upsert(item);
                    }
                }
                else
                {
                    var generated = await _thumbGenerator.GenerateAsync(filePath);
                    if (generated != null)
                    {
                        if (cached != null) { generated.Rating = cached.Rating; generated.Tags = cached.Tags; }
                        item = generated;
                        _cacheLookup[filePath] = item;
                        _cacheDb.Upsert(item);
                    }
                }
            }

            if (item == null) return;
            AddOrUpdateItem(item, loadCover: true);
        }
        catch { }
    }

    private void AddOrUpdateItem(VideoItem item, bool loadCover = false)
    {
        if (_itemsByPath.TryGetValue(item.FilePath, out var existing))
        {
            existing.Model.ThumbnailPaths = item.ThumbnailPaths;
            existing.Model.DurationSeconds = item.DurationSeconds;
            existing.Model.FileSize = item.FileSize;
            existing.Model.LastWriteTicks = item.LastWriteTicks;
            existing.Model.Kind = item.Kind;
            return;
        }

        var vm = new VideoItemViewModel(item, _cacheDb);
        if (loadCover) vm.LoadCoverThumbnail();
        _allItems.Add(vm);
        _itemsByPath[item.FilePath] = vm;
    }

    private void RemoveItem(string filePath)
    {
        if (!_itemsByPath.TryGetValue(filePath, out var vm)) return;

        _allItems.Remove(vm);
        _itemsByPath.Remove(filePath);
        _cacheLookup.Remove(filePath);
        _cacheDb.Delete(filePath);
    }

    private async Task HandleFileAddedAsync(string filePath)
    {
        await ProcessFileAsync(filePath);
        RebuildFolderTree();
        RebuildRows();
        UpdateSummaryStatus();
    }

    private void HandleFileRemoved(string filePath)
    {
        RemoveItem(filePath);
        RebuildRows();
        UpdateSummaryStatus();
    }

    private void HandleFileRenamed(string oldPath, string newPath)
    {
        if (_itemsByPath.TryGetValue(oldPath, out var vm))
        {
            vm.Model.FilePath = newPath;
            _itemsByPath.Remove(oldPath);
            _itemsByPath[newPath] = vm;

            _cacheLookup.Remove(oldPath);
            _cacheLookup[newPath] = vm.Model;

            _cacheDb.RenamePath(oldPath, newPath);

            RebuildRows();
        }
        else
        {
            _ = HandleFileAddedAsync(newPath);
        }
    }

    private void RebuildFolderTree()
    {
        FolderTree.Clear();
        foreach (var folder in Folders)
        {
            var tree = FolderTreeBuilder.BuildTree(folder);
            FolderTree.Add(new FolderTreeNodeViewModel(tree));
        }
    }

    private void SelectFolderFilter(string? folderPath)
    {
        _selectedFolderFilter = folderPath;
        RebuildRows();
    }

    private void RebuildRows()
    {
        IEnumerable<VideoItemViewModel> filtered = _allItems;

        if (_selectedFolderFilter != null)
            filtered = filtered.Where(v => IsUnderFolder(v.FilePath, _selectedFolderFilter));

        // 種別フィルター
        if (_kindFilter == KindFilter.Video)
            filtered = filtered.Where(v => !v.IsArchive);
        else if (_kindFilter == KindFilter.Archive)
            filtered = filtered.Where(v => v.IsArchive);

        if (_ratingFilter > 0)
            filtered = filtered.Where(v => v.Rating >= _ratingFilter);

        if (_activeTagFilters.Count > 0)
            filtered = filtered.Where(v =>
                _activeTagFilters.All(tag =>
                    v.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))));

        var query = ParsedSearchQuery.Parse(SearchText);
        if (!query.IsEmpty)
            filtered = filtered.Where(v => MatchesQuery(v, query));

        _filteredItems = ApplySort(filtered).ToList();
        _currentPage = 1;
        NotifyPageProps();
        BuildRowsFromPage();
    }

    private void BuildRowsFromPage()
    {
        Rows.Clear();
        var pageItems = _filteredItems.Skip((_currentPage - 1) * _pageSize).Take(_pageSize).ToList();

        for (var i = 0; i < pageItems.Count; i += ColumnCount)
        {
            var row = new RowViewModel();
            foreach (var item in pageItems.Skip(i).Take(ColumnCount))
                row.Items.Add(item);
            Rows.Add(row);
        }
    }

    private void NotifyPageProps()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PageStartIndex));
        OnPropertyChanged(nameof(PageEndIndex));
        OnPropertyChanged(nameof(CurrentPage));

        // ページ番号ボタン一覧を再構築（最大15ページ分表示、現在ページ周辺を優先）
        PageNumbers.Clear();
        var total = TotalPages;
        if (total <= 1) return;

        const int maxButtons = 15;
        int half = maxButtons / 2;
        int start = Math.Max(1, _currentPage - half);
        int end   = Math.Min(total, start + maxButtons - 1);
        if (end - start < maxButtons - 1)
            start = Math.Max(1, end - maxButtons + 1);

        for (var i = start; i <= end; i++)
            PageNumbers.Add(i);
    }

    private static bool MatchesQuery(VideoItemViewModel item, ParsedSearchQuery query)
    {
        foreach (var keyword in query.NameKeywords)
        {
            // ファイル名またはフォルダパスの部分一致
            if (!item.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !item.FolderPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var tagKeyword in query.TagKeywords)
        {
            if (!item.Tags.Any(t => t.Contains(tagKeyword, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (query.RatingFilter is { } rf)
        {
            var ok = rf.op switch
            {
                ">=" => item.Rating >= rf.value,
                "<=" => item.Rating <= rf.value,
                ">" => item.Rating > rf.value,
                "<" => item.Rating < rf.value,
                _ => item.Rating == rf.value
            };
            if (!ok) return false;
        }

        return true;
    }

    private void OpenSettings()
    {
        var vm = new SettingsViewModel(
            _settings.ThumbnailsPerVideo,
            _settings.ThumbnailDisplayWidth,
            _settings.VideoApps,
            _settings.ArchiveApps,
            _settings.PageSize);

        var window = new SettingsWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (window.ShowDialog() != true) return;

        ThumbnailDisplayWidth = vm.ThumbnailDisplayWidth;
        _settings.ThumbnailDisplayWidth = vm.ThumbnailDisplayWidth;
        _settings.VideoApps = vm.GetValidVideoApps();
        _settings.ArchiveApps = vm.GetValidArchiveApps();

        if (vm.PageSize != _settings.PageSize)
        {
            _settings.PageSize = vm.PageSize;
            _pageSize = vm.PageSize;
            RebuildRows(); // ページサイズ変更は即時反映
        }

        if (vm.ThumbnailsPerVideo != _settings.ThumbnailsPerVideo)
        {
            _settings.ThumbnailsPerVideo = vm.ThumbnailsPerVideo;
            var (ff, ffp) = ResolveFfmpegPaths();
            var profileDir = _profileManager.GetThumbnailDir(_profileManager.ActiveProfile);
            _thumbGenerator = CreateThumbGenerator(ff, ffp, profileDir, vm.ThumbnailsPerVideo);
            StatusText = "サムネイル枚数の設定を変更しました。「再スキャン」で反映されます。";
        }

        _profileManager.SaveProfileFile(_profileManager.ActiveProfile, _settings);
    }

    private void EditTags(VideoItemViewModel? item)
    {
        if (item == null) return;

        var suggestions = _cacheDb.GetAllTagNames();
        var window = new TagEditWindow(item.Tags, suggestions)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (window.ShowDialog() != true) return;

        item.SetTags(window.ResultTags);
    }

    private IEnumerable<VideoItemViewModel> ApplySort(IEnumerable<VideoItemViewModel> source)
    {
        IOrderedEnumerable<VideoItemViewModel> ordered = _sortKey switch
        {
            "RegisteredAt"  => source.OrderBy(v => v.Model.RegisteredTicks),
            "LastWriteTime" => source.OrderBy(v => v.Model.LastWriteTicks),
            "PlayCount"     => source.OrderBy(v => v.PlayCount),
            "Rating"        => source.OrderBy(v => v.Rating),
            "FileSize"      => source.OrderBy(v => v.FileSize),
            "Duration"      => source.OrderBy(v => v.DurationSeconds),
            "TagCount"      => source.OrderBy(v => v.TagCount),
            "FolderPath"    => source.OrderBy(v => v.FolderPath, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(v => v.FileName, StringComparer.OrdinalIgnoreCase),
            _               => source.OrderBy(v => v.FileName, StringComparer.OrdinalIgnoreCase)
        };

        return _sortAscending ? ordered : ordered.Reverse();
    }

    // ---- プロファイル管理 ----

    private void AddProfile()
    {
        var dialog = new InputDialog("新しいプロファイル名を入力してください:", "プロファイルを追加")
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;
        var name = dialog.Result.Trim();

        if (_profileManager.Profiles.Any(p => p.Name == name))
        {
            System.Windows.MessageBox.Show("同じ名前のプロファイルが既に存在します。", "エラー",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var profile = _profileManager.AddProfile(name);
        Profiles.Add(profile);
        SwitchProfile(profile);
    }

    private void SwitchProfile(DbProfile profile)
    {
        _profileManager.SwitchTo(profile.Name);
        ActiveProfileName = profile.Name;

        _watcherService.UnwatchAll();
        _allItems.Clear();
        _itemsByPath.Clear();
        _cacheLookup.Clear();
        Folders.Clear();
        Rows.Clear();
        FolderTree.Clear();

        // プロファイルの設定を読み込む
        _settings = _profileManager.LoadProfileSettings(profile);
        _thumbnailDisplayWidth = _settings.ThumbnailDisplayWidth;
        ThumbnailDisplayWidth = _thumbnailDisplayWidth;
        _sortKey = _settings.SortKey;
        _sortAscending = _settings.SortAscending;
        _pageSize = _settings.PageSize > 0 ? _settings.PageSize : 20;

        var thumbDir = _profileManager.GetThumbnailDir(profile);
        _cacheDb = new ThumbnailCacheDb(_profileManager.GetDbPath(profile));

        var (ff, ffp) = ResolveFfmpegPaths();
        _thumbGenerator = CreateThumbGenerator(ff, ffp, thumbDir, _settings.ThumbnailsPerVideo);
        _archiveExtractor = new ArchiveThumbnailExtractor(Path.Combine(thumbDir, "archives"));

        foreach (var folder in _settings.Folders)
            Folders.Add(folder);

        _ = InitialLoadAsync();
    }

    private void DeleteProfile(DbProfile profile)
    {
        if (profile.Name == "Default")
        {
            System.Windows.MessageBox.Show("Defaultプロファイルは削除できません。", "エラー",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"プロファイル「{profile.Name}」を削除しますか？\n（DBとキャッシュファイルは残ります）",
            "確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        _profileManager.DeleteProfile(profile.Name);
        Profiles.Remove(profile);

        if (ActiveProfileName == profile.Name)
            SwitchProfile(_profileManager.ActiveProfile);
    }

    // ---- トークン操作 ----

    /// <summary>検索バーにトークンを追加（既にあれば追加しない）。</summary>
    public void AddTokenToSearch(string token)
    {
        var current = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!current.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(token);
            SearchText = string.Join(" ", current);
        }
    }

    /// <summary>指定動画にトークンをタグとして追加登録する。</summary>
    public void AddTokenAsTag(VideoItemViewModel item, string token)
    {
        var merged = item.Tags
            .Append(token)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        item.SetTags(merged);
    }

    // ---- 外部アプリ起動 ----

    public void OpenWithApp(VideoItemViewModel item, int appIndex)
    {
        var apps = item.IsArchive ? _settings.ArchiveApps : _settings.VideoApps;
        if (appIndex < 0 || appIndex >= apps.Count) return;
        var app = apps[appIndex];
        if (string.IsNullOrWhiteSpace(app.ExePath)) return;

        try
        {
            var args = string.Format(
                string.IsNullOrWhiteSpace(app.Arguments) ? "\"{0}\"" : app.Arguments,
                item.FilePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = app.ExePath,
                Arguments = args,
                UseShellExecute = true
            });
            item.IncrementPlayCount();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"起動に失敗しました。\n{ex.Message}", "エラー",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public List<Models.ExternalApp> GetAppsForItem(VideoItemViewModel item) =>
        item.IsArchive ? _settings.ArchiveApps : _settings.VideoApps;

    private void UpdateSummaryStatus()
    {
        StatusText = $"{_allItems.Count} 件の動画{_ffmpegWarning}";
    }

    // ---- 評価・タグフィルター管理 ----

    /// <summary>ドロップダウンを開く直前に呼んでタグ一覧を最新化する。</summary>
    public void RefreshTagFilterList()
    {
        var allTagNames = _cacheDb.GetAllTagNames();
        TagFilterItems.Clear();
        foreach (var name in allTagNames)
        {
            var item = new TagFilterItem(name, _activeTagFilters.Contains(name));
            item.IsCheckedChanged += () =>
            {
                if (item.IsChecked) _activeTagFilters.Add(item.Name);
                else _activeTagFilters.Remove(item.Name);
                OnPropertyChanged(nameof(TagFilterLabel));
                OnPropertyChanged(nameof(IsAnyFilterActive));
                RebuildRows();
            };
            TagFilterItems.Add(item);
        }
    }

    private void ClearTagFilter()
    {
        _activeTagFilters.Clear();
        foreach (var item in TagFilterItems) item.IsChecked = false;
        OnPropertyChanged(nameof(TagFilterLabel));
        OnPropertyChanged(nameof(IsAnyFilterActive));
        RebuildRows();
    }

    public void ClearAllFilters()
    {
        _ratingFilter = 0;
        OnPropertyChanged(nameof(RatingFilterLabel));
        _activeTagFilters.Clear();
        foreach (var item in TagFilterItems) item.IsChecked = false;
        OnPropertyChanged(nameof(TagFilterLabel));
        _kindFilter = KindFilter.All;
        OnPropertyChanged(nameof(KindFilterLabel));
        OnPropertyChanged(nameof(IsAnyFilterActive));
        RebuildRows();
    }

    // ---- 複数選択・一括タグ付与 ----

    public void ToggleSelect(VideoItemViewModel? item, bool ctrlDown = false, bool shiftDown = false)
    {
        if (item == null) return;

        if (shiftDown && _selectedItems.Count > 0)
        {
            // Shift: 最後に選択したアイテムから今クリックしたアイテムまで範囲選択
            var flatList = Rows.SelectMany(r => r.Items).ToList();
            var lastIndex = flatList.IndexOf(_selectedItems.Last());
            var currentIndex = flatList.IndexOf(item);
            if (lastIndex >= 0 && currentIndex >= 0)
            {
                var from = Math.Min(lastIndex, currentIndex);
                var to = Math.Max(lastIndex, currentIndex);
                for (var i = from; i <= to; i++)
                {
                    var target = flatList[i];
                    if (!target.IsSelected)
                    {
                        target.IsSelected = true;
                        _selectedItems.Add(target);
                    }
                }
            }
        }
        else if (ctrlDown)
        {
            // Ctrl: 追加/解除トグル
            if (item.IsSelected)
            {
                item.IsSelected = false;
                _selectedItems.Remove(item);
            }
            else
            {
                item.IsSelected = true;
                _selectedItems.Add(item);
            }
        }
        else
        {
            // 通常クリック: 自分だけ選択（既に自分だけなら解除）
            if (_selectedItems.Count == 1 && _selectedItems[0] == item)
            {
                ClearSelection();
                return;
            }

            ClearSelection();
            item.IsSelected = true;
            _selectedItems.Add(item);
        }

        SelectedCount = _selectedItems.Count;
    }

    public void ClearSelection()
    {
        foreach (var item in _selectedItems)
            item.IsSelected = false;
        _selectedItems.Clear();
        SelectedCount = 0;
    }

    public void SelectAll()
    {
        // 現在表示中（フィルター適用後）の全アイテムを選択
        var visible = Rows.SelectMany(r => r.Items).ToList();
        foreach (var item in visible)
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
                _selectedItems.Add(item);
            }
        }
        SelectedCount = _selectedItems.Count;
    }

    /// <summary>評価を設定する。複数選択中なら全選択アイテムに適用。単体はトグル。</summary>
    public void SetRating(VideoItemViewModel target, int rating)
    {
        if (_selectedItems.Count > 1 && _selectedItems.Contains(target))
        {
            foreach (var item in _selectedItems.ToList())
                item.Rating = rating;
        }
        else
        {
            target.Rating = target.Rating == rating ? 0 : rating;
        }
    }

    private void OpenBulkTagDialog()
    {
        if (_selectedItems.Count == 0) return;

        var suggestions = _cacheDb.GetAllTagNames();
        var window = new BulkTagWindow(_selectedItems.Count, suggestions)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (window.ShowDialog() != true) return;

        foreach (var item in _selectedItems.ToList())
        {
            // 既存タグを保持したまま追加する（重複は除く）。
            var merged = item.Tags
                .Concat(window.TagsToAdd)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            item.SetTags(merged);
        }

        RebuildRows(); // タグ表示を更新
    }
}
