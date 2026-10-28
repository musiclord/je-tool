# Read-only checks for the diagram package; no product execution or indexing.
function Get-JetMemberName([string]$Identifier) {
    return (($Identifier -replace '\(.*$', '').Split('.')[-1] -replace '``\d+$', '')
}

function Get-JetMemberErrors([hashtable]$Item, [object[]]$Edges) {
    if (-not $Item.ContainsKey('members')) { return @() }
    $names = @($Edges | ForEach-Object { Get-JetMemberName ([string]$_.target) })
    return @($Item.members | Where-Object { $_ -cnotin $names } | ForEach-Object { 'member_not_in_evidence' } | Select-Object -First 1)
}

function Get-JetMemberSourceErrors {
    param([Parameter(Mandatory=$true)][AllowEmptyCollection()][string[]]$DrawnMembers,
          [Parameter(Mandatory=$true)][AllowEmptyCollection()][object[]]$Listed,
          [Parameter(Mandatory=$true)][AllowEmptyCollection()][string[]]$KnownExcerpts)
    $drawn = [Collections.Generic.HashSet[string]]::new([string[]]$DrawnMembers, [StringComparer]::Ordinal)
    $known = [Collections.Generic.HashSet[string]]::new([string[]]$KnownExcerpts, [StringComparer]::Ordinal)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $problems = [Collections.Generic.List[object]]::new()
    foreach ($member in $Listed) {
        $name = "$($member.type).$($member.member)"
        if (-not $seen.Add($name) -or -not $drawn.Contains($name)) { $problems.Add([pscustomobject]@{ Code='member_not_drawn'; Member=$name }) }
        if (-not $known.Contains([string]$member.excerpt)) { $problems.Add([pscustomobject]@{ Code='member_excerpt_missing'; Member=$name }) }
    }
    foreach ($name in @($DrawnMembers | Where-Object { -not $seen.Contains($_) })) { $problems.Add([pscustomobject]@{ Code='member_source_missing'; Member=$name }) }
    return $problems.ToArray()
}

function Get-JetDrawnConnectionKind([string]$DiagramType, [string]$Variant) {
    if ($DiagramType -cne 'sequence') { return 'fact' }
    switch -CaseSensitive ($Variant) {
        '' { return 'calls' }
        'dashed' { return 'viaInterface' }
        'emphasis' { return 'indirect' }
        'return' { return 'return' }
        default { return 'unknown' }
    }
}

function Get-JetConnectionEvidenceErrors {
    param([Parameter(Mandatory=$true)][hashtable]$Entry,
          [Parameter(Mandatory=$true)][AllowEmptyCollection()][string[]]$KnownFacts)
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($key in @('diagram','connection','kind','execution','facts')) {
        if (-not $Entry.ContainsKey($key)) { $problems.Add("missing_$key") }
    }
    if ($problems.Count) { return $problems.ToArray() }
    if ($Entry.execution -cne 'not-observed') { $problems.Add('runtime_claim_without_observation') }
    $roslyn = @($Entry['roslyn'] | Where-Object { $null -ne $_ })
    $implementations = @($Entry['implementations'] | Where-Object { $null -ne $_ })
    $assembly = @($Entry['assembly'] | Where-Object { $null -ne $_ })
    $facts = @($Entry.facts | Where-Object { $null -ne $_ })
    if (@($facts | Where-Object { $_ -cnotin $KnownFacts }).Count) { $problems.Add('fact_missing') }
    $fromRoot = { param($edge) $edge.kind -ceq 'constructs' -and ([string]$edge.source).StartsWith('M:JET.AppCompositionRoot.', [StringComparison]::Ordinal) }
    switch -CaseSensitive ($Entry.kind) {
        'calls' {
            if (-not $roslyn.Count -or @($roslyn | Where-Object { $_.kind -cne 'invokes' -or $_.dispatch -cne 'statically_bound' }).Count) {
                $problems.Add('compile_target_not_concrete')
            }
        }
        'viaInterface' {
            if (-not $roslyn.Count -or @($roslyn | Where-Object { $_.kind -cne 'invokes' -or $_.dispatch -cne 'interface_declared_target' }).Count) {
                $problems.Add('interface_call_not_proven')
            }
            if ($implementations.Count -and -not $assembly.Count) { $problems.Add('implementation_without_assembly') }
            if (@($implementations | Where-Object { $_.kind -cnotin @('implements','inherits') }).Count) { $problems.Add('implementation_not_proven') }
            if (@($assembly | Where-Object { -not (& $fromRoot $_) }).Count) { $problems.Add('injection_not_from_composition_root') }
        }
        'implements' {
            if (-not $roslyn.Count -or @($roslyn | Where-Object { $_.kind -cnotin @('implements','inherits') }).Count) { $problems.Add('implementation_not_proven') }
        }
        'injects' {
            if (-not $roslyn.Count -or @($roslyn | Where-Object { -not (& $fromRoot $_) }).Count) { $problems.Add('injection_not_from_composition_root') }
        }
        'return' {
            if (-not [string]$Entry['returnOf']) { $problems.Add('return_without_call') }
        }
        default {
            if ($Entry.kind -cin @('indirect','data','uses','fact')) {
                if (-not $facts.Count) { $problems.Add('missing_facts') }
                if (@($roslyn | Where-Object { $_.dispatch -ceq 'statically_bound' }).Count) { $problems.Add('indirect_has_compile_target') }
            } else {
                $problems.Add('unknown_connection_kind')
            }
        }
    }
    foreach ($code in @(Get-JetMemberErrors $Entry $roslyn)) { $problems.Add($code) }
    return $problems.ToArray()
}

