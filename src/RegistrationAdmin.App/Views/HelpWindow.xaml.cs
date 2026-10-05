using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.Presentation;

namespace RegistrationAdmin.App.Views;

public partial class HelpWindow : Window
{
    private readonly IDialogService _dialogs;

    public HelpWindow(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/RegistrationAdmin;component/Themes/Theme.xaml", UriKind.Relative) });
        InitializeComponent();
        ChapterList.ItemsSource = UserManual.Chapters;
        ChapterList.SelectedIndex = 0;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        };
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }

    public void OpenTopic(string? number)
    {
        SearchBox.Clear();
        ChapterList.SelectedItem = UserManual.Topic(number);
        Activate();
    }

    private void CloseHelp(object sender, RoutedEventArgs e) => Close();

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ChapterList is null) return;
        var selected = ChapterList.SelectedItem;
        var chapters = UserManual.Search(SearchBox.Text);
        ChapterList.ItemsSource = chapters;
        ChapterList.SelectedItem = chapters.Contains(selected) ? selected : chapters.FirstOrDefault();
        NoResults.Visibility = chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Reader is null) return;
        Reader.Document = ChapterList.SelectedItem is ManualChapter chapter ? Render(chapter) : new FlowDocument();
        Reader.Document.Blocks.FirstBlock?.BringIntoView();
    }

    private FlowDocument Render(ManualChapter chapter)
    {
        var document = new FlowDocument { FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"), PagePadding = new Thickness(20), ColumnWidth = double.PositiveInfinity };
        document.Blocks.Add(new Paragraph(new Run(chapter.Title)) { FontSize = 22, FontWeight = FontWeights.Bold });
        var lines = chapter.Markdown.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line == "---") continue;
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i]);
                document.Blocks.Add(new Paragraph(new Run(string.Join("\n", code))) { FontFamily = new FontFamily("Consolas"),
                    Background = (Brush)FindResource("SubtleBrush"), Padding = new Thickness(10) });
                continue;
            }
            if (line.StartsWith('|'))
            {
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 14) };
                var group = new TableRowGroup();
                table.RowGroups.Add(group);
                do
                {
                    var cells = lines[i].Trim().Trim('|').Split('|');
                    if (cells.All(c => Regex.IsMatch(c.Trim(), @"^:?-+:?$"))) continue;
                    var row = new TableRow();
                    foreach (var value in cells)
                    {
                        var cell = new TableCell(Paragraph(value.Trim())) { Padding = new Thickness(7),
                            BorderBrush = (Brush)FindResource("DividerBrush"), BorderThickness = new Thickness(0.5) };
                        if (group.Rows.Count == 0) { cell.FontWeight = FontWeights.SemiBold; cell.Background = (Brush)FindResource("SubtleBrush"); }
                        row.Cells.Add(cell);
                    }
                    group.Rows.Add(row);
                } while (++i < lines.Length && lines[i].TrimStart().StartsWith('|'));
                i--;
                document.Blocks.Add(table);
                continue;
            }
            var picture = Regex.Match(line, @"^!\[([^\]]*)\]\(images/([a-zA-Z0-9-]+\.png)\)$");
            if (picture.Success)
            {
                var image = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/RegistrationAdmin;component/Help/images/" + picture.Groups[2].Value)),
                    Stretch = Stretch.Uniform, MaxWidth = 740 };
                AutomationProperties.SetName(image, picture.Groups[1].Value);
                document.Blocks.Add(new BlockUIContainer(image) { Margin = new Thickness(0, 8, 0, 12) });
                continue;
            }
            var paragraph = Paragraph(line.TrimStart('#', '>').Trim());
            if (line.StartsWith("###", StringComparison.Ordinal)) { paragraph.FontSize = 17; paragraph.FontWeight = FontWeights.SemiBold; }
            else if (line.StartsWith('>')) { paragraph.Background = (Brush)FindResource("InfoBannerBrush"); paragraph.Padding = new Thickness(10); }
            else if (Regex.IsMatch(line, @"^(- |\d+\. )")) paragraph.Margin = new Thickness(12, 0, 0, 8);
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 24 };
        var offset = 0;
        foreach (Match match in Regex.Matches(text, @"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\((https?://[^)]+)\)"))
        {
            paragraph.Inlines.Add(new Run(text[offset..match.Index]));
            if (match.Groups[1].Success) paragraph.Inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            else if (match.Groups[2].Success) paragraph.Inlines.Add(new Run(match.Groups[2].Value) { FontFamily = new FontFamily("Consolas") });
            else
            {
                var link = new Hyperlink(new Run(match.Groups[3].Value)) { NavigateUri = new Uri(match.Groups[4].Value),
                    Foreground = (Brush)FindResource("PrimaryTextBrush") };
                link.RequestNavigate += (_, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    { _dialogs.ShowError("無法開啟連結，請確認 Windows 預設瀏覽器設定。"); }
                    e.Handled = true;
                };
                paragraph.Inlines.Add(link);
            }
            offset = match.Index + match.Length;
        }
        paragraph.Inlines.Add(new Run(text[offset..]));
        return paragraph;
    }
}
