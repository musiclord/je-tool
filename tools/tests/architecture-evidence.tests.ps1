function Test-JetArchitectureRules {
    function Copy-Binding($value) { $value | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable -Depth 20 }
    $base = @{
        source='T:Example.Caller';target='T:Example.IStore';kind='dependency';execution='not-observed'
        roslyn=@(@{kind='invokes';source='M:Example.Caller.Run';targetType='Example.IStore';target='M:Example.IStore.Save';dispatch='interface_declared_target'})
    }
    $count=0
    function Assert-Case($value,[string]$expected) {
        $actual=@(Get-JetDiagramBindingErrors -Binding $value)
        if (($expected -ceq 'pass' -and $actual.Count -ne 0) -or ($expected -cne 'pass' -and $expected -cnotin $actual)) {
            throw [IO.InvalidDataException]::new("Architecture rule test failed: $expected")
        }
    }
    Assert-Case $base 'pass'; $count++
    $changed=Copy-Binding $base;$changed.kind='inheritance'
    Assert-Case $changed 'relation_kind_or_target_not_supported';$count++
    $changed=Copy-Binding $base;$changed.target='T:Example.ConcreteStore'
    Assert-Case $changed 'relation_kind_or_target_not_supported';$count++
    $changed=Copy-Binding $base;$changed.execution='always'
    Assert-Case $changed 'runtime_claim_without_observation';$count++
    $changed=Copy-Binding $base;$changed.roslyn=@()
    Assert-Case $changed 'relation_kind_or_target_not_supported';$count++
    $changed=Copy-Binding $base;[void]$changed.Remove('execution')
    Assert-Case $changed 'missing_execution';$count++
    $implemented=@{source='T:Example.Store';target='T:Example.IStore';kind='realization';execution='not-observed';roslyn=@(@{kind='implements';source='T:Example.Store';target='T:Example.IStore'})}
    Assert-Case $implemented 'pass';$count++
    $inherited=@{source='T:Example.Child';target='T:Example.Parent';kind='inheritance';execution='not-observed';roslyn=@(@{kind='inherits';source='T:Example.Child';target='T:Example.Parent'})}
    Assert-Case $inherited 'pass';$count++
    return $count
}