function Get-JetDiagramBindingErrors {
    param([Parameter(Mandatory=$true)][hashtable]$Binding)
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($key in @('source','target','kind','execution','roslyn')) {
        if (-not $Binding.ContainsKey($key)) { $problems.Add("missing_$key") }
    }
    if ($problems.Count) { return $problems.ToArray() }
    if ($Binding.execution -cne 'not-observed') { $problems.Add('runtime_claim_without_observation') }
    if ($Binding.source -cnotmatch '^T:' -or $Binding.target -cnotmatch '^T:') {
        $problems.Add('invalid_type_identity')
        return $problems.ToArray()
    }
    $sourceType = $Binding.source.Substring(2)
    $targetType = $Binding.target.Substring(2)
    $supported = @($Binding.roslyn | Where-Object {
        $edge = $_
        switch ($Binding.kind) {
            'dependency' {
                $edge.kind -cin @('invokes','constructs') -and
                ([string]$edge.source).StartsWith("M:$sourceType.", [StringComparison]::Ordinal) -and
                $edge.targetType -ceq $targetType
            }
            'realization' { $edge.kind -ceq 'implements' -and $edge.source -ceq $Binding.source -and $edge.target -ceq $Binding.target }
            'inheritance' { $edge.kind -ceq 'inherits' -and $edge.source -ceq $Binding.source -and $edge.target -ceq $Binding.target }
            default { $false }
        }
    })
    if (-not $supported.Count) { $problems.Add('relation_kind_or_target_not_supported') }
    foreach ($code in @(Get-JetMemberErrors $Binding @($Binding.roslyn))) { $problems.Add($code) }
    return $problems.ToArray()
}

