$currentCommit=git rev-parse HEAD

Remove-Item -Recurse -Force packaging

git worktree remove -f packaging

$ErrorActionPreference="Stop"

git worktree add -f packaging $currentCommit
cd packaging
Remove-Item -Recurse -Force .git

[XML]$versionXML = Get-Content build/Version.props
$tgsVersion = $versionXML.Project.PropertyGroup.TgsCoreVersion

$devProductCode = 'D24887FA-3228-4509-B5F3-4E07E349F278'
$devVersion = '0.22.475'

(Get-Content build/package/winget/Tgstation.Server.Host.Service.Msi/Tgstation.Server.Host.Service.Msi.vdproj).Replace($devVersion, $tgsVersion) | Set-Content build/package/winget/Tgstation.Server.Host.Service.Msi/Tgstation.Server.Host.Service.Msi.vdproj
(Get-Content build/package/winget/Tgstation.Server.Host.Service.Msi/Tgstation.Server.Host.Service.Msi.vdproj).Replace($devProductCode, [guid]::NewGuid().ToString().ToUpperInvariant()) | Set-Content build/package/winget/Tgstation.Server.Host.Service.Msi/Tgstation.Server.Host.Service.Msi.vdproj

dotnet restore
cd build/package/winget
dotnet build -c Release Tgstation.Server.Host.Service.Configure

# We make _some_ assumptions
DevEnv installer.sln /Project Tgstation.Server.Host.Service.Msi/Tgstation.Server.Host.Service.Msi.vdproj /build Release

cd ..