using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace ChatGPTConnector.Core;

public sealed record LocalToolCall(string CallId, string Name, string Arguments);

public sealed record WorkspaceToolPlan(
    string Name,
    string Summary,
    bool RequiresApproval,
    IReadOnlyList<string> AffectedPaths);

public sealed class WorkspaceFileTools(string workspaceRoot)
{
    private const int MaxReadCharacters = 120_000;
    private const long MaxTextFileBytes = 4 * 1024 * 1024;
    private const int MaxListedEntries = 500;
    private readonly string _root = NormalizeRoot(workspaceRoot);

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".svn", ".hg", ".idea", ".vs", "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "coverage" };
    private static readonly HashSet<string> SensitiveDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".ssh", ".gnupg", ".aws", ".azure", ".kube" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".xml", ".html", ".htm", ".css", ".scss", ".less",
        ".yml", ".yaml", ".toml", ".ini", ".conf", ".config", ".properties", ".csv", ".tsv", ".sql",
        ".cs", ".xaml", ".csproj", ".sln", ".props", ".targets", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".py", ".java", ".kt", ".kts", ".go", ".rs", ".c", ".cc", ".cpp", ".h", ".hpp", ".sh", ".ps1", ".bat", ".cmd",
        ".gradle", ".swift", ".rb", ".php", ".vue", ".svelte", ".dockerfile", ".editorconfig", ".gitignore", ".gitattributes",
    };

    public static IReadOnlyList<object> ToolDefinitions { get; } =
    [
        Function("list_files", "列出用户已选择项目目录中的文件和子目录。", new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "相对于项目根目录的路径；根目录使用空字符串。" },
                recursive = new { type = "boolean", description = "是否递归列出。" },
            },
            required = new[] { "path", "recursive" }, additionalProperties = false,
        }),
        Function("search_files", "按文件名搜索用户已选择的项目目录。", new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "搜索起点，相对于项目根目录。" },
                query = new { type = "string", description = "文件名中要查找的文字。" },
            },
            required = new[] { "path", "query" }, additionalProperties = false,
        }),
        Function("read_text_file", "读取项目内的 UTF-8 文本文件。不要用于密钥、凭据或二进制文件。", new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "相对于项目根目录的文件路径。" },
                start_line = new { type = "integer", minimum = 1, description = "起始行，从 1 开始。" },
                line_count = new { type = "integer", minimum = 1, maximum = 1000, description = "最多读取的行数。" },
            },
            required = new[] { "path", "start_line", "line_count" }, additionalProperties = false,
        }),
        Function("write_text_file", "创建或完整覆盖项目内的文本文件。此操作执行前必须由用户确认。", new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "相对于项目根目录的文件路径。" },
                content = new { type = "string", description = "要写入的完整文本内容。" },
            },
            required = new[] { "path", "content" }, additionalProperties = false,
        }),
        Function("replace_in_file", "在项目文本文件中精确替换文字。此操作执行前必须由用户确认。", new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "相对于项目根目录的文件路径。" },
                old_text = new { type = "string", description = "必须原样匹配的旧文字。" },
                new_text = new { type = "string", description = "替换后的文字。" },
                replace_all = new { type = "boolean", description = "是否替换全部匹配；否则只替换第一处。" },
            },
            required = new[] { "path", "old_text", "new_text", "replace_all" }, additionalProperties = false,
        }),
        Function("create_directory", "在项目内创建目录。此操作执行前必须由用户确认。", new
        {
            type = "object",
            properties = new { path = new { type = "string", description = "相对于项目根目录的新目录路径。" } },
            required = new[] { "path" }, additionalProperties = false,
        }),
        Function("move_path", "移动或重命名项目内的文件或目录。此操作执行前必须由用户确认。", new
        {
            type = "object",
            properties = new
            {
                source = new { type = "string", description = "相对于项目根目录的源路径。" },
                destination = new { type = "string", description = "相对于项目根目录的目标路径。" },
            },
            required = new[] { "source", "destination" }, additionalProperties = false,
        }),
        Function("delete_path", "删除项目内的文件或空目录。此危险操作执行前必须由用户确认。", new
        {
            type = "object",
            properties = new { path = new { type = "string", description = "相对于项目根目录的路径。" } },
            required = new[] { "path" }, additionalProperties = false,
        }),
        Function("run_command", "在用户选择的项目目录中运行一条前台开发命令，用于构建、测试、格式化、静态检查或查看版本控制状态。每次执行都必须由用户确认；不要用它删除文件、修改系统设置、提权或发布到远程服务。", new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "要执行的完整命令。保持单行，不要包含交互式提示或后台常驻进程。" },
                working_directory = new { type = "string", description = "相对于项目根目录的工作目录；项目根目录使用空字符串。" },
                shell = new { type = "string", @enum = new[] { "cmd", "powershell" }, description = "Windows 命令解释器。通常使用 cmd；仅在确实需要 PowerShell 语法时选择 powershell。" },
                timeout_seconds = new { type = "integer", minimum = 5, maximum = 600, description = "超时时间，5 到 600 秒。" },
            },
            required = new[] { "command", "working_directory", "shell", "timeout_seconds" }, additionalProperties = false,
        }),
    ];

    public WorkspaceToolPlan Describe(LocalToolCall call)
    {
        using var document = ParseArguments(call.Arguments);
        var args = document.RootElement;
        return call.Name switch
        {
            "list_files" => ReadPlan(call.Name, "列出项目文件", args, "path"),
            "search_files" => ReadPlan(call.Name, "搜索项目文件", args, "path"),
            "read_text_file" => ReadPlan(call.Name, "读取项目文件", args, "path"),
            "write_text_file" => WritePlan(call.Name, "写入文件", args, "path"),
            "replace_in_file" => WritePlan(call.Name, "修改文件", args, "path"),
            "create_directory" => WritePlan(call.Name, "创建目录", args, "path"),
            "move_path" => new WorkspaceToolPlan(call.Name, $"移动或重命名 {Relative(args, "source")} → {Relative(args, "destination")}", true,
                [Relative(args, "source"), Relative(args, "destination")]),
            "delete_path" => WritePlan(call.Name, "删除文件或空目录", args, "path"),
            "run_command" => CommandPlan(args),
            _ => throw new InvalidOperationException("模型请求了未知的本地工具。"),
        };
    }

    public async Task<string> ExecuteAsync(LocalToolCall call, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = ParseArguments(call.Arguments);
            var args = document.RootElement;
            var value = call.Name switch
            {
                "list_files" => await ListFilesAsync(args, cancellationToken),
                "search_files" => await SearchFilesAsync(args, cancellationToken),
                "read_text_file" => await ReadTextFileAsync(args, cancellationToken),
                "write_text_file" => await WriteTextFileAsync(args, cancellationToken),
                "replace_in_file" => await ReplaceInFileAsync(args, cancellationToken),
                "create_directory" => CreateDirectory(args),
                "move_path" => MovePath(args),
                "delete_path" => DeletePath(args),
                "run_command" => await RunCommandAsync(args, cancellationToken),
                _ => throw new InvalidOperationException("未知的本地文件工具。"),
            };
            return JsonSerializer.Serialize(new { ok = true, result = value });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return JsonSerializer.Serialize(new { ok = false, error = error.Message });
        }
    }

    private Task<object> ListFilesAsync(JsonElement args, CancellationToken cancellationToken) => Task.Run<object>(() =>
    {
        var directory = ResolvePath(Relative(args, "path"), mustExist: true, expectDirectory: true);
        var recursive = args.GetProperty("recursive").GetBoolean();
        var results = new List<object>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0 && results.Count < MaxListedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(entry);
                var isDirectory = Directory.Exists(entry);
                if (isDirectory && (IgnoredDirectories.Contains(Path.GetFileName(entry)) || SensitiveDirectories.Contains(Path.GetFileName(entry)) || IsReparsePoint(entry))) continue;
                if (!isDirectory && IsSensitiveFile(entry)) continue;
                results.Add(new { path = ToRelative(entry), type = isDirectory ? "directory" : "file", size = isDirectory ? (long?)null : info.Length });
                if (results.Count >= MaxListedEntries) break;
                if (recursive && isDirectory) pending.Push(entry);
            }
        }
        return new { entries = results, truncated = results.Count >= MaxListedEntries };
    }, cancellationToken);

    private Task<object> SearchFilesAsync(JsonElement args, CancellationToken cancellationToken) => Task.Run<object>(() =>
    {
        var directory = ResolvePath(Relative(args, "path"), mustExist: true, expectDirectory: true);
        var query = args.GetProperty("query").GetString()?.Trim() ?? "";
        if (query.Length is < 1 or > 120) throw new ArgumentException("搜索文字长度无效。");
        var matches = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0 && matches.Count < 200)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(current))
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)) && !SensitiveDirectories.Contains(Path.GetFileName(child)) && !IsReparsePoint(child)) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(current))
                if (!IsSensitiveFile(file) && Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(ToRelative(file));
        }
        return new { matches, truncated = matches.Count >= 200 };
    }, cancellationToken);

    private async Task<object> ReadTextFileAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = ResolvePath(Relative(args, "path"), mustExist: true, expectDirectory: false);
        if (IsSensitiveFile(path)) throw new UnauthorizedAccessException("为保护账号和密钥，该文件不允许读取。");
        EnsureTextPath(path);
        if (new FileInfo(path).Length > MaxTextFileBytes) throw new IOException("文本文件过大，请分割文件后再让 AI 读取。");
        if (await LooksBinaryAsync(path, cancellationToken)) throw new InvalidOperationException("该文件不是可读取的文本文件。");
        var start = args.GetProperty("start_line").GetInt32();
        var count = args.GetProperty("line_count").GetInt32();
        if (start < 1 || count is < 1 or > 1000) throw new ArgumentException("读取行数无效。");
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (text.IndexOf('\0') >= 0) throw new InvalidOperationException("该文件不是可读取的文本文件。");
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var selected = lines.Skip(start - 1).Take(count);
        var content = string.Join("\n", selected);
        var truncated = false;
        if (content.Length > MaxReadCharacters) { content = content[..MaxReadCharacters]; truncated = true; }
        return new { path = ToRelative(path), start_line = start, content, truncated, total_lines = lines.Length };
    }

    private async Task<object> WriteTextFileAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = ResolvePath(Relative(args, "path"), mustExist: false, expectDirectory: false);
        if (IsSensitiveFile(path)) throw new UnauthorizedAccessException("为保护账号和密钥，该文件不允许写入。");
        EnsureTextPath(path);
        if (File.Exists(path) && await LooksBinaryAsync(path, cancellationToken)) throw new InvalidOperationException("不能用文本内容覆盖二进制文件。");
        var content = args.GetProperty("content").GetString() ?? "";
        if (content.Length > 2_000_000) throw new ArgumentException("单次写入内容不能超过 200 万字符。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".luodongchat.tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new { path = ToRelative(path), characters = content.Length };
    }

    private async Task<object> ReplaceInFileAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = ResolvePath(Relative(args, "path"), mustExist: true, expectDirectory: false);
        if (IsSensitiveFile(path)) throw new UnauthorizedAccessException("为保护账号和密钥，该文件不允许修改。");
        EnsureTextPath(path);
        if (new FileInfo(path).Length > MaxTextFileBytes) throw new IOException("文本文件过大，不能安全地执行精确替换。");
        if (await LooksBinaryAsync(path, cancellationToken)) throw new InvalidOperationException("不能修改二进制文件。");
        var oldText = args.GetProperty("old_text").GetString() ?? "";
        var newText = args.GetProperty("new_text").GetString() ?? "";
        if (oldText.Length == 0) throw new ArgumentException("旧文字不能为空。");
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var occurrences = CountOccurrences(content, oldText);
        if (occurrences == 0) throw new InvalidOperationException("文件中没有找到要替换的原文。");
        var replaceAll = args.GetProperty("replace_all").GetBoolean();
        var updated = replaceAll ? content.Replace(oldText, newText, StringComparison.Ordinal) : ReplaceFirst(content, oldText, newText);
        var temp = path + ".luodongchat.tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, updated, new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new { path = ToRelative(path), replacements = replaceAll ? occurrences : 1 };
    }

    private object CreateDirectory(JsonElement args)
    {
        var path = ResolvePath(Relative(args, "path"), mustExist: false, expectDirectory: true);
        Directory.CreateDirectory(path);
        return new { path = ToRelative(path) };
    }

    private object MovePath(JsonElement args)
    {
        var source = ResolvePath(Relative(args, "source"), mustExist: true, expectDirectory: null);
        var destination = ResolvePath(Relative(args, "destination"), mustExist: false, expectDirectory: null);
        if (IsSensitiveFile(source) || IsSensitiveFile(destination)) throw new UnauthorizedAccessException("为保护账号和密钥，不允许移动该路径。");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标路径已经存在。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
        return new { source = ToRelative(source), destination = ToRelative(destination) };
    }

    private object DeletePath(JsonElement args)
    {
        var path = ResolvePath(Relative(args, "path"), mustExist: true, expectDirectory: null);
        if (IsSensitiveFile(path)) throw new UnauthorizedAccessException("为保护账号和密钥，不允许删除该路径。");
        if (Directory.Exists(path))
        {
            if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("为避免误删，当前只允许删除空目录。");
            if (OperatingSystem.IsWindows()) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else Directory.Delete(path);
        }
        else if (OperatingSystem.IsWindows()) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else File.Delete(path);
        return new { path = ToRelative(path) };
    }

    private async Task<object> RunCommandAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var command = Command(args);
        ValidateCommand(command);
        var workingDirectory = ResolvePath(Relative(args, "working_directory"), mustExist: true, expectDirectory: true);
        var shell = args.GetProperty("shell").GetString() ?? "cmd";
        if (shell is not ("cmd" or "powershell")) throw new ArgumentException("不支持的命令解释器。");
        var timeoutSeconds = args.GetProperty("timeout_seconds").GetInt32();
        if (timeoutSeconds is < 5 or > 600) throw new ArgumentException("命令超时时间必须在 5 到 600 秒之间。");

        var startInfo = CreateShellStartInfo(shell, command, workingDirectory);
        SanitizeEnvironment(startInfo.Environment);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("无法启动命令进程。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new
            {
                command,
                working_directory = ToRelative(workingDirectory),
                exit_code = process.ExitCode,
                stdout = stdout.Text,
                stderr = stderr.Text,
                output_truncated = stdout.Truncated || stderr.Truncated,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new TimeoutException($"命令运行超过 {timeoutSeconds} 秒，已停止。");
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
    }

    private string ResolvePath(string relative, bool mustExist, bool? expectDirectory)
    {
        if (Path.IsPathRooted(relative)) throw new UnauthorizedAccessException("只允许使用项目内的相对路径。");
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (normalized.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => IgnoredDirectories.Contains(part) || SensitiveDirectories.Contains(part)))
            throw new UnauthorizedAccessException("该目录被安全策略排除，不能由 AI 访问。");
        var full = Path.GetFullPath(Path.Combine(_root, normalized));
        if (!IsInsideRoot(full) || string.Equals(full, _root, StringComparison.OrdinalIgnoreCase) && expectDirectory == false)
            throw new UnauthorizedAccessException("路径超出了用户选择的项目目录。");
        EnsureNoReparsePointEscape(full);
        var existsFile = File.Exists(full);
        var existsDirectory = Directory.Exists(full);
        if (mustExist && !existsFile && !existsDirectory) throw new IOException("目标路径不存在。");
        if (expectDirectory == true && existsFile) throw new IOException("目标路径不是目录。");
        if (expectDirectory == false && existsDirectory) throw new IOException("目标路径不是文件。");
        return full;
    }

    private void EnsureNoReparsePointEscape(string fullPath)
    {
        var current = _root;
        var relative = Path.GetRelativePath(_root, fullPath);
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new UnauthorizedAccessException("不允许通过符号链接或目录联接访问项目外部路径。");
        }
    }

    private bool IsInsideRoot(string path) => string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private string ToRelative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');
    private static bool IsReparsePoint(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(SensitiveDirectories.Contains)
            || name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal)
            || name.Contains("secret", StringComparison.Ordinal) || name.Contains("credential", StringComparison.Ordinal)
            || name.Contains("password", StringComparison.Ordinal) || name.Contains("privatekey", StringComparison.Ordinal)
            || name is "id_rsa" or "id_ed25519" or "id_ecdsa" or "id_dsa"
            || name is "auth.json" or "credentials.json" or "secrets.json" or ".npmrc" or ".pypirc" or "nuget.config"
            || name.EndsWith(".pem", StringComparison.Ordinal) || name.EndsWith(".key", StringComparison.Ordinal)
            || name.EndsWith(".pfx", StringComparison.Ordinal) || name.EndsWith(".p12", StringComparison.Ordinal);
    }

    private static JsonDocument ParseArguments(string arguments)
    {
        var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new JsonException("工具参数必须是对象。"); }
        return document;
    }

    private static void EnsureTextPath(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        if (extension.Length > 0 && !TextExtensions.Contains(extension))
            throw new InvalidOperationException("该文件类型不支持文本读取或写入。");
        if (extension.Length == 0 && name.Contains(".", StringComparison.Ordinal) && !TextExtensions.Contains(name))
            throw new InvalidOperationException("该文件类型不支持文本读取或写入。");
    }

    private static async Task<bool> LooksBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, useAsync: true);
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        return buffer.AsSpan(0, read).Contains((byte)0);
    }

    private static string Relative(JsonElement args, string property)
    {
        var value = args.GetProperty(property).GetString()?.Trim() ?? "";
        if (value.Length > 500) throw new ArgumentException("路径过长。");
        return value;
    }

    private static string Command(JsonElement args)
    {
        var value = args.GetProperty("command").GetString()?.Trim() ?? "";
        if (value.Length is < 1 or > 8_000) throw new ArgumentException("命令长度无效。");
        if (value.Contains('\r') || value.Contains('\n') || value.IndexOf('\0') >= 0)
            throw new ArgumentException("命令必须是单行文本。");
        return value;
    }

    private static WorkspaceToolPlan CommandPlan(JsonElement args)
    {
        var command = Command(args);
        var workingDirectory = Relative(args, "working_directory");
        return new WorkspaceToolPlan("run_command", $"运行命令：{command}", true,
            [workingDirectory.Length == 0 ? "项目根目录" : workingDirectory]);
    }

    private static void ValidateCommand(string command)
    {
        var blocked = new[]
        {
            @"(?i)(^|[;&|]\s*)(runas|shutdown|format|diskpart|bcdedit|vssadmin|wbadmin|takeown|icacls|taskkill)(\.exe)?(\s|$)",
            @"(?i)\b(reg(\.exe)?\s+(add|delete|import|restore)|net(\.exe)?\s+(user|localgroup)|sc(\.exe)?\s+(create|delete|config)|stop-process|restart-computer|stop-computer)\b",
            @"(?i)(^|[;&|]\s*)(del|erase|rd|rmdir|rm|remove-item)(\s|$)",
            @"(?i)\bgit\s+(push|clean|reset\s+--hard|checkout\s+--|restore\b|remote\s+(add|set-url|remove))",
            @"(?i)\b(gh|ssh|scp|sftp|ftp|curl|wget)(\.exe)?(\s|$)",
            @"(?i)\b(invoke-webrequest|invoke-restmethod|start-process\b.*-verb\s+runas|npm\s+publish|dotnet\s+nuget\s+push)\b",
            @"(?i)\b(git\s+config\s+--(global|system)|npm\s+config\s+set|dotnet\s+nuget\s+(add|update|remove)\s+source|setx|schtasks|wmic)\b",
            @"(?i)(powershell|pwsh)(\.exe)?\b.*-(encodedcommand|enc)\b",
        };
        if (blocked.Any(pattern => Regex.IsMatch(command, pattern, RegexOptions.CultureInvariant)))
            throw new UnauthorizedAccessException("该命令包含提权、系统修改、删除、远程访问或发布操作，已被安全策略阻止。请改用项目文件工具或在系统终端中手动执行。");
    }

    private static ProcessStartInfo CreateShellStartInfo(string shell, string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!OperatingSystem.IsWindows())
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
            return startInfo;
        }
        if (shell == "powershell")
        {
            startInfo.FileName = ResolvePowerShell();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        return startInfo;
    }

    private static string ResolvePowerShell()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? pwsh : "powershell.exe";
    }

    private static void SanitizeEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(IsSensitiveEnvironmentKey).ToArray()) environment.Remove(key);
        environment["LUODONGCHAT_SHELL"] = "project-command";
        environment["CI"] = "true";
        environment["NO_COLOR"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private static bool IsSensitiveEnvironmentKey(string key)
    {
        var upper = key.ToUpperInvariant();
        return upper.Contains("API_KEY", StringComparison.Ordinal)
            || upper.Contains("SECRET", StringComparison.Ordinal)
            || upper.Contains("PASSWORD", StringComparison.Ordinal)
            || upper.Contains("CREDENTIAL", StringComparison.Ordinal)
            || upper.Contains("PRIVATE_KEY", StringComparison.Ordinal)
            || upper.Contains("AUTHORIZATION", StringComparison.Ordinal)
            || upper.Contains("ACCESS_TOKEN", StringComparison.Ordinal)
            || upper.Contains("REFRESH_TOKEN", StringComparison.Ordinal)
            || upper is "GITHUB_TOKEN" or "GH_TOKEN" or "NPM_TOKEN" or "OPENAI_API_KEY" or "ANTHROPIC_API_KEY";
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int limit = 96_000;
        var output = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var remaining = limit - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) truncated = true;
        }
        return (output.ToString(), truncated);
    }

    private static void TryKillProcessTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static WorkspaceToolPlan ReadPlan(string name, string action, JsonElement args, string property)
    {
        var path = Relative(args, property);
        return new WorkspaceToolPlan(name, $"{action}：{(path.Length == 0 ? "项目根目录" : path)}", false, [path]);
    }

    private static WorkspaceToolPlan WritePlan(string name, string action, JsonElement args, string property)
    {
        var path = Relative(args, property);
        return new WorkspaceToolPlan(name, $"{action}：{path}", true, [path]);
    }

    private static object Function(string name, string description, object parameters) => new { type = "function", name, description, parameters, strict = true };
    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new DirectoryNotFoundException("项目目录不存在。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (IsReparsePoint(full)) throw new UnauthorizedAccessException("不能把符号链接或目录联接作为项目根目录。");
        return full;
    }
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }
    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
    }
}
