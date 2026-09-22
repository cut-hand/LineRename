using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using LineRename.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace LineRename.App;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<FileRow> Files { get; } = [];
    private readonly RenameExecutor executor = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineRename", "Recovery"));
    private RenamePlan plan = new([], []);
    private RenameReceipt? lastReceipt;
    private bool ready;
    private bool busy;
    private bool recoveryRequired;
    private bool dialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(1180 * scale), workArea.Width - 24);
        var height = Math.Min((int)(800 * scale), workArea.Height - 24);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(980 * scale), width);
            presenter.PreferredMinimumHeight = Math.Min((int)(680 * scale), height);
        }
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Closing += (_, args) => args.Cancel = busy;
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        var openShortcut = new KeyboardAccelerator { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control };
        openShortcut.Invoked += (_, args) => { args.Handled = true; AddFiles_Click(this, new RoutedEventArgs()); };
        Root.KeyboardAccelerators.Add(openShortcut);
        var renameShortcut = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        renameShortcut.Invoked += (_, args) => { args.Handled = true; Rename_Click(this, new RoutedEventArgs()); };
        Root.KeyboardAccelerators.Add(renameShortcut);
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ready = true;
        UpdateTitleBar();
        RefreshPreview();
    }

    private void UpdateTitleBar()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }

    private void Theme_Click(object sender, RoutedEventArgs e) =>
        Root.RequestedTheme = Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;

    private void Names_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();
    private void KeepExtension_Toggled(object sender, RoutedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (!ready) return;
        var names = RenamePlanner.ParseNames(NamesBox.Text);
        plan = RenamePlanner.Create(Files.Select(file => file.Path).ToArray(), names, KeepExtension.IsOn);
        for (var i = 0; i < Files.Count; i++) Files[i].Update(i, plan.Rows[i]);
        NameCount.Text = $"{names.Length} 行";
        FileCount.Text = $"{Files.Count} 个文件";
        EmptyState.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.IsEnabled = !busy && !recoveryRequired && plan.CanExecute;
        UndoButton.IsEnabled = !busy && !recoveryRequired && lastReceipt is not null;
        UpdateSelectionButtons();
        if (recoveryRequired) return;
        if (Files.Count == 0)
            SetStatus(InfoBarSeverity.Informational, "准备就绪", "新文件加入后按文件名升序排列，仍可拖动调整。支持 Ctrl+O 添加文件。");
        else if (plan.Errors.Count > 0)
            SetStatus(InfoBarSeverity.Warning, "请检查对应关系", string.Join("  ", plan.Errors.Take(2)));
        else if (!plan.CanExecute)
            SetStatus(InfoBarSeverity.Informational, "名称一致", "所有文件已使用这些名称，无需重复操作。");
        else
            SetStatus(InfoBarSeverity.Success, "对应关系已就绪", $"{plan.Entries.Count(entry => entry.HasChange)} 个文件将重命名。拖动文件可调整顺序，悬停可查看完整路径。");
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (busy || dialogOpen || recoveryRequired) return;
        dialogOpen = true;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await picker.PickMultipleFilesAsync();
            AddFiles(selected.Select(file => file.Path));
        }
        catch (Exception error) { await ShowError(error); }
        finally { dialogOpen = false; }
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var known = Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalCount = Files.Count;
        var skipped = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { skipped++; continue; }
            var fullPath = System.IO.Path.GetFullPath(path);
            if (known.Add(fullPath)) Files.Add(new(fullPath));
            else skipped++;
        }
        if (Files.Count > originalCount) SortFiles(false);
        else RefreshPreview();
        if (skipped > 0) SetStatus(InfoBarSeverity.Informational, "已跳过重复或不可用的项目", $"跳过 {skipped} 项；文件不会重复导入。请检查列表与名称数量。");
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        if (busy || recoveryRequired || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "添加到重命名列表";
        e.Handled = true;
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (busy || recoveryRequired || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            AddFiles(items.OfType<StorageFile>().Select(file => file.Path));
            if (items.Any(item => item is StorageFolder))
                SetStatus(InfoBarSeverity.Warning, "已忽略文件夹", "请直接添加需要重命名的文件，不会递归处理文件夹。");
        }
        catch (Exception error) { await ShowError(error); }
        finally { deferral.Complete(); }
    }

    private void Files_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => RefreshPreview();
    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionButtons();

    private void UpdateSelectionButtons()
    {
        if (!ready) return;
        var index = FilesList.SelectedIndex;
        FileUpButton.IsEnabled = !busy && index > 0;
        FileDownButton.IsEnabled = !busy && index >= 0 && index < Files.Count - 1;
        RemoveButton.IsEnabled = !busy && index >= 0;
    }

    private void FileUp_Click(object sender, RoutedEventArgs e) => MoveFile(-1);
    private void FileDown_Click(object sender, RoutedEventArgs e) => MoveFile(1);

    private void MoveFile(int direction)
    {
        var index = FilesList.SelectedIndex;
        if (busy || index < 0 || index + direction < 0 || index + direction >= Files.Count) return;
        var selected = Files[index];
        Files.Move(index, index + direction);
        FilesList.SelectedItem = selected;
        FilesList.ScrollIntoView(selected);
        RefreshPreview();
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (busy || FilesList.SelectedItem is not FileRow row) return;
        Files.Remove(row);
        RefreshPreview();
    }

    private void Files_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;
        RemoveFile_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        Files.Clear();
        RefreshPreview();
    }

    private void SortAscending_Click(object sender, RoutedEventArgs e) => SortFiles(false);
    private void SortDescending_Click(object sender, RoutedEventArgs e) => SortFiles(true);
    private void SortFiles(bool descending)
    {
        var sorted = Files.OrderBy(file => file.OriginalName,
            Comparer<string>.Create((left, right) => StrCmpLogicalW(left, right))).ToArray();
        if (descending) Array.Reverse(sorted);
        ReplaceOrder(sorted);
    }
    private void ReverseFiles_Click(object sender, RoutedEventArgs e) => ReplaceOrder(Files.Reverse().ToArray());
    private void ReplaceOrder(FileRow[] ordered)
    {
        var selected = FilesList.SelectedItem;
        Files.Clear();
        foreach (var file in ordered) Files.Add(file);
        FilesList.SelectedItem = selected;
        RefreshPreview();
    }

    private void NameUp_Click(object sender, RoutedEventArgs e) => MoveName(-1);
    private void NameDown_Click(object sender, RoutedEventArgs e) => MoveName(1);
    private void MoveName(int direction)
    {
        var names = RenamePlanner.ParseNames(NamesBox.Text).ToList();
        var normalizedText = NamesBox.Text.Replace("\r\n", "\r").Replace('\n', '\r');
        var prefix = normalizedText[..NamesBox.SelectionStart];
        var lastBreak = prefix.LastIndexOf('\r');
        var index = lastBreak < 0 ? 0 : RenamePlanner.ParseNames(prefix[..(lastBreak + 1)]).Length;
        if (index >= names.Count || index + direction < 0 || index + direction >= names.Count) return;
        (names[index], names[index + direction]) = (names[index + direction], names[index]);
        // WinUI caret offsets count each line break as one character.
        NamesBox.Text = string.Join("\r", names);
        NamesBox.SelectionStart = names.Take(index + direction).Sum(name => name.Length + 1);
        NamesBox.SelectionLength = names[index + direction].Length;
        NamesBox.Focus(FocusState.Programmatic);
    }
    private void ReverseNames_Click(object sender, RoutedEventArgs e) =>
        NamesBox.Text = string.Join("\r", RenamePlanner.ParseNames(NamesBox.Text).Reverse());

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (busy || dialogOpen || recoveryRequired) return;
        RefreshPreview();
        if (!plan.CanExecute) return;
        dialogOpen = true;
        var entries = plan.Entries.ToArray();
        try
        {
            var changed = entries.Where(entry => entry.HasChange).ToArray();
            var preview = string.Join("\n", changed.Take(5).Select(entry => $"{entry.SourceName}  →  {entry.TargetName}"));
            if (changed.Length > 5) preview += $"\n以及另外 {changed.Length - 5} 个文件";
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
                Title = $"确认重命名 {changed.Length} 个文件？",
                Content = new TextBlock { Text = preview + "\n\n直接修改源文件名，内容和所在文件夹不变，不另存副本。\n若新名称被本批次以外的文件占用，将停止，不会替换它。\n本次操作将替换上一条撤销记录。", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "确认重命名",
                CloseButtonText = "返回检查",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            SetBusy(true);
            var receipt = await Task.Run(() => executor.Execute(entries));
            lastReceipt = receipt;
            var targets = entries.ToDictionary(entry => entry.SourcePath, entry => entry.TargetPath, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files) if (targets.TryGetValue(file.Path, out var target)) file.Path = target;
            SetBusy(false);
            SetStatus(InfoBarSeverity.Success, "重命名完成", $"已更新 {receipt.Entries.Count} 个文件。关闭软件前，可撤销上次操作。");
        }
        catch (Exception error)
        {
            SetBusy(false);
            await ShowError(error);
        }
        finally { dialogOpen = false; }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (busy || dialogOpen || recoveryRequired || lastReceipt is null) return;
        dialogOpen = true;
        var receipt = lastReceipt;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
                Title = "撤销上次重命名？",
                Content = $"将恢复 {receipt.Entries.Count} 个文件的原始名称。若文件已被替换或名称被占用，将停止操作。",
                PrimaryButtonText = "恢复原名",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            SetBusy(true);
            await Task.Run(() => executor.Undo(receipt));
            var originals = receipt.Entries.ToDictionary(entry => entry.CurrentPath, entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files) if (originals.TryGetValue(file.Path, out var original)) file.Path = original;
            lastReceipt = null;
            SetBusy(false);
            SetStatus(InfoBarSeverity.Success, "已恢复原名", $"撤销完成，共恢复 {receipt.Entries.Count} 个文件。");
        }
        catch (Exception error)
        {
            SetBusy(false);
            await ShowError(error);
        }
        finally { dialogOpen = false; }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Workspace.IsEnabled = !value && !recoveryRequired;
        BusyRing.IsActive = value;
        BusyRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
        if (value) SetStatus(InfoBarSeverity.Informational, "正在处理", "请稍候，完成前请不要移动或修改这些文件。");
    }

    private async Task ShowError(Exception error)
    {
        if (error is RenameFailureException { RecoveryPath: not null })
        {
            recoveryRequired = true;
            Workspace.IsEnabled = false;
            RenameButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
        }
        SetStatus(InfoBarSeverity.Error, "操作未完成", error.Message);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "操作未完成",
            Content = new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "知道了"
        };
        await dialog.ShowAsync();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string left, string right);
}
