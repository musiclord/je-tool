# Read-only checks for the frozen diagram package; no product execution or indexing.
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
    if ($manifest.schemaVersion -ne 1 -or $manifest.scope -cne 'frozen-research-not-current-product') {
        throw [IO.InvalidDataException]::new('Unexpected architecture manifest.')
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
        $original = @($catalog | Where-Object { $_.diagram -ceq $binding.diagram -and $_.source -ceq $binding.source -and $_.target -ceq $binding.target -and $_.kind -ceq $binding.kind })
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
    return [ordered]@{
        status=$(if($failures.Count){'failed'}else{'passed'})
        files=$manifest.files.Count
        archifyDiagrams=$manifest.diagrams.Count
        likec4Views=$manifest.likec4Views
        checkedClassRelations=$proofs.Count
        sourceExcerpts=$excerpts.Count
        runtimeObserved=$false
        completeSemanticCoverage=$false
        errors=$failures.ToArray()
    }
}
