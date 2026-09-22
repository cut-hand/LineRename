# Tauri 版 LineRename 冒烟测试：通过 WebView2 CDP 驱动真实界面，
# 完成 写名称→加文件→预览→重命名→撤销 全链路，并校验文件系统结果。
param([string]$ExePath = (Join-Path (Split-Path $PSScriptRoot) 'src-tauri\target\release\LineRename.exe'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$port = 9223
$fixtureRoot = Join-Path (Split-Path $PSScriptRoot) ('artifacts\tauri-ui-test-' + [Guid]::NewGuid().ToString('N'))
$receiptDir = Join-Path $fixtureRoot 'receipt'
[IO.Directory]::CreateDirectory($receiptDir) | Out-Null
$env:LINERENAME_RECEIPT_DIR = $receiptDir
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$port"

$paths = @(1, 2, 10 | ForEach-Object {
    $p = Join-Path $fixtureRoot ($_.ToString() + '.txt')
    [IO.File]::WriteAllText($p, 'payload-' + $_)
    $p
})

$exe = (Resolve-Path $ExePath).Path
$app = Start-Process -FilePath $exe -PassThru

$ws = $null
try {
    # 等待 CDP 目标出现
    $target = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt 25000) {
        try { $target = Invoke-RestMethod -Uri "http://127.0.0.1:$port/json" -TimeoutSec 2 | Where-Object { $_.type -eq 'page' } | Select-Object -First 1 } catch { $null }
        if ($target) { break }
        if ($app.HasExited) { throw ('应用过早退出，code=' + $app.ExitCode) }
        [Threading.Thread]::Sleep(250)
    }
    if (-not $target) { throw 'CDP target 未出现（WebView2 调试端口未打开）。' }

    $ws = [Net.WebSockets.ClientWebSocket]::new()
    $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait(10000) | Out-Null
    if (-not $ws.State -eq [Net.WebSockets.WebSocketState]::Open) { throw 'WebSocket 连接失败。' }

    $script:msgId = 0
    function Send-Cdp([string]$method, [hashtable]$params) {
        $script:msgId++
        $payload = (@{ id = $script:msgId; method = $method; params = $params } | ConvertTo-Json -Depth 8 -Compress)
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $ws.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(10000) | Out-Null
        $buffer = [byte[]]::new(262144)
        while ($true) {
            $sb = New-Object Text.StringBuilder
            do {
                $res = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
                if ($res.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'CDP socket closed.' }
                $sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $res.Count)) | Out-Null
            } while (-not $res.EndOfMessage)
            $msg = $sb.ToString() | ConvertFrom-Json
            if ($msg.id -eq $script:msgId) { return $msg }
        }
    }
    function Eval([string]$expression) {
        $r = Send-Cdp 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true; awaitPromise = $true }
        if ($r.result.PSObject.Properties['exceptionDetails']) {
            $ex = $r.result.exceptionDetails
            throw ('JS 异常: ' + $ex.text + ' ' + ($ex.exception.description | Out-String))
        }
        return $r.result.result.value
    }

    Eval "document.readyState" | Out-Null
    Write-Output 'PASS 应用启动，CDP 已连接'

    # 1) 写名称列表
    Eval "document.getElementById('names').value = `"First\nSecond\nThird`"; document.getElementById('names').dispatchEvent(new Event('input')); 'ok'" | Out-Null
    Start-Sleep -Milliseconds 400
    Write-Output 'PASS 名称列表输入'

    # 2) 添加文件（经命令桥，等同点击“添加文件”后确认）
    $pathsJson = ($paths | ConvertTo-Json -Compress)
    $added = Eval ('window.__TAURI__.core.invoke("add_paths", { paths: ' + $pathsJson + ' }).then(() => { document.getElementById("names").dispatchEvent(new Event("input")); return "added"; })')
    if ($added -ne 'added') { throw 'add_paths 未返回预期值。' }
    Start-Sleep -Milliseconds 400

    $rowText = Eval 'Array.from(document.querySelectorAll("#rows tr")).map(tr => tr.textContent).join("|")'
    if ($rowText -notmatch 'First\.txt') { throw ('预览缺少 First.txt: ' + $rowText) }
    if ($rowText -notmatch 'Third\.txt') { throw ('预览缺少 Third.txt: ' + $rowText) }
    if ($rowText -notmatch '就绪') { throw ('预览缺少 就绪 状态: ' + $rowText) }
    if (($rowText -split '\|').Count -ne 3) { throw ('应为 3 行: ' + $rowText) }
    Write-Output ('PASS 添加文件 + 自然序预览: ' + $rowText)

    # 截图（经 CDP 捕获 WebView2 实际渲染内容；PrintWindow 截不到 WebView2 表面）
    $shot = Send-Cdp 'Page.captureScreenshot' @{ format = 'png' }
    $shotPath = Join-Path (Split-Path $PSScriptRoot) 'artifacts\tauri-window.png'
    [IO.Directory]::CreateDirectory((Split-Path $shotPath)) | Out-Null
    [IO.File]::WriteAllBytes($shotPath, [Convert]::FromBase64String($shot.result.data))
    Write-Output ('截图: ' + $shotPath)

    # 3) 重命名（覆盖 confirm 后点击真实按钮）
    Eval 'window.confirm = () => true; "ok"' | Out-Null
    [void](Eval "document.getElementById('btnRename').click(); 'clicked'")
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $done = $false
    while ($deadline.ElapsedMilliseconds -lt 15000) {
        if (Test-Path (Join-Path $fixtureRoot 'First.txt')) { $done = $true; break }
        if ($app.HasExited) { throw '重命名过程中应用退出。' }
        [Threading.Thread]::Sleep(200)
    }
    if (-not $done) { throw '重命名未完成（First.txt 未出现）。' }
    foreach ($pair in @(@('First.txt', 'payload-1'), @('Second.txt', 'payload-2'), @('Third.txt', 'payload-10'))) {
        $content = [IO.File]::ReadAllText((Join-Path $fixtureRoot $pair[0]))
        if ($content -ne $pair[1]) { throw ('内容不符: ' + $pair[0]) }
    }
    if (-not (Test-Path (Join-Path $receiptDir 'last-undo.txt'))) { throw '撤销凭据未写入。' }
    Write-Output 'PASS 按钮触发重命名 + 内容校验 + 凭据写入'

    # 4) 撤销
    [void](Eval "document.getElementById('btnUndo').click(); 'clicked'")
    $restored = $false
    $deadline.Restart()
    while ($deadline.ElapsedMilliseconds -lt 15000) {
        if ((Test-Path (Join-Path $fixtureRoot '1.txt')) -and (Test-Path (Join-Path $fixtureRoot '10.txt'))) { $restored = $true; break }
        if ($app.HasExited) { throw '撤销过程中应用退出。' }
        [Threading.Thread]::Sleep(200)
    }
    if (-not $restored) { throw '撤销未完成（源文件名未恢复）。' }
    foreach ($n in @(1, 2, 10)) {
        $content = [IO.File]::ReadAllText((Join-Path $fixtureRoot ($n.ToString() + '.txt')))
        if ($content -ne ('payload-' + $n)) { throw ('撤销后内容不符: ' + $n + '.txt') }
    }
    Write-Output 'PASS 撤销恢复原始文件名与内容'

    # 5) 非法名称在预览中显示为跳过
    Eval "document.getElementById('names').value = `"bad/name\nx\ny`"; document.getElementById('names').dispatchEvent(new Event('input')); 'ok'" | Out-Null
    Start-Sleep -Milliseconds 400
    $badText = Eval 'Array.from(document.querySelectorAll("#rows tr")).map(tr => tr.textContent).join("|")'
    if ($badText -notmatch '非法字符') { throw ('非法名称未被标出: ' + $badText) }
    Write-Output 'PASS 非法名称预览标记'

    Write-Output '全部 Tauri UI 冒烟检查通过。'
} finally {
    if ($ws) { try { [void]$ws.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', [Threading.CancellationToken]::None).Wait(3000) } catch {} }
    if ($app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit(5000) | Out-Null }
    Remove-Item Env:LINERENAME_RECEIPT_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 5; $i++) {
        try { [IO.Directory]::Delete($fixtureRoot, $true); break } catch { [Threading.Thread]::Sleep(300) }
    }
}
