using System.Collections.ObjectModel;
using System.Globalization;
using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// JET 審計程序的唯一 facade。程序總表沿六步驟主線排列；規則 leaf 的 slug 與中文名
/// 一律取 RuleCatalog，artifact 一律取 JetSchemaCatalog 正準名。
/// </summary>
public static partial class JetAuditProgram
{
    private const string ValidationAction = "validate.run";
    private const string PrescreenAction = "prescreen.run";

    private static readonly IReadOnlyList<string> NoArtifacts = Array.AsReadOnly(Array.Empty<string>());

    public static IReadOnlyList<ProcedureDefinition> Procedures { get; } = BuildProgram();

    /// <summary>
    /// Resume renderer：保留既存 prescreen 結果，只把已退役的工程式 N/A 理由轉成目前權威文案。
    /// </summary>
    internal static System.Text.Json.JsonElement RenderPrescreenSummary(string summaryJson) =>
        PrescreenProcedures.RenderSummary(summaryJson);

    /// <summary>
    /// 步驟四與流程總覽共用的預篩選定位文案。Application 只轉成 wire；
    /// frontend fallback 必須由 mirror 守衛證明逐字一致。
    /// </summary>
    internal static PrescreenPositioning RenderPrescreenPositioning() =>
        PrescreenPositioningRenderer.Render();

    /// <summary>
    /// Validation production path 的 typed Plan。既有 public review plan 仍是程序 verdict
    /// 的唯一來源；typed plan 只補上具名、且不需由 Infrastructure 解讀的輸入。
    /// </summary>
    internal static ValidationPlan Plan(ValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reviewPlan = Plan(
            new AuditCaseSnapshot(
                request.ProjectId,
                request.HasGlMapping,
                request.HasTbMapping,
                request.PeriodStart,
                request.PeriodEnd,
                request.MoneyScale,
                request.SampleSeed,
                SampleSeedVersion: request.SampleSeedVersion),
            new AuditUserParameters(
                request.RunId,
                request.GeneratedUtc,
                request.SampleSize,
                ValidationAction));

        return new ValidationPlan(
            request,
            reviewPlan,
            ValidationAmountDistributionCatalog.Plan(request.MoneyScale));
    }

