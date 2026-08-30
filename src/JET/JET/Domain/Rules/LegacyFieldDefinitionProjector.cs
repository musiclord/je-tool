namespace JET.Domain;

/// <summary>
/// 將 import source scope 轉成 Legacy post-mapping TableDef。它只依 persisted source metadata、
/// committed mapping 所用的 spec 與 Domain 欄位目錄運作；不掃 staging/target rows，也不依 provider DDL 猜型別。
/// </summary>
internal static class LegacyFieldDefinitionProjector
{
    internal static IReadOnlyList<LegacyFieldDefinitionState> ProjectGl(
        IReadOnlyList<LegacyFieldDefinitionState> source,
        GlMappingSpec spec,
        int moneyScale)
    {
        // 本階段不替舊 schema／舊批次回推 metadata；既有 project 的資料投影仍可照舊完成，
        // 但 target facts 保持空白，直到使用者以 replace 建立本版的新批次。
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
            if (TryMappedSource(spec.Mapping, slot.Key, out var sourceName))
            {
                if (field.SemanticIdentity is JetFieldCatalog.GlDocNum or JetFieldCatalog.GlAccNum)
                {
                    // Legacy 對傳票號碼／科目編號有明示 Num→Char 轉換；這是普通 rename
                    // 保留來源型態規則的兩個例外。Numeric source 會保留 _Temp shadow，
                    // canonical text field 則以 equation append；文字來源仍只在原位改名。
                    projection.RenameAsText(sourceName, field.LegacyFieldName);
                }
                else if (field.SemanticIdentity == JetFieldCatalog.GlManual)
                {
                    // manual 的 actual target 已由 GlRowProjector 正規化為 0/1；Legacy TableDef
                    // 描述的是 target 值，不得沿用來源的 y/yes/no 文字型態。
                    projection.RenameAsNumber(
                        sourceName,
                        field.LegacyFieldName,
                        decimalPlaces: 0);
                }
                else
                {
                    projection.RenamePreservingSource(sourceName, field.LegacyFieldName);
                }
            }
            else if (field.SemanticIdentity == JetFieldCatalog.GlDocDate
                     && spec.Options.ApprovalDateMode == ApprovalDateModeNames.SameAsPostDate)
            {
                projection.AppendDerived(
                    field.LegacyFieldName,
                    LegacyFieldKind.Date,
                    textLength: 0,
                    decimalPlaces: null,
                    description: $"{field.LegacyFieldName} 由系統產生 : 同總帳日期_JE來源");
            }
            else if (field.SemanticIdentity == JetFieldCatalog.GlLineId)
            {
                // 現行 target 在未配 lineID 時同交易以 ROW_NUMBER 產生整數序號。
                projection.AppendDerived(
                    field.LegacyFieldName,
                    LegacyFieldKind.Number,
                    textLength: 0,
                    decimalPlaces: 0);
            }
        }

