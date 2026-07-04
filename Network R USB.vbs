Set objShell = CreateObject("Shell.Application")

psCommand = "$adapters = Get-NetAdapter | Where-Object {$_.Status -eq 'Up'};" & _
" foreach ($adapter in $adapters) {" & _
"   $pnp = Get-PnpDevice | Where-Object {$_.FriendlyName -eq $adapter.InterfaceDescription};" & _
"   if ($pnp -and $pnp.InstanceId -match '^USB') {" & _
"       $parentId = ($pnp | Get-PnpDeviceProperty DEVPKEY_Device_Parent).Data;" & _
"       if ($parentId) {" & _
"           $parent = Get-PnpDevice -InstanceId $parentId;" & _
"           Disable-PnpDevice -InstanceId $parent.InstanceId -Confirm:$false;" & _
"           Start-Sleep -Seconds 2;" & _
"           Enable-PnpDevice -InstanceId $parent.InstanceId -Confirm:$false;" & _
"       }" & _
"   }" & _
"}"

objShell.ShellExecute "powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command """ & psCommand & """", "", "runas", 0

Set shell = Nothing
WScript.Quit