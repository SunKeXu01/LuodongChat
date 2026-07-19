using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTConnector.Core;

namespace ChatGPTConnector.App;

public enum ComposerAttachmentStatus { Pending, Uploading, Success, Error, Cancelled }

public sealed class ComposerAttachment : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public required long Size { get; init; }
    public required string MimeType { get; init; }
    public required string Category { get; init; }
    public required string Fingerprint { get; init; }
    public bool IsTemporary { get; init; }
    public ImageSource? Preview { get; init; }
    public string SizeText => Size switch
    {
        >= 1024 * 1024 => $"{Size / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{Size / 1024d:0.#} KB",
        _ => $"{Size} B",
    };
    public string TypeText => string.IsNullOrWhiteSpace(Extension) ? "FILE" : Extension.TrimStart('.').ToUpperInvariant();
    public string DetailText => $"{TypeText} · {SizeText}";
    public string IconText => Category switch
    {
        "video" => "▶", "audio" => "♪", "archive" => "ZIP", "code" => "</>",
        "document" when Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) => "PDF",
        "document" when Extension is ".xls" or ".xlsx" or ".csv" or ".tsv" => "XLS",
        "document" when Extension is ".doc" or ".docx" or ".rtf" or ".odt" => "DOC",
        "document" when Extension is ".ppt" or ".pptx" => "PPT",
        _ => "FILE",
    };
    public bool IsImage => Category == "image";
    public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileVisibility => IsImage ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PreviewVisibility => IsImage && Preview is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PreviewPlaceholderVisibility => IsImage && Preview is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Status == ComposerAttachmentStatus.Uploading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => Status is ComposerAttachmentStatus.Error or ComposerAttachmentStatus.Cancelled ? Visibility.Visible : Visibility.Collapsed;
    public string StatusText => Status switch
    {
        ComposerAttachmentStatus.Pending => "等待上传", ComposerAttachmentStatus.Uploading => $"上传中 {Progress:0}%",
        ComposerAttachmentStatus.Success => "上传完成", ComposerAttachmentStatus.Cancelled => "已取消", _ => Error ?? "上传失败",
    };

    private double _progress;
    public double Progress { get => _progress; set { _progress = value; Changed(nameof(Progress), nameof(StatusText)); } }
    private ComposerAttachmentStatus _status;
    public ComposerAttachmentStatus Status { get => _status; set { _status = value; Changed(nameof(Status), nameof(StatusText), nameof(ProgressVisibility), nameof(ErrorVisibility)); } }
    private string? _error;
    public string? Error { get => _error; set { _error = value; Changed(nameof(Error), nameof(StatusText)); } }
    public string? ServerFileId { get; set; }
    internal CancellationTokenSource? UploadCancellation { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(params string[] names) { foreach (var name in names) PropertyChanged?.Invoke(this, new(name)); }
}

public sealed class AttachmentComposerController : IDisposable
{
    public const int MaximumCount = 10;
    public const long MaximumDefaultFileBytes = 20 * 1024 * 1024;
    public const long MaximumDocumentFileBytes = 40 * 1024 * 1024;
    private readonly AttachmentUploadClient _client;
    private readonly Uri _gateway;
    private readonly Func<string?> _token;
    private readonly string _temporaryRoot;
    private bool _disposed;

    public ObservableCollection<ComposerAttachment> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool IsReady => Items.Count > 0 && Items.All(item => item.Status == ComposerAttachmentStatus.Success);
    public string? BlockingReason => Items.FirstOrDefault(item => item.Status == ComposerAttachmentStatus.Uploading) is not null ? "请等待附件上传完成"
        : Items.FirstOrDefault(item => item.Status is ComposerAttachmentStatus.Error or ComposerAttachmentStatus.Cancelled) is not null ? "请重试或删除上传失败的附件" : null;
    public event EventHandler? StateChanged;
    public event EventHandler<string>? ValidationFailed;

