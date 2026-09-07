param([int]$Port = 5092)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/OSManager.Api/OSManager.Api.csproj'
$contentRoot = Split-Path -Parent $project
$application = Join-Path $contentRoot 'bin/Release/net8.0/OSManager.Api.dll'
$runId = [Guid]::NewGuid().ToString('N')
$targetName = "test-delete-root-$runId"
$target = Join-Path $contentRoot $targetName
$data = Join-Path $repo "test-artifacts/api-delete-data-$runId"
$stdout = Join-Path $repo "test-artifacts/api-delete-$runId.stdout.log"
$stderr = Join-Path $repo "test-artifacts/api-delete-$runId.stderr.log"
$replacement = Join-Path $repo "test-artifacts/replacement-$runId.txt"
$zipSource = Join-Path $repo "test-artifacts/zip-source-$runId"
$replacementZip = Join-Path $repo "test-artifacts/replacement-$runId.zip"
$downloadZip = Join-Path $repo "test-artifacts/download-$runId.zip"
$baseUrl = "http://127.0.0.1:$Port"

New-Item -ItemType Directory -Path (Join-Path $target 'folder') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $target 'direct-folder') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $target 'replace-folder') -Force | Out-Null
New-Item -ItemType Directory -Path $zipSource -Force | Out-Null
Set-Content -LiteralPath (Join-Path $target 'restore-me.txt') -Value 'RESTORE-CONTENT' -NoNewline
Set-Content -LiteralPath (Join-Path $target 'folder/delete-me.txt') -Value 'DELETE-CONTENT' -NoNewline
Set-Content -LiteralPath (Join-Path $target 'direct-folder/delete-me.txt') -Value 'DIRECT-CONTENT' -NoNewline
Set-Content -LiteralPath (Join-Path $target 'replace-me.txt') -Value 'OLD-FILE' -NoNewline
Set-Content -LiteralPath (Join-Path $target 'replace-folder/old.txt') -Value 'OLD-FOLDER' -NoNewline
Set-Content -LiteralPath $replacement -Value 'NEW-FILE' -NoNewline
Set-Content -LiteralPath (Join-Path $zipSource 'new.txt') -Value 'NEW-FOLDER' -NoNewline
Compress-Archive -Path (Join-Path $zipSource '*') -DestinationPath $replacementZip

