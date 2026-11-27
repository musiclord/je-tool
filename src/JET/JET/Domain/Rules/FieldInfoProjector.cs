namespace JET.Domain;

/// <summary>
/// 把匯入時記下的來源欄位定義，投影成配對後的欄位定義，內容照舊版配對後的欄位定義。
/// 只依匯入時保存的來源欄位定義、確認配對用的 spec 與欄位目錄運作；不掃資料列，也不依資料庫 DDL 猜型別。
/// 每一欄都記下來歷（<see cref="FieldInfoOrigin"/>）與對應的 JET 欄位，底稿與報告照這兩項判斷，
/// 不從欄名結尾推斷。
/// </summary>
internal static class FieldInfoProjector
{
    /// <summary>數字型傳票號碼與科目編號轉成文字前，原欄照舊版的叫法加這個後綴。</summary>
    internal const string TempSuffix = "_Temp";

    /// <summary>來源欄本來就叫 JET 輸出欄名時，原始來源欄讓名加上的字樣。</summary>
    internal const string SourceNameSuffix = "（來源）";

    internal static IReadOnlyList<FieldInfoState> ProjectGl(
        IReadOnlyList<FieldInfoState> source,
        GlMappingSpec spec,
        int moneyScale)
    {
        // 沒有來源欄位定義的批次不回推；配對後的欄位定義留空，直到使用者重新匯入建立新批次。
        if (source.Count == 0)
        {
            return [];
        }

        var projection = new MutableProjection(source);

        foreach (var field in JetFieldCatalog.GlFields)
        {
            if (field.SemanticIdentity == JetFieldCatalog.GlAmount || field.LegacyFieldName is null)
            {
                continue;
            }

            var slot = field.MappingSlots[0];
            if (field.SemanticIdentity == JetFieldCatalog.GlPostingStatus)
            {
                // 舊版配對了過帳狀態時，來源欄留在原位，另外照來源欄複製出文字欄「過帳碼_JE」，
                // 說明寫來源欄與過帳判斷值。
                if (TryMappedSource(spec.Mapping, slot.Key, out var postingSource))
                {
                    projection.AppendTextCopy(
                        postingSource,
                        field.LegacyFieldName,
                        PostingCodeDescription(postingSource, spec.Options.PostingStatusPolicy),
                        field.SemanticIdentity);
                }

                continue;
            }

            if (TryMappedSource(spec.Mapping, slot.Key, out var sourceName))
            {
                if (field.SemanticIdentity is JetFieldCatalog.GlVoucherNumber or JetFieldCatalog.GlAccountNumber)
                {
                    // 舊版對傳票號碼與科目編號明示數字轉文字：數字型來源欄留下 _Temp 原欄，
                    // 另外追加轉成文字的欄位；文字型來源欄只在原位置改名。
                    projection.RenameAsText(sourceName, field.LegacyFieldName, field.SemanticIdentity);
                }
                else if (field.SemanticIdentity == JetFieldCatalog.GlManual)
                {
                    // 人工/自動的值已由投影器正規化成 0/1；欄位定義描述的是整理後的值，不沿用來源的 y/yes/no 文字型態。
                    projection.RenameAsNumber(
                        sourceName,
                        field.LegacyFieldName,
                        field.SemanticIdentity,
                        decimalPlaces: 0);
                }
                else
                {
                    projection.RenamePreservingSource(sourceName, field.LegacyFieldName, field.SemanticIdentity);
                }
            }
            else if (field.SemanticIdentity == JetFieldCatalog.GlApprovalDate
                     && spec.Options.ApprovalDateMode == ApprovalDateModeNames.SameAsGlDate)
            {
                projection.AppendDerived(
                    field.LegacyFieldName,
                    LegacyFieldKind.Date,
                    textLength: 0,
                    decimalPlaces: null,
                    field.SemanticIdentity,
                    description: ApprovalSameAsGlDateDescription());
            }
            else if (field.SemanticIdentity == JetFieldCatalog.GlLineItem)
            {
                // 沒有配對傳票文件項次時，同一張傳票的分錄依列序自動編成整數序號。
                projection.AppendDerived(
                    field.LegacyFieldName,
                    LegacyFieldKind.Number,
                    textLength: 0,
                    decimalPlaces: 0,
                    field.SemanticIdentity);
            }
        }

        // 攸關資料元素照舊版「自行定義欄位」在原位置改名加 _JE，型態照使用者選的值型別。
        var rdeNames = GlRdeOutputNames.Resolve(spec.Options.RdeFields);
        foreach (var rde in spec.Options.RdeFields)
        {
            projection.RenameRde(rde, rdeNames[rde.FieldId], moneyScale);
        }

        var amountField = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlAmount);
        // 舊版 JET 的較新版本對單一帶號、金額與借貸別、借貸兩欄三種方式都保留來源欄，另外追加統一的帶號金額；
        // JET 的第四種（金額與旗標）和金額與借貸別共用同一個形狀。
        projection.AppendDerived(
            amountField.LegacyFieldName!,
            LegacyFieldKind.Number,
            textLength: 0,
            decimalPlaces: DecimalPlacesForScale(moneyScale),
            amountField.SemanticIdentity,
            description: GlAmountDescription(spec));

