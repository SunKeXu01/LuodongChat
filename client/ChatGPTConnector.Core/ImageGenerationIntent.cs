using System.Text.RegularExpressions;

namespace ChatGPTConnector.Core;

public static partial class ImageGenerationIntent
{
    public static bool IsExplicit(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return ChineseIntent().IsMatch(text) || EnglishIntent().IsMatch(text);
    }

    [GeneratedRegex(@"(?:生成|画|绘制|创作|制作|设计|做)(?:一张|一个|一下|些)?[^。！？\n]{0,28}(?:图片|图像|插画|海报|头像|壁纸|照片|图标)", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseIntent();

    [GeneratedRegex(@"\b(?:generate|create|draw|design|make)\b.{0,40}\b(?:image|picture|illustration|poster|avatar|wallpaper|photo|icon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishIntent();
}
