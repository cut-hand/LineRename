using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LineRename.Core;

public sealed record FileIdentity(uint Volume, ulong Index);
public sealed record UndoEntry(string OriginalPath, string CurrentPath, FileIdentity Identity);
public sealed record RenameReceipt(IReadOnlyList<UndoEntry> Entries);

public sealed class RenameFailureException(string message, string? recoveryPath, Exception inner)
    : IOException(message, inner)
{
    public string? RecoveryPath { get; } = recoveryPath;
}

public sealed class RenameExecutor
{
    private readonly string recoveryDirectory;
    private readonly Action<string, string> move;

    public RenameExecutor(string recoveryDirectory) : this(recoveryDirectory, File.Move) { }

    internal RenameExecutor(string recoveryDirectory, Action<string, string> move)
    {
        this.recoveryDirectory = recoveryDirectory;
        this.move = move;
    }

    public RenameReceipt Execute(IReadOnlyList<RenameEntry> entries)
    {
        foreach (var entry in entries)
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.SourcePath)),
                    Path.GetDirectoryName(Path.GetFullPath(entry.TargetPath)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只能在原文件夹内重命名文件。");

        var plan = RenamePlanner.Create(entries.Select(entry => entry.SourcePath).ToArray(),
            entries.Select(entry => entry.TargetName).ToArray(), false);
        if (!plan.CanExecute) throw new InvalidOperationException(plan.Errors.FirstOrDefault() ?? "没有需要重命名的文件。");

        var batchId = Guid.NewGuid().ToString("N");
        var operations = plan.Entries.Where(entry => entry.HasChange).Select((entry, index) => new Operation
        {
            Entry = entry,
            CurrentPath = entry.SourcePath,
            TemporaryPath = Path.Combine(Path.GetDirectoryName(entry.SourcePath)!, $".lr-{batchId}-{index}.tmp"),
            Identity = ReadIdentity(entry.SourcePath)
        }).ToArray();
        Directory.CreateDirectory(recoveryDirectory);
        var journalPath = Path.Combine(recoveryDirectory, $"{batchId}.json");
        var journal = JsonSerializer.Serialize(operations.Select(operation => new
        {
            OriginalPath = operation.Entry.SourcePath,
            TargetPath = operation.Entry.TargetPath,
            operation.TemporaryPath,
            operation.Identity
        }), new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(journal));
            stream.Flush(true);
        }

        try
        {
            foreach (var operation in operations) MoveTo(operation, operation.TemporaryPath);
            foreach (var operation in operations) MoveTo(operation, operation.Entry.TargetPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var operation in operations.Where(operation => operation.CurrentPath == operation.Entry.TargetPath))
                TryMove(operation, operation.TemporaryPath, rollbackErrors);
            foreach (var operation in operations.Where(operation => operation.CurrentPath == operation.TemporaryPath))
                TryMove(operation, operation.Entry.SourcePath, rollbackErrors);
            if (rollbackErrors.Count == 0)
            {
                TryDeleteJournal(journalPath);
                throw new RenameFailureException($"未完成重命名，已恢复原文件名。原因：{error.Message}", null, error);
            }
            throw new RenameFailureException(
                $"重命名中断，部分文件未能恢复。请勿再次执行；恢复记录：{journalPath}。原因：{error.Message}",
                journalPath, new AggregateException(new[] { error }.Concat(rollbackErrors)));
        }

        TryDeleteJournal(journalPath);
        return new(operations.Select(operation => new UndoEntry(operation.Entry.SourcePath,
            operation.Entry.TargetPath, operation.Identity)).ToArray());
    }

    public void Undo(RenameReceipt receipt)
    {
        foreach (var entry in receipt.Entries)
        {
            if (!File.Exists(entry.CurrentPath) || ReadIdentity(entry.CurrentPath) != entry.Identity)
                throw new IOException($"无法撤销：{Path.GetFileName(entry.CurrentPath)} 已被移动、删除或替换。");
        }
        Execute(receipt.Entries.Select(entry => new RenameEntry(entry.CurrentPath, entry.OriginalPath)).ToArray());
    }

    private void MoveTo(Operation operation, string target)
    {
        if (ReadIdentity(operation.CurrentPath) != operation.Identity)
            throw new IOException("文件已被替换，停止处理该文件以避免误操作。");
        move(operation.CurrentPath, target);
        operation.CurrentPath = target;
    }

    private void TryMove(Operation operation, string target, List<Exception> errors)
    {
        try
        {
            MoveTo(operation, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            errors.Add(error);
        }
    }

    private static void TryDeleteJournal(string path)
    {
        // Journal cleanup must not turn a completed rename into a reported failure.
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static FileIdentity ReadIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class Operation
    {
        public required RenameEntry Entry { get; init; }
        public required string TemporaryPath { get; init; }
        public required string CurrentPath { get; set; }
        public required FileIdentity Identity { get; init; }
    }
}
