<#
.SYNOPSIS
    GitHub Actions 专用：准备版本提交和 tag，或发布已编译的完整 Release。
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'Publish')]
    [string]$Phase
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ReleaseBranch = 'main'
$ProjectPath = 'src/SidePeek.App/SidePeek.App.csproj'
$ChangelogPath = 'CHANGELOG.md'
$NotesPath = 'dist/release-notes.md'
$VersionPattern = '<Version>(?<version>\d+\.\d+\.\d+)</Version>'
$TagPattern = '^v\.?(?<version>\d+\.\d+\.\d+)$'
$RunTrailer = 'SidePeek-Release-Run'
$SourceTrailer = 'SidePeek-Release-Source'
$MaxPushAttempts = 3
$CommitAbbreviationLength = 7
$Utf8NoBom = [Text.UTF8Encoding]::new($false)

# 防止开发机误执行时提交或推送；本地打包继续使用根目录的 build 脚本。
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REF -ne "refs/heads/$ReleaseBranch") {
    throw '此脚本只能由 main 分支的 GitHub Actions 工作流调用。'
}
if ($env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$' -or
    $env:GITHUB_REPOSITORY -notmatch '^[\w.-]+/[\w.-]+$') {
    throw '缺少有效的 GitHub Actions 运行信息。'
}
Set-Location (Join-Path $PSScriptRoot '../..')

function Invoke-Git {
    param([string[]]$Arguments)
    $result = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments[0]) 失败，退出码 $LASTEXITCODE。" }
    return $result
}

function Invoke-Gh {
    param([string[]]$Arguments)
    $result = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments[0]) 失败，退出码 $LASTEXITCODE。" }
    return $result
}

function Get-ProjectVersion {
    $match = [regex]::Match([IO.File]::ReadAllText($ProjectPath), $VersionPattern)
    if (-not $match.Success) { throw "项目中缺少稳定版 Version：$ProjectPath" }
    return [version]$match.Groups['version'].Value
}

function Get-VersionTags {
    foreach ($tag in (Invoke-Git @('tag', '--list'))) {
        if ($tag -match $TagPattern) {
            [pscustomobject]@{ Tag = $tag; Version = [version]$Matches['version'] }
        }
    }
}

function Get-Releases {
    # --slurp 将分页合为一个 JSON，权限或网络失败必须中止，不能当作“尚未发布”。
    $json = (Invoke-Gh @('api', '--paginate', '--slurp',
        "repos/$env:GITHUB_REPOSITORY/releases")) -join "`n"
    foreach ($page in (ConvertFrom-Json -InputObject $json -NoEnumerate)) {
        foreach ($release in $page) { $release }
    }
}

function Get-ReleaseNotes {
    param([string]$Tag)
    $text = [IO.File]::ReadAllText($ChangelogPath)
    $pattern = '(?ms)^## ' + [regex]::Escape($Tag) + '\r?\n(?<notes>.*?)(?=^## v\d+\.\d+\.\d+\r?$|\z)'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { throw "更新日志中缺少 $Tag 的说明。" }
    return $match.Groups['notes'].Value.Trim()
}

