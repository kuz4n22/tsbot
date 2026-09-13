param([switch]$Apply, [switch]$Verbose)
# TSBot: проверка сети. Открывается ли YouTube напрямую? Если нет — подбирает стратегию ByeDPI (ciadpi),
# которая работает у этого провайдера, и с -Apply записывает её в bot\ts3audiobot.toml ([tools.bypass] args).
# Ничего системного не трогает.
$ErrorActionPreference = 'SilentlyContinue'
$root   = Split-Path $PSScriptRoot -Parent
$env:PATH = "$root\bin;$env:PATH"   # yt-dlp finds deno / ffmpeg in bin\
$ciadpi = Join-Path $root 'bin\ciadpi.exe'
$ytdlp  = Join-Path $root 'bin\yt-dlp.exe'
$toml   = Join-Path $root 'bot\ts3audiobot.toml'
$testPort = 1081
$base = "--ip 127.0.0.1 --port 1080 --timeout 4 --auto-mode 3"
$presets = @(
  '--split 1+s --disorder 3+s',
  '--tlsrec 3+s --split 1+s --disorder 3+s',
  '--disorder 1 --fake -1 --ttl 8',
  '--fake -1 --ttl 5 --tlsrec 3+s --disorder 1',
  '--oob 3+s --split 1+s',
  '--split 1+s --disorder 3+s --mod-http=h,d,r'
)
function Say($m) { Write-Host $m }
function Curl204($proxy) {
  $args = @('-s','-o','NUL','-w','%{http_code}','-m','8','https://www.youtube.com/generate_204')
  if ($proxy) { $args = @('--proxy', $proxy) + $args }
  $r = & curl.exe @args 2>$null
  return ($r -eq '204')
}
function TestPreset($preset) {
  $p = Start-Process -FilePath $ciadpi -ArgumentList "--ip 127.0.0.1 --port $testPort $preset" -WindowStyle Hidden -PassThru
  Start-Sleep -Milliseconds 700
  $ok = $false; $speed = 0
  try {
    if (Curl204 "socks5h://127.0.0.1:$testPort") {
      $url = & $ytdlp --proxy "socks5h://127.0.0.1:$testPort" --no-warnings --no-config -f 140 --get-url -- dQw4w9WgXcQ 2>$null | Select-Object -First 1
      if ($url) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = & curl.exe --proxy "socks5h://127.0.0.1:$testPort" -s -o NUL -w '%{http_code} %{size_download}' -m 20 -r 0-1500000 $url 2>$null
        $sw.Stop()
        $parts = "$r".Split(' ')
        if ($parts[0] -match '^20[06]$' -and [int]$parts[1] -gt 1000000) { $ok = $true; $speed = [math]::Round([int]$parts[1] / 1024 / [math]::Max(0.2, $sw.Elapsed.TotalSeconds)) }
      }
    }
  } finally { Stop-Process -Id $p.Id -Force }
  return @{ ok = $ok; kbps = $speed }
}

$direct = Curl204 $null
if ($direct) {
  $mode = 'adaptive'
  $order = $presets
  Say 'YouTube открывается напрямую (VPN или провайдер не блокирует) — обход в режиме «по требованию».'
} else {
  Say 'YouTube напрямую НЕ открывается — подбираю стратегию обхода (ByeDPI)...'
  $winner = $null
  foreach ($pr in $presets) {
    $res = TestPreset $pr
    if ($Verbose) { Say ("  {0,-48} {1} {2}" -f $pr, $(if ($res.ok) {'OK'} else {'--'}), $(if ($res.ok) {"$($res.kbps) KB/s"} else {''})) }
    if ($res.ok) { $winner = $pr; break }
  }
  if ($winner) {
    $mode = 'desync-first'
    $order = @($winner) + ($presets | Where-Object { $_ -ne $winner })
    Say "Работает: $winner"
  } else {
    $mode = 'adaptive'
    $order = $presets
    Say 'Ни одна стратегия не сработала — без VPN YouTube у этого провайдера пока не обойти. Бот будет пробовать сам.'
  }
}
$groups = ($order | ForEach-Object { "--auto=torst,ssl_err $_" }) -join ' '
if ($mode -eq 'desync-first') { $args = "$base $($order[0]) " + (($order | Select-Object -Skip 1 | ForEach-Object { "--auto=torst,ssl_err $_" }) -join ' ') }
else { $args = "$base $groups" }
Say "режим: $mode"
if ($Verbose) { Say "args=$args" }
if ($Apply -and (Test-Path $toml)) {
  $t = [IO.File]::ReadAllText($toml)
  $new = [regex]::Replace($t, '(?m)^args = ".*"(?=\r?$)', ('args = "' + $args + '"'), 1)
  if ($new -ne $t) { [IO.File]::WriteAllText($toml, $new, (New-Object Text.UTF8Encoding($false))); Say 'конфиг обновлён' }
}