    public AttachmentComposerController(AttachmentUploadClient client, Uri gateway, Func<string?> token)
    {
        _client = client;
        _gateway = gateway;
        _token = token;
        _temporaryRoot = Path.Combine(ApplicationDirectories.Data, "attachment-drafts");
        Items.CollectionChanged += (_, _) => NotifyState();
    }

    public async Task AddFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Items.Count >= MaximumCount) { ValidationFailed?.Invoke(this, $"每条消息最多添加 {MaximumCount} 个附件。"); break; }
            ComposerAttachment item;
            try { item = await CreateAsync(path, false, cancellationToken); }
            catch (Exception error) { ValidationFailed?.Invoke(this, error.Message); continue; }
            if (Items.Any(existing => existing.Fingerprint == item.Fingerprint)) { ValidationFailed?.Invoke(this, $"附件“{item.Name}”已添加，请勿重复上传。"); continue; }
            Items.Add(item);
            _ = UploadAsync(item);
        }
    }

    public async Task AddClipboardImageAsync(BitmapSource image, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_temporaryRoot);
        var path = Path.Combine(_temporaryRoot, $"粘贴的图片-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, true)) encoder.Save(stream);
        ComposerAttachment item;
        try { item = await CreateAsync(path, true, cancellationToken); }
        catch { File.Delete(path); throw; }
        if (Items.Any(existing => existing.Fingerprint == item.Fingerprint)) { File.Delete(path); ValidationFailed?.Invoke(this, "剪贴板中的图片已经添加。"); return; }
        if (Items.Count >= MaximumCount) { File.Delete(path); ValidationFailed?.Invoke(this, $"每条消息最多添加 {MaximumCount} 个附件。"); return; }
        Items.Add(item);
        _ = UploadAsync(item);
    }

    public async Task RetryAsync(ComposerAttachment item)
    {
        if (!Items.Contains(item) || item.Status == ComposerAttachmentStatus.Uploading) return;
        await UploadAsync(item);
    }

    public async Task RemoveAsync(ComposerAttachment item)
    {
        if (!Items.Contains(item)) return;
        item.UploadCancellation?.Cancel();
        var serverId = item.ServerFileId;
        Items.Remove(item);
        if (item.IsTemporary) TryDelete(item.FilePath);
        var token = _token();
        if (serverId is not null && token is not null)
            try { await _client.DeleteAsync(_gateway, token, serverId); } catch { }
    }

    public async Task CompleteSendAsync()
    {
        var snapshot = Items.ToArray();
        foreach (var item in snapshot) await RemoveAsync(item);
    }

    public void DetachForSend(IEnumerable<ComposerAttachment> attachments)
    {
        foreach (var item in attachments)
            if (Items.Contains(item)) Items.Remove(item);
    }

    public async Task ReleaseSentAsync(IEnumerable<ComposerAttachment> attachments)
    {
        foreach (var item in attachments)
        {
            item.UploadCancellation?.Dispose();
            item.UploadCancellation = null;
            if (item.IsTemporary) TryDelete(item.FilePath);
            var token = _token();
            if (item.ServerFileId is not null && token is not null)
                try { await _client.DeleteAsync(_gateway, token, item.ServerFileId); } catch { }
        }
    }

    public IReadOnlyList<ComposerAttachment> Snapshot() => Items.ToArray();

    private async Task UploadAsync(ComposerAttachment item)
    {
        var token = _token();
        if (string.IsNullOrWhiteSpace(token)) { item.Error = "请先登录"; item.Status = ComposerAttachmentStatus.Error; NotifyState(); return; }
        item.UploadCancellation?.Dispose();
        item.UploadCancellation = new CancellationTokenSource();
        item.Error = null;
        item.Progress = 0;
        item.Status = ComposerAttachmentStatus.Uploading;
        NotifyState();
        try
        {
            var progress = new Progress<double>(value => item.Progress = value * 100);
            var uploaded = await _client.UploadAsync(_gateway, token, item.FilePath, item.MimeType, progress, item.UploadCancellation.Token);
            if (_disposed || !Items.Contains(item)) return;
            item.ServerFileId = uploaded.Id;
            item.Progress = 100;
            item.Status = ComposerAttachmentStatus.Success;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && Items.Contains(item)) item.Status = ComposerAttachmentStatus.Cancelled;
        }
        catch (Exception error)
        {
            if (!_disposed && Items.Contains(item)) { item.Error = error.Message; item.Status = ComposerAttachmentStatus.Error; }
        }
        finally { NotifyState(); }
    }

    private static async Task<ComposerAttachment> CreateAsync(string path, bool temporary, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("文件不存在或已被移动。", path);
        var info = new FileInfo(path);
        if (info.Length == 0) throw new InvalidDataException($"“{info.Name}”是空文件，无法上传。");
        var extension = info.Extension.ToLowerInvariant();
        var (mime, category) = FileKind(extension);
        if (mime is null) throw new InvalidDataException($"暂不支持 {extension.ToUpperInvariant()} 文件。");
        var maximumBytes = category == "document" ? MaximumDocumentFileBytes : MaximumDefaultFileBytes;
        if (info.Length > maximumBytes)
            throw new InvalidDataException($"“{info.Name}”超过 {(maximumBytes / 1024 / 1024)} MB 限制。");
        string fingerprint;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true))
            fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var preview = category == "image"
            ? await Task.Run(() => LoadPreview(path), cancellationToken)
            : null;
        return new ComposerAttachment
        {
            Id = Guid.NewGuid().ToString("N"), FilePath = path, Name = info.Name, Extension = extension,
            Size = info.Length, MimeType = mime, Category = category, Fingerprint = fingerprint, IsTemporary = temporary,
            Preview = preview, Status = ComposerAttachmentStatus.Pending,
        };
    }

    private static (string? Mime, string Category) FileKind(string extension) => extension switch
    {
        ".png" => ("image/png", "image"), ".jpg" or ".jpeg" => ("image/jpeg", "image"), ".webp" => ("image/webp", "image"), ".gif" => ("image/gif", "image"),
        ".mp4" => ("video/mp4", "video"), ".mov" => ("video/quicktime", "video"), ".webm" => ("video/webm", "video"),
        ".mp3" => ("audio/mpeg", "audio"), ".wav" => ("audio/wav", "audio"), ".m4a" => ("audio/mp4", "audio"), ".ogg" => ("audio/ogg", "audio"),
        ".pdf" => ("application/pdf", "document"), ".doc" => ("application/msword", "document"), ".docx" => ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "document"),
        ".rtf" => ("application/rtf", "document"), ".odt" => ("application/vnd.oasis.opendocument.text", "document"),
        ".ppt" => ("application/vnd.ms-powerpoint", "document"), ".pptx" => ("application/vnd.openxmlformats-officedocument.presentationml.presentation", "document"),
        ".xls" => ("application/vnd.ms-excel", "document"), ".xlsx" => ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "document"),
        ".csv" => ("text/csv", "document"), ".tsv" => ("text/tsv", "document"), ".txt" or ".md" => ("text/plain", "document"),
        ".json" => ("application/json", "document"), ".xml" => ("application/xml", "document"), ".html" => ("text/html", "document"),
        ".zip" => ("application/zip", "archive"), ".rar" => ("application/vnd.rar", "archive"), ".7z" => ("application/x-7z-compressed", "archive"), ".gz" => ("application/gzip", "archive"), ".tar" => ("application/octet-stream", "archive"),
        ".cs" or ".xaml" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".java" or ".kt" or ".go" or ".rs" or ".c" or ".cpp" or ".h" or ".hpp" or ".sql" or ".sh" or ".ps1" or ".yml" or ".yaml" or ".toml" or ".css" or ".scss" => ("text/plain", "code"),
        _ => (null, "other"),
    };

    private static BitmapImage? LoadPreview(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 240;
            image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
        }
        catch { return null; }
    }

    private void NotifyState() => StateChanged?.Invoke(this, EventArgs.Empty);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in Items) { item.UploadCancellation?.Cancel(); item.UploadCancellation?.Dispose(); if (item.IsTemporary) TryDelete(item.FilePath); }
        Items.Clear();
    }
}
