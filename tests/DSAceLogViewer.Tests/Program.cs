using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DSAceLogViewer;
using DSAceLogViewer.Core;

internal static class Program
{
    private static int _passed, _failed;
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "DSAceViewer-tests-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Scratch);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var operation = RunAsync(args);
        operation.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.Run();
        try { operation.GetAwaiter().GetResult(); }
        catch (Exception error) { Console.WriteLine("RUNNER FAILURE: " + error); _failed++; }
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
        // Only the unique scratch directory made by this test runner is removed.
        Directory.Delete(Scratch, true);
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string[] args)
    {
        await Test("timestamp and source preserved", () =>
        {
            const string raw = "[10.11.2026 16:08:24] [OOC] Pilot.One: hello there";
            var entry = LogParser.Parse(raw, 42);
            Equal("10.11.2026 16:08:24", entry.Timestamp); Equal(raw, entry.Raw); Equal(42L, entry.Number); Equal(LogCategory.OOC, entry.Category);
        });
        var classifications = new (string Text, LogCategory Category)[]
        {
            ("[OOC] Pilot: hello", LogCategory.OOC), ("(OOC) Pilot: hello", LogCategory.OOC),
            ("2026-11-10 16:11:24 SMT  Traffic control alert: Pilot has docked", LogCategory.Traffic),
            ("Traffic control alert: Pilot has launched", LogCategory.Traffic),
            ("Death: Pilot was put out of action by a hostile vessel.", LogCategory.Combat),
            ("Initialising", LogCategory.Login), ("Freelancer Started", LogCategory.Login), ("Shutdown", LogCategory.Login),
            ("Welcome to the Discovery Freelancer 24/7 RP server!", LogCategory.Login),
            ("Player Pilot has logged in", LogCategory.Login), ("[Login] Pilot", LogCategory.Login),
            ("Pilot: I logged in yesterday", LogCategory.Chat), ("Pilot: Traffic control alert: hello", LogCategory.Chat),
            ("Pilot: Death: this is a chat message", LogCategory.Chat),
            ("Ping: 125ms (Max: 1000ms) Fluct: 0ms Loss: 0% (Max: 70%) Lag: 1% (Max: 100%)", LogCategory.Other),
            ("[10.11.2026 12:00:00] Ping: 125ms (Max: 1000ms) Fluct: 0ms Loss: 0% (Max: 70%) Lag: 1% (Max: 100%)", LogCategory.Other),
            ("PING: 35.5 ms (Max: 1000ms) Fluct: 0ms Loss: 0% Lag: 0%", LogCategory.Other),
            ("/ping", LogCategory.Other),
            ("Pilot: Ping: 125ms (Max: 1000ms)", LogCategory.Chat),
            ("Ping: hello there", LogCategory.Chat),
            ("[Group] Pilot: please use /ping", LogCategory.Chat),
            ("Rules Tip: Discovery is a role-playing server.", LogCategory.System),
            ("ERR: Base cannot accept goods, stock limit reached.", LogCategory.System),
            ("Permitted player-owned base good sale due to base admin login.", LogCategory.System),
            ("[Group] Pilot: hello", LogCategory.Chat), ("An unrecognized server notice", LogCategory.Other),
            ("CONSOLE: scheduled server notice", LogCategory.CONSOLE),
            ("console: lowercase sender", LogCategory.CONSOLE),
            ("[OOC] CONSOLE: channel notice", LogCategory.CONSOLE),
            ("Pilot: CONSOLE: quoted message", LogCategory.Chat),
            ("CONSOLE-Pilot: regular player", LogCategory.Chat)
        };
        foreach (var (text, category) in classifications)
            await Test("classification: " + text.Split(':')[0], () => Equal(category, LogParser.Parse(text, 1).Category));
        await Test("joined Shutdown and timestamp split", () =>
        {
            var split = LogParser.SplitJoinedEntries("Shutdown[11.11.2026 01:02:03] Initialising").ToArray();
            Equal(2, split.Length); Equal("Shutdown", split[0]); Equal("[11.11.2026 01:02:03] Initialising", split[1]);
        });
        var chat = LogParser.Parse("[11.11.2026 12:00:00] Pilot-Ataraxia: approaching Manhattan station", 1);
        await Test("search ignores case", () => True(new LogFilter("MANHATTAN").Matches(chat)));
        await Test("search handles omitted letters", () => True(new LogFilter("atarxia").Matches(chat)));
        await Test("search handles transposition", () => True(new LogFilter("statoin").Matches(chat)));
        await Test("search handles substitution", () => True(new LogFilter("pikot").Matches(chat)));
        await Test("search handles multiple unordered terms", () => True(new LogFilter("station atarxia").Matches(chat)));
        await Test("search requires every term", () => True(!new LogFilter("station nomad").Matches(chat)));
        await Test("inverted search hides matches", () => True(!new LogFilter("manhattan", true).Matches(chat)));
        await Test("inverted search shows nonmatches", () => True(new LogFilter("nomad", true).Matches(chat)));
        await Test("empty inverted query keeps all rows", () => True(new LogFilter("   ", true).Matches(chat)));
        await Test("inversion respects category filter", () => True(!new LogFilter("nomad", true, [LogCategory.Traffic]).Matches(chat)));
        await Test("multiple categories are OR", () => True(new LogFilter(categories: [LogCategory.Chat, LogCategory.OOC]).Matches(chat)));
        await Test("unselecting all categories hides every entry", () => True(!new LogFilter(categories: []).Matches(chat)));
        await Test("invert cannot restore an unselected category", () => True(!new LogFilter("nomad", true, []).Matches(chat)));
        await Test("unrelated fuzzy terms do not match", () => True(!new LogFilter("wombat").Matches(chat)));

        await TestAsync("tail initial load, append and no duplicates", async () =>
        {
            var path = Temp("append.log");
            await File.WriteAllTextAsync(path, "Pilot: first\r\n", new UTF8Encoding(false));
            var tailer = new LogTailer(path);
            var initial = await tailer.ReadOnceAsync(); True(initial.Reset); Equal("Pilot: first", initial.Lines.Single());
            Equal(0, (await tailer.ReadOnceAsync()).Lines.Count);
            await File.AppendAllTextAsync(path, "[OOC] Pilot: second\n");
            var next = await tailer.ReadOnceAsync(); True(!next.Reset); Equal("[OOC] Pilot: second", next.Lines.Single());
        });
        await TestAsync("partial final lines are buffered exactly once", async () =>
        {
            var path = Temp("partial.log"); await File.WriteAllTextAsync(path, "Pilot: par"); var tailer = new LogTailer(path);
            Equal(0, (await tailer.ReadOnceAsync()).Lines.Count);
            await File.AppendAllTextAsync(path, "tial\r"); Equal(0, (await tailer.ReadOnceAsync()).Lines.Count);
            await File.AppendAllTextAsync(path, "\n"); Equal("Pilot: partial", (await tailer.ReadOnceAsync()).Lines.Single());
            Equal(0, (await tailer.ReadOnceAsync()).Lines.Count);
        });
        await TestAsync("split UTF8 characters survive append boundaries", async () =>
        {
            var path = Temp("unicode.log"); var bytes = Encoding.UTF8.GetBytes("Pilot: café 🚀\n");
            await File.WriteAllBytesAsync(path, bytes[..^3]); var tailer = new LogTailer(path, "UTF-8");
            Equal(0, (await tailer.ReadOnceAsync()).Lines.Count);
            await using (var writer = new FileStream(path, FileMode.Append)) await writer.WriteAsync(bytes.AsMemory(bytes.Length - 3));
            Equal("Pilot: café 🚀", (await tailer.ReadOnceAsync()).Lines.Single());
        });
        await TestAsync("UTF16 BOM detected", async () =>
        {
            var path = Temp("utf16.log"); await File.WriteAllTextAsync(path, "Pilot: café\r\n", Encoding.Unicode);
            Equal("Pilot: café", (await new LogTailer(path).ReadOnceAsync()).Lines.Single());
        });
        await TestAsync("split BOM is not consumed prematurely", async () =>
        {
            var path = Temp("bom.log"); await File.WriteAllBytesAsync(path, [0xEF]); var tailer = new LogTailer(path);
            Equal(0, (await tailer.ReadOnceAsync()).Lines.Count);
            await using (var writer = new FileStream(path, FileMode.Append)) await writer.WriteAsync(new byte[] { 0xBB, 0xBF, 65, 10 });
            Equal("A", (await tailer.ReadOnceAsync()).Lines.Single());
        });
        await TestAsync("legacy ANSI encoding auto-detected", async () =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var path = Temp("ansi.log"); await File.WriteAllBytesAsync(path, Encoding.GetEncoding(1252).GetBytes("Pilot: café\n"));
            Equal("Pilot: café", (await new LogTailer(path).ReadOnceAsync()).Lines.Single());
        });
        await TestAsync("truncation resets line decoder and history", async () =>
        {
            var path = Temp("truncate.log"); await File.WriteAllTextAsync(path, "old long line\nunfinished"); var tailer = new LogTailer(path);
            await tailer.ReadOnceAsync(); await File.WriteAllTextAsync(path, "new\n");
            var update = await tailer.ReadOnceAsync(); True(update.Reset); Equal("new", update.Lines.Single());
        });
        await TestAsync("same-size in-place replacement detected", async () =>
        {
            var path = Temp("same-size.log"); await File.WriteAllTextAsync(path, "old\n"); var tailer = new LogTailer(path);
            await tailer.ReadOnceAsync(); await File.WriteAllTextAsync(path, "new\n");
            var update = await tailer.ReadOnceAsync(); True(update.Reset); Equal("new", update.Lines.Single());
        });
        await TestAsync("rotation to a new file identity detected", async () =>
        {
            var path = Temp("rotate.log"); await File.WriteAllTextAsync(path, "old\n"); var tailer = new LogTailer(path);
            await tailer.ReadOnceAsync(); File.Move(path, path + ".1"); await File.WriteAllTextAsync(path, "new\n");
            var update = await tailer.ReadOnceAsync(); True(update.Reset); Equal("new", update.Lines.Single());
        });
        await TestAsync("absent file recovers when created", async () =>
        {
            var path = Temp("missing.log"); var tailer = new LogTailer(path); True(!(await tailer.ReadOnceAsync()).Available);
            await File.WriteAllTextAsync(path, "ready\n"); var update = await tailer.ReadOnceAsync(); True(update.Available); Equal("ready", update.Lines.Single());
        });
        await TestAsync("long malformed line bounded and next line retained", async () =>
        {
            var path = Temp("long.log"); await File.WriteAllTextAsync(path, new string('x', LogTailer.MaximumLineCharacters + 5000) + "\nnext\n");
            var update = await new LogTailer(path).ReadOnceAsync(); Equal(2, update.Lines.Count); True(update.Lines[0].Contains("line truncated")); Equal("next", update.Lines[1]);
        });
        await TestAsync("writer remains free while viewer reads", async () =>
        {
            var path = Temp("sharing.log");
            await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            await writer.WriteAsync(Encoding.UTF8.GetBytes("live\n")); await writer.FlushAsync();
            Equal("live", (await new LogTailer(path).ReadOnceAsync()).Lines.Single());
        });
        await TestAsync("cancellation exits polling promptly", async () =>
        {
            using var token = new CancellationTokenSource(); var tailer = new LogTailer(Temp("cancel.log"));
            var operation = tailer.RunAsync(_ => { token.Cancel(); return Task.CompletedTask; }, token.Token);
            try { await operation; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        });
        await Test("custom settings persist across reload", () =>
        {
            var path = Temp("settings.json"); var settings = new ViewerSettings { CustomLogPath = Temp("chosen.log"), Encoding = "Windows-1252" };
            settings.Save(path); var restored = ViewerSettings.Load(path); Equal(settings.CustomLogPath, restored.EffectivePath); Equal(settings.Encoding, restored.Encoding);
        });
        await Test("resetting to auto clears saved custom location", () =>
        {
            var path = Temp("default-settings.json"); new ViewerSettings { CustomLogPath = null }.Save(path); True(ViewerSettings.Load(path).CustomLogPath is null);
        });
        await Test("display preferences and font size persist", () =>
        {
            var path = Temp("display-settings.json");
            new ViewerSettings { ShowLineNumbers = true, ShowEntryType = true, WordWrap = true, LogFontSize = 19 }.Save(path);
            var restored = ViewerSettings.Load(path);
            True(restored.ShowLineNumbers); True(restored.ShowEntryType); True(restored.WordWrap); Equal(19d, restored.LogFontSize);
        });
        await Test("legacy settings leave optional columns off", () =>
        {
            var path = Temp("legacy-settings.json"); File.WriteAllText(path, "{\"Encoding\":\"Auto\"}");
            var restored = ViewerSettings.Load(path);
            True(!restored.ShowLineNumbers); True(!restored.ShowEntryType); True(restored.WordWrap); Equal(14d, restored.LogFontSize);
        });
        await Test("malformed settings safely use defaults", () =>
        {
            var path = Temp("broken.json"); File.WriteAllText(path, "{broken"); True(ViewerSettings.Load(path).CustomLogPath is null);
        });
        await Test("auto detection includes AppData and redirected Documents", () =>
        {
            var candidates = LogDiscovery.Candidates().ToArray();
            True(candidates.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Discovery Freelancer", "DSAce.log")));
            True(candidates.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Freelancer", "DSAce.log")));
        });
        await TestAsync("text export preserves original selected lines and Unicode", async () =>
        {
            var path = Temp("export.log"); var entries = new[] { LogParser.Parse("[11.11.2026 01:00:00] Pilot: café", 4) };
            await LogExporter.ExportAsync(path, entries, false); Equal(entries[0].Raw + Environment.NewLine, await File.ReadAllTextAsync(path));
        });
        await TestAsync("CSV escapes quotes, commas and preserves multiline cells", async () =>
        {
            var path = Temp("export.csv"); var entry = LogParser.Parse("Pilot: \"hello, world\"", 4);
            await LogExporter.ExportAsync(path, [entry], true); var text = await File.ReadAllTextAsync(path);
            True(text.StartsWith("Line,Timestamp,Category,Message")); True(text.Contains("\"Pilot: \"\"hello, world\"\"\""));
        });

        await TestAsync("WPF event filters, fuzzy search, invert, following and layout", async () => await TestWindowAsync(args));
        var logIndex = Array.IndexOf(args, "--log");
        if (logIndex >= 0 && logIndex + 1 < args.Length)
            await TestAsync("real log read and classification (read-only)", async () =>
            {
                var update = await new LogTailer(args[logIndex + 1]).ReadOnceAsync(); True(update.Available); True(update.Lines.Count > 0);
                var categories = update.Lines.Select((line, index) => LogParser.Parse(line, index + 1)).GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count());
                Console.WriteLine("  Real-log categories: " + string.Join(", ", categories.Select(c => $"{c.Key}={c.Value}")));
                True(categories.ContainsKey(LogCategory.OOC)); True(categories.ContainsKey(LogCategory.Traffic));
            });
    }

    private static async Task TestWindowAsync(string[] args)
    {
        var app = new App(); app.InitializeComponent();
        var uiSettingsPath = Temp("ui-settings.json");
        var window = new MainWindow(initialSettings: new() { WordWrap = false }, settingsPath: uiSettingsPath); // Never shown; no desktop automation or user-preference writes.
        var grid = (DataGrid)window.FindName("LogGrid");
        var root = (FrameworkElement)window.Content;
        void Layout(double width = 1228, double height = 750)
        { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        var lines = new[]
        {
            "[11.11.2026 12:00:00] Freelancer Started",
            "[11.11.2026 12:00:01] Welcome to the Discovery Freelancer 24/7 RP server!",
            "[11.11.2026 12:00:02] [OOC] ZDF-Scout: anyone around Manhattan?",
            "[11.11.2026 12:00:03] 2026-11-11 12:00:03 SMT  Traffic control alert: ZDF-Scout has docked",
            "[11.11.2026 12:00:04] ZDF-Scout: requesting clearance for departure",
            "[11.11.2026 12:00:05] Death: Rogue.Patrol was put out of action by a hostile vessel.",
            "[11.11.2026 12:00:06] [OOC] Wingmate: meet you at the station",
            "[11.11.2026 12:00:07] Tip: Use /help to find available commands."
        };
        var entries = Enumerable.Range(0, 100).SelectMany(round => lines.Select(line => line.Replace("12:00:", $"12:{round % 60:00}:")))
            .Select((line, index) => LogParser.Parse(line, index + 1)).ToArray();
        Invoke(window, "Receive", new TailUpdate([], true, "Watching for new entries", true), entries);
        Layout(); Invoke(window, "LogGrid_Loaded", grid, new RoutedEventArgs());
        await Task.Delay(100);
        Equal(800, grid.Items.Count);
        Equal("#FF191919", window.Background.ToString());
        Equal("#FFE8E8E8", window.Foreground.ToString());
        Equal(Visibility.Collapsed, ((DataGridColumn)window.FindName("LineNumberColumn")).Visibility);
        Equal(Visibility.Collapsed, ((DataGridColumn)window.FindName("EntryTypeColumn")).Visibility);
        True(grid.FontFamily.Source.StartsWith("Arial Unicode MS"));
        True(((TextBlock)window.FindName("LastReadText")).Text.StartsWith("last read "));
        var fontSlider = (Slider)window.FindName("FontSizeSlider"); fontSlider.Value = 18;
        await Task.Delay(500); Layout(); Equal(18d, grid.FontSize); Equal(18d, ViewerSettings.Load(uiSettingsPath).LogFontSize);
        fontSlider.Value = 14; await Task.Delay(500); Layout();
        var buttons = (Panel)window.FindName("CategoryPanel");
        True(buttons.Children.OfType<ToggleButton>().All(button => button.IsChecked == true));
        var ooc = buttons.Children.OfType<ToggleButton>().Single(b => b.Tag is LogCategory.OOC);
        ooc.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(500); Layout(); Equal(600, grid.Items.Count);
        var allButton = (ToggleButton)window.FindName("AllCategories");
        allButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(500); Equal(800, grid.Items.Count);
        allButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(500); Equal(0, grid.Items.Count);
        ooc.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(500); Layout(); Equal(200, grid.Items.Count);
        var search = (TextBox)window.FindName("SearchBox"); search.Text = "manhatan";
        await Task.Delay(500); Layout(); Equal(100, grid.Items.Count);
        var invert = (ToggleButton)window.FindName("InvertToggle"); invert.IsChecked = true; invert.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(500); Layout(); Equal(100, grid.Items.Count); True(grid.Items.Cast<LogEntry>().All(e => e.Message.Contains("Wingmate")));
        search.Clear(); await Task.Delay(500); Equal(200, grid.Items.Count);
        ((ToggleButton)window.FindName("AllCategories")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(500); Layout(); Equal(800, grid.Items.Count);
        // Simulate the ScrollChanged event emitted by a user moving into history.
        var scroll = FindVisual<ScrollViewer>(grid) ?? throw new Exception("Grid scroll viewer missing");
        scroll.ScrollToTop(); Layout();
        var scrollEvent = Activator.CreateInstance(typeof(ScrollChangedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new Vector(0, 0), new Vector(0, -10), new Size(50, 800), new Vector(0, 0), new Size(50, 10), new Vector(0, 0) }, null)!;
        Invoke(window, "LogGrid_ScrollChanged", grid, scrollEvent);
        True(((TextBlock)window.FindName("LastReadText")).ToolTip.ToString()!.Contains("Reading history"));
        Equal(Visibility.Visible, ((Button)window.FindName("JumpToBottomButton")).Visibility);
        var wideEntry = LogParser.Parse("[11.11.2026 13:00:00] [OOC] ZDF-Scout: " + new string('W', 180), 801);
        Invoke(window, "Receive", new TailUpdate([], false, "Watching for new entries", true), new[] { wideEntry });
        Equal(801, grid.Items.Count); True(((TextBlock)window.FindName("LastReadText")).ToolTip.ToString()!.Contains("1 new"));
        ((Button)window.FindName("JumpToBottomButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(100); Layout(); True(((TextBlock)window.FindName("LastReadText")).ToolTip.ToString()!.Contains("Following"));
        Equal(Visibility.Collapsed, ((Button)window.FindName("JumpToBottomButton")).Visibility);
        // A click focuses the cell, which raises this same WPF BringIntoView request.
        Layout(850, 570);
        Invoke(window, "ResumeFollowing");
        await Task.Delay(100);
        Layout(850, 570);
        var row = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(wideEntry)
            ?? throw new Exception("The wide log row was not realized");
        var cell = FindAllVisual<DataGridCell>(row).Single(candidate => candidate.Column == grid.Columns[^1]);
        True(cell.ActualWidth > scroll.ViewportWidth);
        True(scroll.ScrollableWidth > 75);
        foreach (var offset in new[] { 0d, 75d })
        {
            scroll.ScrollToHorizontalOffset(offset);
            Layout(850, 570);
            Equal(offset, scroll.HorizontalOffset);
            cell.BringIntoView();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Layout(850, 570);
            Equal(offset, scroll.HorizontalOffset);
        }
        Invoke(window, "ResumeFollowing");
        await Task.Delay(100);
        Layout(850, 570);
        Equal(75d, scroll.HorizontalOffset);
        True(scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5);
        var outIndex = Array.IndexOf(args, "--render");
        if (outIndex >= 0 && outIndex + 1 < args.Length)
        {
            var directory = Path.GetFullPath(args[outIndex + 1]); Directory.CreateDirectory(directory);
            Layout(); Invoke(window, "ResumeFollowing");
            await Task.Delay(100); Layout(); Render(root, Path.Combine(directory, "viewer-desktop.png"), 1228, 750);
            Console.WriteLine($"  Desktop following offset: {scroll.VerticalOffset}/{scroll.ScrollableHeight}; viewport {scroll.ViewportHeight}");
            True(scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5);
            Layout(850, 570); Invoke(window, "ResumeFollowing");
            await Task.Delay(100); Layout(850, 570); Render(root, Path.Combine(directory, "viewer-compact.png"), 850, 570);
            Console.WriteLine($"  Compact following offset: {scroll.VerticalOffset}/{scroll.ScrollableHeight}; viewport {scroll.ViewportHeight}");
            True(scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5);
            var settings = new SettingsWindow(new ViewerSettings());
            True(((CheckBox)settings.FindName("LineNumbersBox")).IsChecked == false);
            True(((CheckBox)settings.FindName("EntryTypeBox")).IsChecked == false);
            True(((CheckBox)settings.FindName("WordWrapBox")).IsChecked == true);
            var settingsRoot = (FrameworkElement)settings.Content; settingsRoot.Measure(new Size(620, 300)); settingsRoot.Arrange(new Rect(0, 0, 620, 300)); settingsRoot.UpdateLayout();
            Render(settingsRoot, Path.Combine(directory, "settings.png"), 620, 300); settings.Close();
            Console.WriteLine("  WPF renders: " + directory);
        }
        var displaySettings = ViewerSettings.Load(uiSettingsPath) with
        {
            ShowLineNumbers = true, ShowEntryType = true, WordWrap = true, LogFontSize = 24
        };
        var useSettings = window.GetType().GetMethod("UseSettingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)useSettings.Invoke(window, [displaySettings])!;
        Equal(Visibility.Visible, ((DataGridColumn)window.FindName("LineNumberColumn")).Visibility);
        Equal(Visibility.Visible, ((DataGridColumn)window.FindName("EntryTypeColumn")).Visibility);
        Equal(801, grid.Items.Count); // Display-only settings preserve the loaded history.
        True(ViewerSettings.Load(uiSettingsPath).WordWrap);

        async Task<DataGridRow> LayoutLastRow(double width)
        {
            Layout(width, 570);
            Invoke(window, "ResumeFollowing");
            await Task.Delay(100);
            Layout(width, 570);
            return (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(wideEntry)
                ?? throw new Exception("The long log row was not visible after reflow");
        }

        var wideLayoutRow = await LayoutLastRow(1228);
        var wideLayoutHeight = wideLayoutRow.ActualHeight;
        var narrowLayoutRow = await LayoutLastRow(850);
        var wrappedMessage = (TextBlock)grid.Columns[^1].GetCellContent(narrowLayoutRow);
        Equal(TextWrapping.Wrap, wrappedMessage.TextWrapping);
        Equal(wideEntry.Message, wrappedMessage.Text);
        True(narrowLayoutRow.ActualHeight > wideLayoutHeight);
        Equal(0d, scroll.ScrollableWidth);
        True(scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5);
        Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(grid));
        if (outIndex >= 0 && outIndex + 1 < args.Length)
            Render(root, Path.Combine(Path.GetFullPath(args[outIndex + 1]), "viewer-wrapped.png"), 850, 570);

        scroll.ScrollToVerticalOffset(Math.Max(0, scroll.ScrollableHeight - 40));
        Layout(850, 570);
        var historyAnchor = (LogEntry?)window.GetType().GetMethod("GetHistoryAnchor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Equal(wideEntry, historyAnchor);
        invert.IsChecked = false;
        search.Text = "WWWW";
        await Task.Delay(500);
        Layout(850, 570);
        Equal(1, grid.Items.Count);
        Equal(wideEntry, grid.Items[0]);
        search.Clear();
        await Task.Delay(500);
        Layout(850, 570);
        Equal(801, grid.Items.Count);

        await (Task)useSettings.Invoke(window, [displaySettings with { WordWrap = false }])!;
        var unwrappedRow = await LayoutLastRow(850);
        Equal(TextWrapping.NoWrap, ((TextBlock)grid.Columns[^1].GetCellContent(unwrappedRow)).TextWrapping);
        True(unwrappedRow.ActualHeight < wideLayoutHeight);
        True(scroll.ScrollableWidth > 75);
        True(!ViewerSettings.Load(uiSettingsPath).WordWrap);

        var consoleButton = buttons.Children.OfType<ToggleButton>().Single(button => button.Tag is LogCategory.CONSOLE);
        var trafficButton = buttons.Children.OfType<ToggleButton>().Single(button => button.Tag is LogCategory.Traffic);
        var chatButton = buttons.Children.OfType<ToggleButton>().Single(button => button.Tag is LogCategory.Chat);
        var pinEntries = Enumerable.Range(0, 120).Select(index =>
        {
            var message = (index % 4) switch
            {
                0 => "CONSOLE: scheduled server notice",
                1 => "Pilot: rendezvous at the station",
                2 => "[OOC] Wingmate: ready for the next trip",
                _ => "Traffic control alert: Pilot has docked"
            };
            if (index % 3 == 0) message += " " + string.Join(" ", Enumerable.Repeat("extra context for a wrapped message", 3));
            return LogParser.Parse($"[11.11.2026 14:00:00] {message} {index + 1}", index + 1);
        }).ToArray();
        var pinned = new[] { pinEntries[41], pinEntries[45] };

        foreach (var wrap in new[] { false, true })
        {
            await (Task)useSettings.Invoke(window, [displaySettings with
            {
                WordWrap = wrap, LogFontSize = 14, ShowLineNumbers = false, ShowEntryType = false
            }])!;
            Invoke(window, "Receive", new TailUpdate([], true, "Watching for new entries", true), pinEntries);
            Layout(850, 570);
            Invoke(window, "ResumeFollowing");
            await Task.Delay(100);
            Layout(850, 570);
            grid.ScrollIntoView(pinned[1]);
            await Task.Delay(100);
            Layout(850, 570);
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll)!;
            double PinTop()
            {
                var pinRow = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(pinned[1])
                    ?? throw new Exception("The selected anchor row left the viewport");
                return pinRow.TranslatePoint(new Point(), viewport).Y;
            }
            if (wrap) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + PinTop() - 78);
            else scroll.ScrollToVerticalOffset(42);
            Layout(850, 570);
            grid.SelectedItems.Add(pinned[0]);
            grid.SelectedItems.Add(pinned[1]);
            var pinnedTop = PinTop();
            var pinnedHorizontal = scroll.HorizontalOffset;

            async Task ToggleAndCheck(ToggleButton button, int expectedSelection)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Task.Delay(500);
                Layout(850, 570);
                Equal(expectedSelection, grid.SelectedItems.Count);
                if (expectedSelection > 0)
                {
                    True(pinned.All(entry => grid.SelectedItems.Contains(entry)));
                    if (Math.Abs(PinTop() - pinnedTop) >= 1.5)
                        throw new Exception($"Selected row moved with wrap={wrap}, filter={button.Tag}: {pinnedTop} to {PinTop()}, scroll={scroll.VerticalOffset}");
                    Equal(pinnedHorizontal, scroll.HorizontalOffset);
                }
            }

            await ToggleAndCheck(trafficButton, 2);
            await ToggleAndCheck(trafficButton, 2);
            await ToggleAndCheck(chatButton, 0);
            await ToggleAndCheck(chatButton, 2);
            await ToggleAndCheck(allButton, 0);
            Equal(0, grid.Items.Count);
            await ToggleAndCheck(allButton, 2);
            await ToggleAndCheck(consoleButton, 2);
            True(grid.Items.Cast<LogEntry>().All(entry => entry.Category != LogCategory.CONSOLE));
            True(grid.Items.Cast<LogEntry>().Any(entry => entry.Category == LogCategory.Chat));
            await ToggleAndCheck(consoleButton, 2);
            Equal(Visibility.Visible, ((Button)window.FindName("JumpToBottomButton")).Visibility);
            if (outIndex >= 0 && outIndex + 1 < args.Length)
            {
                var imageName = wrap ? "viewer-pinned-wrapped.png" : "viewer-pinned.png";
                Render(root, Path.Combine(Path.GetFullPath(args[outIndex + 1]), imageName), 850, 570);
            }
            ((Button)window.FindName("JumpToBottomButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(100);
            Layout(850, 570);
            True(scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5);
            Equal(Visibility.Collapsed, ((Button)window.FindName("JumpToBottomButton")).Visibility);
            grid.UnselectAll();
        }
        window.Close();
    }

    private static T? FindVisual<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        { var child = FindVisual<T>(VisualTreeHelper.GetChild(element, i)); if (child is not null) return child; }
        return null;
    }

    private static IEnumerable<T> FindAllVisual<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T found) yield return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var child in FindAllVisual<T>(VisualTreeHelper.GetChild(element, i))) yield return child;
    }

    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 25, 25)), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Invoke(object instance, string method, params object[] values) => instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, values);
    private static string Temp(string filename) => Path.Combine(Scratch, filename);
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static Task Test(string name, Action action) => TestAsync(name, () => { action(); return Task.CompletedTask; });
    private static async Task TestAsync(string name, Func<Task> action)
    {
        try { await action(); Console.WriteLine("PASS " + name); _passed++; }
        catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error); _failed++; }
    }
}
