using System.Text.RegularExpressions;
using RegistrationAdmin.App.Presentation;

namespace RegistrationAdmin.App.Tests;

public sealed class UserManualTests
{
    [Fact]
    public void EveryChapter_RendersInNativeReader_WithoutOpeningGoogle()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new RegistrationAdmin.App.Views.HelpWindow(new FakeDialogs());
                for (var number = 1; number <= 15; number++)
                {
                    window.OpenTopic(number.ToString());
                    window.Measure(new System.Windows.Size(1040, 760));
                    window.Arrange(new System.Windows.Rect(0, 0, 1040, 760));
                    window.UpdateLayout();
                    var reader = (System.Windows.Controls.FlowDocumentScrollViewer)window.FindName("Reader");
                    Assert.NotEmpty(reader.Document.Blocks);
                    Assert.Contains(UserManual.Topic(number.ToString()).Title,
                        new System.Windows.Documents.TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text);
                    // Opt-in visual QA renders only the fixture-free manual, never registration data.
                    var qaDirectory = Environment.GetEnvironmentVariable("RA_HELP_QA_DIR");
                    if (!string.IsNullOrEmpty(qaDirectory) && number is 1 or 6 or 7 or 12)
                    {
                        System.IO.Directory.CreateDirectory(qaDirectory);
                        var content = (System.Windows.FrameworkElement)window.Content;
                        content.Measure(new System.Windows.Size(1000, 720));
                        content.Arrange(new System.Windows.Rect(20, 20, 1000, 720));
                        content.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1040, 760, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var output = System.IO.File.Create(System.IO.Path.Combine(qaDirectory, $"help-{number}.png"));
                        encoder.Save(output);
                    }
                }
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "手冊渲染未在 30 秒內完成。");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void EmbeddedManual_HasEveryChapterAndReferencedImage()
    {
        Assert.Equal(15, UserManual.Chapters.Count);
        for (var number = 1; number <= 15; number++)
        {
            var chapter = UserManual.Topic(number.ToString());
            Assert.StartsWith(number + ". ", chapter.Title);
            Assert.False(string.IsNullOrWhiteSpace(chapter.Markdown));
        }

        // Check the compiled WPF resource inventory, including linked manual screenshots.
        using var stream = typeof(UserManual).Assembly.GetManifestResourceStream("RegistrationAdmin.g.resources");
        Assert.NotNull(stream);
        using var resources = new System.Resources.ResourceReader(stream);
        var keys = resources.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        foreach (Match image in Regex.Matches(string.Join("\n", UserManual.Chapters.Select(c => c.Markdown)), @"!\[[^\]]*\]\(images/([^)]+)\)"))
        {
            Assert.Contains("help/images/" + image.Groups[1].Value.ToLowerInvariant(), keys);
        }
    }

    [Fact]
    public void Search_FindsContentWithinChapter_AndDoesNotModifyCatalog()
    {
        var result = UserManual.Search("  預設瀏覽器  ");
        Assert.Contains(UserManual.Topic("1"), result);
        Assert.Empty(UserManual.Search("完全不存在的說明關鍵字"));
        Assert.Equal(15, UserManual.Search(" ").Count);
        Assert.Equal(15, UserManual.Chapters.Count);
        Assert.Equal(UserManual.Topic("1"), UserManual.Topic("unknown"));
    }
}
