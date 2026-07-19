namespace ChatGPTConnector.Core;

internal sealed record WorkspacePatchDocument(IReadOnlyList<WorkspacePatchOperation> Operations)
{
    private const string BeginMarker = "*** Begin Patch";
    private const string EndMarker = "*** End Patch";
    private const string AddMarker = "*** Add File: ";
    private const string DeleteMarker = "*** Delete File: ";
    private const string UpdateMarker = "*** Update File: ";
    private const string MoveMarker = "*** Move to: ";
    private const string EndOfFileMarker = "*** End of File";

    public static WorkspacePatchDocument Parse(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch) || patch.Length > 500_000)
            throw new ArgumentException("补丁为空或超过 500,000 个字符。");
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != BeginMarker || lines[^1].Trim() != EndMarker)
            throw new ArgumentException("补丁必须以 *** Begin Patch 开始，并以 *** End Patch 结束。");

        var operations = new List<WorkspacePatchOperation>();
        var index = 1;
        while (index < lines.Length - 1)
        {
            var line = lines[index].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }
            if (line.StartsWith(AddMarker, StringComparison.Ordinal))
                operations.Add(ParseAdd(lines, ref index, PathAfter(line, AddMarker)));
            else if (line.StartsWith(DeleteMarker, StringComparison.Ordinal))
            {
                operations.Add(new WorkspacePatchDelete(PathAfter(line, DeleteMarker)));
                index++;
            }
            else if (line.StartsWith(UpdateMarker, StringComparison.Ordinal))
                operations.Add(ParseUpdate(lines, ref index, PathAfter(line, UpdateMarker)));
            else throw new ArgumentException($"补丁第 {index + 1} 行不是有效的文件操作标记。");
            if (operations.Count > 50) throw new ArgumentException("单次补丁最多修改 50 个文件。");
        }
        if (operations.Count == 0) throw new ArgumentException("补丁没有包含任何文件修改。");
        return new WorkspacePatchDocument(operations);
    }

    private static WorkspacePatchAdd ParseAdd(string[] lines, ref int index, string path)
    {
        index++;
        var content = new List<string>();
        while (index < lines.Length - 1 && !IsOperationMarker(lines[index]))
        {
            var line = lines[index];
            if (!line.StartsWith('+')) throw new ArgumentException($"新增文件的第 {index + 1} 行必须以 + 开头。");
            content.Add(line[1..]);
            index++;
        }
        if (content.Count == 0) throw new ArgumentException($"新增文件 {path} 没有内容。");
        return new WorkspacePatchAdd(path, string.Join('\n', content) + "\n");
    }

    private static WorkspacePatchUpdate ParseUpdate(string[] lines, ref int index, string path)
    {
        index++;
        string? movePath = null;
        if (index < lines.Length - 1 && lines[index].TrimEnd().StartsWith(MoveMarker, StringComparison.Ordinal))
        {
            movePath = PathAfter(lines[index].TrimEnd(), MoveMarker);
            index++;
        }

        var chunks = new List<WorkspacePatchChunk>();
        WorkspacePatchChunkBuilder? current = null;
        while (index < lines.Length - 1 && !IsOperationMarker(lines[index]))
        {
            var line = lines[index];
            if (line == "@@" || line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushChunk(chunks, current, path);
                current = new WorkspacePatchChunkBuilder(line.Length > 3 ? line[3..] : null);
                index++;
                continue;
            }
            if (line.TrimEnd() == EndOfFileMarker)
            {
                if (current is null) throw new ArgumentException($"文件 {path} 的结尾标记前缺少修改块。");
                current.IsEndOfFile = true;
                index++;
                continue;
            }
            if (line.Length == 0 || line[0] is not (' ' or '+' or '-'))
                throw new ArgumentException($"补丁第 {index + 1} 行必须以空格、+ 或 - 开头。");
            current ??= new WorkspacePatchChunkBuilder(null);
            current.Add(line[0], line[1..]);
            index++;
        }
        FlushChunk(chunks, current, path);
        if (chunks.Count == 0 && movePath is null) throw new ArgumentException($"更新文件 {path} 没有修改内容。");
        return new WorkspacePatchUpdate(path, movePath, chunks);
    }

    private static void FlushChunk(ICollection<WorkspacePatchChunk> chunks, WorkspacePatchChunkBuilder? current, string path)
    {
        if (current is null) return;
        if (!current.HasChange) throw new ArgumentException($"文件 {path} 的修改块没有新增或删除内容。");
        chunks.Add(current.Build());
    }

    private static string PathAfter(string line, string marker)
    {
        var path = line[marker.Length..].Trim();
        if (path.Length is < 1 or > 500 || path.IndexOf('\0') >= 0)
            throw new ArgumentException("补丁中的文件路径无效。");
        return path;
    }

    private static bool IsOperationMarker(string line)
    {
        var value = line.TrimEnd();
        return value.StartsWith(AddMarker, StringComparison.Ordinal)
            || value.StartsWith(DeleteMarker, StringComparison.Ordinal)
            || value.StartsWith(UpdateMarker, StringComparison.Ordinal)
            || value.Trim() == EndMarker;
    }

    private sealed class WorkspacePatchChunkBuilder(string? context)
    {
        private readonly List<string> _oldLines = [];
        private readonly List<string> _newLines = [];
        public bool IsEndOfFile { get; set; }
        public bool HasChange { get; private set; }

        public void Add(char marker, string value)
        {
            if (marker is ' ' or '-') _oldLines.Add(value);
            if (marker is ' ' or '+') _newLines.Add(value);
            if (marker is '+' or '-') HasChange = true;
        }

        public WorkspacePatchChunk Build() => new(context, _oldLines, _newLines, IsEndOfFile);
    }
}

internal abstract record WorkspacePatchOperation(string Path);
internal sealed record WorkspacePatchAdd(string Path, string Content) : WorkspacePatchOperation(Path);
internal sealed record WorkspacePatchDelete(string Path) : WorkspacePatchOperation(Path);
internal sealed record WorkspacePatchUpdate(
    string Path, string? MovePath, IReadOnlyList<WorkspacePatchChunk> Chunks) : WorkspacePatchOperation(Path);
internal sealed record WorkspacePatchChunk(
    string? Context, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines, bool IsEndOfFile);
