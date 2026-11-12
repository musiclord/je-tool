namespace JET.Domain;

/// <summary>
/// 欄位資訊裡每一欄的來歷。底稿與報告看這個欄位判斷哪一欄是 JET 欄位，
/// 不看欄名結尾是不是 _JE、_TB，也不看說明文字是否空白。
/// </summary>
internal enum FieldInfoOrigin
{
    /// <summary>原始來源欄，沒有配對。欄名撞到 JET 欄名時讓名成「原名（來源）」，說明欄寫原名。</summary>
    Source,

    /// <summary>配對後在原位置改名的來源欄，說明欄寫來源欄名；攸關資料元素也算這一類。</summary>
    Renamed,

    /// <summary>系統產生的欄，例如傳票金額_JE、同總帳日期的傳票核准日_JE、自動編號的傳票文件項次與過帳碼_JE。</summary>
    Generated,

    /// <summary>數字型傳票號碼或科目編號轉成文字前的原欄，照 IDEA 的叫法加 _Temp。</summary>
    Temp
}

/// <summary>
/// 一個已持久化欄位定義的完整合併狀態。MaxRenderedLength 與 HasObservation
/// 是 Append 所需的內部證據；對外 facts 只投影 Legacy TableDef 會使用的欄位。
/// Origin 是這一欄的來歷，JetField 是它對應的 JET 欄位：標準欄寫語意欄名稱（例如 glDate），
/// 攸關資料元素寫它的 fieldId；原始來源欄沒有對應的 JET 欄位。
/// </summary>
internal sealed record FieldInfoState(
    int Ordinal,
    string FieldName,
    string? Description,
    LegacyFieldKind Kind,
    int TextLength,
    int? DecimalPlaces,
    int MaxRenderedLength,
    bool HasObservation,
    FieldInfoOrigin Origin = FieldInfoOrigin.Source,
    string? JetField = null)
{
    /// <summary>配對後改名與系統產生的欄位才是 JET 欄位；原始來源欄與 _Temp 原欄不是。</summary>
    internal bool IsJetField => Origin is FieldInfoOrigin.Renamed or FieldInfoOrigin.Generated;
}

/// <summary>
/// 匯入同一遍列串流中的欄位定義彙總器。狀態量只隨欄數成長；空值不提供型態證據，
/// 同欄不同型態永久降為 Text，Append 可由持久化 state 恢復後繼續合併。
/// 這裡只處理來源範圍的欄位，所以每一欄的來歷都是原始來源欄。
/// </summary>
internal sealed class FieldInfoAccumulator
{
    private readonly Dictionary<string, MutableField> _fields;

    private FieldInfoAccumulator(Dictionary<string, MutableField> fields)
    {
        _fields = fields;
    }

