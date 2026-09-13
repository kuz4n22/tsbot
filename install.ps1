#Requires -Version 5.1
<#
  TSBot - TeamSpeak music bot (YouTube) one-shot installer for Windows 10/11 x64.
  Everything lands in one folder (default %USERPROFILE%\TSBot), no admin rights needed.

  Quick install (PowerShell):
    irm https://raw.githubusercontent.com/kuz4n22/tsbot/tsbot/install.ps1 | iex

  With parameters (no questions asked):
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/kuz4n22/tsbot/tsbot/install.ps1))) `
        -Address "host:port" -Channel "Room" -ChannelPassword "secret" -Name "DJ Bot"

  Offline / manual: unzip TSBot-win-x64.zip from the Releases page and run Setup.cmd (it calls this script with -Payload).

  Re-running the installer updates the bot and keeps your settings (ts3audiobot.toml / rights.toml / queue);
  the server/room settings (bot\bots\dj\bot.toml) are rewritten from the values you pass or type in.
#>
param(
	[string]$Address,
	[string]$Channel,
	[string]$ChannelPassword,
	[string]$ServerPassword,
	[string]$Name = 'DJ Bot',
	[string]$Dir = "$env:USERPROFILE\TSBot",
	[string]$Repo = 'kuz4n22/tsbot',
	[string]$Branch = 'tsbot',
	[string]$Payload,      # local folder with bot\, scripts\, config\ (unpacked release) - skips all downloads of the bot
	[switch]$FromSource,   # ignore prebuilt releases, build from source
	[switch]$NoStart,
	[switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Say($m) { Write-Host "[TSBot] $m" -ForegroundColor Cyan }
function Dl($url, $out) { Say "Downloading $url"; Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 900 }
function Unzip($zip, $dest) {
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
	[IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
}
function FindFile($root, $name) { Get-ChildItem $root -Recurse -Filter $name | Select-Object -First 1 | ForEach-Object { $_.FullName } }
function TomlPath($p) { return $p.Replace('\', '\\') }
function TomlStr($s) { if ($null -eq $s) { return '' }; return ($s -replace '\\', '\\\\' -replace '"', '\"') }

if (-not [Environment]::Is64BitOperatingSystem) { throw '64-bit Windows is required.' }

# ---------- questions ----------
if (-not $Address) { $Address = Read-Host 'TeamSpeak server address (host or host:port)' }
if (-not $Address) { throw 'No server address, nothing to connect to.' }
if (-not $Channel) { $Channel = Read-Host 'Channel / room name (nested: "Parent/Room"; Enter = server default channel)' }
if (-not $PSBoundParameters.ContainsKey('ChannelPassword')) { $ChannelPassword = Read-Host 'Room password (Enter if none)' }
if (-not $PSBoundParameters.ContainsKey('ServerPassword')) { $ServerPassword = Read-Host 'Server password (Enter if none)' }
if (-not $PSBoundParameters.ContainsKey('Name')) { $n = Read-Host "Bot nickname [$Name]"; if ($n) { $Name = $n } }

New-Item -ItemType Directory -Force $Dir, "$Dir\_dl", "$Dir\bin", "$Dir\bot" | Out-Null
Say "Folder: $Dir"

# ---------- stop a running copy (only the one living in $Dir) ----------
$running = Get-Process TS3AudioBot, ciadpi -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Dir\*" }
if ($running) {
	Say 'Stopping the running bot'
	if (Test-Path "$Dir\Stop TSBot.cmd") { & cmd.exe /c "`"$Dir\Stop TSBot.cmd`"" | Out-Null }
	$running | Stop-Process -Force -ErrorAction SilentlyContinue
	Start-Sleep 2
}