    /// <summary>
    /// ExecuteAsync 保持四動詞外形，但改由具名 validation facts port 執行 raw SQL facts。
    /// </summary>
    internal static Task<ValidationFacts> ExecuteAsync(
        ValidationPlan plan,
        IValidationFactsPort factsPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Validation typed Finalize：part(a) match 與所有 status／N/A 均在 AuditCore 裁定，
    /// 再沿用既有 public manifest finalizer，避免第二套 audit decision。
    /// </summary>
    internal static ValidationResult Finalize(
        ValidationPlan plan,
        ValidationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        ValidatePopulationSummary(facts.PopulationSummary);
        var effective = facts.PopulationSummary.Effective;
        var stats = new GlPopulationStats(
            effective.RowCount,
            effective.VoucherCount,
            effective.TotalDebitScaled,
            effective.TotalCreditScaled,
            effective.NetScaled);
        var partA = facts.ControlTotals is { } control
            ? new CompletenessPartA(
                new CompletenessPopulationTotals(
                    control.EligibleSourceRowCount,
                    control.EligibleSourceDebitScaled,
                    control.EligibleSourceCreditScaled),
                new CompletenessPopulationTotals(
                    effective.RowCount,
                    effective.TotalDebitScaled,
                    effective.TotalCreditScaled),
                RowCountMatch: control.EligibleSourceRowCount == effective.RowCount,
                AmountMatch: control.EligibleSourceDebitScaled == effective.TotalDebitScaled
                    && control.EligibleSourceCreditScaled == effective.TotalCreditScaled)
            : null;

        var data = new ValidationRunResult(
            stats,
            facts.PopulationSummary,
            facts.CompletenessDiffAccountCount,
            facts.CompletenessDiffAccounts,
            facts.UnbalancedDocumentCount,
            facts.InfSampleCount,
            facts.NullAccountCount,
            facts.NullDocumentCount,
            facts.NullDescriptionCount,
            facts.OutOfRangeDateCount,
            facts.SourceQualityFindingCount,
            facts.UnbalancedDocuments,
            facts.NullRecordRows,
            partA);
        var manifest = Finalize(plan.ReviewPlan, new AuditOutcome(data));
        var amountDistribution = FinalizeAmountDistribution(facts.AmountBinCounts);

        return new ValidationResult(data, manifest, amountDistribution);
    }

    private static void ValidatePopulationSummary(GlPopulationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(summary.Raw);
        ArgumentNullException.ThrowIfNull(summary.Effective);
        ArgumentNullException.ThrowIfNull(summary.Excluded);

        if (summary.Raw.RowCount < 0
            || summary.Effective.RowCount < 0
            || summary.Effective.VoucherCount < 0
            || summary.Excluded.RowCount < 0
            || summary.Excluded.ByPeriodCount < 0
            || summary.Excluded.ByPostingStatusCount < 0
            || summary.Effective.VoucherCount > summary.Effective.RowCount
            || summary.Raw.RowCount != checked(summary.Effective.RowCount + summary.Excluded.RowCount)
            || summary.Excluded.RowCount != checked(
                summary.Excluded.ByPeriodCount + summary.Excluded.ByPostingStatusCount)
            || summary.Effective.NetScaled != checked(
                summary.Effective.TotalDebitScaled - summary.Effective.TotalCreditScaled))
        {
            throw new InvalidOperationException(
                "Validation population summary 不符合 raw／effective／excluded 互斥分區 invariant。");
        }
    }

    /// <summary>
    /// Provider raw groups 依 canonical catalog 補齊為固定 15 bins，並以全部非零元
    /// 分錄作 ECDF 分母。零元及非零分母為 0 時回 null，不冒充 0%。
    /// </summary>
    private static ValidationAmountDistribution FinalizeAmountDistribution(
        IReadOnlyList<ValidationAmountBinCount> rawCounts)
    {
        ArgumentNullException.ThrowIfNull(rawCounts);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in rawCounts)
        {
            if (!ValidationAmountDistributionCatalog.BinKeys.Contains(
                    raw.Key,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Validation amount distribution 收到未知 bucket '{raw.Key}'。");
            }
            if (raw.Count < 0)
            {
                throw new InvalidOperationException(
                    $"Validation amount distribution bucket '{raw.Key}' 的 count 不可為負數。");
            }
            if (!counts.TryAdd(raw.Key, raw.Count))
            {
                throw new InvalidOperationException(
                    $"Validation amount distribution bucket '{raw.Key}' 重複。");
            }
        }

        var nonZeroTotal = 0L;
        foreach (var key in ValidationAmountDistributionCatalog.BinKeys.Skip(1))
        {
            nonZeroTotal = checked(nonZeroTotal + counts.GetValueOrDefault(key));
        }

        var cumulative = 0L;
        var bins = new List<ValidationAmountDistributionBin>(
            ValidationAmountDistributionCatalog.BinKeys.Count);
        foreach (var key in ValidationAmountDistributionCatalog.BinKeys)
        {
            var count = counts.GetValueOrDefault(key);
            decimal? ecdfPct = null;
            if (!string.Equals(key, "zero", StringComparison.Ordinal)
                && nonZeroTotal > 0)
            {
                cumulative = checked(cumulative + count);
                ecdfPct = Math.Round(
                    (decimal)cumulative * 100m / nonZeroTotal,
                    1,
                    MidpointRounding.AwayFromZero);
            }

            bins.Add(new ValidationAmountDistributionBin(key, count, ecdfPct));
        }

        return new ValidationAmountDistribution(bins);
    }

    /// <summary>Typed validation result 的 Explain 沿用既有 review manifest 文字。</summary>
    internal static string Explain(ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Explain(result.Manifest);
    }

    /// <summary>
    /// Validation／prescreen 共用的 GL target 前置條件。Application 可在讀取其他
    /// 案件 metadata 前先呼叫，以維持既有 no_target_data 錯誤優先序；實際裁定
    /// 仍只有 AuditCore 這一份。
    /// </summary>
    internal static void RequireGlMapping([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool hasGlMapping)
    {
        if (!hasGlMapping)
        {
            throw new JetActionException(
                JetErrorCodes.NoTargetData,
                "尚未提交 GL 欄位配對（無投影資料），請先完成欄位配對步驟。");
        }
    }

    /// <summary>依 action 選取 validation／prescreen 家族，裁定 N/A，並綁定執行參數。</summary>
    public static AuditExecutionPlan Plan(
        AuditCaseSnapshot caseSnapshot,
        AuditUserParameters userParameters)
    {
        ArgumentNullException.ThrowIfNull(caseSnapshot);
        ArgumentNullException.ThrowIfNull(userParameters);

        RequireGlMapping(caseSnapshot.HasGlMapping);

        var selected = userParameters.ActionName switch
        {
            ValidationAction => Procedures
                .Where(definition => string.Equals(definition.ActionName, ValidationAction, StringComparison.Ordinal))
                .Select(definition => PlanValidation(definition, caseSnapshot, userParameters))
                .ToArray(),
            PrescreenAction => Procedures
                .Where(definition => string.Equals(definition.ActionName, PrescreenAction, StringComparison.Ordinal))
                .Select(definition => PlanPrescreen(definition, caseSnapshot))
                .ToArray(),
            _ => throw new InvalidOperationException(
                $"AuditCore 尚未登錄 action '{userParameters.ActionName}' 的程序家族。")
        };

        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                $"審計程序總表未登錄 {userParameters.ActionName} 程序。");
        }

        return new AuditExecutionPlan(caseSnapshot, userParameters, Array.AsReadOnly(selected));
    }

