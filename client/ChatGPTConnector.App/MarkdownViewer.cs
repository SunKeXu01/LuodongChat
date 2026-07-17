using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace ChatGPTConnector.App;

/// <summary>A small, dependency-free Markdown viewer for selectable chat output.</summary>
public sealed class MarkdownViewer : RichTextBox
{
    private static readonly Regex InlinePattern = new(
        @"\[(?<linkText>[^\]]+)\]\((?<url>https?://[^\s)]+)\)|\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|(?<!\*)\*(?<italic>[^*]+)\*(?!\*)",
        RegexOptions.Compiled);

    public static readonly DependencyProperty MarkdownTextProperty = DependencyProperty.Register(
        nameof(MarkdownText), typeof(string), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnMarkdownChanged));

    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private string _pendingMarkdown = "";

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public MarkdownViewer()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AcceptsReturn = true;
        Cursor = System.Windows.Input.Cursors.IBeam;
        Document.PagePadding = new Thickness(0);
        Document.TextAlignment = TextAlignment.Left;
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RenderMarkdown(_pendingMarkdown);
        };
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var viewer = (MarkdownViewer)sender;
        viewer._pendingMarkdown = args.NewValue as string ?? "";
        if (!viewer._renderTimer.IsEnabled) viewer._renderTimer.Start();
    }

    private void RenderMarkdown(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            FontFamily = FontFamily,
            FontSize = FontSize,
            LineHeight = Math.Max(24, FontSize * 1.68),
        };

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var codeLines = new List<string>();
        var inCode = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode) AddCodeBlock(document, codeLines);
                inCode = !inCode;
                codeLines.Clear();
                continue;
            }
            if (inCode) { codeLines.Add(line); continue; }
            if (string.IsNullOrWhiteSpace(line))
            {
                document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 6), LineHeight = 8 });
                continue;
            }

            var trimmed = line.TrimStart();
            var headingLevel = trimmed.TakeWhile(character => character == '#').Count();
            if (headingLevel is > 0 and <= 4 && trimmed.Length > headingLevel && trimmed[headingLevel] == ' ')
            {
                var paragraph = CreateParagraph(trimmed[(headingLevel + 1)..], new Thickness(0, 6, 0, 10));
                paragraph.FontSize = headingLevel switch { 1 => 20, 2 => 18, _ => 16.5 };
                paragraph.FontWeight = FontWeights.SemiBold;
                document.Blocks.Add(paragraph);
                continue;
            }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                var paragraph = CreateParagraph(trimmed[2..], new Thickness(14, 2, 0, 10));
                paragraph.FontStyle = FontStyles.Italic;
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                document.Blocks.Add(paragraph);
                continue;
            }
            var bullet = Regex.Match(trimmed, @"^(?:[-*+]\s+|\d+[.)]\s+)(?<text>.+)$");
            if (bullet.Success)
            {
                var paragraph = CreateParagraph("•  " + bullet.Groups["text"].Value, new Thickness(12, 0, 0, 6));
                document.Blocks.Add(paragraph);
                continue;
            }
            document.Blocks.Add(CreateParagraph(line.Trim(), new Thickness(0, 0, 0, 10)));
        }
        if (inCode || codeLines.Count > 0) AddCodeBlock(document, codeLines);
        Document = document;
    }

    private Paragraph CreateParagraph(string text, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        AddInlineContent(paragraph.Inlines, text);
        return paragraph;
    }

    private void AddInlineContent(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            if (match.Groups["linkText"].Success && Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink(new Run(match.Groups["linkText"].Value)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                link.SetResourceReference(TextElement.ForegroundProperty, "LinkBrush");
                link.RequestNavigate += OpenLink;
                inlines.Add(link);
            }
            else if (match.Groups["bold"].Success) inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeights.SemiBold });
            else if (match.Groups["code"].Success)
            {
                var run = new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = Math.Max(12, FontSize - 1) };
                run.SetResourceReference(TextElement.BackgroundProperty, "SecondarySurfaceBrush");
                inlines.Add(run);
            }
            else if (match.Groups["italic"].Success) inlines.Add(new Run(match.Groups["italic"].Value) { FontStyle = FontStyles.Italic });
            position = match.Index + match.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    private void AddCodeBlock(FlowDocument document, IReadOnlyCollection<string> lines)
    {
        var paragraph = new Paragraph(new Run(string.Join(Environment.NewLine, lines)))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = Math.Max(12, FontSize - 1),
            Margin = new Thickness(0, 3, 0, 12), Padding = new Thickness(12, 9, 12, 9), LineHeight = Math.Max(20, FontSize * 1.45),
        };
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "SecondarySurfaceBrush");
        document.Blocks.Add(paragraph);
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        args.Handled = true;
    }
}
