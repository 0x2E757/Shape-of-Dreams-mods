# Runs AreMyGemsCompatible's rules outside the game, over every essence and memory at once.
#
# The mod decides two things, and both are cheap to get subtly wrong on one essence out of ninety-
# five. This is how they were settled and how a change to either is re-measured:
#
#   * what an essence waits for - the same reflection and the same
#     PatchProcessor.ReadMethodBody scan of OnEquipSkill and OnEquipGem that GemTriggers does;
#   * what a memory does - the same regexes MemoryFacts applies to RawData\en-US\memories.json,
#     and for an identity memory, which is passive, the same code reading PassiveMemory does.
#
# It is the mod's own logic re-stated rather than the mod itself, which is the cost of running it
# without Unity around. **When a rule changes in one, change it in the other**; a disagreement
# between this and the game is a bug in whichever was edited last, and the tables below are what
# makes it visible.
#
# Two things it cannot see, both prefab data rather than code. enableStatBonus: Gem_E_Might and
# Gem_E_Apathy appear warnable here and are silenced in game. TriggerConfig: which memories are
# passive, and which status effect a passive applies, are guessed here from the Identity location
# and the Se_D_/St_D_ naming. Both gaps are expected.
#
# A third, for the element rule: a memory's elements come from its description and tags alone
# here. The game adds the constants in its code and the elements authored into its DamageInstance
# prefabs; the in-game audit found those add nothing the dump lacks except St_C_Sneeze's rare
# Light beam, so Sneeze is the one memory where this and the game can disagree. Siblings, statuses
# and the room, which can change an element, are not modelled here either - a pairing is judged on
# its own, as the in-game pair count does.
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Shape of Dreams",

    # Prints every warned (essence, memory) pair rather than a count per essence.
    [switch]$Pairs
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $GameDir "Shape of Dreams_Data\Managed"
if (-not (Test-Path $managed)) { throw "no game assemblies at $managed" }

# The Dew assemblies reference a great many Unity ones that are not on the probing path, so each
# is resolved out of the same folder by hand.
#
# The display name is split rather than handed to AssemblyName, and the path is tested with a
# bare .NET call rather than Join-Path and Test-Path: a resolve handler runs during PowerShell's
# own binding, and anything in it that needs a cmdlet or a new type can ask for another assembly
# and re-enter this handler until the call stack is gone. That shows up as a "call depth overflow"
# on the way out of the script, long after the useful output has been printed.
$managedDir = $managed
$onResolve = [System.ResolveEventHandler]{
    param($sender, $e)
    $name = $e.Name.Split(',')[0]
    $path = [System.IO.Path]::Combine($managedDir, "$name.dll")
    if ([System.IO.File]::Exists($path)) { return [System.Reflection.Assembly]::LoadFrom($path) }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)

$harmony = [System.Reflection.Assembly]::LoadFrom((Join-Path $managed "0Harmony.dll"))
$assemblies = @("Dew.Core", "Dew.Contents") | ForEach-Object {
    [System.Reflection.Assembly]::LoadFrom((Join-Path $managed "$_.dll"))
}

# Dew.Contents does not fully load outside the game; ReflectionTypeLoadException still carries
# every type that did, which is all of the essences.
function Get-SafeTypes($asm) {
    try { return $asm.GetTypes() }
    catch [System.Reflection.ReflectionTypeLoadException] { return $_.Exception.Types | Where-Object { $_ -ne $null } }
}
$types = @(); foreach ($a in $assemblies) { $types += Get-SafeTypes $a }
$gemBase = $types | Where-Object { $_.FullName -eq 'Gem' } | Select-Object -First 1
if (-not $gemBase) { throw "Gem not found in the loaded assemblies" }

$readBody = $harmony.GetType('HarmonyLib.PatchProcessor').GetMethod(
    'ReadMethodBody', [type[]]@([System.Reflection.MethodBase]))

