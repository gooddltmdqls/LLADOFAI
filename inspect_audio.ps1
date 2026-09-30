$managed = 'C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed'
[AppDomain]::CurrentDomain.add_AssemblyResolve({ param($sender,$args) $name=([System.Reflection.AssemblyName]::new($args.Name)).Name+'.dll'; $path=Join-Path $managed $name; if(Test-Path $path){return [System.Reflection.Assembly]::LoadFrom($path)}; return $null })
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
$codes = @{}
[System.Reflection.Emit.OpCodes].GetFields() | ForEach-Object { $op=$_.GetValue($null); $codes[[int][uint16]$op.Value]=$op }
$targets = 'UnityEngine.AudioSource','UnityEngine.AudioSettings','UnityEngine.AudioListener','UnityEngine.AudioClip'
foreach($type in $asm.GetTypes()) {
 foreach($method in $type.GetMethods([System.Reflection.BindingFlags]'DeclaredOnly,Instance,Static,Public,NonPublic')) {
  $body=$method.GetMethodBody(); if($null -eq $body){continue}; $il=$body.GetILAsByteArray(); $i=0
  while($i -lt $il.Length){$v=[int]$il[$i++]; if($v -eq 254){$v=65280+[int]$il[$i++]}; $op=$codes[$v]; if($null -eq $op){break}; $kind=$op.OperandType.ToString(); $n=0
   switch($kind){ 'InlineNone' {$n=0} 'ShortInlineBrTarget' {$n=1} 'ShortInlineI' {$n=1} 'ShortInlineVar' {$n=1} 'InlineVar' {$n=2} 'InlineI' {$n=4} 'InlineBrTarget' {$n=4} 'InlineField' {$n=4} 'InlineMethod' {$n=4} 'InlineSig' {$n=4} 'InlineString' {$n=4} 'InlineTok' {$n=4} 'InlineType' {$n=4} 'ShortInlineR' {$n=4} 'InlineR' {$n=8} 'InlineI8' {$n=8} 'InlineSwitch' {$n=4+4*[BitConverter]::ToInt32($il,$i)} }
   if($kind -eq 'InlineMethod') {try{$token=[BitConverter]::ToInt32($il,$i);$called=$asm.ManifestModule.ResolveMethod($token);if($targets -contains $called.DeclaringType.FullName){"$($type.FullName).$($method.Name) -> $($called.DeclaringType.Name).$($called.Name)"}}catch{}}
   $i+=$n
  }
 }
}
