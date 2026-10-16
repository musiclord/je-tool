using System.Text;
using System.Text.Json;

namespace JET.Tests.Application;

/// <summary>
/// 一個 action 收到的資料或送回的資料長什麼樣子：每一層可能是哪些 JSON 型別、物件有哪些欄位、哪些欄位不是每次都有、
/// 陣列的元素長什麼樣子。只記型別與欄位名稱，不記任何值。
/// 前端測試的假後端用寫出來的檔案核對自己假造的回應與送出的資料（<c>tools/tests/support/action-contract.cjs</c>）。
/// 寫出的格式：只會是字串、數字、布林或 null 的位置寫成一個字串，例如 <c>"string|null"</c>；
/// 可能是物件或陣列的位置寫成物件，<c>kind</c> 是型別，<c>fields</c> 是欄位，<c>optional</c> 列出不是每次都有的欄位，
/// <c>items</c> 是陣列元素，<c>values</c> 是以資料為鍵的物件的值；只見過空陣列或空物件時寫 null。
/// </summary>
internal sealed class ShapeNode
{
    private readonly SortedSet<string> _kinds = new(StringComparer.Ordinal);
    private SortedDictionary<string, ShapeNode>? _fields;
    private SortedDictionary<string, int>? _fieldCounts;
    private int _objectCount;
    private ShapeNode? _items;
    private ShapeNode? _values;

    /// <summary>欄位名稱是資料而不是固定名稱的物件（例如以來源欄名或分類代碼當鍵），只記值的形狀。</summary>
    public bool IsMap { get; init; }

    public void Add(JsonElement value, Func<string, bool> isMapPath, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                _kinds.Add("object");
                _objectCount++;
                if (IsMap)
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        _values ??= new ShapeNode { IsMap = isMapPath(path + ".*") };
                        _values.Add(property.Value, isMapPath, path + ".*");
                    }