$oldData = $env:OSManager__DataDirectory
$oldUrls = $env:ASPNETCORE_URLS
$env:OSManager__DataDirectory = $data
$env:ASPNETCORE_URLS = $baseUrl
$process = $null
try {
    $process = Start-Process dotnet -ArgumentList @($application,'--urls',$baseUrl) -WorkingDirectory $contentRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw "API 启动失败，详情见 $stderr" }
        try { Invoke-WebRequest "$baseUrl/" -UseBasicParsing -TimeoutSec 1 | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw "API 未在端口 $Port 启动" }

    $login = Invoke-RestMethod "$baseUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body (@{ userName='admin'; password='ChangeMe!123' } | ConvertTo-Json)
    $headers = @{ Authorization = "Bearer $($login.token)" }
    $directory = @{ id='delete-test'; name='Delete test'; path="`${CONTENT_ROOT}/$targetName"; canUpload=$true; relatedService=''; backupExcludes='folder' }
    Invoke-RestMethod "$baseUrl/api/settings/directories/delete-test" -Method Put -Headers $headers -ContentType 'application/json' -Body ($directory | ConvertTo-Json) | Out-Null

    $backedUp = Invoke-RestMethod "$baseUrl/api/files/delete" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ rootId='delete-test'; path='restore-me.txt'; backup=$true } | ConvertTo-Json)
    if (Test-Path -LiteralPath (Join-Path $target 'restore-me.txt')) { throw '备份删除后文件仍存在' }
    if (-not $backedUp.backedUp -or -not $backedUp.deploymentId) { throw '备份删除未返回恢复记录' }
    $deployment = (Invoke-RestMethod "$baseUrl/api/deployments" -Headers $headers) | Where-Object id -eq $backedUp.deploymentId
    if ($deployment.operation -ne 'Delete') { throw '删除恢复记录的 operation 不正确' }

    Invoke-RestMethod "$baseUrl/api/deployments/$($backedUp.deploymentId)/rollback" -Method Post -Headers $headers | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $target 'restore-me.txt') -Raw) -ne 'RESTORE-CONTENT') { throw '还原后的文件内容不正确' }

    $folderBackup = Invoke-RestMethod "$baseUrl/api/files/delete" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ rootId='delete-test'; path='folder'; backup=$true } | ConvertTo-Json)
    if (Test-Path -LiteralPath (Join-Path $target 'folder')) { throw '备份删除后文件夹仍存在' }
    Invoke-RestMethod "$baseUrl/api/deployments/$($folderBackup.deploymentId)/rollback" -Method Post -Headers $headers | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $target 'folder/delete-me.txt') -Raw) -ne 'DELETE-CONTENT') { throw '还原后的文件夹内容不正确，或错误应用了发布备份排除规则' }

    $fileReplace = Invoke-RestMethod "$baseUrl/api/files/replace?rootId=delete-test&path=replace-me.txt" -Method Post -Headers $headers -Form @{ file = Get-Item -LiteralPath $replacement }
    if ((Get-Content -LiteralPath (Join-Path $target 'replace-me.txt') -Raw) -ne 'NEW-FILE') { throw '文件替换内容不正确' }
    Invoke-RestMethod "$baseUrl/api/deployments/$($fileReplace.id)/rollback" -Method Post -Headers $headers | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $target 'replace-me.txt') -Raw) -ne 'OLD-FILE') { throw '文件替换回滚内容不正确' }

    $folderReplace = Invoke-RestMethod "$baseUrl/api/files/replace?rootId=delete-test&path=replace-folder" -Method Post -Headers $headers -Form @{ file = Get-Item -LiteralPath $replacementZip }
    if (Test-Path -LiteralPath (Join-Path $target 'replace-folder/old.txt')) { throw '文件夹替换后仍残留旧文件' }
    if ((Get-Content -LiteralPath (Join-Path $target 'replace-folder/new.txt') -Raw) -ne 'NEW-FOLDER') { throw '文件夹替换内容不正确' }
    Invoke-WebRequest "$baseUrl/api/files/download?rootId=delete-test&path=replace-folder" -Headers $headers -OutFile $downloadZip
    if ((Get-Item -LiteralPath $downloadZip).Length -eq 0) { throw '文件夹下载 ZIP 为空' }
    Invoke-RestMethod "$baseUrl/api/deployments/$($folderReplace.id)/rollback" -Method Post -Headers $headers | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $target 'replace-folder/old.txt') -Raw) -ne 'OLD-FOLDER') { throw '文件夹替换回滚内容不正确' }

    $beforeCount = @(Invoke-RestMethod "$baseUrl/api/deployments" -Headers $headers).Count
    $direct = Invoke-RestMethod "$baseUrl/api/files/delete" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ rootId='delete-test'; path='direct-folder'; backup=$false } | ConvertTo-Json)
    if (Test-Path -LiteralPath (Join-Path $target 'direct-folder')) { throw '直接删除后文件夹仍存在' }
    if ($direct.backedUp -or $direct.deploymentId) { throw '直接删除不应返回恢复记录' }
    $afterCount = @(Invoke-RestMethod "$baseUrl/api/deployments" -Headers $headers).Count
    if ($afterCount -ne $beforeCount) { throw '直接删除不应新增恢复记录' }

    Write-Output 'API_FILE_OPERATIONS_OK download-directory, replace-file, replace-directory, rollback, delete'
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:OSManager__DataDirectory = $oldData
    $env:ASPNETCORE_URLS = $oldUrls
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
    if (Test-Path -LiteralPath $zipSource) { Remove-Item -LiteralPath $zipSource -Recurse -Force }
    foreach ($file in @($replacement,$replacementZip,$downloadZip)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
}
