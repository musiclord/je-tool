# Read-only checks for the diagram package; no product execution or indexing.
function Get-JetMemberName([string]$Identifier) {
    return (($Identifier -replace '\(.*$', '').Split('.')[-1] -replace '``\d+$', '')
}

function Get-JetMemberErrors([hashtable]$Item, [object[]]$Edges) {
    if (-not $Item.ContainsKey('members')) { return @() }
    $names = @($Edges | ForEach-Object { Get-JetMemberName ([string]$_.target) })
    return @($Item.members | Where-Object { $_ -cnotin $names } | ForEach-Object { 'member_not_in_evidence' } | Select-Object -First 1)
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
        sourceExcerpts=$excerpts.Count
        runtimeObserved=$false
        completeSemanticCoverage=$false
        errors=$failures.ToArray()
    }
}
