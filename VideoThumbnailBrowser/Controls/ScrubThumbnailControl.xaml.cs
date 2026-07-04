using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoThumbnailBrowser.ViewModels;

namespace VideoThumbnailBrowser.Controls;

public partial class ScrubThumbnailControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty VideoItemProperty =
        DependencyProperty.Register(nameof(VideoItem), typeof(VideoItemViewModel),
            typeof(ScrubThumbnailControl), new PropertyMetadata(null, OnVideoItemChanged));

    public VideoItemViewModel? VideoItem
    {
        get => (VideoItemViewModel?)GetValue(VideoItemProperty);
        set => SetValue(VideoItemProperty, value);
    }

    public static readonly DependencyProperty EditTagsCommandProperty =
        DependencyProperty.Register(nameof(EditTagsCommand), typeof(ICommand),
            typeof(ScrubThumbnailControl), new PropertyMetadata(null));

    public ICommand? EditTagsCommand
    {
        get => (ICommand?)GetValue(EditTagsCommandProperty);
        set => SetValue(EditTagsCommandProperty, value);
    }

    public static readonly DependencyProperty ToggleSelectCommandProperty =
        DependencyProperty.Register(nameof(ToggleSelectCommand), typeof(ICommand),
            typeof(ScrubThumbnailControl), new PropertyMetadata(null));

    public ICommand? ToggleSelectCommand
    {
        get => (ICommand?)GetValue(ToggleSelectCommandProperty);
        set => SetValue(ToggleSelectCommandProperty, value);
    }

    private const int StarCount = 5;

    public ScrubThumbnailControl()
    {
        InitializeComponent();
        BuildStarButtons();
    }

    private void BuildStarButtons()
    {
        for (var i = 1; i <= StarCount; i++)
        {
            var index = i;
            var btn = new System.Windows.Controls.Button
            {
                Content = "★", FontSize = 13,
                Padding = new Thickness(1, 0, 1, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = System.Windows.Media.Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index
            };
            btn.Click += (_, _) =>
            {
                if (VideoItem == null) return;
                FindMainViewModel()?.SetRating(VideoItem, index);
                UpdateStarDisplay();
            };
            StarPanel.Children.Add(btn);
        }
    }

    private static void OnVideoItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrubThumbnailControl ctrl)
        {
            if (e.OldValue is VideoItemViewModel oldVm)
                oldVm.PropertyChanged -= ctrl.OnVideoItemPropertyChanged;
            if (e.NewValue is VideoItemViewModel newVm)
                newVm.PropertyChanged += ctrl.OnVideoItemPropertyChanged;
            ctrl.RefreshDisplay();
        }
    }

    private void OnVideoItemPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoItemViewModel.IsSelected)) UpdateSelectionDisplay();
        else if (e.PropertyName == nameof(VideoItemViewModel.TagsText)) UpdateTagsDisplay();
    }

    private void RefreshDisplay()
    {
        if (VideoItem == null)
        {
            ThumbImage.Source = null;
            FileNameText.Text = DurationText.Text = TagsText.Text = string.Empty;
            TagsText.Visibility = Visibility.Collapsed;
            ArchiveBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // 遅延ロード: まだ画像を読んでいなければここで読む（初回表示時のみ）
        if (VideoItem.CoverImage == null)
            VideoItem.LoadCoverThumbnail();

        ThumbImage.Source = VideoItem.CoverImage;
        FileNameText.Text = VideoItem.FileName;
        DurationText.Text = VideoItem.IsArchive ? "📦" : VideoItem.DurationText;
        ArchiveBadge.Visibility = VideoItem.IsArchive ? Visibility.Visible : Visibility.Collapsed;

        UpdateTagsDisplay();
        UpdateStarDisplay();
        UpdateSelectionDisplay();
    }

    private void UpdateTagsDisplay()
    {
        if (VideoItem == null) return;
        TagsText.Text = VideoItem.TagsText;
        TagsText.Visibility = string.IsNullOrEmpty(VideoItem.TagsText)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStarDisplay()
    {
        if (VideoItem == null) return;
        foreach (System.Windows.Controls.Button btn in StarPanel.Children)
        {
            var idx = (int)btn.Tag;
            btn.Foreground = idx <= VideoItem.Rating
                ? System.Windows.Media.Brushes.Gold
                : System.Windows.Media.Brushes.Gray;
        }
    }

    private void UpdateSelectionDisplay()
    {
        if (VideoItem == null) return;
        OuterBorder.BorderBrush = VideoItem.IsSelected
            ? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        CheckMark.Visibility = VideoItem.IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- コンテキストメニュー ----

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (VideoItem == null) return;
        var mainVm = FindMainViewModel();
        if (mainVm == null) return;

        var apps = mainVm.GetAppsForItem(VideoItem);

        // ソフト2
        if (apps.Count >= 2 && !string.IsNullOrWhiteSpace(apps[1].ExePath))
        {
            OpenApp2Item.Header = $"{apps[1].Name} で開く";
            OpenApp2Item.Visibility = Visibility.Visible;
        }
        else
        {
            OpenApp2Item.Visibility = Visibility.Collapsed;
        }

        // ソフト3
        if (apps.Count >= 3 && !string.IsNullOrWhiteSpace(apps[2].ExePath))
        {
            OpenApp3Item.Header = $"{apps[2].Name} で開く";
            OpenApp3Item.Visibility = Visibility.Visible;
        }
        else
        {
            OpenApp3Item.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenApp1Click(object sender, RoutedEventArgs e)
    {
        if (VideoItem != null) FindMainViewModel()?.OpenWithApp(VideoItem, 0);
    }

    private void OnOpenApp2Click(object sender, RoutedEventArgs e)
    {
        if (VideoItem != null) FindMainViewModel()?.OpenWithApp(VideoItem, 1);
    }

    private void OnOpenApp3Click(object sender, RoutedEventArgs e)
    {
        if (VideoItem != null) FindMainViewModel()?.OpenWithApp(VideoItem, 2);
    }

    private void OnOpenDefaultClick(object sender, RoutedEventArgs e) => OpenDefault();

    // ---- マウスイベント ----

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        VideoItem?.EnsureScrubFramesLoaded();
        RatingPanel.Visibility = Visibility.Visible;
        PlayButton.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (VideoItem == null || VideoItem.FrameCount <= 1 || VideoItem.IsArchive) return;
        var pos = e.GetPosition(ThumbImage);
        var w = ThumbImage.ActualWidth;
        if (w <= 0) return;
        var fraction = Math.Clamp(pos.X / w, 0.0, 1.0);
        var idx = (int)Math.Round(fraction * (VideoItem.FrameCount - 1));
        var frame = VideoItem.GetFrame(idx);
        if (frame != null) ThumbImage.Source = frame;
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (VideoItem != null) ThumbImage.Source = VideoItem.CoverImage;
        RatingPanel.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        if (e.OriginalSource is System.Windows.Controls.Button) return;

        var ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        var shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        FindMainViewModel()?.ToggleSelect(VideoItem, ctrl, shift);
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        // ソフト1が登録されていればそれで、なければ既定のアプリで開く
        var mainVm = FindMainViewModel();
        if (mainVm != null && VideoItem != null)
        {
            var apps = mainVm.GetAppsForItem(VideoItem);
            if (apps.Count > 0 && !string.IsNullOrWhiteSpace(apps[0].ExePath))
            {
                mainVm.OpenWithApp(VideoItem, 0);
                e.Handled = true;
                return;
            }
        }
        OpenDefault();
        e.Handled = true;
    }

    private void OnPlayButtonClick(object sender, MouseButtonEventArgs e)
    {
        var mainVm = FindMainViewModel();
        if (mainVm != null && VideoItem != null)
        {
            var apps = mainVm.GetAppsForItem(VideoItem);
            if (apps.Count > 0 && !string.IsNullOrWhiteSpace(apps[0].ExePath))
            {
                mainVm.OpenWithApp(VideoItem, 0);
                e.Handled = true;
                return;
            }
        }
        OpenDefault();
        e.Handled = true;
    }

    // ---- ファイル操作 ----

    private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (VideoItem == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"/select,\"{VideoItem.FilePath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnCopyFileNameClick(object sender, RoutedEventArgs e)
    {
        if (VideoItem != null) System.Windows.Clipboard.SetText(VideoItem.FileName);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (VideoItem != null) System.Windows.Clipboard.SetText(VideoItem.FilePath);
    }

    private void OnEditTagsClick(object sender, RoutedEventArgs e)
    {
        if (VideoItem != null) EditTagsCommand?.Execute(VideoItem);
    }

    private void OnDeleteFileClick(object sender, RoutedEventArgs e)
    {
        if (VideoItem == null) return;
        var result = System.Windows.MessageBox.Show(
            $"このファイルをごみ箱に移動しますか？\n\n{VideoItem.FileName}",
            "ファイルを削除", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                VideoItem.FilePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"削除に失敗しました。\n{ex.Message}", "エラー",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OpenDefault()
    {
        if (VideoItem == null) return;
        try
        {
            VideoItem.IncrementPlayCount();
            Process.Start(new ProcessStartInfo(VideoItem.FilePath) { UseShellExecute = true });
        }
        catch { }
    }

    private MainViewModel? FindMainViewModel() =>
        System.Windows.Application.Current.MainWindow?.DataContext as MainViewModel;
}