# LikeC4 模型的最小解析，只處理 jet.c4 一直維持的寫法：一行一個敘述，區塊在行尾開啟。
# 讀出三樣東西：元件宣告（完整識別字對標題）、帶證據編號的關係，以及動態視圖的步驟。
function Get-JetLikeC4Model([string]$Text) {
    $elements = @{}
    $relations = [Collections.Generic.List[object]]::new()
    $views = @{}
    $scope = [Collections.Generic.List[object]]::new()
    $depth = 0
    $view = $null
    $viewCloseAt = 0
    $relation = $null
    foreach ($line in ($Text -split "`r?`n")) {
        $code = [regex]::Replace([regex]::Replace($line, "'[^']*'", "''"), '//.*$', '')
        $after = $depth + ([regex]::Matches($code, '\{')).Count - ([regex]::Matches($code, '\}')).Count
        $path = @($scope | ForEach-Object { $_.name }) -join '.'
        if ($null -ne $view) {
            $step = [regex]::Match($line, "^\s*([\w.]+)\s+(->|<-)\s+([\w.]+)\s+'([^']*)'")
            if ($step.Success) {
                $reverse = $step.Groups[2].Value -ceq '<-'
                $views[$view].Add(@{
                    source = $step.Groups[$(if ($reverse) { 3 } else { 1 })].Value
                    target = $step.Groups[$(if ($reverse) { 1 } else { 3 })].Value
                    label = $step.Groups[4].Value
                    isReturn = $reverse })
            }
            if ($after -le $viewCloseAt) { $view = $null }
        } else {
            $dynamic = [regex]::Match($line, '^\s*dynamic view (\w+)\s*\{')
            if ($dynamic.Success) {
                $view = $dynamic.Groups[1].Value
                $views[$view] = [Collections.Generic.List[object]]::new()
                $viewCloseAt = $depth
            }
            $declaration = [regex]::Match($line, "^\s*(\w+)\s*=\s*\w+\s+'([^']*)'")
            if ($declaration.Success) {
                $id = $declaration.Groups[1].Value
                $elements[$(if ($path) { "$path.$id" } else { $id })] = $declaration.Groups[2].Value
                if ($after -gt $depth) { $scope.Add(@{ name = $id; closeAt = $depth }) }
            }
            $arrow = [regex]::Match($line, "^\s*([\w.]+)\s+-\[(\w+)\]->\s+([\w.]+)\s+'([^']*)'")
            if ($arrow.Success) {
                $relation = @{ scope = $path; from = $arrow.Groups[1].Value; kind = $arrow.Groups[2].Value
                               to = $arrow.Groups[3].Value; label = $arrow.Groups[4].Value; evidence = $null }
                $relations.Add($relation)
            }
            $evidence = [regex]::Match($line, "evidence '(\w+)'")
            if ($evidence.Success -and $null -ne $relation) { $relation.evidence = $evidence.Groups[1].Value; $relation = $null }
        }
        $depth = $after
        while ($scope.Count -and $scope[$scope.Count - 1].closeAt -ge $depth) { $scope.RemoveAt($scope.Count - 1) }
    }
    # 關係兩端寫的是所在區塊裡的短名稱，或從外層算起的路徑；由內往外找第一個宣告過的元件。
    function Resolve-Reference([string]$Scope, [string]$Reference) {
        $parts = @(if ($Scope) { $Scope.Split('.') } else { @() })
        for ($i = $parts.Count; $i -gt 0; $i--) {
            $candidate = (($parts[0..($i - 1)]) -join '.') + '.' + $Reference
            if ($elements.ContainsKey($candidate)) { return $candidate }
        }
        return $Reference
    }
    $resolved = @(foreach ($item in $relations) {
        @{ from = Resolve-Reference $item.scope $item.from; to = Resolve-Reference $item.scope $item.to
           kind = $item.kind; label = $item.label; evidence = $item.evidence }
    })
    return @{ elements = $elements; relations = $resolved; dynamicViews = $views }
}

