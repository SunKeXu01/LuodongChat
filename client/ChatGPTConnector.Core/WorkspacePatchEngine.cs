namespace ChatGPTConnector.Core;

internal static class WorkspacePatchEngine
{
    public static string ApplyUpdate(string original, WorkspacePatchUpdate update)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = original.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hadFinalNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (hadFinalNewline) lines.RemoveAt(lines.Count - 1);

        var searchStart = 0;
        foreach (var chunk in update.Chunks)
        {
            if (chunk.Context is { } context)
            {
                var contextIndex = FindSequence(lines, [context], searchStart, false);
                if (contextIndex < 0) throw new InvalidOperationException($"无法在 {update.Path} 中找到补丁上下文：{context}");
                searchStart = contextIndex + 1;
            }

            if (chunk.OldLines.Count == 0)
            {
                var insertion = chunk.IsEndOfFile ? lines.Count : lines.Count;
                lines.InsertRange(insertion, chunk.NewLines);
                searchStart = insertion + chunk.NewLines.Count;
                continue;
            }

            var found = FindSequence(lines, chunk.OldLines, searchStart, chunk.IsEndOfFile);
            if (found < 0)
                throw new InvalidOperationException($"无法在 {update.Path} 中找到要替换的原始内容：\n{string.Join('\n', chunk.OldLines)}");
            lines.RemoveRange(found, chunk.OldLines.Count);
            lines.InsertRange(found, chunk.NewLines);
            searchStart = found + chunk.NewLines.Count;
        }

        // Codex apply_patch writes text files with a trailing newline. This also keeps generated
        // source files stable across Windows and Unix tooling.
        return string.Join(newline, lines) + newline;
    }

    private static int FindSequence(IReadOnlyList<string> lines, IReadOnlyList<string> pattern, int start, bool atEnd)
    {
        if (pattern.Count == 0) return Math.Clamp(start, 0, lines.Count);
        if (pattern.Count > lines.Count) return -1;
        var last = lines.Count - pattern.Count;
        if (atEnd && Matches(lines, pattern, last, MatchMode.Exact)) return last;
        foreach (var mode in new[] { MatchMode.Exact, MatchMode.TrimEnd, MatchMode.Trim })
            for (var index = Math.Clamp(start, 0, last); index <= last; index++)
                if (Matches(lines, pattern, index, mode)) return index;
        return -1;
    }

    private static bool Matches(IReadOnlyList<string> lines, IReadOnlyList<string> pattern, int start, MatchMode mode)
    {
        for (var offset = 0; offset < pattern.Count; offset++)
        {
            var actual = lines[start + offset];
            var expected = pattern[offset];
            if (mode == MatchMode.TrimEnd) { actual = actual.TrimEnd(); expected = expected.TrimEnd(); }
            else if (mode == MatchMode.Trim) { actual = actual.Trim(); expected = expected.Trim(); }
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private enum MatchMode { Exact, TrimEnd, Trim }
}
