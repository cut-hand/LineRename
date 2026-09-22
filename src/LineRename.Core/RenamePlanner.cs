namespace LineRename.Core;

public sealed record RenameEntry(string SourcePath, string TargetPath)
{
    public string SourceName => Path.GetFileName(SourcePath);
    public string TargetName => Path.GetFileName(TargetPath);
    public bool HasChange => !string.Equals(SourcePath, TargetPath, StringComparison.Ordinal);
}

public sealed record RenamePreview(RenameEntry Entry, string? Error);

public sealed record RenamePlan(IReadOnlyList<RenamePreview> Rows, IReadOnlyList<string> Errors)
{
    public bool CanExecute => Rows.Count > 0 && Errors.Count == 0 && Rows.Any(row => row.Entry.HasChange);
    public IReadOnlyList<RenameEntry> Entries => Rows.Select(row => row.Entry).ToArray();
}

public static class RenamePlanner
{
    public static string[] ParseNames(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();

    public static RenamePlan Create(IReadOnlyList<string> paths, IReadOnlyList<string> names, bool keepExtension)
    {
        var errors = new List<string>();
        var rows = new List<RenamePreview>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count != names.Count)
            errors.Add($"文件 {paths.Count} 个，名称 {names.Count} 行，请调整为相同数量。");

        for (var index = 0; index < paths.Count; index++)
        {
            var source = Path.GetFullPath(paths[index]);
            var name = index < names.Count ? names[index] : "";
            var targetName = name + (keepExtension ? Path.GetExtension(source) : "");
            string? error = index >= names.Count ? "缺少对应名称" : ValidateName(targetName);
            if (name.Length == 0) error = "缺少对应名称";
            var target = error is null ? Path.Combine(Path.GetDirectoryName(source)!, targetName) : source;
            if (!File.Exists(source)) error = "源文件不存在或无法访问";
            if (!sources.Add(source)) error = "重复导入了同一个文件";
            rows.Add(new(new(source, target), error));
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var error = row.Error;
            if (error is null && !targets.Add(row.Entry.TargetPath)) error = "多个文件将使用同一个目标路径";
            if (error is null && (Directory.Exists(row.Entry.TargetPath) ||
                File.Exists(row.Entry.TargetPath) && !sources.Contains(row.Entry.TargetPath)))
                error = "目标名称已被其他文件或文件夹占用";
            if (error is null && row.Entry.TargetPath.Length >= 260) error = "目标路径过长，请缩短名称或移动到更短的目录";
            if (error is not null)
            {
                rows[index] = row with { Error = error };
                errors.Add($"第 {index + 1} 项：{error}");
            }
        }
        return new(rows, errors);
    }

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return "文件名不能为空";
        if (name.Length > 255) return "文件名不能超过 255 个字符";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称含有非法字符（如 / \\ : * ? \" < > |）";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "文件名不能以点或空格结尾";
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            return "不能使用 Windows 保留名称";
        return null;
    }
}
