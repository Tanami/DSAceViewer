using System.Windows;
using System.Windows.Controls;
using DSAceLogViewer.Core;
using Microsoft.Win32;

namespace DSAceLogViewer;

public partial class SettingsWindow : Window
{
    public ViewerSettings Result { get; private set; }
    public SettingsWindow(ViewerSettings settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.UseDarkTitleBar(this);
        Result = settings;
        EncodingBox.SelectedIndex = settings.Encoding switch { "UTF-8" => 1, "Windows-1252" => 2, _ => 0 };
        LineNumbersBox.IsChecked = settings.ShowLineNumbers;
        EntryTypeBox.IsChecked = settings.ShowEntryType;
        WordWrapBox.IsChecked = settings.WordWrap;
        RefreshPath();
    }

    private void RefreshPath()
    {
        PathBox.Text = Result.EffectivePath;
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose DSAce.log", Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*", FileName = "DSAce.log", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        Result = Result with { CustomLogPath = dialog.FileName };
        RefreshPath();
    }

    private void Default_Click(object sender, RoutedEventArgs e) { Result = Result with { CustomLogPath = null }; RefreshPath(); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Result with
        {
            Encoding = ((ComboBoxItem)EncodingBox.SelectedItem).Content.ToString() ?? "Auto",
            ShowLineNumbers = LineNumbersBox.IsChecked == true,
            ShowEntryType = EntryTypeBox.IsChecked == true,
            WordWrap = WordWrapBox.IsChecked == true
        };
        DialogResult = true;
    }
}
