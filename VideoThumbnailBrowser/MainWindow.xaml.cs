using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoThumbnailBrowser.ViewModels;

namespace VideoThumbnailBrowser;

public partial class MainWindow : Window
{
    private const double MarginPerItem = 8;
    private bool _sidebarOpen = false;
    private const double SidebarWidth = 200;

    // トークンバーで右クリックしたトークン名を一時保持
    private string? _contextGlobalToken;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Vm.DetailPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DetailPanelViewModel.IsPanelOpen))
                DetailColumn.Width = Vm.DetailPanel.IsPanelOpen
                    ? new GridLength(260) : new GridLength(0);
        };
        Vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SearchText))
                SearchClearButton.Visibility = string.IsNullOrEmpty(Vm.SearchText)
                    ? Visibility.Collapsed : Visibility.Visible;
        };
        UpdateSortButtonText();
    }

    // ---- サイドバートグル ----
    private void OnSidebarToggle(object sender, RoutedEventArgs e)
    {
        _sidebarOpen = !_sidebarOpen;
        SidebarColumn.Width = _sidebarOpen
            ? new GridLength(SidebarWidth)
            : new GridLength(0);
        SidebarToggleButton.Content = _sidebarOpen ? "✕" : "☰";
    }

    // ---- サムネイル操作 ----
    private void OnThumbnailClicked(object sender, MouseButtonEventArgs e)
    {
        // シングルクリック → 詳細パネルを開く（ダブルクリックイベントと競合しないよう
        // ClickCount==1 かつダブルクリックでないときだけ処理）
        if (e.ClickCount == 1 && sender is FrameworkElement fe
            && fe.DataContext is VideoItemViewModel item)
        {
            Vm.ShowDetailCommand.Execute(item);
        }
    }

    private void OnThumbnailDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is VideoItemViewModel item)
        {
            // ダブルクリック → 再生
            item.IncrementPlayCount();
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(item.FilePath)
                    { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
        }
    }

    // ---- トークンバー ----
    private void OnGlobalTokenLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string token)
        {
            Vm.AddTokenToSearch(token);
            e.Handled = true;
        }
    }

    private void OnGlobalTokenRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string token)
            _contextGlobalToken = token;
    }

    private void OnGlobalTokenSearchClick(object sender, RoutedEventArgs e)
    {
        if (_contextGlobalToken != null)
            Vm.AddTokenToSearch(_contextGlobalToken);
    }

    private void OnGlobalTokenTagClick(object sender, RoutedEventArgs e)
    {
        if (_contextGlobalToken == null) return;
        if (Vm.DetailPanel.Item != null)
            Vm.AddTokenAsTag(Vm.DetailPanel.Item, _contextGlobalToken);
        else
            System.Windows.MessageBox.Show("タグを登録する動画を選択してください（詳細パネルを開いてください）。",
                "動画が未選択", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    // ---- フォルダツリー ----
    private void OnFolderNodeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FolderTreeNodeViewModel node)
            Vm.SelectFolderNodeCommand.Execute(node);
    }

    private void OnFolderFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FolderTreeNodeViewModel node)
            Vm.SelectFolderNodeCommand.Execute(node);
    }

    private void OnRescanFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not FolderTreeNodeViewModel node) return;
        var folder = Vm.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, node.FullPath, StringComparison.OrdinalIgnoreCase));
        if (folder != null) Vm.RescanFolderCommand.Execute(folder);
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not FolderTreeNodeViewModel node) return;
        // ルートノード（監視フォルダ）のみ削除可能
        if (!node.IsWatchedRoot) return;
        var folder = Vm.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, node.FullPath, StringComparison.OrdinalIgnoreCase));
        if (folder != null) Vm.RemoveFolderCommand.Execute(folder);
    }

    // ---- 並べ替え ----
    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnSortMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string key)
        {
            Vm.SortKey = key;
            UpdateSortButtonText();
        }
    }

    private void OnSortAscClick(object sender, RoutedEventArgs e) { Vm.SortAscending = true;  UpdateSortButtonText(); }
    private void OnSortDescClick(object sender, RoutedEventArgs e) { Vm.SortAscending = false; UpdateSortButtonText(); }

    private void UpdateSortButtonText()
    {
        SortKeyText.Text = Vm.SortKey switch
        {
            "RegisteredAt"  => "登録日時",
            "LastWriteTime" => "更新日時",
            "PlayCount"     => "再生回数",
            "Rating"        => "評価",
            "FileSize"      => "サイズ",
            "Duration"      => "動画時間",
            "TagCount"      => "タグ数",
            _               => "ファイル名"
        };
        SortDirText.Text = Vm.SortAscending ? " ↑" : " ↓";
    }

    // ---- フィルター ----
    private void OnRatingFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnRatingMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string tagStr
            && int.TryParse(tagStr, out var rating))
            Vm.RatingFilter = rating;
    }

    private void OnTagFilterClick(object sender, RoutedEventArgs e)
    {
        Vm.RefreshTagFilterList();
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnTagContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu cm) cm.DataContext = DataContext;
    }

    private void OnClearAllFiltersClick(object sender, RoutedEventArgs e) => Vm.ClearAllFilters();

    // ---- 種別フィルター ----
    private void OnKindFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnKindMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string tag)
            Vm.SetKindFilterCommand.Execute(tag);
    }

    // ---- D&Dでフォルダ登録 ----
    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        foreach (var path in paths)
            if (System.IO.Directory.Exists(path))
                Vm.AddFolderPath(path);
    }

    // ---- ページ番号直接入力 ----
    private void OnPageInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox tb)
        {
            Vm.GoToPageInputCommand.Execute(tb.Text);
            tb.Text = string.Empty;
        }
    }

    // ---- その他 ----
    private void ThumbnailScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var availableWidth = e.NewSize.Width - 24;
        var itemWidth = Vm.ThumbnailDisplayWidth + MarginPerItem;
        Vm.ColumnCount = Math.Max(1, (int)(availableWidth / itemWidth));
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
        => System.Windows.Application.Current.Shutdown();

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        Vm.SearchText = string.Empty;
        SearchBox.Focus();
    }
}