# --- GemTriggers, restated -------------------------------------------------------------------

$slotScoped = @{
    'dealtDamageProcessor' = 'Damage'; 'ActorEvent_OnDealDamage' = 'Damage'; 'TrackKills' = 'Damage'
    'dealtHealProcessor'   = 'Heal';   'ActorEvent_OnDoHeal'     = 'Heal'
    'dealtShieldProcessor' = 'Shield'; 'ActorEvent_OnGiveShield' = 'Shield'
}
# Cast events, and the cooldown and charges a SkillBonus is made of: live in every memory that is
# ever cast, and in no memory that is not. An identity memory is passive and never cast.
$castOnSkill = @('TriggerEvent_', 'AddSkillBonus', 'SetCharge', 'LockCooldown', 'mainConfigOriginalCharge')
$aliveOnSkill = @('configs', 'abilityIndex', 'specialOverlayColor', 'ClientTriggerEvent_')
$aliveOnHero = @('EntityEvent_', 'ActorEvent_', 'ClientHeroEvent_', 'ClientEntityEvent_',
                 'takenDamageProcessor', 'AddStatBonus', 'CreateStatusEffect', 'CreateBasicEffect',
                 'TrackKills', 'get_Status', 'get_Ability')

# An essence supplies a capability to its memory only if it creates something parented under the
# memory's own cast, and then only whatever that something actually does. Both halves of the first
# test are needed - touching EventInfoCast.instance without creating through it is what
# Gem_E_Overload does, and it supplies nothing.
$withSource = @('CreateAbilityInstanceWithSource', 'CreateStatusEffectWithSource', 'CreateBasicEffectWithSource')
$createAny = @('CreateAbilityInstance', 'CreateStatusEffect', 'CreateBasicEffect', 'CreateEntity')
$castInstance = 'EventInfoCast.instance'

# Where the walk up a created type's bases stops: these declare DealDamage, DoHeal and GiveShield
# rather than calling them.
$baseRoots = @('Actor', 'AbilityInstance', 'StatusEffect', 'BasicEffect', 'Entity', 'Gem',
               'SkillTrigger', 'AbilityTrigger')

