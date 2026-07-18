using System.Text.RegularExpressions;

namespace ChatGPTConnector.Core;

public static partial class ImageGenerationIntent
{
    public static bool IsExplicit(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim();
        if (ChineseCapabilityQuestion().IsMatch(normalized) || EnglishCapabilityQuestion().IsMatch(normalized))
            return false;
        return ChineseIntent().IsMatch(normalized) || EnglishIntent().IsMatch(normalized);
    }

    [GeneratedRegex(@"(?:生成|画|绘制|创作|制作|设计|做)(?:一张|一个|一下|些)?[^。！？\n]{0,28}(?:图片|图像|插画|海报|头像|壁纸|照片|图标)|(?:帮我|给我|为我|替我)(?:生成|画|绘制|创作|制作|设计|做|生)(?:一张|一个|一下|些)?[^。！？\n]{0,28}(?:图|图片|图像|插画|海报|头像|壁纸|照片|图标)", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseIntent();

    [GeneratedRegex(@"\b(?:generate|create|draw|design|make)\b.{0,40}\b(?:image|picture|illustration|poster|avatar|wallpaper|photo|icon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishIntent();

    [GeneratedRegex(@"^\s*(?:(?:你好|您好|嗨)[，,！!。\s]*)?(?:请问[，,\s]*)?(?:你)?(?:能|可以|会|支持|是否|能否|可否).{0,4}(?:生成|画|绘制|创作|制作|设计|做|生)(?:一张|一个|一下|些)?(?:图|图片|图像|插画|海报|头像|壁纸|照片|图标)(?:吗|么|呢)?[？?！!。\.]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseCapabilityQuestion();

    [GeneratedRegex(@"^\s*(?:(?:hi|hello)[,!\.\s]*)?(?:(?:can|could|would)\s+you\s+(?:generate|create|draw|design|make)\s+(?:an?\s+|some\s+)?(?:images?|pictures?|illustrations?|posters?|avatars?|wallpapers?|photos?|icons?)|do\s+you\s+support\s+(?:image|picture)\s+generation)\s*[?!.]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishCapabilityQuestion();
}
