[CmdletBinding()]
param([string]$GameRoot,[ValidateSet('Check','Update','Undo')][string]$Action='Update',[switch]$CheckOnly)
# Player update "Alpha2" (Twin Cells 0.2.0-alpha, 2026-11-12): replaces the CoopPack 0.1.0-alpha build (Alpha1) in
# coremod\mods\CoopPack with the CoopPack 0.2.0-alpha build. The folder, the DLL name and the module name stay CoopPack;
# only CoopPack.dll and modinfo.json change (CoopSteamInviteWorker.dll and Launch-Coop.ps1, which keeps
# COOP_SYNC_DISABLE_COLORS=1, stay byte for byte). Accepts only a copy at ...-Load1-Lobby1-Alpha1 that is one of the two
# recognised Alpha1 states (the owner's updated copy, or a copy made by the Alpha1 full installer): every file
# coop-install.json owns must be exactly the Alpha1 file set below. Brings it to ...-Load1-Lobby1-Alpha1-Alpha2.
#   -CheckOnly      (or -Action Check) read only: prints the detected state and every planned change; writes nothing.
#   -Action Update  (default) the update.
#   -Action Undo    returns an Alpha2 copy to exactly the Alpha1 state it had before the update (every owned file and
#                   coop-install.json byte for byte) from the backup kept under coop-fix-updates\<alpha2Update>\previous.
# One transaction per run: lock, backup of every moved file, journal in transaction.json, rollback on any failure. Saves
# (save\), configs (coremod\config\, mods\DeadCellsMultiplayerMod\config.json) and the pack's data folder
# (coremod\data\CoopPack\, e.g. CoopPadFix.state) are never written: they are hashed before and after and must be
# unchanged. Windows PowerShell 5.1 and PowerShell 7. This file is ASCII only.
# Ctrl+C (or any stop of the pipeline) inside the transaction: PowerShell then skips every catch block and runs only the
# finally blocks, so the transaction settles in its finally: a run whose Committed record is on disk stays committed;
# any other run moves every moved file back (the same rollback as a failure) and records RolledBack with interrupted=true.
# The lock coop-setup.lock is removed only when the copy is settled (committed, or rolled back without an error); a run
# that cannot settle keeps the lock, so the next run and the launcher refuse the copy until it is recovered.
Set-StrictMode -Version 2
$ErrorActionPreference='Stop'
if($CheckOnly){if($Action -eq 'Undo'){$Action='Undo'}else{$Action='Check'}}
$kit=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
# Public distribution: developer fixtures and fault-injection hooks are disabled.
$devZone=''
$inDevZone=$false
$qa=$false
$script:txStarted=$false