function Set-ReleaseOutputs {
    param([string]$Tag)
    $head = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
    $tagCommit = (Invoke-Git @('rev-parse', "$Tag^{commit}")) -join ''
    if ($head -ne $tagCommit -or $Tag -ne "v$(Get-ProjectVersion)") {
        throw '版本、tag 和当前源码不一致，拒绝继续发布。'
    }
    $release = @(Get-Releases | Where-Object { $_.tag_name -eq $Tag })
    $shouldPublish = $release.Count -eq 0 -or $release[0].draft
    New-Item -ItemType Directory -Force -Path dist | Out-Null
    [IO.File]::WriteAllText($NotesPath, (Get-ReleaseNotes $Tag), $Utf8NoBom)
    "tag=$Tag" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    "publish=$($shouldPublish.ToString().ToLowerInvariant())" |
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    Write-Host "[Release] tag=$Tag，待发布=$shouldPublish，提交=$head"
    if (-not $shouldPublish) {
        "版本 [$Tag]($env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/releases/tag/$Tag) 已发布，无需重复构建。" |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
}

function New-ReleaseNotes {
    param([string]$Tag, [string]$SourceCommit, [object[]]$VersionTags)
    $publishedTags = @(Get-Releases | Where-Object { -not $_.draft -and -not $_.prerelease } |
        ForEach-Object { $_.tag_name })
    $previousTag = $null
    foreach ($candidate in ($VersionTags | Sort-Object Version -Descending)) {
        if ($candidate.Tag -notin $publishedTags) { continue }
        & git merge-base --is-ancestor $candidate.Tag $SourceCommit
        if ($LASTEXITCODE -eq 0) { $previousTag = $candidate.Tag; break }
        if ($LASTEXITCODE -ne 1) { throw '检查历史版本祖先关系失败。' }
    }
    # 以已成功发布的版本为边界，失败版本的修改会包含在下一次发布说明中。
    $range = if ($previousTag) { "$previousTag..$SourceCommit" } else { $SourceCommit }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('### 更新内容')
    $lines.Add('')
    foreach ($commit in (Invoke-Git @('log', '--reverse', '--no-merges', '--format=%H', $range))) {
        $body = (Invoke-Git @('show', '-s', '--format=%B', $commit)) -join "`n"
        if ($body -match "(?m)^${RunTrailer}: \d+$") { continue }
        $messageLines = $body.Trim() -split '\r?\n'
        $shortHash = $commit.Substring(0, $CommitAbbreviationLength)
        $lines.Add("- $($messageLines[0]) ([$shortHash]($env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/commit/$commit))")
        foreach ($line in ($messageLines | Select-Object -Skip 1)) {
            if ($line -match '^(Co-authored-by|Signed-off-by):' -or [string]::IsNullOrWhiteSpace($line)) { continue }
            $lines.Add("  $line")
        }
        $lines.Add('')
    }
    $lines.Add('### 下载说明')
    $lines.Add('')
    $lines.Add('- 普通版：需要安装 .NET 9 Desktop Runtime（Windows x64）。')
    $lines.Add('- 自包含版：文件名带 selfcontained，无需另外安装 .NET 运行时。')
    $lines.Add('- 解压后运行 SidePeek.exe；SHA256SUMS.txt 用于核对下载文件。')
    if ($previousTag) {
        $lines.Add('')
        $lines.Add("[完整变更]($env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/compare/$previousTag...$Tag)")
    }
    return ($lines -join "`n").Trim()
}

if ($Phase -eq 'Prepare') {
    Invoke-Git @('fetch', 'origin', "refs/heads/${ReleaseBranch}:refs/remotes/origin/$ReleaseBranch", '--tags') | Out-Host
    # 相同 run 的重跑复用原提交，即使 main 已前进，也不会再消耗版本号。
    $existingCommit = (Invoke-Git @('log', '--all', '-1', '--format=%H',
        "--grep=^${RunTrailer}: $env:GITHUB_RUN_ID$")) -join ''
    if (-not $existingCommit) {
        # 手动恢复或排队任务可能复用另一运行准备的版本；也按原始事件提交定位。
        $eventMessage = (Invoke-Git @('show', '-s', '--format=%B', $env:GITHUB_SHA)) -join "`n"
        if ($eventMessage -match "(?m)^${RunTrailer}: \d+$") {
            $existingCommit = $env:GITHUB_SHA
        } else {
            $existingCommit = (Invoke-Git @('log', '--all', '-1', '--format=%H',
                "--grep=^${SourceTrailer}: $env:GITHUB_SHA$")) -join ''
        }
    }
    if ($existingCommit) {
        Invoke-Git @('checkout', '--detach', $existingCommit) | Out-Host
        Set-ReleaseOutputs "v$(Get-ProjectVersion)"
        exit 0
    }

    Invoke-Git @('config', 'user.name', 'github-actions[bot]') | Out-Host
    Invoke-Git @('config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com') | Out-Host
    for ($attempt = 1; $attempt -le $MaxPushAttempts; $attempt++) {
        Invoke-Git @('checkout', '--detach', "origin/$ReleaseBranch") | Out-Host
        $sourceCommit = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
        $headMessage = (Invoke-Git @('show', '-s', '--format=%B', 'HEAD')) -join "`n"
        if ($headMessage -match "(?m)^${RunTrailer}: \d+$") {
            # 排队任务读取到机器人版本提交时，完成未发布版本或直接跳过。
            Set-ReleaseOutputs "v$(Get-ProjectVersion)"
            exit 0
        }

        $versionTags = @(Get-VersionTags)
        $baseVersion = Get-ProjectVersion
        foreach ($entry in $versionTags) {
            if ($entry.Version -gt $baseVersion) { $baseVersion = $entry.Version }
        }
        $nextVersion = [version]::new($baseVersion.Major, $baseVersion.Minor, $baseVersion.Build + 1)
        $tag = "v$nextVersion"
        Write-Host "[Release] 准备 $tag，源提交=$sourceCommit，推送尝试=$attempt/$MaxPushAttempts"
        $notes = New-ReleaseNotes $tag $sourceCommit $versionTags
        $projectText = [IO.File]::ReadAllText($ProjectPath)
        $versionRegex = [regex]::new($VersionPattern)
        [IO.File]::WriteAllText($ProjectPath,
            $versionRegex.Replace($projectText, "<Version>$nextVersion</Version>", 1), $Utf8NoBom)
        $history = if (Test-Path -LiteralPath $ChangelogPath) {
            ([IO.File]::ReadAllText($ChangelogPath) -replace '\A# 更新日志\s*', '').Trim()
        } else { '' }
        $changelog = "# 更新日志`n`n## $tag`n`n$notes`n"
        if ($history) { $changelog += "`n$history`n" }
        [IO.File]::WriteAllText($ChangelogPath, $changelog, $Utf8NoBom)
        $message = "自动发布 $tag`n`n1. 将应用版本更新为 $nextVersion。`n2. 根据提交记录生成更新日志。`n`n${RunTrailer}: $env:GITHUB_RUN_ID`n${SourceTrailer}: $sourceCommit`n"
        $messagePath = Join-Path $env:RUNNER_TEMP 'sidepeek-release-commit.txt'
        [IO.File]::WriteAllText($messagePath, $message, $Utf8NoBom)
        Invoke-Git @('add', '--', $ProjectPath, $ChangelogPath) | Out-Host
        Invoke-Git @('commit', '--file', $messagePath) | Out-Host
        Invoke-Git @('tag', '-a', $tag, '-m', "SidePeek $tag") | Out-Host

        # 原子推送保证版本提交和 tag 同时成功；遇到用户的新 push 则重新基于最新源码准备。
        & git push --atomic origin "HEAD:refs/heads/$ReleaseBranch" "refs/tags/$tag"
        if ($LASTEXITCODE -eq 0) {
            Set-ReleaseOutputs $tag
            exit 0
        }
        $preparedCommit = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
        Invoke-Git @('fetch', 'origin', "refs/heads/${ReleaseBranch}:refs/remotes/origin/$ReleaseBranch") | Out-Host
        $remoteTag = @(Invoke-Git @('ls-remote', 'origin', "refs/tags/$tag^{}"))
        if ($remoteTag.Count -gt 0 -and ($remoteTag[0] -split '\s+')[0] -eq $preparedCommit) {
            # 推送成功但连接中断也可能返回失败，不生成重复版本。
            Set-ReleaseOutputs $tag
            exit 0
        }
        Invoke-Git @('tag', '-d', $tag) | Out-Host
        $remoteHead = (Invoke-Git @('rev-parse', "origin/$ReleaseBranch")) -join ''
        if ($remoteHead -eq $sourceCommit) {
            throw '版本推送被拒绝；请查看日志中的权限或分支规则错误。不会强推或修改保护规则。'
        }
        Invoke-Git @('fetch', 'origin', '--tags') | Out-Host
        Write-Warning '[Release] main 已前进，将重新计算版本和更新说明。'
    }
    throw "连续 $MaxPushAttempts 次推送发生冲突，请重跑工作流。"
}

$tag = $env:RELEASE_TAG
if ($tag -notmatch '^v\d+\.\d+\.\d+$') { throw '缺少有效的 RELEASE_TAG。' }
$version = Get-ProjectVersion
$tagCommit = (Invoke-Git @('rev-parse', "$tag^{commit}")) -join ''
$head = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
if ($head -ne $tagCommit -or $tag -ne "v$version") { throw '待发布包的源码、版本与 tag 不一致。' }
$releases = @(Get-Releases)
$existing = @($releases | Where-Object { $_.tag_name -eq $tag })
if ($existing.Count -gt 0 -and -not $existing[0].draft) {
    Write-Host "[Release] $tag 已发布，保留现有发行包。"
    exit 0
}
$assets = @("dist/SidePeek-$version-win-x64.zip", "dist/SidePeek-$version-win-x64-selfcontained.zip")
$checksums = foreach ($asset in $assets) {
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf) -or (Get-Item -LiteralPath $asset).Length -eq 0) {
        throw "缺少完整发布包：$asset"
    }
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($asset))"
}
$checksumPath = 'dist/SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, [string[]]$checksums, $Utf8NoBom)
[IO.File]::WriteAllText($NotesPath, (Get-ReleaseNotes $tag), $Utf8NoBom)
$assets += $checksumPath
$assets += $NotesPath
if ($existing.Count -eq 0) {
    Invoke-Gh @('release', 'create', $tag, '--repo', $env:GITHUB_REPOSITORY,
        '--verify-tag', '--draft', '--title', "SidePeek $tag", '--notes-file', $NotesPath) | Out-Host
} else {
    Invoke-Gh @('release', 'edit', $tag, '--repo', $env:GITHUB_REPOSITORY,
        '--title', "SidePeek $tag", '--notes-file', $NotesPath) | Out-Host
}
# 仅替换尚未公开的草稿附件；全部上传成功后再公开，兼容不可变 Release。
Invoke-Gh (@('release', 'upload', $tag, '--repo', $env:GITHUB_REPOSITORY, '--clobber') + $assets) | Out-Host
$isLatest = $true
foreach ($release in $releases) {
    if (-not $release.draft -and -not $release.prerelease -and $release.tag_name -match $TagPattern) {
        if ([version]$Matches['version'] -gt $version) { $isLatest = $false }
    }
}
Invoke-Gh @('release', 'edit', $tag, '--repo', $env:GITHUB_REPOSITORY, '--draft=false',
    "--latest=$($isLatest.ToString().ToLowerInvariant())") | Out-Host
Write-Host "[Release] 已发布 $tag，包含普通包、自包含包、校验文件和更新说明。"
"已发布 [SidePeek $tag]($env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/releases/tag/$tag)。" |
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
exit 0
