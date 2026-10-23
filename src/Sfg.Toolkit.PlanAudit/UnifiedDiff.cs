namespace Sfg.Toolkit.PlanAudit;

/// <summary>
/// One changed file in a unified diff: the b-side path, whether the file is
/// new or deleted, and the hunks. <see cref="AddedLine"/> anchors the first
/// added line (the citation line); deletion-only hunks fall back to the
/// hunk's start line in the new file.
/// </summary>
public sealed record DiffFile(string Path, bool IsNew, bool IsDeleted, IReadOnlyList<DiffHunk> Hunks)
{
    public int AddedLine
    {
        get
        {
            foreach (var hunk in Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.Kind == '+')
                    {
                        return line.NewLine;
                    }
                }
            }

            return Hunks.Count > 0 ? Hunks[0].NewStart : 0;
        }
    }
}

/// <summary>One hunk: start lines in the old and new file plus the body lines.</summary>
public sealed record DiffHunk(int OldStart, int NewStart, IReadOnlyList<DiffLine> Lines);

/// <summary>One body line: '+' added, '-' removed, ' ' context; <see cref="NewLine"/> is the line number in the new file (0 for removed lines).</summary>
public sealed record DiffLine(char Kind, int NewLine, string Text);

/// <summary>
/// Minimal, loud unified-diff reader (git diff format). Nothing is silently
/// skipped or guessed: the parser either understands the whole file or
/// throws naming the offending line. Renames resolve to the b-side
/// ("rename to") path.
/// </summary>
public static class UnifiedDiff
{
    public static IReadOnlyList<DiffFile> Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"diff not found: {path}");
        }

        return ParseText(File.ReadAllText(path), path);
    }

    public static IReadOnlyList<DiffFile> ParseText(string text, string source)
    {
        var raw = text.Replace("\r\n", "\n").Split('\n');
        // a trailing newline produces a final empty element; drop exactly one
        if (raw.Length > 0 && raw[^1] == string.Empty)
        {
            raw = raw[..^1];
        }

        var files = new List<DiffFile>();
        string? currentPath = null;
        var isNew = false;
        var isDeleted = false;
        var oldSeen = false;
        var hunks = new List<DiffHunk>();
        List<DiffLine>? body = null;
        var oldStart = 0;
        var newStart = 0;
        var oldCount = 0;
        var newCount = 0;

        void Flush()
        {
            if (currentPath is null)
            {
                return;
            }

            if (hunks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{source}: file header for '{currentPath}' has no hunks — not a unified diff?");
            }

            files.Add(new DiffFile(currentPath, isNew, isDeleted, hunks.ToArray()));
            currentPath = null;
            isNew = false;
            isDeleted = false;
            oldSeen = false;
            hunks = new List<DiffHunk>();
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            var number = i + 1;

            if (body is null)
            {
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    Flush();
                    continue;
                }

                if (line.StartsWith("new file mode", StringComparison.Ordinal))
                {
                    isNew = true;
                    continue;
                }

                if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
                {
                    isDeleted = true;
                    continue;
                }

                if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    currentPath = line["rename to ".Length..];
                    continue;
                }

                if (line.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    currentPath = line["copy to ".Length..];
                    continue;
                }

                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (currentPath is null)
                    {
                        currentPath = StripAbPath(line["--- ".Length..]);
                    }

                    oldSeen = true;
                    continue;
                }

                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    if (!oldSeen)
                    {
                        throw new InvalidOperationException(
                            $"{source}: line {number}: '+++' without a preceding '---' — not a unified diff");
                    }

                    var newPath = StripAbPath(line["+++ ".Length..]);
                    // deletions name the b-side /dev/null; keep the a-side path
                    if (newPath != "/dev/null")
                    {
                        currentPath = newPath;
                    }

                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (currentPath is null)
                    {
                        throw new InvalidOperationException(
                            $"{source}: line {number}: hunk header before any file header — not a unified diff");
                    }

                    var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
                    var header = end < 0 ? line[2..] : line[2..end];
                    var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 ||
                        !parts[0].StartsWith('-') || !parts[1].StartsWith('+'))
                    {
                        throw new InvalidOperationException(
                            $"{source}: line {number}: malformed hunk header '{line}'");
                    }

                    oldStart = ParseStart(parts[0][1..], source, number);
                    oldCount = ParseCount(parts[0][1..]);
                    newStart = ParseStart(parts[1][1..], source, number);
                    newCount = ParseCount(parts[1][1..]);
                    body = new List<DiffLine>();

                    // a zero-line hunk (pure marker) closes immediately
                    if (oldCount == 0 && newCount == 0)
                    {
                        hunks.Add(new DiffHunk(oldStart, newStart, body.ToArray()));
                        body = null;
                    }

                    continue;
                }

                if (line.StartsWith("index ", StringComparison.Ordinal) ||
                    line.StartsWith("similarity index ", StringComparison.Ordinal) ||
                    line.StartsWith("dissimilarity index ", StringComparison.Ordinal) ||
                    line.StartsWith("old mode ", StringComparison.Ordinal) ||
                    line.StartsWith("new mode ", StringComparison.Ordinal) ||
                    line.StartsWith("rename from ", StringComparison.Ordinal) ||
                    line.StartsWith("copy from ", StringComparison.Ordinal) ||
                    line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                    line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    continue;
                }

                if (currentPath is not null && string.IsNullOrWhiteSpace(line))
                {
                    // git always ends hunks before returning to header lines; a blank
                    // line here means the hunk counts lied
                    throw new InvalidOperationException(
                        $"{source}: line {number}: blank line outside a hunk — hunk counts did not match the body");
                }

                throw new InvalidOperationException(
                    $"{source}: line {number}: not a unified diff line: '{line}'");
            }

            // hunk body
            if (line.StartsWith("\\", StringComparison.Ordinal))
            {
                // "\ No newline at end of file" — belongs to the hunk, carries no line
                continue;
            }

            var kind = line.Length == 0 ? ' ' : line[0];
            if (kind is not ('+' or '-' or ' '))
            {
                throw new InvalidOperationException(
                    $"{source}: line {number}: hunk body line does not start with '+', '-', or ' ': '{line}'");
            }

            var newLine = 0;
            if (kind != '-')
            {
                newLine = newStart + CountedNewLines(body);
            }

            body.Add(new DiffLine(kind, newLine, kind == ' ' ? line : line[1..]));

            var seenOld = body.Count(b => b.Kind != '+');
            var seenNew = body.Count(b => b.Kind != '-');
            if (seenOld >= oldCount && seenNew >= newCount)
            {
                hunks.Add(new DiffHunk(oldStart, newStart, body.ToArray()));
                body = null;
            }
        }

        if (body is not null)
        {
            throw new InvalidOperationException(
                $"{source}: truncated hunk — body ended before the hunk counts were satisfied");
        }

        Flush();

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"{source}: no changed files — not a unified diff?");
        }

        return files;
    }

    private static int CountedNewLines(List<DiffLine> body)
    {
        var count = 0;
        foreach (var entry in body)
        {
            if (entry.Kind != '-')
            {
                count++;
            }
        }

        return count;
    }

    private static int ParseStart(string value, string source, int number)
    {
        var comma = value.IndexOf(',');
        var start = comma < 0 ? value : value[..comma];
        if (!int.TryParse(start, out var startLine) || startLine < 0)
        {
            throw new InvalidOperationException($"{source}: line {number}: malformed hunk range '{value}'");
        }

        return startLine;
    }

    private static int ParseCount(string value)
    {
        var comma = value.IndexOf(',');
        var count = comma < 0 ? "1" : value[(comma + 1)..];
        return int.TryParse(count, out var parsed) && parsed >= 0 ? parsed : 0;
    }

    private static string StripAbPath(string path)
    {
        path = path.TrimEnd('\t');
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
        {
            return path[2..];
        }

        return path;
    }
}
