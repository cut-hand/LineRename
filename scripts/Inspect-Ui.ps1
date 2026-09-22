param([int]$ProcessId = 0, [string]$ScreenshotPath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if ($ProcessId -eq 0) { $ProcessId = (Get-Process LineRename | Select-Object -First 1).Id }
$condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $ProcessId
$window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
if (-not $window) { throw 'Application window was not found.' }
$elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $elements) {
    $current = $element.Current
    if ($current.Name -or $current.AutomationId) {
        [pscustomobject]@{ Type = $current.ControlType.ProgrammaticName; Id = $current.AutomationId; Name = $current.Name; Enabled = $current.IsEnabled; Bounds = $current.BoundingRectangle.ToString() }
    }
}
if ($ScreenshotPath) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class WindowCapture { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags); }'
    $bounds = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap ([int]$bounds.Width), ([int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $captured = [WindowCapture]::PrintWindow([IntPtr]$window.Current.NativeWindowHandle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    if ($captured) { $bitmap.Save($ScreenshotPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    $graphics.Dispose()
    $bitmap.Dispose()
    if (-not $captured) { throw 'Window-only capture failed.' }
}
