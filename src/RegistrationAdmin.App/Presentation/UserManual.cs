using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RegistrationAdmin.App.Presentation;

public sealed record ManualChapter(string Title, string Markdown);

/// <summary>直接讀取隨程式嵌入的行政使用手冊，離線可用且與文件共用來源。</summary>
public static class UserManual
{
    public static IReadOnlyList<ManualChapter> Chapters { get; } = Load();

    public static IReadOnlyList<ManualChapter> Search(string? keyword)
    {
        var text = keyword?.Trim() ?? "";
        return Chapters.Where(c => text.Length == 0 || c.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
            || c.Markdown.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static ManualChapter Topic(string? number) => Chapters.FirstOrDefault(c =>
        number is not null && c.Title.StartsWith(number + ". ", StringComparison.Ordinal)) ?? Chapters[0];

    private static IReadOnlyList<ManualChapter> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RegistrationAdmin.App.Help.UserManual.md")
            ?? throw new InvalidOperationException("程式內的使用手冊遺失，請重新安裝程式。");
        using var reader = new StreamReader(stream);
        var markdown = reader.ReadToEnd();
        var headings = Regex.Matches(markdown, @"^## (\d+\. .+)\r?$", RegexOptions.Multiline);
        return headings.Select((h, i) => new ManualChapter(h.Groups[1].Value.Trim(), markdown.Substring(
            h.Index + h.Length, (i + 1 < headings.Count ? headings[i + 1].Index : markdown.Length) - h.Index - h.Length).Trim()))
            .ToList();
    }
}
