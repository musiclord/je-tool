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
    $named=Copy-Binding $base;$named.members=@('Save')
    Assert-Case $named 'pass';$count++
    $named.members=@('Load')
    Assert-Case $named 'member_not_in_evidence';$count++

    function Assert-Connection($value,[string]$expected) {
        $actual=@(Get-JetConnectionEvidenceErrors -Entry $value -KnownFacts @('example-fact'))
        if (($expected -ceq 'pass' -and $actual.Count -ne 0) -or ($expected -cne 'pass' -and $expected -cnotin $actual)) {
            throw [IO.InvalidDataException]::new("Architecture connection test failed: $expected")
        }
    }
    $call=@{diagram='example';connection='m01';kind='calls';execution='not-observed';facts=@();members=@('Run')
        roslyn=@(@{kind='invokes';source='M:Example.Caller.Start';target='M:Example.Worker.Run';targetType='Example.Worker';dispatch='statically_bound'})}
    Assert-Connection $call 'pass';$count++
    $changed=Copy-Binding $call;$changed.roslyn[0].dispatch='interface_declared_target'
    Assert-Connection $changed 'compile_target_not_concrete';$count++
    $changed=Copy-Binding $call;$changed.members=@('Stop')
    Assert-Connection $changed 'member_not_in_evidence';$count++
    $changed=Copy-Binding $call;$changed.execution='observed'
    Assert-Connection $changed 'runtime_claim_without_observation';$count++
    $interface=@{diagram='example';connection='m02';kind='viaInterface';execution='not-observed';facts=@()
        roslyn=@(@{kind='invokes';source='M:Example.Caller.Start';target='M:Example.IStore.Save';targetType='Example.IStore';dispatch='interface_declared_target'})
        implementations=@(@{kind='implements';source='T:Example.Store';target='T:Example.IStore'})
        assembly=@(@{kind='constructs';source='M:JET.AppCompositionRoot.Create';target='M:Example.Store.#ctor';targetType='Example.Store';dispatch='statically_bound'})}
    Assert-Connection $interface 'pass';$count++
    $changed=Copy-Binding $interface;$changed.roslyn[0].dispatch='statically_bound'
    Assert-Connection $changed 'interface_call_not_proven';$count++
    $changed=Copy-Binding $interface;$changed.assembly=@()
    Assert-Connection $changed 'implementation_without_assembly';$count++
    $changed=Copy-Binding $interface;$changed.assembly[0].source='M:Example.Factory.Create'
    Assert-Connection $changed 'injection_not_from_composition_root';$count++
    $indirect=@{diagram='example';connection='m03';kind='indirect';execution='not-observed';facts=@('example-fact')}
    Assert-Connection $indirect 'pass';$count++
    $changed=Copy-Binding $indirect;$changed.facts=@()
    Assert-Connection $changed 'missing_facts';$count++
    $changed=Copy-Binding $indirect;$changed.facts=@('unknown-fact')
    Assert-Connection $changed 'fact_missing';$count++
    $changed=Copy-Binding $indirect;$changed.roslyn=$call.roslyn
    Assert-Connection $changed 'indirect_has_compile_target';$count++
    Assert-Connection @{diagram='example';connection='m04';kind='return';execution='not-observed';facts=@()} 'return_without_call';$count++
    Assert-Connection @{diagram='example';connection='m05';kind='guess';execution='not-observed';facts=@()} 'unknown_connection_kind';$count++
    function Assert-Members([object[]]$listed,[string]$expected) {
        $actual=@(Get-JetMemberSourceErrors -DrawnMembers @('Store.Save','Store.Load') -Listed $listed -KnownExcerpts @('Store.cs:3-3','Store.cs:5-5') | ForEach-Object Code)
        if (($expected -ceq 'pass' -and $actual.Count -ne 0) -or ($expected -cne 'pass' -and $expected -cnotin $actual)) {
            throw [IO.InvalidDataException]::new("Architecture member test failed: $expected")
        }
    }
    $save=@{type='Store';member='Save';excerpt='Store.cs:3-3'};$load=@{type='Store';member='Load';excerpt='Store.cs:5-5'}
    Assert-Members @($save,$load) 'pass';$count++
    Assert-Members @($save) 'member_source_missing';$count++
    Assert-Members @($save,$load,@{type='Store';member='Delete';excerpt='Store.cs:3-3'}) 'member_not_drawn';$count++
    Assert-Members @($save,$load,$save) 'member_not_drawn';$count++
    Assert-Members @($save,@{type='Store';member='Load';excerpt='Store.cs:9-9'}) 'member_excerpt_missing';$count++
    if ((Get-JetDrawnConnectionKind 'sequence' '') -cne 'calls' -or (Get-JetDrawnConnectionKind 'sequence' 'dashed') -cne 'viaInterface' -or
        (Get-JetDrawnConnectionKind 'sequence' 'emphasis') -cne 'indirect' -or (Get-JetDrawnConnectionKind 'workflow' 'dashed') -cne 'fact') {
        throw [IO.InvalidDataException]::new('Architecture drawn kind test failed.')
    }
    $count++
    return $count
}