# 畫同一個流程的 LikeC4 動態視圖與 Archify 循序圖，逐步比對呼叫順序、呼叫對象與程式位置。
# 動態視圖的每一步經元件對應找到模型關係，再從連線證據取 Roslyn 呼叫位置；循序圖那邊取範圍內的呼叫訊息。
# 沒對到的步驟要列在 skippedSteps，範圍外的呼叫訊息要列在 sequenceOutOfScope，兩邊都要寫原因。
function Get-JetDynamicViewPairErrors {
    param([Parameter(Mandatory=$true)][hashtable]$Pair,
          [Parameter(Mandatory=$true)][hashtable]$Model,
          [Parameter(Mandatory=$true)][hashtable]$Connections,
          [Parameter(Mandatory=$true)][AllowEmptyCollection()][object[]]$Messages)
    $problems = [Collections.Generic.List[object]]::new()
    function Add-Problem([string]$Code, [string]$Detail) { $problems.Add([pscustomobject]@{ Code = $Code; Detail = $Detail }) }
    function Get-Locations($Edges, [string]$Method) {
        return @($Edges | Where-Object { $null -ne $_ -and (Get-JetMemberName ([string]$_.target)) -ceq $Method } |
            ForEach-Object { "$($_.location.path):$($_.location.line)" } | Sort-Object -Unique)
    }
    function Get-Method([string]$Label) { return $Label.Split('.')[-1] }
    $steps = $Model.dynamicViews[[string]$Pair.dynamicView]
    if ($null -eq $steps) { Add-Problem 'dynamic_view_missing' ([string]$Pair.dynamicView); return $problems.ToArray() }
    $skips = @{}
    foreach ($skip in @($Pair['skippedSteps'] | Where-Object { $null -ne $_ })) { $skips[[string]$skip.label] = $false }
    $participants = $Pair['participants']
    if ($null -eq $participants) { $participants = @{} }
    $left = [Collections.Generic.List[object]]::new()
    foreach ($step in $steps) {
        $label = [string]$step.label
        if ($skips.ContainsKey($label)) { $skips[$label] = $true; continue }
        $method = Get-Method $label
        if ($step.isReturn -or $method -cnotmatch '^[A-Za-z_]\w*$') { Add-Problem 'dynamic_step_unmatched' $label; continue }
        $interface = if ($label.Contains('.')) { $label.Substring(0, $label.LastIndexOf('.')) } else { $null }
        $matching = @($Model.relations | Where-Object {
            $_.from -ceq $step.source -and $_.kind -cin @('calls','viaInterface') -and
            $(if ($interface) { $Model.elements[$_.to] -ceq $interface } else { $_.to -ceq $step.target }) })
        if ($matching.Count -ne 1) { Add-Problem 'dynamic_step_relation_missing' $label; continue }
        $evidence = $Connections["likec4:$($matching[0].evidence)"]
        if ($null -eq $evidence) { Add-Problem 'dynamic_step_evidence_missing' $label; continue }
        if ($method -cnotin @($evidence['members'] | Where-Object { $null -ne $_ })) { Add-Problem 'dynamic_step_member_missing' $label; continue }
        $locations = @(Get-Locations @($evidence['roslyn']) $method)
        if (-not $locations.Count) { Add-Problem 'dynamic_step_location_missing' $label; continue }
        $from = $participants[[string]$step.source]
        $to = $participants[[string]$step.target]
        if (-not $from -or -not $to) { Add-Problem 'participant_unmapped' $label; continue }
        $left.Add(@{ name = $label; from = [string]$from; to = [string]$to; method = $method; locations = $locations })
    }
    foreach ($label in @($skips.Keys | Where-Object { -not $skips[$_] })) { Add-Problem 'skipped_step_not_drawn' $label }
    $scope = @($Pair['sequenceMessages'] | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $outOfScope = @{}
    foreach ($item in @($Pair['sequenceOutOfScope'] | Where-Object { $null -ne $_ })) { $outOfScope[[string]$item.message] = $true }
    $byId = @{}
    foreach ($message in @($Messages | Where-Object { $null -ne $_ })) { $byId[[string]$message.id] = $message }
    $right = [Collections.Generic.List[object]]::new()
    foreach ($id in $scope) {
        if ($outOfScope.ContainsKey($id)) { Add-Problem 'sequence_scope_conflict' $id }
        $message = $byId[$id]
        if ($null -eq $message) { Add-Problem 'sequence_message_missing' $id; continue }
        $evidence = $Connections["$($Pair.sequence):$id"]
        if ($null -eq $evidence) { Add-Problem 'sequence_evidence_missing' $id; continue }
        if ($evidence.kind -cnotin @('calls','viaInterface')) { Add-Problem 'sequence_message_not_call' $id; continue }
        $method = Get-Method ([string]$message.label)
        $locations = @(Get-Locations @($evidence['roslyn']) $method)
        if (-not $locations.Count) { Add-Problem 'sequence_location_missing' $id; continue }
        $right.Add(@{ name = $id; from = [string]$message.from; to = [string]$message.to; method = $method; locations = $locations })
    }
    foreach ($id in @($outOfScope.Keys)) { if (-not $byId.ContainsKey($id)) { Add-Problem 'sequence_message_missing' $id } }
    foreach ($message in @($Messages | Where-Object { $null -ne $_ })) {
        $id = [string]$message.id
        $evidence = $Connections["$($Pair.sequence):$id"]
        if ($null -ne $evidence -and $evidence.kind -cin @('calls','viaInterface') -and $id -cnotin $scope -and -not $outOfScope.ContainsKey($id)) {
            Add-Problem 'sequence_call_not_in_scope' $id
        }
    }
    if ($left.Count -ne $right.Count) { Add-Problem 'step_count_differs' "dynamic $($left.Count), sequence $($right.Count)" }
    for ($i = 0; $i -lt [Math]::Min($left.Count, $right.Count); $i++) {
        $l = $left[$i]; $r = $right[$i]; $where = "$($i + 1):$($l.name)/$($r.name)"
        if ($l.method -cne $r.method) { Add-Problem 'step_method_differs' $where }
        elseif ($l.from -cne $r.from -or $l.to -cne $r.to) { Add-Problem 'step_participants_differ' $where }
        elseif (($l.locations -join '|') -cne ($r.locations -join '|')) { Add-Problem 'step_location_differs' $where }
    }
    return $problems.ToArray()
}

function Invoke-JetArchitectureCheck {
    param([Parameter(Mandatory=$true)][string]$RepositoryRoot,
          [Parameter(Mandatory=$true)][string]$ManifestPath)
    $failures = [Collections.Generic.List[object]]::new()
    $repository = [IO.Path]::GetFullPath($RepositoryRoot)
    function Resolve-ArchiveFile([string]$Base,[string]$Relative) {
        if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains('\') -or
            @($Relative.Split('/') | Where-Object { $_ -ceq '..' }).Count) {
            throw [IO.InvalidDataException]::new('Unsafe archive path.')
        }
        $full = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
        Assert-NoReparsePoint -Root $repository -Candidate $full
        return $full
    }
    function Problem([string]$Code,[string]$File) {
        $failures.Add([ordered]@{code="architecture_$Code";file=$File;line=$null})
    }
    $manifestFile = Resolve-ArchiveFile $repository $ManifestPath
    $archive = Split-Path $manifestFile -Parent
    $manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 50
    if ($manifest.schemaVersion -ne 1 -or $manifest.scope -cne 'frozen-snapshot-redraw-not-current-product' -or -not $manifest.ContainsKey('connections')) {
        throw [IO.InvalidDataException]::new('Unexpected architecture manifest.')
    }
    function Read-ArchiveJson([string]$Relative) {
        return Get-Content -LiteralPath (Resolve-ArchiveFile $archive $Relative) -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 60
    }
    $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifest.files) {
        if (-not $declared.Add($entry.path)) { Problem 'duplicate_file' $entry.path }
        $path = Resolve-ArchiveFile $archive $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Problem 'file_missing' $entry.path; continue }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
            Problem 'file_changed' $entry.path
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $archive -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($archive,$file.FullName).Replace('\','/')
        if ($relative -cne 'manifest.json' -and -not $declared.Contains($relative)) { Problem 'undeclared_file' $relative }
    }
    $proofs = Get-Content -LiteralPath (Resolve-ArchiveFile $archive $manifest.bindings) -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 50
    $catalog = Get-Content -LiteralPath (Resolve-ArchiveFile $archive 'evidence/class-relations.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 50
    $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($binding in $proofs) {
        if (-not $identities.Add("$($binding.diagram):$($binding.relationship)")) { Problem 'duplicate_binding' $manifest.bindings }
        $original = @($catalog | Where-Object { $_.diagram -ceq $binding.diagram -and $_.source -ceq $binding.source -and $_.target -ceq $binding.target -and $_.kind -ceq $binding.kind -and
            (-not $_.ContainsKey('relationship') -or $_.relationship -ceq $binding.relationship) })
        if ($original.Count -ne 1 -or
            ($original[0].roslyn | ConvertTo-Json -Depth 50 -Compress) -cne ($binding.roslyn | ConvertTo-Json -Depth 50 -Compress)) {
            Problem 'binding_differs_from_analysis' $binding.diagram
        }
        foreach ($code in @(Get-JetDiagramBindingErrors -Binding $binding)) { Problem $code "$($binding.diagram):$($binding.relationship)" }
        $diagram = @($manifest.diagrams | Where-Object id -CEQ $binding.diagram)
        if ($diagram.Count -ne 1) { Problem 'diagram_missing' $binding.diagram; continue }
        $candidate = Get-Content -LiteralPath (Resolve-ArchiveFile $archive $diagram[0].candidate) -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 50
        $relation = @($candidate.relationships | Where-Object id -CEQ $binding.relationship)
        if ($relation.Count -ne 1) { Problem 'relationship_missing' $binding.diagram; continue }
        if ($relation[0].kind -cne $binding.kind) { Problem 'relationship_kind_changed' $binding.diagram }
        $from = @($candidate.types | Where-Object id -CEQ $relation[0].from)
        $to = @($candidate.types | Where-Object id -CEQ $relation[0].to)
        if ($from.Count -ne 1 -or $to.Count -ne 1 -or
            $from[0].label -cne ($binding.source.Split('.')[-1]) -or
            $to[0].label -cne ($binding.target.Split('.')[-1])) { Problem 'relationship_endpoint_changed' $binding.diagram }
    }
    if ($proofs.Count -ne $manifest.checkedClassRelations) { Problem 'binding_count_changed' $manifest.bindings }
    foreach ($diagram in $manifest.diagrams) {
        foreach ($kind in @('candidate','html')) {
            $path = Resolve-ArchiveFile $archive $diagram[$kind]
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $diagram[$kind+'Sha256']) {
                Problem 'original_diagram_changed' $diagram[$kind]
            }
        }
    }
    $sourceManifest = Get-Content -LiteralPath (Resolve-ArchiveFile $archive 'evidence/source-manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 20
    $sourceHashes=@{}
    foreach($source in $sourceManifest.files){$sourceHashes[$source.path]=$source.sha256}
    $excerpts = Get-Content -LiteralPath (Resolve-ArchiveFile $archive $manifest.sourceExcerpts) -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 30
    foreach($snippet in $excerpts.Values) {
        if (-not $sourceHashes.ContainsKey($snippet.path) -or $sourceHashes[$snippet.path] -cne $snippet.sha256 -or $snippet.line -lt 1 -or $snippet.endLine -lt $snippet.line) {
            Problem 'source_identity_mismatch' $snippet.path
        }
    }
    $facts = @((Read-ArchiveJson $manifest.facts).facts)
    $factIds = [string[]]@($facts | ForEach-Object { [string]$_.id })
    foreach ($fact in $facts) {
        foreach ($source in $fact.sources) {
            if (-not $sourceHashes.ContainsKey($source.path) -or $sourceHashes[$source.path] -cne $source.sha256) { Problem 'fact_source_mismatch' $fact.id }
            if (-not $excerpts.ContainsKey("$($source.path):$($source.line)-$($source.endLine)")) { Problem 'fact_excerpt_missing' $fact.id }
        }
    }
    # Every member drawn on a class diagram keeps one declaration excerpt from the snapshot.
    $memberCount = 0
    if (-not $manifest.ContainsKey('memberSources')) {
        Problem 'member_sources_missing' $ManifestPath
    } else {
        $members = Read-ArchiveJson $manifest.memberSources
        foreach ($diagram in @($manifest.diagrams | Where-Object { $_.type -ceq 'class' })) {
            $candidate = Read-ArchiveJson $diagram.candidate
            $drawnMembers = @(foreach ($type in $candidate.types) {
                foreach ($member in @(@($type['attributes']) + @($type['methods']) | Where-Object { $null -ne $_ })) { "$($type.label).$($member.name)" }
            })
            $listed = @($members[$diagram.id] | Where-Object { $null -ne $_ })
            $memberCount += $listed.Count
            foreach ($problem in @(Get-JetMemberSourceErrors -DrawnMembers $drawnMembers -Listed $listed -KnownExcerpts @($excerpts.Keys))) {
                Problem $problem.Code "$($diagram.id):$($problem.Member)"
            }
        }
        if ($memberCount -ne $manifest.checkedMembers) { Problem 'member_count_changed' $manifest.memberSources }
    }
    $entries = @(Read-ArchiveJson $manifest.connections)
    $byKey = @{}
    foreach ($entry in $entries) {
        $key = "$($entry.diagram):$($entry.connection)"
        if ($byKey.ContainsKey($key)) { Problem 'duplicate_connection' $key; continue }
        $byKey[$key] = $entry
        foreach ($code in @(Get-JetConnectionEvidenceErrors -Entry $entry -KnownFacts $factIds)) { Problem $code $key }
        foreach ($group in @('roslyn','implementations','assembly')) {
            foreach ($edge in @($entry[$group] | Where-Object { $null -ne $_ })) {
                if (-not $sourceHashes.ContainsKey([string]$edge.location.path)) { Problem 'evidence_location_unknown' $key }
            }
        }
    }
    if ($entries.Count -ne $manifest.checkedConnections) { Problem 'connection_count_changed' $manifest.connections }
    # Every drawn line must be bound, and the line style must match the evidence kind.
    $drawn = @{}
    $c4 = Get-Content -LiteralPath (Resolve-ArchiveFile $archive $manifest.likec4Model) -Raw -Encoding utf8
    $bound = [regex]::Matches($c4, "-\[(\w+)\]->[^\r\n{]*\{\s*metadata \{ evidence '(\w+)' \}")
    if ($bound.Count -ne [regex]::Matches($c4, '-\[\w+\]->').Count) { Problem 'likec4_relation_unbound' $manifest.likec4Model }
    foreach ($match in $bound) { $drawn["likec4:$($match.Groups[2].Value)"] = @{ kind = $match.Groups[1].Value; ends = $null } }
    foreach ($diagram in @($manifest.diagrams | Where-Object { $_.type -cne 'class' })) {
        $candidate = Read-ArchiveJson $diagram.candidate
        foreach ($list in @('messages','edges','flows','connections','relationships')) {
            foreach ($item in @($candidate[$list] | Where-Object { $null -ne $_ -and $_.ContainsKey('id') })) {
                $drawn["$($diagram.id):$($item.id)"] = @{
                    kind = Get-JetDrawnConnectionKind ([string]$candidate.diagram_type) ([string]$item['variant'])
                    ends = "$($item.from)>$($item.to)"; reverse = "$($item.to)>$($item.from)"
                }
            }
        }
    }
    foreach ($key in $drawn.Keys) {
        if (-not $byKey.ContainsKey($key)) { Problem 'connection_unbound' $key; continue }
        if ($byKey[$key].kind -cne $drawn[$key].kind) { Problem 'drawn_kind_differs' $key }
    }
    foreach ($key in $byKey.Keys) {
        if (-not $drawn.ContainsKey($key)) { Problem 'connection_not_drawn' $key; continue }
        $entry = $byKey[$key]
        if ($entry.kind -ceq 'return') {
            $call = $drawn["$($entry.diagram):$($entry.returnOf)"]
            if ($null -eq $call -or $call.reverse -cne $drawn[$key].ends) { Problem 'return_does_not_reverse_call' $key }
        }
    }
    # Dynamic views and sequence diagrams that draw the same flow must agree step by step (pairs declared in the package).
    $pairCount = 0
    if (-not $manifest.ContainsKey('dynamicViewPairs')) {
        Problem 'dynamic_view_pairs_missing' $ManifestPath
    } else {
        $pairs = @((Read-ArchiveJson $manifest.dynamicViewPairs).pairs | Where-Object { $null -ne $_ })
        $model = Get-JetLikeC4Model $c4
        foreach ($pair in $pairs) {
            $pairCount++
            $sequence = @($manifest.diagrams | Where-Object { $_.id -ceq $pair.sequence -and $_.type -ceq 'sequence' })
            if ($sequence.Count -ne 1) { Problem 'dynamic_view_pair_sequence_missing' $pair.id; continue }
            $candidate = Read-ArchiveJson $sequence[0].candidate
            foreach ($problem in @(Get-JetDynamicViewPairErrors -Pair $pair -Model $model -Connections $byKey -Messages @($candidate.messages))) {
                Problem $problem.Code "$($pair.id):$($problem.Detail)"
            }
        }
        if ($pairCount -ne $manifest.checkedDynamicViewPairs) { Problem 'dynamic_view_pair_count_changed' $manifest.dynamicViewPairs }
    }
    foreach ($file in Get-ChildItem -LiteralPath $archive -File -Recurse -Include '*.html','*.json','*.md','*.c4') {
        if ([Uri]::UnescapeDataString([IO.File]::ReadAllText($file.FullName)) -match '(?i)\b[a-z]:[\\/]{1,2}users[\\/]') {
            Problem 'local_path_in_package' ([IO.Path]::GetRelativePath($archive,$file.FullName).Replace('\','/'))
        }
    }
    return [ordered]@{
        status=$(if($failures.Count){'failed'}else{'passed'})
        manifest=$ManifestPath
        files=$manifest.files.Count
        archifyDiagrams=$manifest.diagrams.Count
        likec4Views=$manifest.likec4Views
        checkedClassRelations=$proofs.Count
        checkedConnections=$entries.Count
        checkedMembers=$memberCount
        checkedDynamicViewPairs=$pairCount
        sourceExcerpts=$excerpts.Count
        runtimeObserved=$false
        completeSemanticCoverage=$false
        errors=$failures.ToArray()
    }
}