        var amountField = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlAmount);
        // 較新的 idea-tool.bas 對 Signed／Side／Dual 全部保留來源 operand，另 append
        // 統一 signed amount；JET 的第四種 Flag 與 Side 共用同一轉換形狀。
        projection.AppendDerived(
            amountField.LegacyFieldName!,
            LegacyFieldKind.Number,
            textLength: 0,
            decimalPlaces: DecimalPlacesForScale(moneyScale),
            description: GlAmountDescription(spec));

        return projection.Build();
    }

    internal static IReadOnlyList<LegacyFieldDefinitionState> ProjectTb(
        IReadOnlyList<LegacyFieldDefinitionState> source,
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
                if (field.SemanticIdentity == JetFieldCatalog.TbAccNum)
                {
                    projection.RenameAsText(sourceName, field.LegacyFieldName);
                }
                else
                {
                    projection.RenamePreservingSource(sourceName, field.LegacyFieldName);
                }
            }
        }

        var amountField = JetFieldCatalog.TbFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.TbChangeAmount);
        if (spec.ChangeMode == TbChangeMode.DirectChange
            && TryMappedSource(spec.Mapping, JetFieldCatalog.TbAmount, out var amountSource))
        {
            projection.RenamePreservingSource(amountSource, amountField.LegacyFieldName!);
        }
        else
        {
            projection.AppendDerived(
                amountField.LegacyFieldName!,
                LegacyFieldKind.Number,
                textLength: 0,
                decimalPlaces: DecimalPlacesForScale(moneyScale),
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

    private static string RequiredMappedSource(IReadOnlyDictionary<string, string> mapping, string key) =>
        TryMappedSource(mapping, key, out var sourceName) ? sourceName : key;

    private static string GlAmountDescription(GlMappingSpec spec)
    {
        var target = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlAmount).LegacyFieldName!;

        return spec.AmountMode switch
        {
            GlAmountMode.SignedAmount =>
                $"{target} 由系統產生 : {RequiredMappedSource(spec.Mapping, GlMappingKeys.Amount)}",
            GlAmountMode.DualAmount =>
                $"{target} 由系統產生 : {RequiredMappedSource(spec.Mapping, GlMappingKeys.DebitAmount)}-{RequiredMappedSource(spec.Mapping, GlMappingKeys.CreditAmount)}",
            GlAmountMode.AmountWithSide or GlAmountMode.AmountWithFlag =>
                $"{target} 由系統產生 : 金額欄位【{RequiredMappedSource(spec.Mapping, GlMappingKeys.Amount)}】、" +
                $"借貸方判斷欄位【{RequiredMappedSource(spec.Mapping, GlMappingKeys.DcField)}】，" +
                $"借方為【{RequiredMappedSource(spec.Mapping, GlMappingKeys.DcDebitCode)}】",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.AmountMode, null)
        };
    }

    private static string TbAmountDescription(TbMappingSpec spec)
    {
        var target = JetFieldCatalog.TbFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.TbChangeAmount).LegacyFieldName!;

        return spec.ChangeMode switch
        {
            TbChangeMode.DebitCredit =>
                $"{target} 由系統產生 : {RequiredMappedSource(spec.Mapping, TbMappingKeys.DebitAmt)} - {RequiredMappedSource(spec.Mapping, TbMappingKeys.CreditAmt)}",
            TbChangeMode.OpenClose =>
                $"{target} 由系統產生 : {RequiredMappedSource(spec.Mapping, TbMappingKeys.ClosingBalance)} - {RequiredMappedSource(spec.Mapping, TbMappingKeys.OpeningBalance)}",
            TbChangeMode.OpenCloseBySide =>
                $"{target} 由系統產生 : ({RequiredMappedSource(spec.Mapping, TbMappingKeys.ClosingDebit)} - {RequiredMappedSource(spec.Mapping, TbMappingKeys.ClosingCredit)}) - " +
                $"({RequiredMappedSource(spec.Mapping, TbMappingKeys.OpeningDebit)}-{RequiredMappedSource(spec.Mapping, TbMappingKeys.OpeningCredit)})",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.ChangeMode, null)
        };
    }

    private static int DecimalPlacesForScale(int moneyScale)
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
        private readonly List<LegacyFieldDefinitionState> _definitions;
        private readonly Dictionary<string, int> _sourceIndexes;
        private readonly HashSet<int> _claimedIndexes = [];

        internal MutableProjection(IReadOnlyList<LegacyFieldDefinitionState> source)
        {
            _definitions = source.OrderBy(static definition => definition.Ordinal).ToList();
            _sourceIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < _definitions.Count; index++)
            {
                _sourceIndexes.TryAdd(_definitions[index].FieldName, index);
            }
        }

        internal void RenamePreservingSource(string sourceName, string targetName)
        {
            var index = ClaimSourceIndex(sourceName);
            var source = _definitions[index];
            _definitions[index] = source with
            {
                FieldName = targetName,
                Description = sourceName
            };
        }

        internal void RenameAsText(string sourceName, string targetName)
        {
            var index = ClaimSourceIndex(sourceName);
            var source = _definitions[index];

            if (source.Kind == LegacyFieldKind.Number)
            {
                var textProjection = source with
                {
                    Description = sourceName,
                    Kind = LegacyFieldKind.Text,
                    TextLength = source.MaxRenderedLength,
                    DecimalPlaces = null
                };
                _definitions[index] = textProjection with
                {
                    FieldName = $"{targetName}_Temp"
                };
                _definitions.Add(textProjection with
                {
                    Ordinal = _definitions.Count + 1,
                    FieldName = targetName
                });
                return;
            }

            _definitions[index] = source with
            {
                FieldName = targetName,
                Description = sourceName,
                Kind = LegacyFieldKind.Text,
                TextLength = source.MaxRenderedLength,
                DecimalPlaces = null
            };
        }

        internal void RenameAsNumber(
            string sourceName,
            string targetName,
            int decimalPlaces)
        {
            var index = ClaimSourceIndex(sourceName);
            var source = _definitions[index];
            _definitions[index] = source with
            {
                FieldName = targetName,
                Description = sourceName,
                Kind = LegacyFieldKind.Number,
                TextLength = 0,
                DecimalPlaces = Math.Max(0, decimalPlaces)
            };
        }

        private int ClaimSourceIndex(string sourceName)
        {
            if (!_sourceIndexes.TryGetValue(sourceName, out var index))
            {
                throw new InvalidOperationException($"來源欄位定義 '{sourceName}' 不存在。");
            }

            if (!_claimedIndexes.Add(index))
            {
                throw new InvalidOperationException($"來源欄位定義 '{sourceName}' 被重複配對。");
            }

            return index;
        }

        internal void AppendDerived(
            string fieldName,
            LegacyFieldKind kind,
            int textLength,
            int? decimalPlaces,
            string? description = null)
        {
            _definitions.Add(new LegacyFieldDefinitionState(
                _definitions.Count + 1,
                fieldName,
                description,
                kind,
                kind == LegacyFieldKind.Text ? textLength : 0,
                kind == LegacyFieldKind.Number ? decimalPlaces : null,
                Math.Max(0, textLength),
                HasObservation: true));
        }

        internal IReadOnlyList<LegacyFieldDefinitionState> Build() =>
            _definitions
                .Select(static (definition, index) => definition with { Ordinal = index + 1 })
                .ToArray();
    }
}