        return projection.Build();
    }

    internal static IReadOnlyList<FieldInfoState> ProjectTb(
        IReadOnlyList<FieldInfoState> source,
        TbMappingSpec spec,
        int moneyScale)
    {
        if (source.Count == 0)
        {
            return [];
        }

        var projection = new MutableProjection(source);

        foreach (var field in JetFieldCatalog.TbFields)
        {
            if (field.SemanticIdentity == JetFieldCatalog.TbChangeAmount || field.LegacyFieldName is null)
            {
                continue;
            }

            var slot = field.MappingSlots[0];
            if (TryMappedSource(spec.Mapping, slot.Key, out var sourceName))
            {
                if (field.SemanticIdentity == JetFieldCatalog.TbAccountNumber)
                {
                    projection.RenameAsText(sourceName, field.LegacyFieldName, field.SemanticIdentity);
                }
                else
                {
                    projection.RenamePreservingSource(sourceName, field.LegacyFieldName, field.SemanticIdentity);
                }
            }
        }

        var amountField = JetFieldCatalog.TbFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.TbChangeAmount);
        if (spec.ChangeMode == TbChangeMode.DirectChange
            && TryMappedSource(spec.Mapping, JetFieldCatalog.TbChangeAmount, out var amountSource))
        {
            projection.RenamePreservingSource(amountSource, amountField.LegacyFieldName!, amountField.SemanticIdentity);
        }
        else
        {
            projection.AppendDerived(
                amountField.LegacyFieldName!,
                LegacyFieldKind.Number,
                textLength: 0,
                decimalPlaces: DecimalPlacesForScale(moneyScale),
                amountField.SemanticIdentity,
                description: TbAmountDescription(spec));
        }

        return projection.Build();
    }

    private static bool TryMappedSource(
        IReadOnlyDictionary<string, string> mapping,
        string key,
        out string sourceName)
    {
        if (mapping.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            sourceName = value;
            return true;
        }

        sourceName = string.Empty;
        return false;
    }

    // 說明句裡的欄位名稱照來源檔的欄名原樣寫，前後不加任何符號，和前後的文字用半形空白分開；
    // 借方代碼與過帳判斷值這類值不是欄位，照條件說明的寫法用「」。兩欄相減的減號前後空不空格照舊版：
    // GL 借貸兩欄寫「借方欄-貸方欄」；TB 借減貸與期末減期初寫「甲 - 乙」；期末與期初各分借貸時，期初那一組寫「期初借-期初貸」。
    // 欄名本身有連字號時，從底稿帶回配對要靠欄位資訊表列出的來源欄確認切法。
    // 改名欄位的說明欄存的是來源欄名本身，底稿 step4-1 符合高風險條件傳票明細拿它當讀原始資料的欄名，欄位資訊也照這個名稱顯示。
    private static string SourceName(IReadOnlyDictionary<string, string> mapping, string key) =>
        TryMappedSource(mapping, key, out var sourceName) ? sourceName : key;

    private static string GlLegacyName(string semanticIdentity) =>
        JetFieldCatalog.GlFields.Single(field => field.SemanticIdentity == semanticIdentity).LegacyFieldName!;

    /// <summary>
    /// 系統產生欄位的說明句，只由確認的配對決定。案件資料庫存的是確認配對當時寫下的句子；
    /// 讀出欄位定義時依同一份配對重新產生，句子的寫法改變後，已經建立的案件不必重新確認配對。
    /// 改名欄位的說明是來源欄名本身，不在這裡改；沒有說明句的系統產生欄位（自動編號的傳票文件項次）不寫說明。
    /// </summary>
    internal static IReadOnlyList<FieldInfoState> WithCurrentDescriptions(
        IReadOnlyList<FieldInfoState> target,
        GlMappingSpec spec) =>
        target
            .Select(definition => definition is { Origin: FieldInfoOrigin.Generated, JetField: { } jetField }
                                  && GeneratedGlDescription(jetField, spec) is { } description
                ? definition with { Description = description }
                : definition)
            .ToArray();

    /// <summary>TB 只有年度變動金額由系統產生；直接變動金額是在原位置改名的來源欄，沒有說明句。</summary>
    internal static IReadOnlyList<FieldInfoState> WithCurrentDescriptions(
        IReadOnlyList<FieldInfoState> target,
        TbMappingSpec spec) =>
        spec.ChangeMode == TbChangeMode.DirectChange
            ? target
            : target
                .Select(definition => definition is { Origin: FieldInfoOrigin.Generated, JetField: JetFieldCatalog.TbChangeAmount }
                    ? definition with { Description = TbAmountDescription(spec) }
                    : definition)
                .ToArray();

    private static string? GeneratedGlDescription(string jetField, GlMappingSpec spec) => jetField switch
    {
        JetFieldCatalog.GlAmount => GlAmountDescription(spec),
        JetFieldCatalog.GlApprovalDate => ApprovalSameAsGlDateDescription(),
        JetFieldCatalog.GlPostingStatus when TryMappedSource(spec.Mapping, GlMappingKeys.PostingStatus, out var sourceName) =>
            PostingCodeDescription(sourceName, spec.Options.PostingStatusPolicy),
        _ => null
    };

    /// <summary>
    /// 傳票核准日選「與總帳日期相同」時追加的欄位，說明句寫兩個 JET 欄名；「同總帳日期_JE來源」照舊版連著寫，不空格。
    /// </summary>
    private static string ApprovalSameAsGlDateDescription() =>
        $"{GlLegacyName(JetFieldCatalog.GlApprovalDate)} 由系統產生 : 同{GlLegacyName(JetFieldCatalog.GlDate)}來源";

    /// <summary>
    /// 過帳碼_JE 的說明照舊版寫來源欄與過帳判斷值，值用「」。
    /// 使用者勾了空白也算已過帳時，在值後面寫明。
    /// </summary>
    private static string PostingCodeDescription(string sourceName, GlPostingStatusPolicy? policy)
    {
        var values = policy is null
            ? string.Empty
            : string.Concat(policy.AcceptedValues.Select(static value => $"「{value}」"));
        var blank = policy is { IncludeBlank: true } ? "，空白也算已過帳" : string.Empty;
        return $"{sourceName} 過帳判斷值{values}{blank}";
    }

    private static string GlAmountDescription(GlMappingSpec spec)
    {
        var target = GlLegacyName(JetFieldCatalog.GlAmount);

        return spec.AmountMode switch
        {
            GlAmountMode.SignedAmount =>
                $"{target} 由系統產生 : {SourceName(spec.Mapping, GlMappingKeys.Amount)}",
            GlAmountMode.DualAmount =>
                $"{target} 由系統產生 : {SourceName(spec.Mapping, GlMappingKeys.DebitAmount)}-{SourceName(spec.Mapping, GlMappingKeys.CreditAmount)}",
            GlAmountMode.AmountWithSide or GlAmountMode.AmountWithFlag =>
                $"{target} 由系統產生 : 金額欄位 {SourceName(spec.Mapping, GlMappingKeys.Amount)}、" +
                $"借貸方判斷欄位 {SourceName(spec.Mapping, GlMappingKeys.DrCr)}，" +
                DrCrCodeText(spec.Mapping),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.AmountMode, null)
        };
    }

    /// <summary>
    /// 舊版只寫借方代碼。
    /// 新版完整寫出已填的兩側代碼，只有一側時只寫該側，讓看得見的表也能帶回相同設定。
    /// </summary>
    private static string DrCrCodeText(IReadOnlyDictionary<string, string> mapping)
    {
        var codes = new List<string>(2);
        if (TryMappedSource(mapping, GlMappingKeys.DrCrDebitCode, out var debitCode))
        {
            codes.Add($"借方為「{debitCode}」");
        }
        if (TryMappedSource(mapping, GlMappingKeys.DrCrCreditCode, out var creditCode))
        {
            codes.Add($"貸方為「{creditCode}」");
        }
        return string.Join("，", codes);
    }

    private static string TbAmountDescription(TbMappingSpec spec)
    {
        var target = JetFieldCatalog.TbFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.TbChangeAmount).LegacyFieldName!;

        return spec.ChangeMode switch
        {
            TbChangeMode.DebitCredit =>
                $"{target} 由系統產生 : {SourceName(spec.Mapping, TbMappingKeys.DebitAmount)} - {SourceName(spec.Mapping, TbMappingKeys.CreditAmount)}",
            TbChangeMode.OpenClose =>
                $"{target} 由系統產生 : {SourceName(spec.Mapping, TbMappingKeys.ClosingBalance)} - {SourceName(spec.Mapping, TbMappingKeys.OpeningBalance)}",
            TbChangeMode.OpenCloseBySide =>
                $"{target} 由系統產生 : ({SourceName(spec.Mapping, TbMappingKeys.ClosingDebitBalance)} - {SourceName(spec.Mapping, TbMappingKeys.ClosingCreditBalance)}) - " +
                $"({SourceName(spec.Mapping, TbMappingKeys.OpeningDebitBalance)}-{SourceName(spec.Mapping, TbMappingKeys.OpeningCreditBalance)})",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.ChangeMode, null)
        };
    }

    internal static int DecimalPlacesForScale(int moneyScale)
    {
        if (moneyScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moneyScale), moneyScale, "MoneyScale 必須為正整數。");
        }

        var remainder = moneyScale;
        var decimals = 0;
        while (remainder > 1 && remainder % 10 == 0)
        {
            remainder /= 10;
            decimals++;
        }

        if (remainder != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moneyScale),
                moneyScale,
                "Legacy 欄位小數位只支援 10 的整數次方 MoneyScale。");
        }

        return decimals;
    }

    private sealed class MutableProjection
    {
        private readonly List<FieldInfoState> _definitions;
        // 來源欄原本的定義。同一個來源欄配給第二個欄位時，從這裡複製，不沿用第一個欄位改過的名稱與型態。
        private readonly FieldInfoState[] _sources;
        private readonly Dictionary<string, int> _sourceIndexes;
        private readonly HashSet<int> _claimedIndexes = [];

        internal MutableProjection(IReadOnlyList<FieldInfoState> source)
        {
            _definitions = source
                .OrderBy(static definition => definition.Ordinal)
                .Select(static definition => definition with { Origin = FieldInfoOrigin.Source, JetField = null })
                .ToList();
            _sources = [.. _definitions];
            _sourceIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < _definitions.Count; index++)
            {
                _sourceIndexes.TryAdd(_definitions[index].FieldName, index);
            }
        }

        internal void RenamePreservingSource(string sourceName, string targetName, string jetField)
        {
            var (index, shared) = ClaimSource(sourceName);
            Place(index, shared, _sources[index] with
            {
                FieldName = targetName,
                Description = sourceName,
                Origin = FieldInfoOrigin.Renamed,
                JetField = jetField
            });
        }

        internal void RenameAsText(string sourceName, string targetName, string jetField)
        {
            var (index, shared) = ClaimSource(sourceName);
            var source = _sources[index];

            if (source.Kind == LegacyFieldKind.Number)
            {
                var textProjection = source with
                {
                    Description = sourceName,
                    Kind = LegacyFieldKind.Text,
                    TextLength = source.MaxRenderedLength,
                    DecimalPlaces = null,
                    JetField = jetField
                };
                // 來源欄已經改成別的欄位名時，原位置不再留 _Temp，只追加轉成文字的欄位。
                if (!shared)
                {
                    _definitions[index] = textProjection with
                    {
                        FieldName = targetName + TempSuffix,
                        Origin = FieldInfoOrigin.Temp
                    };
                }

                Append(textProjection with { FieldName = targetName, Origin = FieldInfoOrigin.Renamed });
                return;
            }

            Place(index, shared, source with
            {
                FieldName = targetName,
                Description = sourceName,
                Kind = LegacyFieldKind.Text,
                TextLength = source.MaxRenderedLength,
                DecimalPlaces = null,
                Origin = FieldInfoOrigin.Renamed,
                JetField = jetField
            });
        }

        internal void RenameAsNumber(
            string sourceName,
            string targetName,
            string jetField,
            int decimalPlaces)
        {
            var (index, shared) = ClaimSource(sourceName);
            Place(index, shared, _sources[index] with
            {
                FieldName = targetName,
                Description = sourceName,
                Kind = LegacyFieldKind.Number,
                TextLength = 0,
                DecimalPlaces = Math.Max(0, decimalPlaces),
                Origin = FieldInfoOrigin.Renamed,
                JetField = jetField
            });
        }

        /// <summary>攸關資料元素在原位置改名，型態照使用者選的值型別，不沿用來源觀察到的型態。</summary>
        internal void RenameRde(GlRdeFieldMetadata rde, string targetName, int moneyScale)
        {
            var (index, shared) = ClaimSource(rde.SourceColumn);
            var source = _sources[index];
            var renamed = rde.ValueType switch
            {
                RdeFieldValueTypeNames.Date => source with
                {
                    Kind = LegacyFieldKind.Date,
                    TextLength = 0,
                    DecimalPlaces = null
                },
                RdeFieldValueTypeNames.Amount => source with
                {
                    Kind = LegacyFieldKind.Number,
                    TextLength = 0,
                    DecimalPlaces = DecimalPlacesForScale(moneyScale)
                },
                _ => source with
                {
                    Kind = LegacyFieldKind.Text,
                    TextLength = source.MaxRenderedLength,
                    DecimalPlaces = null
                }
            };
            Place(index, shared, renamed with
            {
                FieldName = targetName,
                Description = rde.SourceColumn,
                Origin = FieldInfoOrigin.Renamed,
                JetField = rde.FieldId,
                HasObservation = true
            });
        }

        // 使用者可以把同一個來源欄配給多個欄位，例如總帳日期與傳票核准日都選同一欄（預設使用者可信，不擋）。
        // 依欄位目錄順序，第一個欄位在來源欄原位置改名，之後的欄位追加在後面。
        private (int Index, bool Shared) ClaimSource(string sourceName)
        {
            if (!_sourceIndexes.TryGetValue(sourceName, out var index))
            {
                throw new InvalidOperationException($"來源欄位定義 '{sourceName}' 不存在。");
            }

            return (index, !_claimedIndexes.Add(index));
        }

        private void Place(int index, bool shared, FieldInfoState definition)
        {
            if (shared)
            {
                Append(definition);
                return;
            }

            _definitions[index] = definition;
        }

        private void Append(FieldInfoState definition) =>
            _definitions.Add(definition with { Ordinal = _definitions.Count + 1 });

        internal void AppendDerived(
            string fieldName,
            LegacyFieldKind kind,
            int textLength,
            int? decimalPlaces,
            string jetField,
            string? description = null)
        {
            _definitions.Add(new FieldInfoState(
                _definitions.Count + 1,
                fieldName,
                description,
                kind,
                kind == LegacyFieldKind.Text ? textLength : 0,
                kind == LegacyFieldKind.Number ? decimalPlaces : null,
                Math.Max(0, textLength),
                HasObservation: true,
                FieldInfoOrigin.Generated,
                jetField));
        }

        /// <summary>照來源欄複製出一個文字欄，來源欄留在原位；長度照來源欄實際出現過的最長值。</summary>
        internal void AppendTextCopy(string sourceName, string fieldName, string description, string jetField)
        {
            if (!_sourceIndexes.TryGetValue(sourceName, out var index))
            {
                throw new InvalidOperationException($"來源欄位定義 '{sourceName}' 不存在。");
            }

            var source = _sources[index];
            AppendDerived(
                fieldName,
                LegacyFieldKind.Text,
                textLength: source.MaxRenderedLength,
                decimalPlaces: null,
                jetField,
                description);
        }

        /// <summary>
        /// 來源檔本來就有 JET 輸出欄名時，原始來源欄讓名成「原名（來源）」，說明欄寫原名；
        /// 原名仍是讀原始資料列的鍵。沒有配對的來源欄不會因為欄名結尾被當成 JET 欄位，所以讓名只為了讓名稱不重複。
        /// </summary>
        private void YieldCollidingSourceNames()
        {
            var taken = _definitions
                .Where(static definition => definition.Origin != FieldInfoOrigin.Source)
                .Select(static definition => definition.FieldName)
                .ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < _definitions.Count; index++)
            {
                var definition = _definitions[index];
                if (definition.Origin != FieldInfoOrigin.Source || !taken.Contains(definition.FieldName))
                {
                    continue;
                }

                var candidate = definition.FieldName + SourceNameSuffix;
                while (taken.Contains(candidate) || _sourceIndexes.ContainsKey(candidate))
                {
                    candidate += SourceNameSuffix;
                }

                taken.Add(candidate);
                _definitions[index] = definition with
                {
                    FieldName = candidate,
                    Description = definition.FieldName
                };
            }
        }

        internal IReadOnlyList<FieldInfoState> Build()
        {
            YieldCollidingSourceNames();
            return _definitions
                .Select(static (definition, index) => definition with { Ordinal = index + 1 })
                .ToArray();
        }
    }
}