                    break;
                }

                _fields ??= new SortedDictionary<string, ShapeNode>(StringComparer.Ordinal);
                _fieldCounts ??= new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = path + "." + property.Name;
                    if (!IsFieldName(property.Name))
                    {
                        // 程式欄位一律是小寫開頭的英數字；像資料的鍵（來源欄名、識別碼）表示這是以資料為鍵的物件。
                        throw new InvalidOperationException(
                            $"{childPath} 的鍵不像程式欄位；以資料為鍵的物件要列進 ActionShapeJourney.MapPaths。");
                    }

                    if (!_fields.TryGetValue(property.Name, out var child))
                    {
                        child = new ShapeNode { IsMap = isMapPath(childPath) };
                        _fields.Add(property.Name, child);
                    }

                    _fieldCounts[property.Name] = _fieldCounts.GetValueOrDefault(property.Name) + 1;
                    child.Add(property.Value, isMapPath, childPath);
                }

                break;
            case JsonValueKind.Array:
                _kinds.Add("array");
                foreach (var item in value.EnumerateArray())
                {
                    _items ??= new ShapeNode { IsMap = isMapPath(path + "[]") };
                    _items.Add(item, isMapPath, path + "[]");
                }

                break;
            case JsonValueKind.String:
                _kinds.Add("string");
                break;
            case JsonValueKind.Number:
                _kinds.Add("number");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                _kinds.Add("boolean");
                break;
            case JsonValueKind.Null:
                _kinds.Add("null");
                break;
            default:
                throw new InvalidOperationException($"Unexpected JSON value at {path}: {value.ValueKind}");
        }
    }

    public void Write(Utf8JsonWriter writer)
    {
        var kind = string.Join("|", _kinds);
        if (!_kinds.Contains("object") && !_kinds.Contains("array"))
        {
            writer.WriteStringValue(kind);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", kind);
        if (_kinds.Contains("object"))
        {
            if (IsMap)
            {
                writer.WritePropertyName("values");
                if (_values is null) writer.WriteNullValue();
                else _values.Write(writer);
            }
            else
            {
                var optional = (_fieldCounts ?? []).Where(pair => pair.Value < _objectCount).Select(pair => pair.Key).ToArray();
                if (optional.Length > 0)
                {
                    writer.WriteStartArray("optional");
                    foreach (var name in optional)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteStartObject("fields");
                foreach (var (name, child) in _fields ?? [])
                {
                    writer.WritePropertyName(name);
                    child.Write(writer);
                }

                writer.WriteEndObject();
            }
        }

        if (_kinds.Contains("array"))
        {
            writer.WritePropertyName("items");
            if (_items is null) writer.WriteNullValue();
            else _items.Write(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// 在 remaining 指到的那一層加上一個流程走不到的型別（ActionShapeJourney.UnreachedKinds）。remaining 的格式同 MapPaths：
    /// .欄位、[] 表示陣列元素。那一層沒有出現過、或已經記到這個型別，都表示宣告過時，直接丟錯。
    /// </summary>
    public void Declare(string remaining, string kind, string fullPath)
    {
        if (remaining.Length == 0)
        {
            if (!_kinds.Add(kind))
            {
                throw new InvalidOperationException($"{fullPath} 已經記到 {kind}；流程走得到了，從 ActionShapeJourney.UnreachedKinds 拿掉這一項。");
            }

            return;
        }

        if (remaining.StartsWith("[]", StringComparison.Ordinal))
        {
            (_items ?? throw Missing(fullPath)).Declare(remaining[2..], kind, fullPath);
            return;
        }

        var end = remaining.IndexOfAny(['.', '['], 1);
        var name = end < 0 ? remaining[1..] : remaining[1..end];
        if (_fields is null || !_fields.TryGetValue(name, out var child))
        {
            throw Missing(fullPath);
        }

        child.Declare(end < 0 ? string.Empty : remaining[end..], kind, fullPath);
    }

    private static InvalidOperationException Missing(string fullPath) =>
        new($"{fullPath} 沒有出現在流程記到的資料裡；ActionShapeJourney.UnreachedKinds 只能補已經走到的欄位。");

    private static bool IsFieldName(string name) =>
        name.Length > 0 && char.IsAsciiLetterLower(name[0]) && name.All(char.IsAsciiLetterOrDigit);
}

/// <summary>
/// 依序記下每個 action 收到的資料與送回的資料，合併成每個 action 一組形狀。同一個 action 被呼叫多次時，
/// 欄位與型別取聯集；某個欄位只要有一次沒出現，就不算「每次都有」。
/// </summary>
internal sealed class ActionShapeRecorder(IReadOnlySet<string> mapPaths)
{
    private readonly SortedDictionary<string, (ShapeNode Request, ShapeNode Response)> _actions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Actions => _actions.Keys;

    public void Record(string action, JsonElement request, JsonElement response)
    {
        if (!_actions.TryGetValue(action, out var shapes))
        {
            shapes = (new ShapeNode { IsMap = mapPaths.Contains(action + " request") },
                new ShapeNode { IsMap = mapPaths.Contains(action + " response") });
            _actions.Add(action, shapes);
        }

        shapes.Request.Add(request, mapPaths.Contains, action + " request");
        shapes.Response.Add(response, mapPaths.Contains, action + " response");
    }

    /// <summary>路徑格式是「action request|response」加上 .欄位與 []；在那一層加上流程走不到、但正式後端會回的型別。</summary>
    public void Declare(string path, string kind)
    {
        var space = path.IndexOf(' ', StringComparison.Ordinal);
        var action = path[..space];
        var side = path[(space + 1)..];
        var shapes = _actions.TryGetValue(action, out var found)
            ? found : throw new InvalidOperationException($"{path}：流程沒有走到 {action}。");
        if (side.StartsWith("request", StringComparison.Ordinal))
        {
            shapes.Request.Declare(side["request".Length..], kind, path);
        }
        else
        {
            shapes.Response.Declare(side["response".Length..], kind, path);
        }
    }

    /// <summary>每個 action 一段，鍵依字母排序、縮排兩格；同樣的紀錄一定寫出同樣的文字，才能逐字比對。</summary>
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartObject("actions");
            foreach (var (action, shapes) in _actions)
            {
                writer.WriteStartObject(action);
                writer.WritePropertyName("request");
                shapes.Request.Write(writer);
                writer.WritePropertyName("response");
                shapes.Response.Write(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>單一 action 的形狀文字（分行），用來指出兩種資料庫哪幾個 action、哪一行不一樣。</summary>
    public string SerializeAction(string action)
    {
        var shapes = _actions[action];
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("request");
            shapes.Request.Write(writer);
            writer.WritePropertyName("response");
            shapes.Response.Write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
