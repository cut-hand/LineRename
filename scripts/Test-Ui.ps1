param([int]$ProcessId)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PickerNative {
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
function Find-Id([string]$id) {
    $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Find-Name([string]$name) {
    $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name))
}
function Find-Picker {
    $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770'))
}
function Invoke-Control($control) {
    ([System.Windows.Automation.InvokePattern]$control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Wait-For([scriptblock]$condition, [string]$message) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $condition
        if ($result) { return $result }
        [Threading.Thread]::Sleep(100)
    } while ($watch.ElapsedMilliseconds -lt 10000)
    throw $message
}
function Set-Names([string]$text) {
    ([System.Windows.Automation.ValuePattern](Find-Id 'NamesBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($text)
}
function Get-Rows {
    @((Find-Id 'FilesList').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
}
function Row-Text($row) {
    @($row.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)) | ForEach-Object { $_.Current.Name }) -join '|'
}
function Add-Fixtures([string[]]$paths) {
    Invoke-Control (Find-Id 'AddButton')
    $picker = Wait-For { Find-Picker } 'File picker did not open.'
    $pickerHandle = [IntPtr]$picker.Current.NativeWindowHandle
    $edit = Wait-For {
        $comboEx = [PickerNative]::GetDlgItem($pickerHandle, 1148)
        $combo = [PickerNative]::FindWindowEx($comboEx, [IntPtr]::Zero, 'ComboBox', $null)
        $editHandle = [PickerNative]::FindWindowEx($combo, [IntPtr]::Zero, 'Edit', $null)
        if ($editHandle -ne [IntPtr]::Zero) { $editHandle }
    } 'Filename edit was not available.'
    $selection = ($paths | ForEach-Object { '"' + $_ + '"' }) -join ' '
    [PickerNative]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $selection) | Out-Null
    [PickerNative]::PostMessage([PickerNative]::GetDlgItem($pickerHandle, 1), 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Wait-For { -not (Find-Picker) } 'Picker did not complete.' | Out-Null
}
$handle = Wait-For { $handle = (Get-Process -Id $ProcessId).MainWindowHandle; if ($handle -ne [IntPtr]::Zero) { $handle } } 'Application window did not open.'
$script:window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
Wait-For { Find-Id 'NamesBox' } 'Application controls did not load.' | Out-Null
$picker = Find-Picker
if ($picker) {
    [PickerNative]::PostMessage([IntPtr]$picker.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Wait-For { -not (Find-Picker) } 'Existing picker did not close.' | Out-Null
}
if ((Get-Rows).Count -ne 0) { throw 'UI tests require a fresh empty application window.' }
$fixtureRoot = Join-Path (Split-Path $PSScriptRoot) ('artifacts\ui-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$paths = @(1, 2, 10 | ForEach-Object {
    $path = Join-Path $fixtureRoot ($_.ToString() + '.txt')
    [IO.File]::WriteAllText($path, 'payload-' + $_)
    $path
})
Set-Names "A`r`nB`r`nC`r`nD`r`nE"
$textPattern = [System.Windows.Automation.TextPattern](Find-Id 'NamesBox').GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
$range = $textPattern.DocumentRange.Clone()
$range.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, $range, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
$range.MoveEndpointByUnit([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, [System.Windows.Automation.Text.TextUnit]::Character, 1) | Out-Null
$range.Select()
foreach ($step in @(@('下移名称','B,A,C,D,E'), @('下移名称','B,C,A,D,E'), @('上移名称','B,A,C,D,E'), @('上移名称','A,B,C,D,E'))) {
    Invoke-Control (Find-Name $step[0])
    Wait-For { (([System.Windows.Automation.ValuePattern](Find-Id 'NamesBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -split '\r\n|\r|\n') -join ',' -eq $step[1] } 'Repeated name moves lost the selected line.' | Out-Null
}
Write-Output 'PASS repeated name-line movement'
Set-Names "First`r`n`r`nSecond`r`nThird"
Wait-For { (Find-Id 'NameCount').Current.Name.StartsWith('3') } 'Name count did not update.' | Out-Null
if ((Find-Id 'RenameButton').Current.IsEnabled) { throw 'Rename was enabled with no files.' }
Write-Output 'PASS empty-file protection and blank-line parsing'
Add-Fixtures @($paths[2], $paths[0])
Wait-For { (Get-Rows).Count -eq 2 -and (Row-Text (Get-Rows)[0]) -match '\|1.txt\|' } 'Initial import was not sorted ascending.' | Out-Null
Add-Fixtures @($paths[1])
Wait-For { (Get-Rows).Count -eq 3 } 'Files were not imported.' | Out-Null
Wait-For { (Row-Text (Get-Rows)[0]) -match '\|1.txt\|' -and (Row-Text (Get-Rows)[1]) -match '\|2.txt\|' -and (Row-Text (Get-Rows)[2]) -match '\|10.txt\|' } 'Newly added file was not sorted into natural ascending order.' | Out-Null
Wait-For { (Find-Id 'RenameButton').Current.IsEnabled } 'Valid plan was not enabled.' | Out-Null
Write-Output 'PASS initial and incremental import default natural ascending order'
Set-Names 'Only one'
Wait-For { -not (Find-Id 'RenameButton').Current.IsEnabled } 'Mismatched counts were accepted.' | Out-Null
Set-Names "CON`r`nSecond`r`nThird"
Wait-For { -not (Find-Id 'RenameButton').Current.IsEnabled } 'Reserved filename was accepted.' | Out-Null
Set-Names "Same`r`nSame`r`nThird"
Wait-For { -not (Find-Id 'RenameButton').Current.IsEnabled } 'Duplicate target was accepted.' | Out-Null
Set-Names "First`r`nSecond`r`nThird"
Wait-For { (Find-Id 'RenameButton').Current.IsEnabled } 'Valid plan did not recover.' | Out-Null
Write-Output 'PASS mismatched counts, reserved names and duplicate targets'
$rows = Get-Rows
$before = Row-Text $rows[0]
([System.Windows.Automation.SelectionItemPattern]$rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Invoke-Control (Find-Id 'FileDownButton')
Wait-For { (Row-Text (Get-Rows)[0]) -ne $before } 'File down did not update the preview.' | Out-Null
$manualOrder = @((Get-Rows) | ForEach-Object { Row-Text $_ }) -join ';'
Add-Fixtures @($paths[0])
if ((@((Get-Rows) | ForEach-Object { Row-Text $_ }) -join ';') -ne $manualOrder) { throw 'Duplicate-only import disturbed manual ordering.' }
Invoke-Control (Find-Id 'FileUpButton')
Wait-For { (Row-Text (Get-Rows)[0]) -eq $before } 'File up did not restore the order.' | Out-Null
Write-Output 'PASS manual file ordering'
Invoke-Control (Find-Name '排序')
Invoke-Control (Wait-For { Find-Name '文件名：自然降序' } 'Sort menu did not open.')
Wait-For { (Row-Text (Get-Rows)[0]) -match '\|10.txt\|' } 'Natural descending sort failed.' | Out-Null
Invoke-Control (Find-Name '排序')
Invoke-Control (Wait-For { Find-Name '文件名：自然升序' } 'Sort menu did not reopen.')
Wait-For { (Row-Text (Get-Rows)[0]) -match '\|1.txt\|' } 'Natural ascending sort failed.' | Out-Null
Write-Output 'PASS natural filename sorting'
Invoke-Control (Find-Name '反转顺序')
Wait-For { (Row-Text (Get-Rows)[0]) -match 'Third.txt' } 'Name reversal did not update the preview.' | Out-Null
Invoke-Control (Find-Name '反转顺序')
$toggle = [System.Windows.Automation.TogglePattern](Find-Id 'KeepExtension').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$toggle.Toggle()
Wait-For { (Row-Text (Get-Rows)[0]) -match '\|First$' } 'Full-name mode did not remove extension.' | Out-Null
$toggle.Toggle()
Wait-For { (Row-Text (Get-Rows)[0]) -match 'First.txt' } 'Extension preservation did not recover.' | Out-Null
Write-Output 'PASS name reversal and extension option'
Add-Fixtures @($paths[0])
if ((Get-Rows).Count -ne 3) { throw 'Duplicate import added another row.' }
Write-Output 'PASS duplicate import deduplication'
$mapping = @{}
foreach ($row in (Get-Rows)) {
    $texts = (Row-Text $row).Split('|')
    $source = $texts | Where-Object { $_ -in @('1.txt', '2.txt', '10.txt') } | Select-Object -First 1
    $target = $texts | Where-Object { $_ -in @('First.txt', 'Second.txt', 'Third.txt') } | Select-Object -First 1
    if (-not $source -or -not $target) { throw ('Cannot read preview row: ' + ($texts -join '|')) }
    $mapping[$target] = [IO.File]::ReadAllText((Join-Path $fixtureRoot $source))
}
Invoke-Control (Find-Id 'RenameButton')
$confirm = Wait-For { Find-Id 'PrimaryButton' } 'Confirmation did not appear.'
Invoke-Control $confirm
Wait-For { (Find-Id 'UndoButton').Current.IsEnabled } 'Rename did not complete.' | Out-Null
foreach ($target in $mapping.Keys) {
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot $target)) -ne $mapping[$target]) { throw 'Renamed file content mismatch.' }
}
Write-Output 'PASS confirmed rename and content verification'
Invoke-Control (Find-Id 'UndoButton')
$confirm = Wait-For { Find-Id 'PrimaryButton' } 'Undo confirmation did not appear.'
Invoke-Control $confirm
Wait-For { -not (Find-Id 'UndoButton').Current.IsEnabled -and (Test-Path $paths[0]) } 'Undo did not complete.' | Out-Null
foreach ($number in @(1, 2, 10)) {
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot ($number.ToString() + '.txt'))) -ne ('payload-' + $number)) { throw 'Undo content mismatch.' }
}
Write-Output 'PASS undo through the actual native interface'
Invoke-Control (Find-Name '切换主题')
& (Join-Path $PSScriptRoot 'Inspect-Ui.ps1') -ProcessId $ProcessId -ScreenshotPath (Join-Path (Split-Path $PSScriptRoot) 'artifacts\dark-window.png') | Out-Null
Invoke-Control (Find-Name '切换主题')
& (Join-Path $PSScriptRoot 'Inspect-Ui.ps1') -ProcessId $ProcessId -ScreenshotPath (Join-Path (Split-Path $PSScriptRoot) 'artifacts\tested-window.png') | Out-Null
Invoke-Control (Find-Name '清空列表')
Set-Names ''
if ((Get-Rows).Count -ne 0 -or -not (Test-Path $paths[0])) { throw 'Clear-list safety check failed.' }
[IO.Directory]::Delete($fixtureRoot, $true)
Write-Output 'PASS theme switching and clearing without deleting files'
Write-Output 'All native UI checks passed.'
