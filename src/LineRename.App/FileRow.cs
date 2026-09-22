using System.ComponentModel;
using System.Runtime.CompilerServices;
using LineRename.Core;

namespace LineRename.App;

public sealed class FileRow(string path) : INotifyPropertyChanged
{
    public string Path { get; set; } = path;
    public string OriginalName => System.IO.Path.GetFileName(Path);
    public string Number { get; private set; } = "";
    public string NewName { get; private set; } = "";
    public string StatusGlyph { get; private set; } = "";
    public string StatusText { get; private set; } = "";
    public string Details { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(int index, RenamePreview preview)
    {
        Number = (index + 1).ToString("D2");
        NewName = preview.Error == "缺少对应名称" ? "等待名称…" : preview.Entry.TargetName;
        StatusText = preview.Error ?? (preview.Entry.HasChange ? "可以重命名" : "名称未改变");
        StatusGlyph = preview.Error is not null ? "\uE7BA" : preview.Entry.HasChange ? "\uE73E" : "\uE738";
        Details = $"{Path}\n→ {NewName}\n{StatusText}";
        Changed(nameof(OriginalName));
        Changed(nameof(Number));
        Changed(nameof(NewName));
        Changed(nameof(StatusGlyph));
        Changed(nameof(StatusText));
        Changed(nameof(Details));
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