    internal static FieldInfoAccumulator Create(IReadOnlyList<string> columns)
    {
        var fields = new Dictionary<string, MutableField>(columns.Count, StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
        {
            var fieldName = columns[index];
            fields[fieldName] = MutableField.Empty(index + 1, fieldName);
        }

        return new FieldInfoAccumulator(fields);
    }

    internal static FieldInfoAccumulator Restore(
        IReadOnlyList<FieldInfoState> definitions)
    {
        var fields = new Dictionary<string, MutableField>(definitions.Count, StringComparer.Ordinal);
        foreach (var definition in definitions.OrderBy(item => item.Ordinal))
        {
            fields[definition.FieldName] = MutableField.From(definition);
        }

        return new FieldInfoAccumulator(fields);
    }

    internal void Observe(StagingRow row)
    {
        if (row.FieldObservations.Count == 0)
        {
            // 測試、既有 caller 與 provider adapter 可能只提供 Values；這條相容縫固定視為文字，
            // 不從字串內容猜數字或日期。
            foreach (var value in row.Values)
            {
                Observe(new TabularCellObservation(
                    value.Key,
                    LegacyFieldKind.Text,
                    value.Value.Length,
                    DecimalPlaces: null));
            }

            return;
        }

        foreach (var observation in row.FieldObservations)
        {
            Observe(observation);
        }
    }

    internal IReadOnlyList<FieldInfoState> Build(
        IReadOnlyList<string> authoritativeColumns)
    {
        var result = new List<FieldInfoState>(authoritativeColumns.Count);
        for (var index = 0; index < authoritativeColumns.Count; index++)
        {
            var fieldName = authoritativeColumns[index];
            if (!_fields.TryGetValue(fieldName, out var field))
            {
                field = MutableField.Empty(index + 1, fieldName);
            }

            result.Add(field.ToState(index + 1));
        }

        return result;
    }

    private void Observe(TabularCellObservation observation)
    {
        if (!_fields.TryGetValue(observation.FieldName, out var field))
        {
            // XLSX ragged row 可在標頭範圍外 lazy 產生 COL_n；Replace 必須保留其 native evidence。
            // Append 若因此多欄，repository 仍會在完整串流後拒絕並 rollback。
            field = MutableField.Empty(_fields.Count + 1, observation.FieldName);
            _fields.Add(observation.FieldName, field);
        }

        field.Observe(observation);
    }

    private sealed class MutableField
    {
        private MutableField(
            int ordinal,
            string fieldName,
            string? description,
            LegacyFieldKind kind,
            int textLength,
            int? decimalPlaces,
            int maxRenderedLength,
            bool hasObservation,
            FieldInfoOrigin origin,
            string? jetField)
        {
            Ordinal = ordinal;
            FieldName = fieldName;
            Description = description;
            Kind = kind;
            TextLength = textLength;
            DecimalPlaces = decimalPlaces;
            MaxRenderedLength = maxRenderedLength;
            HasObservation = hasObservation;
            Origin = origin;
            JetField = jetField;
        }

        private int Ordinal { get; }
        private string FieldName { get; }
        private string? Description { get; }
        private LegacyFieldKind Kind { get; set; }
        private int TextLength { get; set; }
        private int? DecimalPlaces { get; set; }
        private int MaxRenderedLength { get; set; }
        private bool HasObservation { get; set; }
        private FieldInfoOrigin Origin { get; }
        private string? JetField { get; }

        internal static MutableField Empty(int ordinal, string fieldName) =>
            new(
                ordinal,
                fieldName,
                description: null,
                LegacyFieldKind.Text,
                textLength: 0,
                decimalPlaces: null,
                maxRenderedLength: 0,
                hasObservation: false,
                FieldInfoOrigin.Source,
                jetField: null);

        internal static MutableField From(FieldInfoState definition) =>
            new(
                definition.Ordinal,
                definition.FieldName,
                definition.Description,
                definition.Kind,
                definition.TextLength,
                definition.DecimalPlaces,
                definition.MaxRenderedLength,
                definition.HasObservation,
                definition.Origin,
                definition.JetField);

        internal void Observe(TabularCellObservation observation)
        {
            MaxRenderedLength = Math.Max(MaxRenderedLength, observation.TextLength);

            if (!HasObservation)
            {
                Kind = observation.Kind;
                DecimalPlaces = observation.Kind == LegacyFieldKind.Number
                    ? Math.Max(0, observation.DecimalPlaces ?? 0)
                    : null;
                HasObservation = true;
            }
            else if (Kind != observation.Kind)
            {
                Kind = LegacyFieldKind.Text;
                DecimalPlaces = null;
            }
            else if (Kind == LegacyFieldKind.Number)
            {
                DecimalPlaces = Math.Max(DecimalPlaces ?? 0, observation.DecimalPlaces ?? 0);
            }

            TextLength = Kind == LegacyFieldKind.Text ? MaxRenderedLength : 0;
            if (Kind != LegacyFieldKind.Number)
            {
                DecimalPlaces = null;
            }
        }

        internal FieldInfoState ToState(int ordinal) =>
            new(
                ordinal,
                FieldName,
                Description,
                HasObservation ? Kind : LegacyFieldKind.Text,
                HasObservation && Kind == LegacyFieldKind.Text ? MaxRenderedLength : 0,
                HasObservation && Kind == LegacyFieldKind.Number ? DecimalPlaces : null,
                MaxRenderedLength,
                HasObservation,
                Origin,
                JetField);
    }
}
