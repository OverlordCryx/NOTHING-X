Set sh = CreateObject("WScript.Shell")
Set objShell = CreateObject("Shell.Application")


WScript.Sleep 6000








On Error Resume Next
sh.Run """C:\START--\RGB\set-logitech-color.vbs""", 0, False
sh.Run """C:\START--\Network R USB.vbs""", 0, true
WScript.Sleep 6000
sh.Run """C:\Program Files\Waterfox\waterfox.exe""", 2, False
sh.Run """C:\Users\Administrator\AppData\Roaming\Spotify\Spotify.exe""", 2, False

fx = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\FxSound\FxSound.lnk"

psLoop = _
"$fx='" & fx & "';" & _
"while($true){" & _
" $audio = Get-PnpDevice -Class AudioEndpoint -PresentOnly | Where-Object {" & _
"  $_.FriendlyName -like '*Baseus Bowie H1i*'" & _
" };" & _
"" & _
" if($audio -and $audio.Status -eq 'OK'){" & _
"  if(-not (Get-Process FxSound -ErrorAction SilentlyContinue)){" & _
"   Start-Process $fx" & _
"  };" & _
"" & _
"  Stop-Process -Id $PID -Force" & _
" }" & _
"" & _
" Start-Sleep -Seconds 2" & _
"}"

sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command """ & psLoop & """", 0, False

Set sh = Nothing
WScript.Quit

