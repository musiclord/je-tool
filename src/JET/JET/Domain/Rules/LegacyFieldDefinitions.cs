namespace JET.Domain;

/// <summary>
/// 一個已持久化欄位定義的完整合併狀態。MaxRenderedLength 與 HasObservation
/// 是 Append 所需的內部證據；對外 facts 只投影 Legacy TableDef 會使用的欄位。
/// </summary>
internal sealed record LegacyFieldDefinitionState(
    int Ordinal,
    string FieldName,
    string? Description,
    LegacyFieldKind Kind,
    int TextLength,
    int? DecimalPlaces,
    int MaxRenderedLength,
    bool HasObservation);

/// <summary>
/// 匯入同一遍列串流中的欄位定義彙總器。狀態量只隨欄數成長；空值不提供型態證據，
/// 同欄不同型態永久降為 Text，Append 可由持久化 state 恢復後繼續合併。
/// </summary>
internal sealed class LegacyFieldDefinitionAccumulator
{
    private readonly Dictionary<string, MutableField> _fields;

    private LegacyFieldDefinitionAccumulator(Dictionary<string, MutableField> fields)
    {
        _fields = fields;
    }

    internal static LegacyFieldDefinitionAccumulator Create(IReadOnlyList<string> columns)
    {
        var fields = new Dictionary<string, MutableField>(columns.Count, StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
        {
            var fieldName = columns[index];
            fields[fieldName] = MutableField.Empty(index + 1, fieldName);
        }

        return new LegacyFieldDefinitionAccumulator(fields);
    }

    internal static LegacyFieldDefinitionAccumulator Restore(
        IReadOnlyList<LegacyFieldDefinitionState> definitions)
    {
        var fields = new Dictionary<string, MutableField>(definitions.Count, StringComparer.Ordinal);
        foreach (var definition in definitions.OrderBy(item => item.Ordinal))
        {
            fields[definition.FieldName] = MutableField.From(definition);
        }

        return new LegacyFieldDefinitionAccumulator(fields);
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

    internal IReadOnlyList<LegacyFieldDefinitionState> Build(
        IReadOnlyList<string> authoritativeColumns)
    {
        var result = new List<LegacyFieldDefinitionState>(authoritativeColumns.Count);
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
            bool hasObservation)
        {
            Ordinal = ordinal;
            FieldName = fieldName;
            Description = description;
            Kind = kind;
            TextLength = textLength;
            DecimalPlaces = decimalPlaces;
            MaxRenderedLength = maxRenderedLength;
            HasObservation = hasObservation;
        }

        private int Ordinal { get; }
        private string FieldName { get; }
        private string? Description { get; }
        private LegacyFieldKind Kind { get; set; }
        private int TextLength { get; set; }
        private int? DecimalPlaces { get; set; }
        private int MaxRenderedLength { get; set; }
        private bool HasObservation { get; set; }

        internal static MutableField Empty(int ordinal, string fieldName) =>
            new(
                ordinal,
                fieldName,
                description: null,
                LegacyFieldKind.Text,
                textLength: 0,
                decimalPlaces: null,
                maxRenderedLength: 0,
                hasObservation: false);

        internal static MutableField From(LegacyFieldDefinitionState definition) =>
            new(
                definition.Ordinal,
                definition.FieldName,
                definition.Description,
                definition.Kind,
                definition.TextLength,
                definition.DecimalPlaces,
                definition.MaxRenderedLength,
                definition.HasObservation);

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

        internal LegacyFieldDefinitionState ToState(int ordinal) =>
            new(
                ordinal,
                FieldName,
                Description,
                HasObservation ? Kind : LegacyFieldKind.Text,
                HasObservation && Kind == LegacyFieldKind.Text ? MaxRenderedLength : 0,
                HasObservation && Kind == LegacyFieldKind.Number ? DecimalPlaces : null,
                MaxRenderedLength,
                HasObservation);
    }
}