    /// <summary>把 validation／prescreen outcome 收斂為每程序的 V/na、N/A 原因與計數。</summary>
    public static AuditRunManifest Finalize(AuditExecutionPlan plan, AuditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);

        var finalized = plan.UserParameters.ActionName switch
        {
            ValidationAction => FinalizeValidation(plan, outcome),
            PrescreenAction => FinalizePrescreen(plan, outcome),
            _ => throw new InvalidOperationException(
                $"Finalize 不支援 action '{plan.UserParameters.ActionName}'。")
        };

        return new AuditRunManifest(plan, Array.AsReadOnly(finalized));
    }

    /// <summary>產生 validation／prescreen 家族的固定 IDEA-log 式中文敘述。</summary>
    public static string Explain(AuditRunManifest runManifest)
    {
        ArgumentNullException.ThrowIfNull(runManifest);

        return runManifest.Plan.UserParameters.ActionName switch
        {
            ValidationAction => ExplainValidation(runManifest),
            PrescreenAction => ExplainPrescreen(runManifest),
            _ => throw new InvalidOperationException(
                $"Explain 不支援 action '{runManifest.Plan.UserParameters.ActionName}'。")
        };
    }

    private static string ExplainValidation(AuditRunManifest runManifest)
    {
        var snapshot = runManifest.Plan.CaseSnapshot;
        var lines = new[]
        {
            $"查核期間參數：@periodStart={snapshot.PeriodStart}；@periodEnd={snapshot.PeriodEnd}。",
            ExplainVerdict(runManifest, "completeness_test"),
            ExplainVerdict(runManifest, "doc_balance_test"),
            ExplainVerdict(runManifest, "inf_sampling_test"),
            ExplainVerdict(runManifest, "null_records_test"),
            "完整性 SQL 要點：JE 僅取查核期間並依科目彙總；TB 取全部本期變動額；" +
            "以 LEFT JOIN 加 UNION ALL 模擬 FULL OUTER JOIN；差異為 TB 減 GL，並標記 GL 有而 TB 無的科目。"
        };

        return string.Join('\n', lines);
    }

    private static string ExplainPrescreen(AuditRunManifest runManifest)
    {
        var snapshot = runManifest.Plan.CaseSnapshot;
        var lines = new List<string>
        {
            $"預篩選查核期間：@periodStart={snapshot.PeriodStart}；@periodEnd={snapshot.PeriodEnd}。"
        };
        lines.AddRange(runManifest.Procedures.Select(verdict =>
            ExplainVerdict(runManifest, verdict.Definition.Slug)));
        lines.Add(
            "預篩選 SQL 要點：JE 母體限查核期間；比較值以參數綁定；非工作日採驗證後的 0–6 白名單展開；" +
            "科目組合使用 EXISTS／NOT EXISTS；週末與假日分別依非工作日與日曆判定；" +
            "低頻規則以 GROUP BY／HAVING 計數。");

        return string.Join('\n', lines);
    }

    private static IReadOnlyList<ProcedureDefinition> BuildProgram()
    {
        // 六筆 spine slug 是本輪新建的程序索引，不冒充 RuleCatalog 的規則 slug；
        // 複合步驟沒有單一 action，ActionName 刻意留 null，真正可執行的 validation leaf 另列。
        var definitions = new List<ProcedureDefinition>
        {
            new(
                "project_create", "建立案件", "建立或載入案件 metadata，確立查核期間與資料庫 provider。", 0,
                NoArtifacts, NoArtifacts, NoArtifacts, "project.create", null),
            new(
                "data_import", "匯入資料", "把受查者提供的來源檔原貌落地，不在前端或 Application 計算母體。", 1,
                NoArtifacts, NoArtifacts,
                Artifacts("staging_gl_raw_row", "staging_tb_raw_row", "target_authorized_preparer", "staging_calendar_raw_day"),
                null, null),
            new(
                "field_mapping", "欄位配對", "依已確認欄位配對把 GL/TB 原貌投影為正準測試母體。", 2,
                Artifacts("staging_gl_raw_row", "staging_tb_raw_row"), NoArtifacts,
                Artifacts("target_gl_entry", "target_tb_balance", "config_field_mapping"),
                null, null),
            new(
                "validation_and_testing", "資料驗證與測試", "執行資料驗證與預篩選，留下可回放的結果摘要。", 3,
                Artifacts("target_gl_entry"),
                Artifacts("target_tb_balance", "gl_control_total", "target_account_mapping", "target_authorized_preparer", "staging_calendar_raw_day"),
                Artifacts("result_rule_run", "result_inf_sampling_test_sample"),
                null, null),
            new(
                "advanced_filter", "進階條件篩選", "把使用者著作的條件情境編譯並物化目前 revision 的命中列。", 4,
                Artifacts("target_gl_entry"),
                Artifacts("target_account_mapping", "target_authorized_preparer", "staging_calendar_raw_day"),
                Artifacts("config_filter_scenario", "result_filter_run"),
                null, null),
            new(
                "workpaper_export", "匯出底稿", "以目前有效的驗證、預篩選與篩選結果串流產生正式底稿。", 5,
                Artifacts("result_rule_run", "config_filter_scenario", "result_filter_run"),
                Artifacts("result_inf_sampling_test_sample"), NoArtifacts,
                "export.workpaperStream", null),
            ValidationProcedures.Definition,
            ValidationDefinition(
                "doc_balance_test", "檢查查核期間內每張傳票的借貸淨額是否為零。",
                Artifacts("target_gl_entry"), NoArtifacts,
                Artifacts("result_rule_run"), ValidationProcedures.UnbalancedCore()),
            ValidationDefinition(
                "inf_sampling_test", "以固定案件種子抽取可重現的非財務欄位樣本。",
                Artifacts("target_gl_entry"), NoArtifacts,
                Artifacts("result_rule_run", "result_inf_sampling_test_sample"),
                ValidationProcedures.InfSampleInsert(string.Empty, CanonicalTemplateDialect.Instance)),
            ValidationDefinition(
                "null_records_test", "辨識關鍵欄位空白與核准日離期；空白總帳日期另列來源品質。",
                Artifacts("target_gl_entry"), NoArtifacts,
                Artifacts("result_rule_run"),
                // category 述詞由同模組 FilterCompilation 組裝；程序總表不複製 SQL 全文。
                null),
            PrescreenDefinition(
                "post_period_approval", "辨識核准日在期末財報準備日當日或之後的分錄。",
                Artifacts("config_field_mapping")),
            PrescreenDefinition(
                "suspicious_keywords", "辨識摘要含預設關鍵字的分錄。", NoArtifacts),
            PrescreenDefinition(
                "unexpected_account_pair", "辨識收入貸方缺少正常借方對方分類的分錄。",
                Artifacts("target_account_mapping")),
            PrescreenDefinition(
                "trailing_zeros", "辨識主單位整數金額具有連續零尾數的分錄。", NoArtifacts),
            PrescreenDefinition(
                "creator_summary", "依分錄建立人彙總筆數與借貸金額。",
                Artifacts("config_field_mapping")),
            PrescreenDefinition(
                "rare_accounts", "彙總使用次數較少的科目。", NoArtifacts),
            PrescreenDefinition(
                "weekend_posting", "辨識日期落在設定週末日的過帳分錄，補班日仍納入。",
                Artifacts("staging_calendar_raw_day")),
            PrescreenDefinition(
                "weekend_approval", "辨識日期落在設定週末日的核准分錄，補班日仍納入。",
                Artifacts("config_field_mapping", "staging_calendar_raw_day")),
            PrescreenDefinition(
                "holiday_posting", "辨識假日曆日期上的過帳分錄。",
                Artifacts("staging_calendar_raw_day")),
            PrescreenDefinition(
                "holiday_approval", "辨識假日曆日期上的核准分錄。",
                Artifacts("config_field_mapping", "staging_calendar_raw_day")),
            PrescreenDefinition(
                "blank_description", "辨識摘要為空白的分錄。", NoArtifacts),
            PrescreenDefinition(
                "backdated_posting", "辨識總帳日期早於傳票日期的分錄。", NoArtifacts),
            PrescreenDefinition(
                "non_authorized_preparer", "辨識建立人不在授權編製人員清單的分錄。",
                Artifacts("target_authorized_preparer")),
            PrescreenDefinition(
                "low_frequency_preparer", "辨識目前測試母體內建立人員分錄筆數為 11 筆以下的分錄；空白人員不命中。", NoArtifacts),
            PrescreenDefinition(
                "low_frequency_account", "辨識目前測試母體內科目分錄筆數為 11 筆以下的分錄；空白科目不命中。", NoArtifacts)
        };

        return Array.AsReadOnly(definitions.ToArray());
    }

    private static ProcedureVerdict PlanValidation(
        ProcedureDefinition definition,
        AuditCaseSnapshot snapshot,
        AuditUserParameters userParameters)
    {
        if (string.Equals(definition.Slug, "completeness_test", StringComparison.Ordinal))
        {
            return CompletenessPartBProcedure.Plan(new CompletenessPartBRequest(
                snapshot.HasTbMapping,
                snapshot.PeriodStart,
                snapshot.PeriodEnd)).Verdict;
        }

        if (string.Equals(definition.Slug, "inf_sampling_test", StringComparison.Ordinal))
        {
            definition = definition with
            {
                Sql = ValidationProcedures.InfSampleInsert(
                    string.Empty,
                    CanonicalTemplateDialect.Instance,
                    snapshot.SampleSeedVersion ?? InfSamplingPrf.LegacyAlgorithmVersion)
            };
        }

        return new ProcedureVerdict(
            definition,
            IsApplicable: true,
            NaReason: null,
            Parameters: ValidationParametersFor(definition.Slug, snapshot, userParameters));
    }

    private static ProcedureVerdict PlanPrescreen(
        ProcedureDefinition definition,
        AuditCaseSnapshot snapshot)
    {
        var verdict = PrescreenProcedures.Evaluate(definition, snapshot);
        return verdict with
        {
            Parameters = PrescreenParametersFor(definition.Slug, snapshot)
        };
    }

    private static IReadOnlyDictionary<string, string> ValidationParametersFor(
        string slug,
        AuditCaseSnapshot snapshot,
        AuditUserParameters userParameters)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@periodStart"] = snapshot.PeriodStart,
            ["@periodEnd"] = snapshot.PeriodEnd
        };

        if (string.Equals(slug, "inf_sampling_test", StringComparison.Ordinal))
        {
            parameters["@runId"] = userParameters.RunId;
            parameters["@seed"] = snapshot.SampleSeed.ToString(CultureInfo.InvariantCulture);
            parameters["@n"] = userParameters.SampleSize.ToString(CultureInfo.InvariantCulture);
        }

        return new ReadOnlyDictionary<string, string>(parameters);
    }

    private static IReadOnlyDictionary<string, string> PrescreenParametersFor(
        string slug,
        AuditCaseSnapshot snapshot)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@periodStart"] = snapshot.PeriodStart,
            ["@periodEnd"] = snapshot.PeriodEnd
        };

        switch (slug)
        {
            case "post_period_approval" when snapshot.LastPeriodStart is not null:
                parameters["@lastPeriodStart"] = snapshot.LastPeriodStart;
                break;
            case "trailing_zeros":
                parameters["@moneyScale"] = snapshot.MoneyScale.ToString(CultureInfo.InvariantCulture);
                parameters["@zerosThreshold"] = TrailingZeroThreshold.DefaultZerosThreshold
                    .ToString(CultureInfo.InvariantCulture);
                break;
            case "low_frequency_preparer":
                parameters["@maxEntries"] = PreparerFrequency.DefaultMaxEntries
                    .ToString(CultureInfo.InvariantCulture);
                break;
            case "low_frequency_account":
                parameters["@maxEntries"] = AccountFrequency.DefaultMaxEntries
                    .ToString(CultureInfo.InvariantCulture);
                break;
        }

        return new ReadOnlyDictionary<string, string>(parameters);
    }

    private static ProcedureVerdict[] FinalizeValidation(
        AuditExecutionPlan plan,
        AuditOutcome outcome)
    {
        var result = outcome.Validation
            ?? throw new InvalidOperationException("validation plan 未收到 ValidationRunResult。");
        var completeness = CompletenessPartBProcedure.Finalize(
            CompletenessPartBProcedure.RequirePlan(plan),
            new CompletenessPartBFacts(result.CompletenessDiffAccountCount));

        return plan.Procedures
            .Select(verdict => string.Equals(
                verdict.Definition.Slug,
                completeness.Definition.Slug,
                StringComparison.Ordinal)
                ? completeness
                : FinalizeProcedure(
                    verdict,
                    verdict.IsApplicable ? CountForValidation(verdict.Definition.Slug, result) : 0L))
            .ToArray();
    }

    private static ProcedureVerdict[] FinalizePrescreen(
        AuditExecutionPlan plan,
        AuditOutcome outcome)
    {
        var result = outcome.Prescreen
            ?? throw new InvalidOperationException("prescreen plan 未收到 PrescreenRunResult。");
        return FinalizeProcedures(plan, slug => CountForPrescreen(slug, result));
    }

    private static ProcedureVerdict[] FinalizeProcedures(
        AuditExecutionPlan plan,
        Func<string, long> countFor)
    {
        return plan.Procedures
            .Select(verdict => FinalizeProcedure(
                verdict,
                verdict.IsApplicable ? countFor(verdict.Definition.Slug) : 0L))
            .ToArray();
    }

    private static ProcedureVerdict FinalizeProcedure(ProcedureVerdict verdict, long count) =>
        verdict with
        {
            Status = count > 0 ? "V" : "na",
            Count = count
        };

    private static long CountForValidation(string slug, ValidationRunResult result) => slug switch
    {
        "doc_balance_test" => result.UnbalancedDocumentCount,
        "inf_sampling_test" => result.InfSampleCount,
        "null_records_test" => result.NullAccountCount + result.NullDocumentCount
            + result.NullDescriptionCount + result.OutOfRangeDateCount,
        _ => throw new InvalidOperationException($"Finalize 不支援未登錄的驗證程序 '{slug}'。")
    };

    private static long CountForPrescreen(string slug, PrescreenRunResult result) => slug switch
    {
        "post_period_approval" => result.PostPeriodApprovalCount,
        "suspicious_keywords" => result.SuspiciousKeywordsCount,
        "unexpected_account_pair" => result.UnexpectedAccountPairCount,
        "trailing_zeros" => result.TrailingZerosCount,
        "creator_summary" => result.Creators.Count,
        "rare_accounts" => result.DistinctAccountCount,
        "weekend_posting" => result.WeekendPostingCount,
        "weekend_approval" => result.WeekendApprovalCount ?? 0L,
        "holiday_posting" => result.HolidayPostingCount,
        "holiday_approval" => result.HolidayApprovalCount ?? 0L,
        "blank_description" => result.BlankDescriptionCount,
        "backdated_posting" => result.BackdatedPostingCount,
        "non_authorized_preparer" => result.NonAuthorizedPreparerCount,
        "low_frequency_preparer" => result.LowFrequencyPreparerCount,
        "low_frequency_account" => result.LowFrequencyAccountCount,
        _ => throw new InvalidOperationException($"Finalize 不支援未登錄的預篩選程序 '{slug}'。")
    };

    private static string ExplainVerdict(AuditRunManifest manifest, string slug)
    {
        var verdict = manifest.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, slug, StringComparison.Ordinal));
        var status = verdict.Status
            ?? throw new InvalidOperationException($"程序 '{slug}' 尚未 Finalize。");
        var count = verdict.Count
            ?? throw new InvalidOperationException($"程序 '{slug}' 尚未產生計數。");
        var countLabel = slug switch
        {
            "null_records_test" => "子計數合計",
            "creator_summary" => "彙總列數",
            "rare_accounts" => "科目數",
            _ => "計數"
        };
        var reason = verdict.NaReason ?? ZeroResultExplanation(slug, status);

        var sentenceEnd = reason.EndsWith('。') ? string.Empty : "。";
        return $"{verdict.Definition.DisplayName}（{slug}）：{status}；{countLabel} {count}；N/A 原因：{reason}{sentenceEnd}";
    }

    private static string ZeroResultExplanation(string slug, string status)
    {
        if (!string.Equals(status, "na", StringComparison.Ordinal))
        {
            return "無";
        }

        return slug switch
        {
            "completeness_test" => "無（已執行但未發現差異）",
            "doc_balance_test" => "無（已執行但未發現不平傳票）",
            _ => "無（已執行但未命中）"
        };
    }

    private static ProcedureDefinition ValidationDefinition(
        string slug,
        string purpose,
        IReadOnlyList<string> requiredInputs,
        IReadOnlyList<string> softInputs,
        IReadOnlyList<string> outputs,
        string? sql)
    {
        var rule = RuleCatalog.All.Single(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        return new ProcedureDefinition(
            rule.Slug, rule.DisplayName, purpose, 3,
            requiredInputs, softInputs, outputs, ValidationAction, sql);
    }

    private static ProcedureDefinition PrescreenDefinition(
        string slug,
        string purpose,
        IReadOnlyList<string> softInputs)
    {
        var rule = RuleCatalog.All.Single(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        return new ProcedureDefinition(
            rule.Slug,
            rule.DisplayName,
            purpose,
            WorkflowStep: 3,
            Artifacts("target_gl_entry"),
            softInputs,
            Artifacts("result_rule_run"),
            PrescreenAction,
            Sql: null);
    }

    private static IReadOnlyList<string> Artifacts(params string[] physicalNames) =>
        Array.AsReadOnly(physicalNames.Select(Canonical).ToArray());

    private static string Canonical(string physicalName) =>
        JetSchemaCatalog.ResolveCanonical(physicalName)
        ?? throw new InvalidOperationException($"JetSchemaCatalog 未登錄 '{physicalName}'。");

    private static IReadOnlyDictionary<string, string> EmptyParameters() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>
    /// INF 定義保存 provider 中立 template；真正 LimitClause 仍由 Infrastructure dialect 決定。
    /// 其他成員不會在建立 template 時被呼叫，若誤用即明確失敗。
    /// </summary>
    private sealed class CanonicalTemplateDialect : ISqlDialect
    {
        public static readonly CanonicalTemplateDialect Instance = new();

        public string ParameterName(int index) => throw Unsupported();

        public string WeekendPredicate(string dateExpr, IReadOnlyCollection<int> nonWorkingDays) => throw Unsupported();

        public string ContainsIgnoreCase(string columnExpr, string parameterName) => throw Unsupported();

        public string IntegerQuotient(string dividendExpression, string divisorExpression) =>
            $"{{dialect integer-quotient ({dividendExpression}) / ({divisorExpression})}}";

        public string DayOfMonth(string dateExpr) => throw Unsupported();
        public string DaysInMonth(string dateExpr) => throw Unsupported();

        public string InfSampleOrderingKey(string sourceRowNumberExpression, string seedExpression) =>
            InfSamplingPrf.SqlOrderingKey(this, sourceRowNumberExpression, seedExpression);

        public string LimitClause(string parameterName) => $"{{dialect limit {parameterName}}}";

        public string ListTablesSql => throw Unsupported();

        public string EngineVersionSql => throw Unsupported();

        private static NotSupportedException Unsupported() =>
            new("CanonicalTemplateDialect 只供 ValidationProcedures.InfSampleInsert 建立 SQL template。");
    }
}
