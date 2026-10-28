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

    # 動態視圖對循序圖的逐步比對（2026-10-25 起）：合成一個小模型、一張小循序圖與一份配對設定。
    # 第二步刻意畫到實作類別、標籤寫介面方法，第三步是沒有呼叫位置的資料線，第四步是讀回；和 jet.c4 的 filterSave 一樣。
    $c4 = @'
model {
  slice = component 'Slice' {
    a = component 'Caller'
    b = component 'Worker'
    sContract = contract 'IStore' { technology 'interface' }
    s = component 'LocalStore'
    db = store 'Db'
    a -[calls]-> b 'Run' {
      metadata { evidence 'S01' }
    }
    a -[viaInterface]-> sContract 'Save' {
      metadata { evidence 'S02' }
    }
    s -[implements]-> sContract '實作' {
      metadata { evidence 'S03' }
    }
    s -[data]-> db 'write' {
      metadata { evidence 'S04' }
    }
  }
}
views {
  dynamic view demo {
    title 'demo'
    slice.a -> slice.b 'Run' {
      notes 'first'
    }
    slice.a -> slice.s 'IStore.Save'
    slice.s -> slice.db 'write rows'
    slice.a <- slice.b 'done'
  }
}
'@
    $model = Get-JetLikeC4Model $c4
    if ($model.elements['slice.s'] -cne 'LocalStore' -or $model.elements['slice.sContract'] -cne 'IStore' -or $model.relations.Count -ne 4 -or
        @($model.relations | Where-Object { $_.evidence -ceq 'S02' -and $_.from -ceq 'slice.a' -and $_.to -ceq 'slice.sContract' }).Count -ne 1 -or
        $model.dynamicViews['demo'].Count -ne 4 -or -not $model.dynamicViews['demo'][3].isReturn -or $model.dynamicViews['demo'][3].source -cne 'slice.b') {
        throw [IO.InvalidDataException]::new('Architecture LikeC4 parse test failed.')
    }
    $count++
    function New-Edge([string]$Target,[int]$Line) { @{kind='invokes';dispatch='statically_bound';target=$Target;location=@{path='src/Caller.cs';line=$Line;endLine=$Line}} }
    $connections=@{
        'likec4:S01'=@{diagram='likec4';connection='S01';kind='calls';members=@('Run');roslyn=@(New-Edge 'M:Example.Worker.Run' 10)}
        'likec4:S02'=@{diagram='likec4';connection='S02';kind='viaInterface';members=@('Save');roslyn=@(New-Edge 'M:Example.IStore.Save(System.String)' 12)}
        'seq:m01'=@{diagram='seq';connection='m01';kind='calls';roslyn=@(New-Edge 'M:Example.Worker.Run' 10)}
        'seq:m02'=@{diagram='seq';connection='m02';kind='viaInterface';roslyn=@(New-Edge 'M:Example.IStore.Save(System.String)' 12)}
        'seq:m03'=@{diagram='seq';connection='m03';kind='return';returnOf='m02'}
        'seq:m04'=@{diagram='seq';connection='m04';kind='calls';roslyn=@(New-Edge 'M:Example.Worker.Stop' 20)}
    }
    $messages=@(@{id='m01';from='a';to='b';label='Run'},@{id='m02';from='a';to='db';label='IStore.Save';variant='dashed'},
        @{id='m03';from='db';to='a';label='saved';variant='return'},@{id='m04';from='a';to='b';label='Stop'})
    $pair=@{id='demo';dynamicView='demo';sequence='seq';participants=@{'slice.a'='a';'slice.b'='b';'slice.s'='db'}
        sequenceMessages=@('m01','m02');sequenceOutOfScope=@(@{message='m04';reason='branch'})
        skippedSteps=@(@{label='write rows';reason='data'},@{label='done';reason='return'})}
    function Assert-Pair($value,[string]$expected,$connectionSet=$connections,$messageSet=$messages,$modelValue=$model) {
        $actual=@(Get-JetDynamicViewPairErrors -Pair $value -Model $modelValue -Connections $connectionSet -Messages $messageSet | ForEach-Object Code)
        if (($expected -ceq 'pass' -and $actual.Count -ne 0) -or ($expected -cne 'pass' -and $expected -cnotin $actual)) {
            throw [IO.InvalidDataException]::new("Architecture dynamic view pair test failed: $expected")
        }
    }
    Assert-Pair $pair 'pass';$count++
    $changed=Copy-Binding $pair;$changed.sequenceMessages=@('m02','m01')
    Assert-Pair $changed 'step_method_differs';$count++
    $changed=Copy-Binding $pair;$changed.sequenceMessages=@('m01')
    Assert-Pair $changed 'step_count_differs';$count++
    $changed=Copy-Binding $pair;$changed.participants['slice.s']='store'
    Assert-Pair $changed 'step_participants_differ';$count++
    $changed=Copy-Binding $pair;$changed.skippedSteps=@(@{label='write rows';reason='data'})
    Assert-Pair $changed 'dynamic_step_unmatched';$count++
    $changed=Copy-Binding $pair;$changed.skippedSteps=@(@{label='done';reason='return'})
    Assert-Pair $changed 'dynamic_step_unmatched';$count++
    $changed=Copy-Binding $pair;$changed.skippedSteps=@($changed.skippedSteps)+@{label='Missing';reason='typo'}
    Assert-Pair $changed 'skipped_step_not_drawn';$count++
    $changed=Copy-Binding $pair;$changed.sequenceOutOfScope=@()
    Assert-Pair $changed 'sequence_call_not_in_scope';$count++
    $changed=Copy-Binding $pair;$changed.dynamicView='other'
    Assert-Pair $changed 'dynamic_view_missing';$count++
    $moved=Copy-Binding $connections;$moved['seq:m02'].roslyn[0].location.line=13
    Assert-Pair $pair 'step_location_differs' $moved;$count++
    $renamed=Copy-Binding $connections;$renamed['likec4:S01'].members=@('Start')
    Assert-Pair $pair 'dynamic_step_member_missing' $renamed;$count++
    $other=Get-JetLikeC4Model ($c4.Replace("'IStore.Save'","'IOther.Save'"))
    Assert-Pair $pair 'dynamic_step_relation_missing' $connections $messages $other;$count++
    return $count
}