function No-Link([string]$Path) {
    $at=[IO.Path]::GetFullPath($Path)
    while($at){
        $attributes=$null
        try{$attributes=[IO.File]::GetAttributes($at)}
        catch [IO.FileNotFoundException]{}
        catch [IO.DirectoryNotFoundException]{}
        if($null -ne $attributes -and ($attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked path refused: $at"}
        $at=[IO.Path]::GetDirectoryName($at)
    }
}
function Child([string]$Root,[string]$Relative) {
    if(!$Relative -or $Relative -match ':|(^|[\\/])\.\.?([\\/]|$)|^[\\/]' -or [IO.Path]::IsPathRooted($Relative)){throw 'Unsafe relative path.'}
    $p=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(!$p.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escaped its root.'}
    No-Link $p
    return $p
}
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Text-Sha([string]$Text){$h=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($h.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($Text))).Replace('-','')}finally{$h.Dispose()}}
function Assert-Hash([string]$Path,[string]$Expected){
    No-Link $Path
    if($Expected -notmatch '^[A-Fa-f0-9]{64}$' -or !(Test-Path -LiteralPath $Path -PathType Leaf) -or (Sha $Path) -ne $Expected){throw "Missing or changed file: $Path"}
}
function Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))}
function Read-Json([string]$Path){Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json}
function Prop($Object,[string]$Name){if($null -eq $Object){return $null};$p=$Object.PSObject.Properties[$Name];if($p){return $p.Value};return $null}
# Sets a property in place when it exists (keeps its position in the JSON), adds it at the end otherwise.
function Set-Prop($Object,[string]$Name,$Value){if($Object.PSObject.Properties[$Name]){$Object.PSObject.Properties[$Name].Value=$Value}else{$Object|Add-Member -NotePropertyName $Name -NotePropertyValue $Value}}
function Sort-Ordinal([string[]]$Lines){$a=[string[]]@($Lines);[Array]::Sort($a,[StringComparer]::Ordinal);return ,$a}
function Save-Rows([string]$Path,[string]$Root) {
    No-Link $Path
    if(!(Test-Path -LiteralPath $Path)){return}
    if(Test-Path -LiteralPath $Path -PathType Leaf){[pscustomobject]@{path=$Path.Substring($Root.Length+1).Replace('\','/');sha256=(Sha $Path)};return}
    [pscustomobject]@{path=$Path.Substring($Root.Length+1).Replace('\','/')+'/';sha256=''}
    foreach($item in @(Get-ChildItem -LiteralPath $Path -Force|Sort-Object Name)){Save-Rows $item.FullName $Root}
}
# Saves, configs and the pack's own data folder: save\ (all of it), coremod\config\ (CoopLobbyColor.json, the
# DeadCellsMultiplayerMod settings, ...), mods\DeadCellsMultiplayerMod\config.json and coremod\data\CoopPack\ (the record
# CoopPadFix keeps, CoopPadFix.state). They must be byte-identical before and after; the update never writes them.
function Get-Preserved([string]$Root){
    $rows=@(Save-Rows (Child $Root 'save') $Root)+@(Save-Rows (Child $Root 'coremod/config') $Root)+@(Save-Rows (Child $Root 'mods/DeadCellsMultiplayerMod/config.json') $Root)+@(Save-Rows (Child $Root 'coremod/data/CoopPack') $Root)
    return (ConvertTo-Json -InputObject @($rows) -Depth 5 -Compress)
}
function Assert-Stopped([string]$Root){
    try{$found=@(Get-CimInstance Win32_Process -Filter "Name='DeadCellsModding.exe' OR Name='deadcells.exe' OR Name='deadcells_gl.exe'")}catch{throw ('Cannot check whether the game is running (Windows process query failed): '+$_.Exception.Message)}
    foreach($p in $found){
        if(!$p.ExecutablePath){throw 'Cannot determine a running game path. Close Dead Cells before updating.'}
        if($p.ExecutablePath.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Close this co-op game before updating. No process was stopped.'}
    }
}
function Verify-Owned($State,[string]$Root){
    $seen=@{}
    foreach($row in @($State.ownedFiles)){
        $p=Child $Root $row.file
        if($seen.ContainsKey($p)){throw 'Duplicate installed file.'};$seen[$p]=$true
        Assert-Hash $p $row.sha256
    }
    foreach($mod in @($State.release.sidecars)){
        foreach($part in @($mod.files)){
            $relative='coremod/mods/'+$mod.name+'/'+$part.file
            $rows=@($State.ownedFiles|Where-Object file -eq $relative)
            if($rows.Count -ne 1 -or $rows[0].sha256 -ne $part.sha256){throw 'Release and installed module inventories disagree.'}
        }
    }
    foreach($part in @($State.release.launcherFiles)){
        $rows=@($State.ownedFiles|Where-Object file -eq $part.file)
        if($rows.Count -ne 1 -or $rows[0].sha256 -ne $part.sha256){throw 'Release and installed launcher inventories disagree.'}
    }
}

# ---- identities ----
$alpha1='DeadCells-Coop-Test-2026-11-08-B893-Steam1-Death1-Anchor1-Dialog1-Frame1-Load1-Lobby1-Alpha1'
$target=$alpha1+'-Alpha2'
$packName='CoopPack';$fromVersion='0.1.0-alpha';$packVersion='0.2.0-alpha'
$packFiles=@('CoopSteamInviteWorker.dll','CoopPack.dll','modinfo.json')
$changedFiles=@('CoopPack.dll','modinfo.json')
$workerSha='3D574B4977FD91F2DE7CC3292254678CAABA874BC0A646A5CC05C7BD16DF2C8B'
$launcherFile='Launch-Coop.ps1';$startFile='Start Dead Cells Coop.cmd'
$launcherSha='8F1A86F7C6645CE7070E282D43C46AEB4CC8F53CDA8B699F35CDF6B33BDFD724';$startSha='C4C4035EC0E5B0AD256989BFED45854258C99BBEED1C87EDFFBB72AF962751F2'
$baseDll='coremod/mods/DeadCellsMultiplayerMod/DeadCellsMultiplayerMod.dll';$baseSha='378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28'
$lsi=[pscustomobject]@{name='LocalSaveIsolation';version='1.0.0';files=@([pscustomobject]@{file='LocalSaveIsolation.dll';sha256='F4226EBF18311342A6813D2ACBA5457AC89759E156E2DFF0697A4243811C4EF7'},[pscustomobject]@{file='modinfo.json';sha256='85BA727B34ABC54433100FB6EA3041034B17E2C49919A1208CD453D5D2BEC5F3'})}
# The handshake capability tokens of CoopPack 0.2.0-alpha (parts.json wireTokens with a leading '+', HELLO/WELCOME order).
$capabilities=@('+TwinCells-Alpha2','+csf1.3a','+csfcine1','+csfelev1','+csfelev2')
# The recognised starting states. Both hold exactly the Alpha1 file set (the Alpha1 kit installs the same files whatever
# state it started from); they are told apart by their recorded history, which is reported. Build-Kit.ps1 reads this block
# for UPDATE.json, and the kit's copy must be identical.
#   state|<id>|<title>
#   file|<path coop-install.json owns outside coremod/core/>|<SHA-256>   (every such row, no other)
#   core|<count>|<SHA-256 of the sorted "path|SHA-256" lines of every owned coremod/core/ row, LF-joined, UTF-8>
# BEGIN FROM-STATES
$fromTable=@'
state|A|the owner's copy: an adopted 2026-11-08 test copy, updated step by step and then by the Alpha1 update kit
state|B|a copy made by the Alpha1 full installer (2026-11-08 base kit + All-Updates 2026-11-10 + Alpha1 update)
file|coremod/ModCoreVersion.txt|5D44411064C0A2DF989149571D4D16D6095EE0BB943431E4A98D7DA0F761DC4F
file|coremod/plugins/ModCore.ModLoader.Default/ModCore.ModLoader.Default.dll|CE5A7939AFDC6D62AB014180B29F8FF5BAA7669FAF484DE3F4CE3970D5118AE8
file|coremod/plugins/ModCore.ModLoader.Default/ModCore.ModLoader.Default.pdb|14930B0DDC06ACB6B8C848911569533ADC5A2ECF80F326A966502502783F120E
file|coremod/mods/DeadCellsMultiplayerMod/.dcmm-installed-build.json|241C827D5028D7E0FBE3A645542F87C72D6298CE6A8FA3D267D23ECDF314DA70
file|coremod/mods/DeadCellsMultiplayerMod/DeadCellsMultiplayerMod.dll|378BE6B0B8517A2C2F50A7A95DD240A4F9026F34AE0FDE1A40576D0E77520D28
file|coremod/mods/DeadCellsMultiplayerMod/DeadCellsMultiplayerMod.pdb|DFB098BB906BC21D0DDBEDFE08799508D2B09856036013952FEB0CC6A064FEBE
file|coremod/mods/DeadCellsMultiplayerMod/modinfo.json|DC06E5EBDDFF1F3C3DD296092FE5DB6EFDE0AAFCAF375955BFF3F42DE366EBC1
file|coremod/mods/DeadCellsMultiplayerMod/res.pak|35901F6B8E0522CB152FDCBA92F9C20182B8ABB513521802D0F1D567D891A551
file|coremod/mods/DeadCellsMultiplayerMod/steam_api64.dll|EB17909A76668CF9AE0B92A618A34A50F6C73D3A6787CB4DD8CE36A8B10BFB75
file|coremod/mods/LocalSaveIsolation/LocalSaveIsolation.dll|F4226EBF18311342A6813D2ACBA5457AC89759E156E2DFF0697A4243811C4EF7
file|coremod/mods/LocalSaveIsolation/modinfo.json|85BA727B34ABC54433100FB6EA3041034B17E2C49919A1208CD453D5D2BEC5F3
file|Launch-Coop.ps1|8F1A86F7C6645CE7070E282D43C46AEB4CC8F53CDA8B699F35CDF6B33BDFD724
file|Start Dead Cells Coop.cmd|C4C4035EC0E5B0AD256989BFED45854258C99BBEED1C87EDFFBB72AF962751F2
file|coremod/mods/CoopPack/CoopSteamInviteWorker.dll|3D574B4977FD91F2DE7CC3292254678CAABA874BC0A646A5CC05C7BD16DF2C8B
file|coremod/mods/CoopPack/CoopPack.dll|A05309D439BC90F52A90C17D9D2928908EAD6051AED22B5AFA95D5BEB2455DAE
file|coremod/mods/CoopPack/modinfo.json|20A1489F3C02D8CAA194017CC167587A9C0BCB7065780277BEE6696E39E49DB7
core|330|CF72E17107E5F888759A34C5865F5AE90AC3AA84C58C1D24DAA99CE346F62809
'@
# END FROM-STATES
$tableRows=@($fromTable -split "`r?`n"|Where-Object{$_})
$stateTitles=[ordered]@{};$alpha1Files=[ordered]@{};$coreCount=-1;$coreDigest=''
foreach($line in $tableRows){
    $c=$line.Split('|')
    if($c[0] -ceq 'state'){$stateTitles[$c[1]]=$c[2]}
    elseif($c[0] -ceq 'file'){$alpha1Files[$c[1]]=$c[2]}
    elseif($c[0] -ceq 'core'){$coreCount=[int]$c[1];$coreDigest=$c[2]}
    else{throw 'Broken from-state table.'}
}
$alpha1Pack=[ordered]@{};foreach($f in $packFiles){$alpha1Pack[$f]=$alpha1Files['coremod/mods/'+$packName+'/'+$f]}
function Table-Lines{return ,(Sort-Ordinal $tableRows)}
function Same-Sidecar($Actual,$Expected){
    if($null -eq $Actual -or [string](Prop $Actual 'version') -cne $Expected.version){return $false}
    $files=@(Prop $Actual 'files');$want=@($Expected.files)
    if($files.Count -ne $want.Count){return $false}
    for($i=0;$i -lt $want.Count;$i++){if([string](Prop $files[$i] 'file') -cne $want[$i].file -or [string](Prop $files[$i] 'sha256') -cne $want[$i].sha256){return $false}}
    return $true
}
# The owned-file rows of a state against an expected set (non-core rows exactly, core rows as count + digest).
function Test-OwnedSet($St,$Files,[string]$Label){
    $core=[Collections.Generic.List[string]]::new();$other=@{}
    foreach($row in @($St.ownedFiles)){
        $f=[string](Prop $row 'file');$h=[string](Prop $row 'sha256')
        if($f.StartsWith('coremod/core/',[StringComparison]::Ordinal)){$core.Add($f+'|'+$h.ToUpperInvariant());continue}
        if($other.ContainsKey($f)){throw ('Duplicate registered file in coop-install.json: '+$f)};$other[$f]=$h
    }
    foreach($f in $Files.Keys){
        if(!$other.ContainsKey($f)){throw ('A file of the '+$Label+' file set is not registered in coop-install.json: '+$f)}
        if($other[$f] -cne $Files[$f]){throw ('Registered file '+$f+' is not the '+$Label+' build (SHA-256 '+$other[$f].Substring(0,[Math]::Min(8,$other[$f].Length))+', expected '+$Files[$f].Substring(0,8)+').')}
    }
    foreach($f in $other.Keys){if(!$Files.Contains($f)){throw ('Unexpected file registered in coop-install.json: '+$f)}}
    if($core.Count -ne $coreCount -or (Text-Sha ((Sort-Ordinal $core.ToArray()) -join "`n")) -cne $coreDigest){throw ('The registered DCCM core files (coremod/core, '+$core.Count+' rows) are not the ones of the '+$Label+' file set.')}
}
# Where the copy came from, from its recorded history (both have the same files). A: adopted (adoptionReceipt, no source
# game recorded); B: made by the base kit's setup (sourceGame recorded, no adoption).
function Get-Origin($St){
    $adopt=[string](Prop $St 'adoptionReceipt');$src=[string](Prop $St 'sourceGame')
    if($adopt -and !$src){return 'A'}
    if($src -and !$adopt){return 'B'}
    return $null
}

# ---- state checks ----
# A copy at Alpha1: exactly the Alpha1 file set, LocalSaveIsolation and CoopPack 0.1.0-alpha registered, the Alpha1
# receipt, the Alpha1 launcher environment, and one of the two recognised histories. Returns the state id (A or B).
function Test-FromState($St){
    if([string](Prop $St 'packageId') -cne $alpha1 -or [string](Prop $St.release 'packageId') -cne $alpha1){throw 'This copy is not at ...-Load1-Lobby1-Alpha1.'}
    $sidecars=@($St.release.sidecars)
    $pack=[pscustomobject]@{name=$packName;version=$fromVersion;files=@($packFiles|ForEach-Object{[pscustomobject]@{file=$_;sha256=$alpha1Pack[$_]}})}
    $lsiRow=@($sidecars|Where-Object{[string](Prop $_ 'name') -ceq $lsi.name});$packRow=@($sidecars|Where-Object{[string](Prop $_ 'name') -ceq $packName})
    if($sidecars.Count -ne 2 -or $lsiRow.Count -ne 1 -or $packRow.Count -ne 1){throw 'The registered module list is not the one of an Alpha1 copy (LocalSaveIsolation and CoopPack).'}
    if(!(Same-Sidecar $lsiRow[0] $lsi)){throw 'Installed module LocalSaveIsolation is not the released build (version or file hashes differ).'}
    if(!(Same-Sidecar $packRow[0] $pack)){throw 'Installed CoopPack is not the released Alpha1 build 0.1.0-alpha A05309D4 (version or file hashes differ).'}
    Test-OwnedSet $St $alpha1Files 'Alpha1'
    $owned=@($St.ownedFiles|Where-Object file -eq $launcherFile);$released=@($St.release.launcherFiles|Where-Object file -eq $launcherFile)
    if($owned.Count -ne 1 -or $released.Count -ne 1 -or $owned[0].sha256 -ne $launcherSha -or $released[0].sha256 -ne $launcherSha){throw 'Launch-Coop.ps1 differs from the one this update expects.'}
    if([string](Prop $St 'packUpdate') -notmatch '^[0-9a-f]{32}$'){throw 'The Alpha1 update receipt (packUpdate) is missing.'}
    if($St.PSObject.Properties['alpha2Update']){throw 'This copy carries an Alpha2 receipt (alpha2Update) but is not at the Alpha2 package.'}
    $env=Prop $St.release 'environment'
    if([string](Prop $env 'COOP_SYNC_DISABLE_COLORS') -cne '1' -or [string](Prop $env 'COOP_TWO_PLAYER_COLORS') -cne '1' -or [string](Prop $St.release 'releaseKind') -cne 'ALPHA'){throw 'The recorded Alpha1 release (environment COOP_SYNC_DISABLE_COLORS=1, COOP_TWO_PLAYER_COLORS=1, release kind ALPHA) is not the one this update expects.'}
    $o=Get-Origin $St
    if(!$o){throw 'The history recorded in coop-install.json is neither the owner''s updated copy nor a copy made by the Alpha1 full installer.'}
    return $o
}
function Assert-Mods([string]$Root,[string[]]$Names){
    $seen=@{}
    foreach($item in @(Get-ChildItem -LiteralPath (Child $Root 'coremod/mods') -Force)){
        No-Link $item.FullName
        if(!$item.PSIsContainer -or $Names -cnotcontains $item.Name){throw ('Unexpected or diagnostic mod in the player copy: coremod\mods\'+$item.Name)}
        $seen[$item.Name]=$true
    }
    foreach($n in $Names){if(!$seen.ContainsKey($n)){throw ('Module folder missing: coremod\mods\'+$n)}}
}
function Assert-ModuleFolder([string]$Root,[string]$Name,[string[]]$Files){
    $items=@(Get-ChildItem -LiteralPath (Child $Root ('coremod/mods/'+$Name)) -Force);$names=@($items|ForEach-Object{$_.Name})
    if(@($items|Where-Object{$_.PSIsContainer}).Count -or $items.Count -ne $Files.Count -or @($Files|Where-Object{$names -cnotcontains $_}).Count){throw ('The folder coremod\mods\'+$Name+' does not hold exactly its registered files (an extra, missing or renamed file or folder).')}
}
function Assert-CopyDisk([string]$Root){
    Assert-Mods $Root ([string[]]@('DeadCellsMultiplayerMod',$lsi.name,$packName))
    Assert-ModuleFolder $Root $packName ([string[]]$packFiles)
    Assert-Hash (Child $Root $launcherFile) $launcherSha
}
# The file set after the update: the Alpha1 set with CoopPack.dll and modinfo.json replaced.
function Get-TargetFiles($PackAfter){$t=[ordered]@{};foreach($k in $alpha1Files.Keys){$t[$k]=$alpha1Files[$k]};foreach($f in $changedFiles){$t['coremod/mods/'+$packName+'/'+$f]=$PackAfter[$f]};return $t}
# A copy at the target: exactly the target file set, CoopPack 0.2.0-alpha with the kit's files, the Alpha2 receipt.
function Test-TargetState($St,$PackAfter){
    if([string](Prop $St 'packageId') -cne $target -or [string](Prop $St.release 'packageId') -cne $target){throw 'This copy is not at the Alpha2 package.'}
    $sidecars=@($St.release.sidecars)
    $pack=[pscustomobject]@{name=$packName;version=$packVersion;files=@($packFiles|ForEach-Object{[pscustomobject]@{file=$_;sha256=$PackAfter[$_]}})}
    $lsiRow=@($sidecars|Where-Object{[string](Prop $_ 'name') -ceq $lsi.name});$packRow=@($sidecars|Where-Object{[string](Prop $_ 'name') -ceq $packName})
    if($sidecars.Count -ne 2 -or $lsiRow.Count -ne 1 -or $packRow.Count -ne 1 -or !(Same-Sidecar $lsiRow[0] $lsi) -or !(Same-Sidecar $packRow[0] $pack)){throw 'Installed CoopPack differs from the one this update installs (another Alpha2 build is installed, or the module list was changed). To change the Alpha2 build, run Undo-Coop-Alpha2.cmd from the kit that installed it first, then this update.'}
    Test-OwnedSet $St (Get-TargetFiles $PackAfter) 'Alpha2'
    $owned=@($St.ownedFiles|Where-Object file -eq $launcherFile);$released=@($St.release.launcherFiles|Where-Object file -eq $launcherFile)
    if($owned.Count -ne 1 -or $released.Count -ne 1 -or $owned[0].sha256 -ne $launcherSha -or $released[0].sha256 -ne $launcherSha){throw 'Launch-Coop.ps1 differs from the one this update keeps.'}
    if([string](Prop $St 'alpha2Update') -notmatch '^[0-9a-f]{32}$'){throw 'The Alpha2 update receipt (alpha2Update) is missing.'}
    if([string](Prop $St 'packUpdate') -notmatch '^[0-9a-f]{32}$'){throw 'The Alpha1 update receipt (packUpdate) is missing.'}
    if([string](Prop (Prop $St.release 'environment') 'COOP_SYNC_DISABLE_COLORS') -cne '1'){throw 'The recorded launcher environment lost COOP_SYNC_DISABLE_COLORS=1.'}
}
function Check-FromCopy($St,[string]$Root){Verify-Owned $St $Root;$o=Test-FromState $St;Assert-CopyDisk $Root;Assert-Hash (Child $Root ('coremod/mods/'+$packName+'/CoopPack.dll')) $alpha1Pack['CoopPack.dll'];return $o}
function Check-TargetCopy($St,[string]$Root,$PackAfter){Verify-Owned $St $Root;Test-TargetState $St $PackAfter;Assert-CopyDisk $Root}
function New-Step([string]$Kind,[string]$Path,$Before,$After){[pscustomobject]@{kind=$Kind;path=$Path;before=$Before;after=$After;oldMoved=$false;newMoved=$false;done=$false}}
# The step list of the update (also used to check the record an Undo relies on).
function Get-UpdateSteps($PackAfter,[string]$StateBefore){
    $steps=[Collections.Generic.List[object]]::new()
    foreach($f in $changedFiles){$steps.Add((New-Step 'file' ('coremod/mods/'+$packName+'/'+$f) $alpha1Pack[$f] $PackAfter[$f]))}
    $steps.Add((New-Step 'file' 'coop-install.json' $StateBefore $null))
    return ,$steps
}
# Undo preconditions: the copy is exactly what the recorded Alpha2 update left, and its backup can restore every changed
# file of the Alpha1 state byte for byte. Returns what the Undo transaction needs. Reads only.
function Get-UndoPlan($St,[string]$Root){
    $id=[string](Prop $St 'alpha2Update')
    if($id -notmatch '^[0-9a-f]{32}$'){throw 'The Alpha2 update receipt (alpha2Update) is missing; Undo is not possible.'}
    $txDir=Child $Root ('coop-fix-updates/'+$id);$txPath=Child $txDir 'transaction.json';$prev=Child $txDir 'previous'
    if(!(Test-Path -LiteralPath $txPath -PathType Leaf)){throw ('The backup of the Alpha2 update is missing: coop-fix-updates\'+$id+'. Undo is not possible.')}
    $tx=Read-Json $txPath
    if((Prop $tx 'schema') -ne 1 -or [string](Prop $tx 'id') -cne $id -or [string](Prop $tx 'action') -cne 'Update' -or [string](Prop $tx 'status') -cne 'Committed' -or [string](Prop $tx 'instanceId') -cne $St.instanceId -or [string](Prop $tx 'root') -ne $Root -or [string](Prop $tx 'from') -cne $alpha1 -or [string](Prop $tx 'to') -cne $target -or $stateTitles.Keys -cnotcontains [string](Prop $tx 'fromState')){throw ('The Alpha2 update record coop-fix-updates\'+$id+'\transaction.json is not a committed update of this copy. Undo is not possible.')}
    $steps=@(Prop $tx 'steps')
    $packAfter=@{};foreach($s in $steps){if([string](Prop $s 'kind') -ceq 'file' -and ([string](Prop $s 'path')).StartsWith('coremod/mods/'+$packName+'/',[StringComparison]::Ordinal)){$packAfter[([string](Prop $s 'path')).Substring(('coremod/mods/'+$packName+'/').Length)]=[string](Prop $s 'after')}}
    $packAfter[$packFiles[0]]=$workerSha
    $stateStep=@($steps|Where-Object{[string](Prop $_ 'path') -ceq 'coop-install.json'})
    if($stateStep.Count -ne 1){throw 'The Alpha2 update record is incomplete. Undo is not possible.'}
    $want=Get-UpdateSteps $packAfter ([string](Prop $stateStep[0] 'before'))
    $a=@($steps|ForEach-Object{[string](Prop $_ 'kind')+'|'+[string](Prop $_ 'path')+'|'+[string](Prop $_ 'before')+'|'+$(if([string](Prop $_ 'path') -ceq 'coop-install.json'){''}else{[string](Prop $_ 'after')})})
    $b=@($want|ForEach-Object{$_.kind+'|'+$_.path+'|'+[string]$_.before+'|'+[string]$_.after})
    if(($a -join "`n") -cne ($b -join "`n") -or $packAfter.Count -ne 3){throw 'The Alpha2 update record does not match this update''s steps. Undo is not possible.'}
    if((Sha (Child $Root 'coop-install.json')) -ne [string](Prop $stateStep[0] 'after')){throw 'coop-install.json was changed after the Alpha2 update; Undo would lose that change.'}
    Check-TargetCopy $St $Root $packAfter
    $fromPath=Child $prev 'coop-install.json';Assert-Hash $fromPath ([string](Prop $stateStep[0] 'before'))
    $from=Read-Json $fromPath
    if($from.schema -ne 2 -or $from.status -ne 'Installed' -or $from.instanceId -cne $St.instanceId -or $from.installedRoot -ne $Root -or $from.saveRoot -ne (Join-Path $Root 'save')){throw 'The backed-up coop-install.json does not belong to this copy. Undo is not possible.'}
    $o=Test-FromState $from
    if($o -cne [string](Prop $tx 'fromState')){throw 'The backed-up coop-install.json is not the state the Alpha2 update recorded. Undo is not possible.'}
    # Every changed file of the starting state is in the backup; every other owned file is still in the copy.
    foreach($f in $changedFiles){Assert-Hash (Child $prev ('coremod/mods/'+$packName+'/'+$f)) $alpha1Pack[$f]}
    $current=@{};foreach($row in @($St.ownedFiles)){$current[[string]$row.file]=[string]$row.sha256}
    foreach($row in @($from.ownedFiles)){
        $f=[string]$row.file
        if(@($changedFiles|ForEach-Object{'coremod/mods/'+$packName+'/'+$_}) -ccontains $f){continue}
        if(!($current.ContainsKey($f) -and $current[$f] -eq $row.sha256)){Assert-Hash (Child $Root $f) $row.sha256}
    }
    return [pscustomobject]@{id=$id;txDir=$txDir;prev=$prev;from=$from;fromState=$o;fromSha=[string](Prop $stateStep[0] 'before');packAfter=$packAfter}
}

# ---- the transaction (Update and Undo) ----
# QA only (a qakit below the developer zone, see the header): parks the run at a named point until the test stops it.
function QA-Hold([string]$Where){
    $f=[string]$env:COOP_QA_ALPHA2_HOLD_FILE
    if($f -and ([IO.Path]::GetFullPath($f)).StartsWith($devZone,[StringComparison]::OrdinalIgnoreCase)){[IO.File]::WriteAllText($f,$Where)}
    $until=[DateTime]::UtcNow.AddSeconds(120)
    while([DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 50}
    throw ('QA hold at '+$Where+' was not stopped within 120 s.')
}
# The rollback of the steps done so far, last first: the new file back to incoming\, the old file back from previous\.
# Adds one message per step that could not be moved back to $Errors. Used by the catch (a failure) and by the finally
# (a stopped run); the flags on each step make a second pass skip what is already back.
function Undo-Steps($Done,[string]$Root,[string]$Prev,[string]$In,$Errors){
    for($i=$Done.Count-1;$i -ge 0;$i--){
        $s=$Done[$i]
        try{
            if($qa -and [string]$env:COOP_QA_ALPHA2_ROLLBACK_FAIL -eq '1'){throw 'QA rollback fault injection.'}
            $active=Child $Root $s.path;$old=Child $Prev $s.path;$new=Child $In $s.path
            if($s.newMoved){[IO.File]::Move($active,$new);$s.newMoved=$false}
            if($s.oldMoved){[IO.Directory]::CreateDirectory((Split-Path -Parent $active))|Out-Null;[IO.File]::Move($old,$active);$s.oldMoved=$false}
        }catch{$Errors.Add($_.Exception.Message)}
    }
}
function Run-Transaction($P){
    $tRoot=$P.root
    $tStatePath=Child $tRoot 'coop-install.json';$tLockPath=Child $tRoot 'coop-setup.lock'
    $tBackup=Child $tRoot ('coop-fix-updates/'+$P.id)
    # $tSettled: the copy is in a final state (committed, or rolled back without an error); only then is the lock removed.
    $tLock=$null;$tRecord=$null;$tDone=[Collections.Generic.List[object]]::new();$tRecovery=$false;$tSettled=$false;$tPrev=$null;$tIn=$null
    try{
        $tLock=[IO.File]::Open($tLockPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
        $script:txStarted=$true
        # Under the lock: the game is still closed, the state file is the one checked, and every precondition still holds.
        Assert-Stopped $tRoot;Assert-Hash $tStatePath $P.stateBefore
        if($P.kind -ceq 'Update'){$null=Check-FromCopy $P.state $tRoot}else{$null=Get-UndoPlan $P.state $tRoot}
        $tPreserve=Get-Preserved $tRoot
        [IO.Directory]::CreateDirectory($tBackup)|Out-Null
        $tPrev=Child $tBackup 'previous';$tIn=Child $tBackup 'incoming'
        [IO.Directory]::CreateDirectory($tPrev)|Out-Null;[IO.Directory]::CreateDirectory($tIn)|Out-Null
        [IO.File]::WriteAllText((Child $tBackup 'preservation.json'),$tPreserve,[Text.UTF8Encoding]::new($false))
        foreach($c in $P.copies){$to=Child $tIn $c.rel;[IO.Directory]::CreateDirectory((Split-Path -Parent $to))|Out-Null;[IO.File]::Copy($c.from,$to,$false);Assert-Hash $to $c.sha256}
        $last=$P.steps[$P.steps.Count-1]
        if($null -ne $P.newState){Json (Child $tIn 'coop-install.json') $P.newState;$last.after=Sha (Child $tIn 'coop-install.json')}
        $tRecord=[ordered]@{schema=1;id=$P.id;action=$P.kind;status='Preparing';instanceId=$P.state.instanceId;root=$tRoot;from=$P.from;to=$P.to;fromState=$P.fromState;undoOf=$P.undoOf;packVersionBefore=$P.versionBefore;packVersionAfter=$P.versionAfter;kitUpdaterSha256=(Sha $PSCommandPath);kitManifestSha256=(Sha (Join-Path $kit 'UPDATE.json'));preservationSha256=(Sha (Child $tBackup 'preservation.json'));createdUtc=[DateTime]::UtcNow.ToString('o');progress=0;steps=@($P.steps|ForEach-Object{[ordered]@{kind=$_.kind;path=$_.path;before=$_.before;after=$_.after}})}
        Json (Child $tBackup 'transaction.json') $tRecord
        for($i=0;$i -lt $P.steps.Count;$i++){
            $s=$P.steps[$i]
            if($qa -and [string]$env:COOP_QA_ALPHA2_FAIL_AT -eq [string]($i+1)){throw ('QA fault injection at step '+($i+1)+'.')}
            if($qa -and [string]$env:COOP_QA_ALPHA2_HOLD_AT -eq [string]($i+1)){QA-Hold ('before step '+($i+1))}
            # Journal first: progress names the step about to run, so an interrupted run shows exactly where it stopped.
            $tRecord.progress=$i+1;Json (Child $tBackup 'transaction.json') $tRecord
            $tDone.Add($s)
            if($s.kind -ceq 'file'){
                $active=Child $tRoot $s.path;$old=Child $tPrev $s.path;$new=Child $tIn $s.path
                if($null -ne $s.before){Assert-Hash $active $s.before}elseif(Test-Path -LiteralPath $active){throw ('Destination appeared during the transaction: '+$s.path)}
                if($null -ne $s.after){Assert-Hash $new $s.after}
                if($null -ne $s.before){[IO.Directory]::CreateDirectory((Split-Path -Parent $old))|Out-Null;[IO.File]::Move($active,$old);$s.oldMoved=$true}
                if($qa -and [string]$env:COOP_QA_ALPHA2_FAIL_MID -eq [string]($i+1)){throw ('QA fault injection inside step '+($i+1)+'.')}
                if($qa -and [string]$env:COOP_QA_ALPHA2_HOLD_MID -eq [string]($i+1)){QA-Hold ('inside step '+($i+1))}
                if($null -ne $s.after){[IO.File]::Move($new,$active);$s.newMoved=$true}
            }else{throw 'Unknown step.'}
        }
        if($qa -and [string]$env:COOP_QA_ALPHA2_HOLD_AT -eq [string]($P.steps.Count+1)){QA-Hold 'after the last step'}
        Assert-Stopped $tRoot
        if($P.kind -ceq 'Update'){Check-TargetCopy $P.newState $tRoot $P.packAfter}else{$null=Check-FromCopy $P.fromStateRecord $tRoot}
        Assert-Hash $tStatePath $last.after
        if((Get-Preserved $tRoot) -cne $tPreserve){throw 'Save or config contents changed during the transaction; refusing to commit.'}
        $tRecord.status='Committed';$tRecord.completedUtc=[DateTime]::UtcNow.ToString('o')
        Json (Child $tBackup 'transaction.json') $tRecord
        if($qa -and [string]$env:COOP_QA_ALPHA2_HOLD_COMMITTED -eq '1'){QA-Hold 'after the commit record'}
        $tSettled=$true
    }catch{
        $failure=$_.Exception.Message;$errs=[Collections.Generic.List[string]]::new()
        Undo-Steps $tDone $tRoot $tPrev $tIn $errs
        $rollbackErrors=@($errs);$tRecovery=$rollbackErrors.Count -gt 0
        if($tRecord){$tRecord.status=if($tRecovery){'RecoveryRequired'}else{'RolledBack'};$tRecord.error=$failure;$tRecord.rollbackErrors=$rollbackErrors;try{Json (Child $tBackup 'transaction.json') $tRecord}catch{}}
        $tSettled=!$tRecovery
        if($tRecovery){throw ($P.kind+" failed; keep the lock and backup for recovery: $tBackup. $failure")}
        throw ($P.kind+" failed; previous files restored. $failure")
    }finally{
        if($tLock){
            $tLock.Dispose()
            # Neither committed nor rolled back: the run was stopped (Ctrl+C) inside the try or the catch, where PowerShell
            # runs no catch block. Settle the copy here; the lock goes only when it is settled.
            if(!$tSettled -and !$tRecovery){
                try{
                    $txFile=$(if($tRecord){Child $tBackup 'transaction.json'}else{$null})
                    $onDisk=$(if($txFile -and (Test-Path -LiteralPath $txFile -PathType Leaf)){[string](Prop (Read-Json $txFile) 'status')}else{''})
                    if($onDisk -ceq 'Committed'){$tSettled=$true}
                    else{
                        $errs=[Collections.Generic.List[string]]::new()
                        Undo-Steps $tDone $tRoot $tPrev $tIn $errs
                        $tRecovery=$errs.Count -gt 0
                        if($tRecord){
                            $tRecord.status=if($tRecovery){'RecoveryRequired'}else{'RolledBack'};$tRecord.interrupted=$true
                            if($tRecord.Contains('completedUtc')){$tRecord.Remove('completedUtc')}
                            $tRecord.error='Interrupted (Ctrl+C or a stopped window) before the '+$P.kind+' finished.';$tRecord.rollbackErrors=@($errs)
                            try{Json $txFile $tRecord}catch{}
                        }
                        $tSettled=!$tRecovery
                        try{Write-Host $(if($tRecovery){$P.kind+' interrupted; keep the lock and backup for recovery: '+$tBackup+'.'}else{$P.kind+' interrupted; previous files restored. Nothing else was changed.'}) -ForegroundColor Red}catch{}
                    }
                }catch{$tSettled=$false}
            }
            if($tSettled){[IO.File]::Delete($tLockPath)}
        }
    }
    return $tBackup
}
function Short([string]$Sha){if($Sha){$Sha.Substring(0,8)}else{'-'}}
function Show-Plan([string]$Title,[string[]]$Lines){Write-Host $Title;foreach($l in $Lines){Write-Host ('  '+$l)}}

try {
    No-Link $kit
    # ---- the kit: manifest, payload, accepted states ----
    $manifest=Read-Json (Join-Path $kit 'UPDATE.json')
    if((Prop $manifest 'schema') -ne 1 -or (Prop $manifest 'releaseReady') -ne $true -or [string](Prop $manifest 'toPackage') -cne $target -or (@(Prop $manifest 'fromPackages') -join '|') -cne $alpha1){throw 'Unfinalized or incompatible update.'}
    # Explicit owner-requested alpha delivery: incomplete validation, never a fully gated release.
    # This admission exception is bound to one reviewed payload and one recorded user request.
    $ownerRequestedAlpha=([string](Prop $manifest 'deliveryMode') -ceq 'maintainer-alpha-1')
    if($ownerRequestedAlpha){
        $ownerApproval=Prop $manifest 'authorization';$ownerBuild=Prop $manifest 'build';$ownerValidation=Prop $manifest 'validationStatus'
        if((Prop $manifest 'gated') -ne $false -or (Prop $manifest 'placeholderPayload') -ne $false -or (Prop $ownerApproval 'approved') -ne $true -or [string](Prop $ownerApproval 'kind') -cne 'direct-user-request' -or [string](Prop $ownerApproval 'requestSha256') -cne '094144695E2E4742AADCE9BF6AA6E28C43B67EA909BEB568A00FB7EB2AC6896E' -or [string](Prop $ownerValidation 'managed') -cne 'PASS' -or [string](Prop $ownerValidation 'native') -cne 'INCOMPLETE' -or (Prop $ownerValidation 'fullNativeValidated') -ne $false){throw 'Invalid owner-requested alpha delivery metadata.'}
        if([string](Prop $ownerBuild 'dllSha256') -cne '3D0A58807946E01A8E68683905EF96A7757185470476AFA5550FB09F6F58C2D1' -or [string](Prop $ownerBuild 'manifestSha256') -cne 'C3DD0D0146A0875028AC08B48386ED7F5B0A690E13419A29BAEE010087B4CA75' -or [string](Prop $ownerBuild 'gatesSha256') -cne '63C65E0EC86799BC653F117E93261294903D6DFD2F6027E1388BC70274D40390' -or (Prop $ownerBuild 'sourceVerified') -ne $true){throw 'Owner-requested alpha requires the exact reviewed build.'}
        Assert-Hash (Child $kit 'RELEASE-AUTHORIZATION.json') 'ACB577DA01A494840A9DC01955FCCE74BD717F0EDC44DB48A5E4D0E73F69359F'
        Assert-Hash (Child $kit 'Source/build-manifest.json') 'C3DD0D0146A0875028AC08B48386ED7F5B0A690E13419A29BAEE010087B4CA75'
        Assert-Hash (Child $kit 'payload/coremod/mods/CoopPack/CoopPack.dll') '3D0A58807946E01A8E68683905EF96A7757185470476AFA5550FB09F6F58C2D1'
        Assert-Hash (Child $kit 'payload/coremod/mods/CoopPack/modinfo.json') 'B1316D1AB0F238C7F8FD3BA9764EE76FB20FE10786F0685C70CFE9D54FC4FA98'
        Write-Host 'Twin Cells Alpha2 prerelease (0.2.0-alpha).' -ForegroundColor Yellow
    }
    $fixtureKit=(!$ownerRequestedAlpha -and ((Prop $manifest 'gated') -ne $true -or (Prop $manifest 'placeholderPayload') -ne $false))
    if($fixtureKit -and !$inDevZone){throw 'This is a fixture-only test kit (no release gates). It must not be used on a player copy.'}
    if($fixtureKit){Write-Host 'FIXTURE-ONLY TEST KIT: not a release. It updates only test copies inside its developer folder, never a player copy.' -ForegroundColor Yellow}
    $mv=Prop $manifest 'moduleVersions'
    if($null -eq $mv -or @($mv.PSObject.Properties).Count -ne 1 -or [string](Prop $mv $packName) -cne $packVersion -or [string](Prop $manifest 'fromModuleVersion') -cne $fromVersion){throw 'Unexpected module version in the kit.'}
    $rows=@(Prop $manifest 'files')
    if($rows.Count -ne 2){throw 'Expected exactly two kit files (CoopPack.dll and modinfo.json).'}
    $seen=@{};$packAfter=@{};$packAfter[$packFiles[0]]=$workerSha
    foreach($row in $rows){
        $f=[string](Prop $row 'file');$after=[string](Prop $row 'after');$leaf=$f.Substring([Math]::Min($f.Length,('coremod/mods/'+$packName+'/').Length))
        if(!$f.StartsWith('coremod/mods/'+$packName+'/',[StringComparison]::Ordinal) -or $changedFiles -cnotcontains $leaf -or $seen.ContainsKey($f)){throw 'Unexpected update target.'};$seen[$f]=$true
        if([string](Prop $row 'before') -cne $alpha1Pack[$leaf]){throw 'The kit replaces another file than the Alpha1 one.'}
        if($after -cnotmatch '^[0-9A-F]{64}$' -or $after -ceq $alpha1Pack[$leaf]){throw 'The kit file is not a new build.'}
        $packAfter[$leaf]=$after
        Assert-Hash (Child $kit ('payload/'+$f)) $after
    }
    $keep=@(Prop $manifest 'unchanged')
    if(((@($keep|ForEach-Object{[string](Prop $_ 'file')+'|'+[string](Prop $_ 'sha256')})) -join ';') -cne ('coremod/mods/'+$packName+'/'+$packFiles[0]+'|'+$workerSha+';'+$launcherFile+'|'+$launcherSha)){throw 'The kit list of unchanged files is not CoopSteamInviteWorker.dll 3D574B49 and Launch-Coop.ps1 8F1A86F7.'}
    if(((@(Prop $manifest 'capabilities')) -join ',') -cne ($capabilities -join ',')){throw 'The kit capability tokens differ from this updater.'}
    # The CoopPack build of this kit, as the first 8 hex digits of CoopPack.dll (tells two Alpha2 kits apart).
    $packTag=([string]$packAfter['CoopPack.dll']).Substring(0,8)
    $meta=Read-Json (Child $kit ('payload/coremod/mods/'+$packName+'/modinfo.json'))
    if([string](Prop $meta 'name') -cne $packName -or [string](Prop $meta 'main') -cne $packName -or [string](Prop $meta 'version') -cne $packVersion -or [string](Prop $meta 'type') -cne 'mod' -or [string](Prop $meta 'dccmversion') -cne '35.13.5' -or (@(Prop $meta 'assemblies') -join '|') -cne 'CoopSteamInviteWorker.dll|CoopPack.dll' -or (@(Prop $meta 'dependencies') -join '|') -cne 'DeadCellsMultiplayerMod' -or @($meta.PSObject.Properties).Count -ne 7){throw 'Payload module identity/version mismatch.'}
    if(((Sort-Ordinal @(Prop $manifest 'fromStateTable')) -join "`n") -cne ((Table-Lines) -join "`n")){throw 'The kit list of accepted starting states differs from this updater.'}

    # ---- the copy ----
    if(!$GameRoot){$GameRoot=Read-Host 'Path to the existing DeadCells-Coop folder (not the Steam installation)'}
    $GameRoot=([string]$GameRoot).Trim().Trim('"').Trim()
    if([string]::IsNullOrWhiteSpace($GameRoot) -or $GameRoot -notmatch '^[A-Za-z]:[\\/]'){throw 'Enter the full path to the existing isolated co-op folder.'}
    $root=[IO.Path]::GetFullPath($GameRoot).TrimEnd('\');No-Link $root
    if($root -match '(?i)[\\/]steamapps([\\/]|$)|[\\/]userdata([\\/]|$)' -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')){throw 'Only an existing isolated co-op copy can be updated.'}
    if($kit.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($kit+'\',[StringComparison]::OrdinalIgnoreCase) -or $kit -eq $root){throw 'Extract the update outside the game folder.'}
    # A fixture kit or a QA kit works only on test copies inside the developer zone. Where the kit sits is not enough: a
    # test kit there must never update a player copy elsewhere.
    if(($fixtureKit -or $qa) -and !$root.StartsWith($devZone,[StringComparison]::OrdinalIgnoreCase)){
        if($fixtureKit){throw 'This is a fixture-only test kit (no release gates). It works only on test copies inside its developer folder and must not be used on a player copy.'}
        throw 'This is a QA test kit (folder qakit, fault hooks). It works only on test copies inside its developer folder and must not be used on a player copy.'
    }
    $statePath=Child $root 'coop-install.json';$lockPath=Child $root 'coop-setup.lock'
    if(Test-Path -LiteralPath $lockPath){throw 'An installer is active or interrupted. Preserve its backup and resolve that transaction first.'}
    if(!(Test-Path -LiteralPath (Child $root 'local-saves-only.flag') -PathType Leaf)){throw 'Isolated-save marker missing.'}
    if(!(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'coop-install.json not found: this is not an installed DeadCells-Coop folder.'}
    $state=Read-Json $statePath
    if($state.schema -ne 2 -or $state.status -ne 'Installed' -or $state.instanceId -notmatch '^[0-9a-f]{32}$' -or $state.installedRoot -ne $root -or $state.saveRoot -ne (Join-Path $root 'save') -or $state.sourceGame -eq $root){throw 'Installation ownership or save boundary mismatch.'}
    $pkg=[string](Prop $state 'packageId');$release=Prop $state 'release'
    $isTarget=$pkg -ceq $target
    if((!$isTarget -and $pkg -cne $alpha1) -or $null -eq $release -or [string](Prop $release 'packageId') -cne $pkg -or (Prop $release 'releaseReady') -ne $true){throw 'This update requires an Alpha1 copy (...-Load1-Lobby1-Alpha1: the owner''s updated copy or a copy made by the Alpha1 full installer).'}
    Assert-Hash (Child $root $baseDll) $baseSha;Assert-Hash (Child $root ('coremod/mods/'+$lsi.name+'/'+$lsi.files[0].file)) $lsi.files[0].sha256
    Assert-Stopped $root
    $stateBefore=Sha $statePath
    $dll='coremod\mods\CoopPack\CoopPack.dll';$mi='coremod\mods\CoopPack\modinfo.json'
    $untouched='save\, coremod\config\, coremod\data\ (the CoopPack data folder included), mods\DeadCellsMultiplayerMod\config.json, the base mod, LocalSaveIsolation, coremod\core\'

    if($isTarget){
        if($Action -ne 'Undo'){
            Check-TargetCopy $state $root $packAfter
            $undo=$true;$why=''
            try{$u=Get-UndoPlan $state $root}catch{$undo=$false;$why=$_.Exception.Message;$u=$null}
            $detected='Alpha2 (...-Load1-Lobby1-Alpha1-Alpha2), CoopPack '+$packVersion+' '+$packTag+$(if($u){', installed by this update from state '+$u.fromState}else{''})
            if($Action -eq 'Check'){Show-Plan ('Twin Cells Alpha2 update (CoopPack '+$packVersion+', build '+$packTag+'): check only, nothing was written.') @(('Copy: '+$root),('Detected state: '+$detected),'Planned changes: none (already updated).',$(if($undo){'Undo is available: Undo-Coop-Alpha2.cmd returns this copy to its Alpha1 state.'}else{'Undo is not available: '+$why}))}
            [pscustomobject]@{status='AlreadyUpdated';packageId=$state.packageId;detectedState=$detected;coopPack=$packTag;plannedChanges=@();undoAvailable=$undo;undoBlockedReason=$why;changed=$false;nativeExecuted=$false};return
        }
        $u=Get-UndoPlan $state $root
        $steps=[Collections.Generic.List[object]]::new();$copies=[Collections.Generic.List[object]]::new()
        foreach($f in $changedFiles){$rel='coremod/mods/'+$packName+'/'+$f;$steps.Add((New-Step 'file' $rel $u.packAfter[$f] $alpha1Pack[$f]));$copies.Add([pscustomobject]@{rel=$rel;from=(Child $u.prev $rel);sha256=$alpha1Pack[$f]})}
        $steps.Add((New-Step 'file' 'coop-install.json' $stateBefore $u.fromSha));$copies.Add([pscustomobject]@{rel='coop-install.json';from=(Child $u.prev 'coop-install.json');sha256=$u.fromSha})
        $planned=@(($dll+': '+$packTag+' -> '+(Short $alpha1Pack['CoopPack.dll'])+' (from the backup)'),($mi+': '+(Short $u.packAfter['modinfo.json'])+' -> '+(Short $alpha1Pack['modinfo.json'])+' (version '+$packVersion+' -> '+$fromVersion+')'),('coop-install.json: restored byte for byte from coop-fix-updates\'+$u.id+'\previous (package ...-Alpha1, state '+$u.fromState+')'),'Backup of the Alpha2 files: coop-fix-updates\<new id>\previous',('Not touched: '+$untouched))
        if($CheckOnly){
            Show-Plan ('Twin Cells Alpha2 undo (CoopPack '+$packVersion+', build '+$packTag+'): check only, nothing was written.') (@(('Copy: '+$root),('Detected state: Alpha2, installed by the Alpha2 update '+$u.id+' from state '+$u.fromState+' ('+$stateTitles[$u.fromState]+')'),'Planned undo:')+@($planned|ForEach-Object{'  '+$_}))
            [pscustomobject]@{status='UndoPreflightPassed';packageId=$state.packageId;detectedState=('Alpha2 from state '+$u.fromState);restores=('state '+$u.fromState);plannedChanges=$planned;changed=$false;nativeExecuted=$false};return
        }
        $plan=[pscustomobject]@{kind='Undo';id=[guid]::NewGuid().ToString('N');root=$root;state=$state;stateBefore=$stateBefore;from=$target;to=$alpha1;fromState=$u.fromState;undoOf=$u.id;versionBefore=$packVersion;versionAfter=$fromVersion;steps=$steps;copies=$copies;newState=$null;fromStateRecord=$u.from;packAfter=$u.packAfter}
        $backup=Run-Transaction $plan
        [pscustomobject]@{status='UndoCompleted';packageId=$alpha1;restored=('state '+$u.fromState+': '+$stateTitles[$u.fromState]);coopPack=(Short $alpha1Pack['CoopPack.dll']);usedBackup=$u.txDir;backup=$backup;savesPreserved=$true;originalGameChanged=$false;nativeExecuted=$false}
        return
    }
    if($Action -eq 'Undo'){throw 'Undo is only possible on a copy at ...-Alpha1-Alpha2 that this update installed. This copy is at ...-Alpha1.'}
    $origin=Check-FromCopy $state $root
    $detected='Alpha1 (...-Load1-Lobby1-Alpha1), CoopPack '+$fromVersion+' '+(Short $alpha1Pack['CoopPack.dll'])+', state '+$origin+': '+$stateTitles[$origin]
    $planned=@(($dll+': '+(Short $alpha1Pack['CoopPack.dll'])+' -> '+$packTag+' (CoopPack '+$fromVersion+' -> '+$packVersion+')'),($mi+': '+(Short $alpha1Pack['modinfo.json'])+' -> '+(Short $packAfter['modinfo.json'])+' (version '+$fromVersion+' -> '+$packVersion+')'),('coop-install.json: package ...-Alpha1 -> ...-Alpha1-Alpha2, CoopPack '+$fromVersion+' -> '+$packVersion+', capabilities '+($capabilities -join ' ')+', release validation text, receipt alpha2Update (packUpdate and earlier receipts kept)'),'Backup of the replaced files: coop-fix-updates\<new id>\previous (CoopPack.dll, modinfo.json, coop-install.json)',('Unchanged: coremod\mods\CoopPack\CoopSteamInviteWorker.dll '+(Short $workerSha)+', Launch-Coop.ps1 '+(Short $launcherSha)+' (COOP_SYNC_DISABLE_COLORS=1 stays), Start Dead Cells Coop.cmd'),('Not touched: '+$untouched))
    if($Action -eq 'Check'){
        Show-Plan ('Twin Cells Alpha2 update (CoopPack '+$packVersion+', build '+$packTag+'): check only, nothing was written.') (@(('Copy: '+$root),('Detected state: '+$detected),'Planned changes:')+@($planned|ForEach-Object{'  '+$_}))
        [pscustomobject]@{status='PreflightPassed';packageId=$state.packageId;detectedState=$detected;fromState=$origin;to=$target;coopPack=$packTag;plannedChanges=$planned;changed=$false;nativeExecuted=$false};return
    }
    # The new coop-install.json: only the package identity, the CoopPack registration (version and the two hashes), the
    # capability tokens, the release text and the receipt change. Earlier receipts stay as history.
    $id=[guid]::NewGuid().ToString('N')
    $after=$state|ConvertTo-Json -Depth 40|ConvertFrom-Json
    $after.packageId=$target;$after.release.packageId=$target
    foreach($sc in @($after.release.sidecars|Where-Object{$_.name -ceq $packName})){$sc.version=$packVersion;foreach($x in @($sc.files)){$x.sha256=$packAfter[[string]$x.file]}}
    foreach($row in @($after.ownedFiles)){$f=[string]$row.file;if($f.StartsWith('coremod/mods/'+$packName+'/',[StringComparison]::Ordinal)){$row.sha256=$packAfter[$f.Substring(('coremod/mods/'+$packName+'/').Length)]}}
    Set-Prop $after.release 'capabilities' @($capabilities)
    Set-Prop $after.release 'validation' ([string](Prop $manifest 'validation'))
    Set-Prop $after.release 'twoPcSteamVerified' $false
    Set-Prop $after 'alpha2Update' $id
    $copies=[Collections.Generic.List[object]]::new()
    foreach($f in $changedFiles){$rel='coremod/mods/'+$packName+'/'+$f;$copies.Add([pscustomobject]@{rel=$rel;from=(Child $kit ('payload/'+$rel));sha256=$packAfter[$f]})}
    $plan=[pscustomobject]@{kind='Update';id=$id;root=$root;state=$state;stateBefore=$stateBefore;from=$alpha1;to=$target;fromState=$origin;undoOf=$null;versionBefore=$fromVersion;versionAfter=$packVersion;steps=(Get-UpdateSteps $packAfter $stateBefore);copies=$copies;newState=$after;fromStateRecord=$null;packAfter=$packAfter}
    $backup=Run-Transaction $plan
    [pscustomobject]@{status='UpdateCompleted';packageId=$target;fromState=$origin;from=$detected;coopPack=$packTag;moduleVersion=$packVersion;filesReplaced=(@($changedFiles|ForEach-Object{'coremod\mods\CoopPack\'+$_}) -join ', ');backup=$backup;savesPreserved=$true;originalGameChanged=$false;nativeExecuted=$false}
}catch{
    $message=$_.Exception.Message
    if(!$script:txStarted -and $message -notlike '*Nothing was changed*'){$message=$message+' Nothing was changed.'}
    Write-Host ($(if($Action -eq 'Undo'){'Undo stopped: '}else{'Update stopped: '})+$message) -ForegroundColor Red
    throw $message
}