# ---------- bot binaries: local payload / prebuilt release / build from source ----------
# $pkg = folder that contains bot\, scripts\, config\ (and README.md)
$pkg = $null
if ($Payload) {
	if (-not (Test-Path "$Payload\bot\TS3AudioBot.exe")) { throw "No bot\TS3AudioBot.exe under $Payload" }
	$pkg = (Resolve-Path $Payload).Path.TrimEnd('\')
	Say "Using local package: $pkg"
}
elseif (-not $FromSource) {
	try {
		Dl "https://github.com/$Repo/releases/latest/download/TSBot-win-x64.zip" "$Dir\_dl\TSBot-win-x64.zip"
		Unzip "$Dir\_dl\TSBot-win-x64.zip" "$Dir\_dl\prebuilt"
		$exe = FindFile "$Dir\_dl\prebuilt" 'TS3AudioBot.exe'
		if (-not $exe) { throw 'no TS3AudioBot.exe in the archive' }
		$pkg = Split-Path (Split-Path $exe -Parent) -Parent
		Say 'Prebuilt bot downloaded'
	}
	catch {
		Say "No prebuilt release ($($_.Exception.Message)) - building from source, 3-5 minutes"
		$pkg = $null
	}
}
if ($pkg) {
	# copy over, never wipe: user configs live in bot\ too (skip when the package was unpacked right into $Dir)
	if ($pkg -ne $Dir.TrimEnd('\')) { Copy-Item "$pkg\bot\*" "$Dir\bot" -Recurse -Force }
}
else {
	Dl "https://github.com/$Repo/archive/refs/heads/$Branch.zip" "$Dir\_dl\src.zip"
	Unzip "$Dir\_dl\src.zip" "$Dir\_dl\srczip"
	$inner = Get-ChildItem "$Dir\_dl\srczip" -Directory | Select-Object -First 1
	if (Test-Path "$Dir\src") { Remove-Item "$Dir\src" -Recurse -Force }
	Move-Item $inner.FullName "$Dir\src"
	if (-not (Test-Path "$Dir\_sdk\dotnet.exe")) {
		Say 'Installing a local .NET 8 SDK (into this folder only)'
		Dl 'https://dot.net/v1/dotnet-install.ps1' "$Dir\_dl\dotnet-install.ps1"
		& "$Dir\_dl\dotnet-install.ps1" -Channel 8.0 -InstallDir "$Dir\_sdk" -NoPath
	}
	$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
	$env:DOTNET_ROOT = "$Dir\_sdk"; $env:PATH = "$Dir\_sdk;$env:PATH"
	Say 'Building the bot'
	Push-Location "$Dir\src"
	& "$Dir\_sdk\dotnet.exe" publish TS3AudioBot\TS3AudioBot.csproj -c Release -r win-x64 --self-contained -o "$Dir\bot" -nologo -v q
	$code = $LASTEXITCODE
	Pop-Location
	if ($code -ne 0) { throw "Build failed (exit code $code)" }

	# web panel (optional): needs Node.js; the bot works fine without it
	$yarn = (Get-Command yarn -ErrorAction SilentlyContinue).Source
	$npx = (Get-Command npx -ErrorAction SilentlyContinue).Source
	if ($yarn -or $npx) {
		try {
			Say 'Building the web panel'
			Push-Location "$Dir\src\WebInterface"
			if ($yarn) { & yarn install --frozen-lockfile; & yarn run build } else { & npx --yes yarn install --frozen-lockfile; & npx --yes yarn run build }
			Pop-Location
			if (Test-Path "$Dir\src\WebInterface\dist") { New-Item -ItemType Directory -Force "$Dir\bot\WebInterface" | Out-Null; Copy-Item "$Dir\src\WebInterface\dist\*" "$Dir\bot\WebInterface" -Recurse -Force }
		} catch { Pop-Location; Say "Web panel skipped: $($_.Exception.Message)" }
	} else { Say 'Web panel skipped (needs Node.js) - the bot itself does not need it' }
	$pkg = "$Dir\src\tsbot"
}

# ---------- scripts ----------
foreach ($f in 'Start TSBot.cmd', 'Stop TSBot.cmd', 'Check YouTube.cmd', 'Update yt-dlp.cmd') { Copy-Item "$pkg\scripts\$f" "$Dir\$f" -Force }
foreach ($f in 'run-loop.cmd', 'netcheck.ps1') { Copy-Item "$pkg\scripts\$f" "$Dir\bin\$f" -Force }
Copy-Item "$pkg\config\yt-dlp.conf" "$Dir\bin\yt-dlp.conf" -Force
$readme = @("$pkg\README.md", "$pkg\..\README.md") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($readme) { Copy-Item $readme "$Dir\README.md" -Force }

# ---------- tools (bin\): yt-dlp, deno (yt-dlp needs a JS runtime for YouTube), ffmpeg, ciadpi ----------
if (-not (Test-Path "$Dir\bin\yt-dlp.exe")) { Dl 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' "$Dir\bin\yt-dlp.exe" }

if (-not (Get-Command deno -ErrorAction SilentlyContinue) -and -not (Test-Path "$Dir\bin\deno.exe")) {
	Dl 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' "$Dir\_dl\deno.zip"
	Unzip "$Dir\_dl\deno.zip" "$Dir\_dl\deno"
	Copy-Item (FindFile "$Dir\_dl\deno" 'deno.exe') "$Dir\bin\deno.exe" -Force
}

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg -and (Test-Path "$Dir\bin\ffmpeg.exe")) { $ffmpeg = "$Dir\bin\ffmpeg.exe" }
if (-not $ffmpeg) {
	Dl 'https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' "$Dir\_dl\ffmpeg.zip"
	Unzip "$Dir\_dl\ffmpeg.zip" "$Dir\_dl\ffmpeg"
	Copy-Item (FindFile "$Dir\_dl\ffmpeg" 'ffmpeg.exe') "$Dir\bin\ffmpeg.exe" -Force
	$ffmpeg = "$Dir\bin\ffmpeg.exe"
}
Say "ffmpeg: $ffmpeg"

if (-not (Test-Path "$Dir\bin\ciadpi.exe")) {
	Dl 'https://github.com/hufrea/byedpi/releases/download/v0.17.3/byedpi-17.3-x86_64-w64.zip' "$Dir\_dl\byedpi.zip"
	Unzip "$Dir\_dl\byedpi.zip" "$Dir\_dl\byedpi"
	Copy-Item (FindFile "$Dir\_dl\byedpi" 'ciadpi.exe') "$Dir\bin\ciadpi.exe" -Force
}

# ---------- config ----------
if (-not (Test-Path "$Dir\bot\ts3audiobot.toml")) {
	$cfg = Get-Content "$pkg\config\ts3audiobot.toml" -Raw
	$cfg = $cfg.Replace('{{YTDLP}}', (TomlPath "$Dir\bin\yt-dlp.exe")).Replace('{{FFMPEG}}', (TomlPath $ffmpeg)).Replace('{{CIADPI}}', (TomlPath "$Dir\bin\ciadpi.exe"))
	[IO.File]::WriteAllText("$Dir\bot\ts3audiobot.toml", $cfg, $utf8)
}
if (-not (Test-Path "$Dir\bot\rights.toml")) { Copy-Item "$pkg\config\rights.toml" "$Dir\bot\rights.toml" }
New-Item -ItemType Directory -Force "$Dir\bot\bots\dj" | Out-Null
$bt = Get-Content "$pkg\config\bot.toml" -Raw
$bt = $bt.Replace('{{ADDRESS}}', (TomlStr $Address)).Replace('{{CHANNEL}}', (TomlStr $Channel)).Replace('{{NAME}}', (TomlStr $Name)).Replace('{{SERVER_PW}}', (TomlStr $ServerPassword)).Replace('{{CHANNEL_PW}}', (TomlStr $ChannelPassword))
# keep the bot's identity between reinstalls so the server remembers it
$botToml = "$Dir\bot\bots\dj\bot.toml"
if (Test-Path $botToml) {
	$m = [regex]::Match((Get-Content $botToml -Raw), '(?ms)^\[connect\.identity\].*?(?=^\[|\z)')
	if ($m.Success) { $bt = $bt.TrimEnd() + "`n`n" + $m.Value.TrimEnd() + "`n" }
}
[IO.File]::WriteAllText($botToml, $bt, $utf8)

# ---------- shortcut + start ----------
if (-not $NoShortcut) { try {
	$ws = New-Object -ComObject WScript.Shell
	$lnk = $ws.CreateShortcut("$([Environment]::GetFolderPath('Desktop'))\TS Music Bot.lnk")
	$lnk.TargetPath = "$Dir\Start TSBot.cmd"; $lnk.WorkingDirectory = $Dir; $lnk.WindowStyle = 7
	$lnk.IconLocation = "$Dir\bot\TS3AudioBot.exe,0"; $lnk.Description = 'TS Music Bot'; $lnk.Save()
} catch { Say "Shortcut not created: $($_.Exception.Message)" } }

Remove-Item "$Dir\_dl" -Recurse -Force -ErrorAction SilentlyContinue

Say 'Done.'
Say "Start: desktop shortcut 'TS Music Bot' or $Dir\Start TSBot.cmd; stop: Stop TSBot.cmd"
Say 'In TeamSpeak, message the bot privately: a YouTube link or !yt <title>. Cheat sheet: !commands'
if (-not $NoStart) {
	Start-Process -FilePath "$Dir\Start TSBot.cmd" -WorkingDirectory $Dir -WindowStyle Minimized
	Say 'Bot started - it should show up on the server within half a minute.'
}