$declared = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor
            [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::DeclaredOnly

# A type and every nested class underneath it, all the way down. One level is not enough: an
# essence with a local iterator gets a display class nested under it and the iterator's real body
# nested under *that*, so a single level sees only the constructor.
function Get-WithNested($type) {
    $out = New-Object System.Collections.ArrayList
    [void]$out.Add($type)
    $flags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    foreach ($n in $type.GetNestedTypes($flags)) {
        foreach ($d in (Get-WithNested $n)) { [void]$out.Add($d) }
    }
    return $out
}

# A type, everything nested in it, and the same for each base class down to but not including the
# roots. The bases are where the answer usually is: Ai_E_Aftershock_Damage declares nothing but an
# OnHit, and it is the abstract DamageInstance two levels up that ends in dmg.Dispatch(...).
function Get-WithBases($type) {
    $out = New-Object System.Collections.ArrayList
    $cur = $type
    while ($null -ne $cur -and $baseRoots -notcontains $cur.Name) {
        foreach ($d in (Get-WithNested $cur)) { [void]$out.Add($d) }
        $cur = $cur.BaseType
    }
    return $out
}

# The resolved operands of a method body, without the name flattening. A body that cannot be read
# is counted, because for a passive memory "found nothing" is a verdict and "could not look" is not.
$script:unreadable = 0
function Get-Operands($method) {
    $out = New-Object System.Collections.ArrayList
    try { $body = $readBody.Invoke($null, @([System.Reflection.MethodBase]$method)) } catch { $script:unreadable++; return $out }
    foreach ($pair in $body) { if ($null -ne $pair.Value) { [void]$out.Add($pair.Value) } }
    return $out
}

# What a set of created types ends up doing, following each up its bases and onward through
# whatever it creates in turn.
function Get-Capabilities($rootTypes) {
    $caps = New-Object System.Collections.Generic.HashSet[string]
    $seen = New-Object System.Collections.Generic.HashSet[string]
    $frontier = @($rootTypes)
    for ($d = 0; $d -lt 3 -and $frontier.Count -gt 0; $d++) {
        $next = New-Object System.Collections.ArrayList
        foreach ($t in $frontier) {
            if ($null -eq $t -or -not $seen.Add($t.FullName)) { continue }
            foreach ($s in (Get-WithBases $t)) {
                foreach ($m in $s.GetMethods($declared)) {
                    foreach ($o in (Get-Operands $m)) {
                        if ($o -isnot [System.Reflection.MethodBase]) { continue }
                        $dt = if ($null -ne $o.DeclaringType) { $o.DeclaringType.Name } else { '' }
                        switch ($o.Name) {
                            'DealDamage'       { [void]$caps.Add('Damage') }
                            'DoBasicAttackHit' { [void]$caps.Add('Damage') }
                            'DoHeal'           { [void]$caps.Add('Heal') }
                            'GiveShield'       { [void]$caps.Add('Shield') }
                            'DoShield'         { [void]$caps.Add('Shield') }
                            'Dispatch'   {
                                if ($dt -eq 'DamageData') { [void]$caps.Add('Damage') }
                                elseif ($dt -eq 'HealData') { [void]$caps.Add('Heal') }
                            }
                        }
                        foreach ($w in $createAny) {
                            if ($o.Name.StartsWith($w, 'Ordinal') -and $o.IsGenericMethod) {
                                foreach ($ga in $o.GetGenericArguments()) { [void]$next.Add($ga) }
                            }
                        }
                    }
                }
            }
        }
        $frontier = @($next)
    }
    # The comma keeps an empty set a set: PowerShell would otherwise unroll it into $null.
    return ,$caps
}

# Every member a method touches, following the essence's own methods one level down so that an
# override calling a private helper does not read as empty. Fields are recorded twice, bare and
# qualified, because one question needs to know whose field it is.
function Get-MemberNames($method, $follow) {
    $names = New-Object System.Collections.ArrayList
    try { $body = $readBody.Invoke($null, @([System.Reflection.MethodBase]$method)) }
    catch { [void]$names.Add('<<unreadable>>'); return $names }
    foreach ($pair in $body) {
        $operand = $pair.Value
        if ($operand -is [System.Reflection.FieldInfo]) {
            [void]$names.Add($operand.Name)
            if ($null -ne $operand.DeclaringType) { [void]$names.Add($operand.DeclaringType.Name + '.' + $operand.Name) }
            continue
        }
        if ($operand -isnot [System.Reflection.MethodBase]) { continue }
        [void]$names.Add($operand.Name)
        if (-not $follow) { continue }
        if ($null -eq $operand.DeclaringType) { continue }
        if (-not $gemBase.IsAssignableFrom($operand.DeclaringType)) { continue }
        if ($operand.DeclaringType -eq $gemBase -or $operand -eq $method) { continue }
        foreach ($n in (Get-MemberNames $operand $false)) { [void]$names.Add($n) }
    }
    return $names
}

# ElementGates, restated: the essences whose whole effect in the slot waits for one element.
$elementGates = @{
    'Gem_R_Frost'  = 'Cold'
    'Gem_E_Apathy' = 'Cold'
    'Gem_E_Fever'  = 'Fire'
    'Gem_R_Lava'   = 'Fire'
}

$essences = foreach ($gem in ($types | Where-Object { $_ -ne $gemBase -and $gemBase.IsAssignableFrom($_) } | Sort-Object FullName)) {
    $needs = New-Object System.Collections.Generic.HashSet[string]
    $alwaysLive = $false
    $t = $gem
    while ($null -ne $t -and $t -ne $gemBase) {
        foreach ($m in $t.GetMethods($declared)) {
            switch ($m.Name) {
                'OnDealDamage' { [void]$needs.Add('Damage') }
                'OnDoHeal'     { [void]$needs.Add('Heal') }
                'OnCastComplete'              { [void]$needs.Add('Cast') }
                'OnCastCompleteBeforePrepare' { [void]$needs.Add('Cast') }
                'OnEquipSkill' {
                    $recognised = $false
                    foreach ($n in (Get-MemberNames $m $true)) {
                        if ($slotScoped.ContainsKey($n)) { [void]$needs.Add($slotScoped[$n]); $recognised = $true; continue }
                        foreach ($p in $castOnSkill) { if ($n.StartsWith($p, 'Ordinal')) { [void]$needs.Add('Cast'); $recognised = $true; break } }
                        foreach ($p in $aliveOnSkill) { if ($n.StartsWith($p, 'Ordinal')) { $alwaysLive = $true; $recognised = $true; break } }
                    }
                    # Unknown is not the same as dead.
                    if (-not $recognised) { $alwaysLive = $true }
                }
                'OnEquipGem' {
                    foreach ($n in (Get-MemberNames $m $true)) {
                        foreach ($p in $aliveOnHero) { if ($n.StartsWith($p, 'Ordinal')) { $alwaysLive = $true; break } }
                    }
                }
            }
        }
        $t = $t.BaseType
    }

    # What this essence hands to the memory it sits in: the types it creates under the memory's
    # own cast, and then whatever those types actually do.
    $touchesCast = $false
    $created = New-Object System.Collections.ArrayList
    $t = $gem
    while ($null -ne $t -and $t -ne $gemBase) {
        foreach ($s in (Get-WithNested $t)) {
            foreach ($m in $s.GetMethods($declared)) {
                foreach ($o in (Get-Operands $m)) {
                    if ($o -is [System.Reflection.FieldInfo]) {
                        if ($null -ne $o.DeclaringType -and ($o.DeclaringType.Name + '.' + $o.Name) -eq $castInstance) { $touchesCast = $true }
                    } elseif ($o -is [System.Reflection.MethodBase]) {
                        foreach ($w in $withSource) {
                            if ($o.Name.StartsWith($w, 'Ordinal') -and $o.IsGenericMethod) {
                                foreach ($ga in $o.GetGenericArguments()) { [void]$created.Add($ga) }
                            }
                        }
                    }
                }
            }
        }
        $t = $t.BaseType
    }
    $supplies = if ($touchesCast -and $created.Count -gt 0) { ((Get-Capabilities $created) | Sort-Object) -join '|' } else { '' }

    # ElementGates: everything these do in the slot sits behind one element, so nothing read above
    # makes them live anywhere else - Gem_R_Frost's empty stat holder on the hero included.
    $gate = $elementGates[$gem.Name]
    if ($gate) { $alwaysLive = $false }

    [pscustomobject]@{
        Gem = $gem.FullName
        Needs = (($needs | Sort-Object) -join '|')
        AlwaysLive = $alwaysLive
        Supplies = $supplies
        Gate = $gate
    }
}

# --- MemoryFacts, restated -------------------------------------------------------------------

$damageProse = '(?i)\bdamage\b'
$damageVar   = '(?i)dmg|damage'
$healProse   = '(?i)\bheal(s|ed|ing)?\b|lifesteal|life steal|omnivamp|regenerat|(restor|recover)\w*[^.]{0,40}\bhealth\b'
$healVar     = '(?i)heal(?!th)'
$shieldProse = '(?i)\bbarrier\b|\bshield(s|ed|ing)?\b'
$shieldVar   = '(?i)shield|barrier'
$elementProse = '(?i)\b(Fire|Cold|Light|Dark)\s+damage\b'
$elementNames = @('Fire', 'Cold', 'Light', 'Dark')

$dump = Join-Path $GameDir "RawData\en-US\memories.json"
if (-not (Test-Path $dump)) { throw "no memory dump at $dump" }
$memories = Get-Content $dump -Raw | ConvertFrom-Json

# A passive memory is read from its code instead - the SkillTrigger and the status effect its
# passive config applies. In the game that effect is TriggerConfig.appliedStatusEffect on the live
# memory; here there is no prefab to ask, so it is found by the name the game gives it, Se_D_<Name>
# beside St_D_<Name>. Likewise "passive" is TriggerConfig.isActive in the game and the Identity
# location here.
$typeByName = @{}
foreach ($t in $types) { if (-not $typeByName.ContainsKey($t.FullName)) { $typeByName[$t.FullName] = $t } }

function Get-CodeFacts($memoryName) {
    $roots = New-Object System.Collections.ArrayList
    if ($typeByName.ContainsKey($memoryName)) { [void]$roots.Add($typeByName[$memoryName]) }
    $effect = $memoryName -replace '^St_', 'Se_'
    if ($typeByName.ContainsKey($effect)) { [void]$roots.Add($typeByName[$effect]) }
    if ($roots.Count -eq 0) { return $null }

    # Outside the game a few bodies do not load - Se_D_AstridsMasterpiecePriorite.CheckExposed is
    # one - and a memory whose code could not all be read is unknown, exactly as in the mod.
    $before = $script:unreadable
    $caps = Get-Capabilities $roots
    if ($script:unreadable -ne $before) { return $null }
    return ,$caps
}

$facts = @{}
foreach ($p in @($memories.PSObject.Properties)) {
    $prose = $p.Value.rawDesc -replace '<[^>]+>', ' '
    $vars = (($p.Value.rawDescVars | ForEach-Object { $_.raw }) -join ' ')
    # travelerMemoryLocation is Identity or Movement for a hero's own two, a letter or two for a
    # hero's starting Q/W/E/R memory, and empty for everything found in a run.
    $location = switch ($p.Value.travelerMemoryLocation) { 'Identity' { 'Identity' } 'Movement' { 'Movement' } default { 'active' } }
    $passive = $location -eq 'Identity'

    $proseDamage = ($prose -match $damageProse) -or ($vars -match $damageVar)
    $proseHeal   = ($prose -match $healProse)   -or ($vars -match $healVar)
    $proseShield = ($prose -match $shieldProse) -or ($vars -match $shieldVar)

    $code = if ($passive) { Get-CodeFacts $p.Name } else { $null }

    # The elements the dump names, in prose before the word damage or among the tags. A passive
    # memory passes on the hero's own damage, so its element is unknown ($null).
    $elements = $null
    if (-not $passive) {
        $elements = New-Object System.Collections.Generic.HashSet[string]
        foreach ($m in [regex]::Matches($prose, $elementProse)) {
            [void]$elements.Add((Get-Culture).TextInfo.ToTitleCase($m.Groups[1].Value.ToLower()))
        }
        foreach ($tag in @($p.Value.tags)) { if ($elementNames -contains $tag) { [void]$elements.Add($tag) } }
    }

    $facts[$p.Name] = [pscustomobject]@{
        Elements    = $elements
        Location    = $location
        Cast        = -not $passive
        Damage      = if ($passive) { $null -ne $code -and $code.Contains('Damage') } else { $proseDamage }
        Heal        = if ($passive) { $null -ne $code -and $code.Contains('Heal') }   else { $proseHeal }
        Shield      = if ($passive) { $null -ne $code -and $code.Contains('Shield') } else { $proseShield }
        Known       = (-not $passive) -or $null -ne $code
        ProseDamage = $proseDamage
        ProseHeal   = $proseHeal
        ProseShield = $proseShield
    }
}

# Every memory in the dump can hold an essence: a Corrupted Chaos shrine adds a slot to any of
# them, identity included, and other mods add slots to Movement.
$slottable = @($facts.Keys | Where-Object { $facts[$_].Known } | Sort-Object)

# Where code and prose disagree about a passive memory. Code saying "no" where prose says "yes" is
# the direction that would cost a false warning, so every row of it has to be accounted for.
$passiveNames = @($facts.Keys | Where-Object { -not $facts[$_].Cast } | Sort-Object)
Write-Host ""
Write-Host "passive memories, code against prose (D H S; lower case = prose only, - = neither):" -ForegroundColor Cyan
foreach ($n in $passiveNames) {
    $f = $facts[$n]
    $cell = {
        param($code, $prose, $letter)
        if ($code) { $letter } elseif ($prose) { $letter.ToLower() } else { '-' }
    }
    "  {0,-34} {1} {2} {3}{4}" -f $n, (& $cell $f.Damage $f.ProseDamage 'D'), (& $cell $f.Heal $f.ProseHeal 'H'),
        (& $cell $f.Shield $f.ProseShield 'S'), $(if (-not $f.Known) { '   unknown: code not found or not readable' } else { '' })
}

# --- The pairing ------------------------------------------------------------------------------

$warnable = @($essences | Where-Object { $_.Needs -ne '' -and -not $_.AlwaysLive })

# A supplier creates through the cast, so it supplies only in a memory that is cast, and only while
# it fires there at all.
$suppliers = @($essences | Where-Object { $_.Supplies -ne '' -and ($_.AlwaysLive -or ($_.Needs -split '\|') -contains 'Cast') })

$byLocation = $slottable | Group-Object { $facts[$_].Location } | Sort-Object Name |
    ForEach-Object { "$($_.Count) $($_.Name)" }

Write-Host ""
Write-Host "$($essences.Count) essences, $($warnable.Count) of them entirely slot-scoped" -ForegroundColor Cyan
Write-Host "$($facts.Count) memories, $($slottable.Count) of them known ($($byLocation -join ', '))" -ForegroundColor Cyan
Write-Host "$($suppliers.Count) essences hand a capability to their memory and can revive a neighbour" -ForegroundColor Cyan
Write-Host ""
if ($Pairs) { $suppliers | ForEach-Object { "  supplies {0,-14} {1}" -f $_.Supplies, $_.Gem }; Write-Host "" }

$total = 0
$byElement = 0
foreach ($e in ($warnable | Sort-Object Gem)) {
    $needs = $e.Needs -split '\|'
    $dead = foreach ($name in $slottable) {
        $f = $facts[$name]
        $alive = $false
        foreach ($n in $needs) {
            if (($n -eq 'Damage' -and $f.Damage) -or ($n -eq 'Heal' -and $f.Heal) -or
                ($n -eq 'Shield' -and $f.Shield) -or ($n -eq 'Cast' -and $f.Cast)) { $alive = $true }
        }

        # The element rule, on top: a memory that does the thing but never with that element.
        if ($alive -and $e.Gate -and $null -ne $f.Elements -and -not $f.Elements.Contains($e.Gate)) {
            $alive = $false
            $script:byElement++
        }

        if (-not $alive) { $name }
    }
    $dead = @($dead)
    $total += $dead.Count
    $where = @($dead | Group-Object { $facts[$_].Location } | Sort-Object Name |
        ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    $label = if ($e.Gate) { "$($e.Needs) ($($e.Gate))" } else { $e.Needs }
    "{0,-24} {1,-17} dead in {2,3} of {3}   {4}" -f $e.Gem, $label, $dead.Count, $slottable.Count, $where
    if ($Pairs) { $dead | ForEach-Object { "                             $_" } }
}

Write-Host ""
$possible = $warnable.Count * $slottable.Count
Write-Host ("$total warned pairs of $possible ({0:P0}), $byElement of them by element" -f ($total / [double]$possible)) -ForegroundColor Green

# The loaded assemblies stay for the life of the session either way, but the handler should not.
[System.AppDomain]::CurrentDomain.remove_AssemblyResolve($onResolve)
