using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DSAceLogViewer.Core;
using Microsoft.Win32;

namespace DSAceLogViewer;

public partial class MainWindow : Window
{
    private const int MaximumRetainedEntries = 100_000;
    private readonly List<LogEntry> _all = [];
    private readonly ObservableCollection<LogEntry> _visible = [];
    private readonly HashSet<LogCategory> _categories = Enum.GetValues<LogCategory>().ToHashSet();
    private readonly Dictionary<LogCategory, ToggleButton> _categoryButtons = [];
    private readonly int[] _categoryCounts = new int[Enum.GetValues<LogCategory>().Length];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _fontSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly string? _settingsPath;
    private ViewerSettings _settings;
    private string _sourcePath;
    private LogFilter _filter = new();
    private CancellationTokenSource? _tailCancellation;
    private CancellationTokenSource? _filterCancellation;
    private ScrollViewer? _scroll;
    private bool _follow = true, _changingView, _filtering, _available;
    private int _pendingCount, _generation;
    private long _totalRead;
    private string _watchStatus = "Starting viewer…";
    private string? _notice;
    private DateTime _noticeUntil;
    private DateTime? _lastReadAt;
    private bool _applyingSettings = true;

    public MainWindow(string? commandLinePath = null, ViewerSettings? initialSettings = null, string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _settings = initialSettings ?? ViewerSettings.Load(settingsPath);
        _sourcePath = _settings.EffectivePath;
        InitializeComponent();
        ApplyDisplaySettings();
        _applyingSettings = false;
        SourceInitialized += (_, _) => WindowAppearance.UseDarkTitleBar(this);
        LogGrid.ItemsSource = _visible;
        var categoryButtonStyle = (Style)FindResource("CategoryFilterButton");
        var brushConverter = new BrushConverter();
        foreach (var category in Enum.GetValues<LogCategory>())
        {
            var button = new ToggleButton
            {
                Style = categoryButtonStyle,
                Foreground = (Brush)brushConverter.ConvertFromString(LogEntry.GetCategoryColor(category))!,
                Tag = category,
                Content = category.ToString(),
                ToolTip = category == LogCategory.Login
                    ? "Login/logout notices, session starts, shutdowns and the server welcome. Other system messages stay in System."
                    : $"Show {category} entries. Select more than one category to combine them."
            };
            button.Click += Category_Click;
            _categoryButtons[category] = button;
            CategoryPanel.Children.Add(button);
        }
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefilterAsync(); };
        _statsTimer.Tick += (_, _) => RefreshStatus();
        _statsTimer.Start();
        _fontSaveTimer.Tick += (_, _) => { _fontSaveTimer.Stop(); SaveSettings(); };
        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(commandLinePath))
            {
                try { _settings = _settings with { CustomLogPath = Path.GetFullPath(commandLinePath) }; }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { Notice("The supplied path is invalid. Choose a log file in Settings."); }
            }
            await StartWatchingAsync();
        };
        Closed += (_, _) =>
        {
            _tailCancellation?.Cancel(); _filterCancellation?.Cancel(); _searchTimer.Stop(); _statsTimer.Stop();
            if (_fontSaveTimer.IsEnabled) SaveSettings();
            _fontSaveTimer.Stop();
        };
    }

    private async Task StartWatchingAsync()
    {
        _tailCancellation?.Cancel();
        _tailCancellation = new();
        var token = _tailCancellation.Token;
        var generation = ++_generation;
        _filterCancellation?.Cancel();
        _filtering = false;
        _all.Clear(); _visible.Clear(); Array.Clear(_categoryCounts); _totalRead = 0; _pendingCount = 0; _follow = true;
        _lastReadAt = null;
        _filter = new LogFilter(SearchBox.Text, InvertToggle.IsChecked == true, _categories);
        var path = _settings.EffectivePath;
        _sourcePath = path;
        Title = "DSAce Log Viewer";
        var tailer = new LogTailer(path, _settings.Encoding);
        RefreshStatus();
        await Task.Run(async () =>
        {
            try
            {
                long number = 0;
                await tailer.RunAsync(async update =>
                {
                    if (update.Reset) number = 0;
                    var entries = update.Lines.Select(line => LogParser.Parse(line, ++number)).ToArray();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (generation != _generation || token.IsCancellationRequested) return;
                        Receive(update, entries);
                    }, DispatcherPriority.Background, token);
                }, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => { _available = false; _watchStatus = "Reading stopped: " + error.Message; RefreshStatus(); });
            }
        });
    }

    private void Receive(TailUpdate update, LogEntry[] entries)
    {
        _available = update.Available;
        _watchStatus = update.Status;
        if (update.Available) _lastReadAt = DateTime.Now;
        if (update.Reset)
        {
            _filterCancellation?.Cancel();
            _filtering = false;
            _all.Clear(); _visible.Clear(); Array.Clear(_categoryCounts); _totalRead = 0; _pendingCount = 0;
            _filter = new LogFilter(SearchBox.Text, InvertToggle.IsChecked == true, _categories);
            _changingView = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _changingView = false);
        }
        if (entries.Length > 0)
        {
            _totalRead += entries.Length;
            _all.AddRange(entries);
            foreach (var entry in entries) _categoryCounts[(int)entry.Category]++;
            var trimmed = _all.Count > MaximumRetainedEntries;
            if (trimmed)
            {
                var removeCount = _all.Count - MaximumRetainedEntries;
                for (var i = 0; i < removeCount; i++) _categoryCounts[(int)_all[i].Category]--;
                _all.RemoveRange(0, removeCount);
            }
            if (!_filtering)
            {
                if (trimmed)
                    while (_visible.Count > 0 && _visible[0].Number < _all[0].Number) _visible.RemoveAt(0);
                var added = 0;
                foreach (var entry in entries)
                    if (entry.Number >= _all[0].Number && _filter.Matches(entry)) { _visible.Add(entry); added++; }
                if (_follow && added > 0) ScrollToLatest();
                else if (!_follow) _pendingCount += added;
            }
        }
        RefreshStatus();
    }

    private void QueueFilter()
    {
        if (!IsInitialized || SearchBox is null) return;
        _searchTimer.Stop();
        _filterCancellation?.Cancel();
        _searchTimer.Start();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefilterAsync()
    {
        _filterCancellation?.Cancel();
        var cancellation = _filterCancellation = new();
        var filter = new LogFilter(SearchBox.Text, InvertToggle.IsChecked == true, _categories);
        var snapshot = _all.ToArray();
        var lastNumber = snapshot.LastOrDefault()?.Number ?? 0;
        var generation = _generation;
        var anchor = GetHistoryAnchor();
        _filtering = true;
        ExportButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var matches = new List<LogEntry>();
                foreach (var entry in snapshot)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter.Matches(entry)) matches.Add(entry);
                }
                return matches;
            }, cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != _generation) return;
            _filter = filter;
            _changingView = true;
            _visible.Clear();
            var oldest = _all.FirstOrDefault()?.Number ?? long.MaxValue;
            foreach (var entry in result) if (entry.Number >= oldest) _visible.Add(entry);
            foreach (var entry in _all) if (entry.Number > lastNumber && filter.Matches(entry)) _visible.Add(entry);
            _filtering = false;
            _pendingCount = 0;
            if (_follow) ScrollToLatest();
            else if (anchor is not null)
            {
                var kept = _visible.FirstOrDefault(entry => entry.Number >= anchor.Number);
                if (kept is not null) LogGrid.ScrollIntoView(kept);
            }
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _changingView = false);
            RefreshStatus();
        }
        catch (OperationCanceledException) { }
    }

    private void RefreshStatus()
    {
        ExportButton.IsEnabled = !_filtering && _visible.Count > 0;
        var noticeActive = _notice is not null && DateTime.UtcNow < _noticeUntil;
        LastReadText.Text = _lastReadAt is { } lastRead ? $"last read {lastRead:HH:mm:ss}" : "last read —";
        var followStatus = _follow ? "Following new entries" : $"Reading history · {_pendingCount:N0} new matching entries · Ctrl+End to follow";
        var retention = _totalRead > _all.Count ? $"\n{_totalRead:N0} read total · latest 100,000 retained" : "";
        LastReadText.ToolTip = $"{_sourcePath}\n{_watchStatus}\n{followStatus}\n{_visible.Count:N0} shown / {_all.Count:N0} loaded{retention}" + (noticeActive ? "\n" + _notice : "");
        EmptyPanel.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _all.Count > 0 ? "No matching entries" : "Waiting for your session";
        EmptyDescription.Text = _all.Count > 0 ? "Try a different search, turn off Invert, or select more event categories."
            : !_available ? _watchStatus : "New log entries will appear here automatically.";
        AllCategories.Content = $"All events  {_all.Count:N0}";
        AllCategories.IsChecked = _categories.Count == _categoryButtons.Count;
        foreach (var (category, button) in _categoryButtons)
        {
            button.Content = $"{category}  {_categoryCounts[(int)category]:N0}";
            button.IsChecked = _categories.Contains(category);
        }
    }

    private void Notice(string message) { _notice = message; _noticeUntil = DateTime.UtcNow.AddSeconds(12); RefreshStatus(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => QueueFilter();
    private void Invert_Click(object sender, RoutedEventArgs e) => QueueFilter();
    private void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); InvertToggle.IsChecked = false; QueueFilter(); SearchBox.Focus(); }
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (((ToggleButton)sender).Tag is LogCategory category)
        {
            if (!_categories.Add(category)) _categories.Remove(category);
        }
        else if (_categories.Count == _categoryButtons.Count) _categories.Clear();
        else _categories.UnionWith(Enum.GetValues<LogCategory>());
        RefreshStatus(); QueueFilter();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose DSAce.log", Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*", FileName = "DSAce.log" };
        if (dialog.ShowDialog(this) == true) await UseSettingsAsync(_settings with { CustomLogPath = dialog.FileName });
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true) await UseSettingsAsync(dialog.Result);
    }

    private async Task UseSettingsAsync(ViewerSettings settings)
    {
        var sourceChanged = !string.Equals(_sourcePath, settings.EffectivePath, StringComparison.OrdinalIgnoreCase) || _settings.Encoding != settings.Encoding;
        var reflow = !sourceChanged && settings.WordWrap != _settings.WordWrap;
        var anchor = reflow ? GetHistoryAnchor() : null;
        if (reflow) _changingView = true;

        _settings = settings;
        SaveSettings();
        ApplyDisplaySettings();
        if (sourceChanged) await StartWatchingAsync();
        else if (reflow)
        {
            if (_follow) ScrollToLatest();
            else if (anchor is not null) LogGrid.ScrollIntoView(anchor);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _changingView = false);
        }
    }

    private void SaveSettings()
    {
        try { _settings.Save(_settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notice("Settings could not be saved: " + error.Message); }
    }

    private void ApplyDisplaySettings()
    {
        _applyingSettings = true;
        DataContext = _settings;
        LineNumberColumn.Visibility = _settings.ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
        EntryTypeColumn.Visibility = _settings.ShowEntryType ? Visibility.Visible : Visibility.Collapsed;
        var scrollUnit = _settings.WordWrap ? ScrollUnit.Pixel : ScrollUnit.Item;
        if (VirtualizingPanel.GetScrollUnit(LogGrid) != scrollUnit)
        {
            VirtualizingPanel.SetScrollUnit(LogGrid, scrollUnit);
            ScrollViewer.SetHorizontalScrollBarVisibility(LogGrid, _settings.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            MessageColumn.MinWidth = _settings.WordWrap ? 80 : 390;
            MessageColumn.Width = _settings.WordWrap ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.SizeToCells;
            MessageColumn.CanUserResize = !_settings.WordWrap;
        }
        FontSizeSlider.Value = _settings.LogFontSize;
        SetLogFontSize(_settings.LogFontSize);
        _applyingSettings = false;
    }

    private void SetLogFontSize(double size)
    {
        LogGrid.FontSize = size;
        LogGrid.RowHeight = _settings.WordWrap ? double.NaN : Math.Max(26, Math.Ceiling(size + 12));
        TimestampColumn.Width = new DataGridLength(size * 12.5);
        FontSizeText.Text = size.ToString("0");
    }

    private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LogGrid is null || FontSizeText is null) return;
        SetLogFontSize(e.NewValue);
        if (_applyingSettings) return;
        _settings = _settings with { LogFontSize = e.NewValue };
        _fontSaveTimer.Stop(); _fontSaveTimer.Start();
        if (_follow) ScrollToLatest();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_filtering || _visible.Count == 0) return;
        var snapshot = _visible.ToArray();
        var dialog = new SaveFileDialog { Title = $"Export {snapshot.Length:N0} visible entries", Filter = "Text log (*.log)|*.log|CSV table (*.csv)|*.csv", FileName = "DSAce-filtered-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), AddExtension = true, DefaultExt = ".log" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(_settings.EffectivePath), StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Choose a different destination to preserve the source log.", "Export", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            await Task.Run(() => LogExporter.ExportAsync(dialog.FileName, snapshot, Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)));
            Notice($"Exported {snapshot.Length:N0} matching entries to {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageBox.Show(this, error.Message, "Could not export", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected();
    private void CopySelected()
    {
        var selected = LogGrid.SelectedItems.Cast<LogEntry>().OrderBy(entry => entry.Number).ToArray();
        if (selected.Length == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(entry => entry.Raw))); Notice($"Copied {selected.Length:N0} original log lines"); }
        catch (System.Runtime.InteropServices.COMException) { Notice("Clipboard is busy. Try copying again."); }
    }

    private void ResumeFollowing() { _follow = true; _pendingCount = 0; ScrollToLatest(); RefreshStatus(); }
    private void ScrollToLatest()
    {
        if (_visible.Count == 0) return;
        _changingView = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_follow && _visible.Count > 0)
            {
                if (_scroll is not null) _scroll.ScrollToBottom();
                else LogGrid.ScrollIntoView(_visible[^1]);
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _changingView = false);
        });
    }

    private void LogGrid_Loaded(object sender, RoutedEventArgs e) => _scroll = FindScrollViewer(LogGrid);
    private void LogGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_follow) ScrollToLatest();
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        { var found = FindScrollViewer(VisualTreeHelper.GetChild(element, i)); if (found is not null) return found; }
        return null;
    }

    private void LogGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0 && _scroll is not null && _scroll.ScrollableHeight > 0) { _follow = false; RefreshStatus(); }
    }

    private void LogCell_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_settings.WordWrap || _scroll is null || sender is not DataGridCell cell || !cell.IsDescendantOf(_scroll)) return;
        var left = cell.TranslatePoint(new Point(), _scroll).X;
        if (left >= _scroll.ViewportWidth || left + cell.ActualWidth <= 0) return;
        if (ItemsControl.ContainerFromElement(LogGrid, cell) is not DataGridRow row) return;

        // Handle focus on an already visible cell before the request reaches the ScrollViewer.
        e.Handled = true;
        var index = row.GetIndex();
        if (index < 0) return;
        if (index < _scroll.VerticalOffset) _scroll.ScrollToVerticalOffset(index);
        else if (index >= _scroll.VerticalOffset + _scroll.ViewportHeight)
            _scroll.ScrollToVerticalOffset(Math.Max(0, index - _scroll.ViewportHeight + 1));
    }

    private LogEntry? GetHistoryAnchor()
    {
        if (_follow || _scroll is null || _visible.Count == 0) return null;
        if (!_settings.WordWrap)
            return _visible[Math.Clamp((int)_scroll.VerticalOffset, 0, _visible.Count - 1)];

        // Wrapped rows have different heights, so the scroll offset is no longer an item index.
        if (_scroll.Template.FindName("PART_ScrollContentPresenter", _scroll) is not ScrollContentPresenter viewport)
            return null;

        var hit = VisualTreeHelper.HitTest(viewport, new Point(viewport.ActualWidth / 2, 1));
        return hit is not null && ItemsControl.ContainerFromElement(LogGrid, hit.VisualHit) is DataGridRow row
            ? row.Item as LogEntry
            : null;
    }

    private void LogGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_changingView || _filtering || _scroll is null || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.VerticalChange == 0) return;
        _follow = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 0.5;
        if (_follow) _pendingCount = 0;
        RefreshStatus();
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
            await UseSettingsAsync(_settings with { CustomLogPath = files[0] });
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.O) { Open_Click(this, new()); e.Handled = true; }
            else if (e.Key == Key.E) { Export_Click(this, new()); e.Handled = true; }
            else if (e.Key == Key.End && !SearchBox.IsKeyboardFocusWithin) { ResumeFollowing(); e.Handled = true; }
            else if (e.Key == Key.C && LogGrid.IsKeyboardFocusWithin) { CopySelected(); e.Handled = true; }
        }
        else if (LogGrid.IsKeyboardFocusWithin && e.Key is Key.PageUp or Key.Home or Key.Up) { _follow = false; RefreshStatus(); }
    }
}
