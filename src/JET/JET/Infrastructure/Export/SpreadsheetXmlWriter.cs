using System.Text;
using System.Xml;

namespace JET.Infrastructure;

/// <summary>
/// 寫出活頁簿零件的 XML，寫法和 Excel 存檔時相同：UTF-8 不帶 BOM，SpreadsheetML 主要命名空間用預設命名空間，
/// 文字裡的換行照原樣寫成 LF。Open XML SDK 的元素一律用前綴 x 寫出主要命名空間，經過這裡時改成預設命名空間，
/// 宣告前綴 x 指向主要命名空間的屬性不寫出；其他命名空間與它們的前綴照原樣寫出。
/// 屬性值裡的 LF、CR 與 Tab 寫成字元參照，文字裡的 CR 也寫成字元參照，讀回時才不會被換成空白或 LF。
/// SDK 元素用 <c>element.WriteTo(writer)</c> 寫進這裡；還沒解析的元素會整段交給 <see cref="WriteRaw(string)"/>，
/// 這裡把那段 XML 解析後再逐個節點寫出，前綴一樣會換掉。
/// 寫出時順便數主要命名空間裡 t="s" 的 c 元素，也就是參照共用字串表的格數，見 <see cref="SharedStringReferences"/>。
/// </summary>
internal sealed class SpreadsheetXmlWriter : XmlWriter
{
    internal const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>SDK 寫主要命名空間時固定用的前綴，解析還沒解析的元素時要認得它。</summary>
    private const string SdkMainPrefix = "x";

    private const int CopyBufferSize = 81_920;

    private readonly XmlWriter _inner;
    private readonly Stream _output;
    private readonly Action<long>? _closedWithReferences;
    private readonly StringBuilder _declarationValue = new();
    private readonly StringBuilder _cellTypeValue = new();
    private string? _declaredPrefix;
    private bool _inCellStartTag;
    private bool _capturingCellType;
    private bool _closed;

    private SpreadsheetXmlWriter(XmlWriter inner, Stream output, Action<long>? closedWithReferences)
    {
        _inner = inner;
        _output = output;
        _closedWithReferences = closedWithReferences;
    }

    /// <summary>
    /// 寫進 <paramref name="output"/>；關閉這個寫出程式時一併關閉 <paramref name="output"/>。
    /// 有 <paramref name="closedWithReferences"/> 時，關閉成功後把參照共用字串表的格數交給它。
    /// </summary>
    internal static SpreadsheetXmlWriter Create(Stream output, Action<long>? closedWithReferences = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new SpreadsheetXmlWriter(
            XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                NewLineHandling = NewLineHandling.Entitize,
                Indent = false,
                CloseOutput = true
            }),
            output,
            closedWithReferences);
    }

    /// <summary>
    /// 到目前為止寫出幾個主要命名空間裡 t="s" 的 c 元素。SDK 的元素、還沒解析的元素與從 XmlReader 複製的節點都算；
    /// inline string、數字、樣式屬性 s 與其他命名空間的 c 不算。
    /// </summary>
    internal long SharedStringReferences { get; private set; }

    /// <summary>
    /// 把已經是合法 XML 的 UTF-8 位元組照原樣接在目前的位置，例如收尾時從暫存檔複製的共用字串項目。
    /// 開始標記先寫完整，位元組直接寫進輸出，不經過 XML 寫出程式，所以內容要由呼叫端保證是這個位置能放的完整元素。
    /// </summary>
    internal void WritePreparedContent(Stream utf8Content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(utf8Content);
        WritePreparedContent(
            (output, token) =>
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = utf8Content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            },
            cancellationToken);
    }

    /// <summary>和 <see cref="WritePreparedContent(Stream, CancellationToken)"/> 相同，由 <paramref name="write"/> 直接寫進輸出。</summary>
    internal void WritePreparedContent(Action<Stream, CancellationToken> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (_declaredPrefix is not null || _capturingCellType)
        {
            throw new InvalidOperationException("屬性還沒寫完，不能接上預先寫好的內容。");
        }

        // 寫一段空的內容讓內層寫出程式補上開始標記的 >，再把緩衝寫進輸出，後面的位元組才會接在正確的位置。
        _inner.WriteRaw(string.Empty);
        _inner.Flush();
        write(_output, cancellationToken);
        _inCellStartTag = false;
    }

    public override WriteState WriteState => _inner.WriteState;

    public override XmlWriterSettings? Settings => _inner.Settings;

    public override XmlSpace XmlSpace => _inner.XmlSpace;

    public override string? XmlLang => _inner.XmlLang;

    public override string? LookupPrefix(string ns) => _inner.LookupPrefix(ns);

    public override void WriteStartDocument() => _inner.WriteStartDocument();

    public override void WriteStartDocument(bool standalone) => _inner.WriteStartDocument(standalone);

    public override void WriteEndDocument() => _inner.WriteEndDocument();

    public override void WriteDocType(string name, string? pubid, string? sysid, string? subset) =>
        _inner.WriteDocType(name, pubid, sysid, subset);

    public override void WriteStartElement(string? prefix, string localName, string? ns)
    {
        _inner.WriteStartElement(IsMain(ns) ? string.Empty : prefix, localName, ns);
        _inCellStartTag = IsMain(ns) && string.Equals(localName, "c", StringComparison.Ordinal);
    }

    public override void WriteEndElement()
    {
        _inCellStartTag = false;
        _inner.WriteEndElement();
    }

    public override void WriteFullEndElement()
    {
        _inCellStartTag = false;
        _inner.WriteFullEndElement();
    }

    /// <summary>
    /// 宣告前綴的屬性先記下來，等值寫完才知道它是不是指向主要命名空間；是的話整個不寫出，其他照原樣寫出。
    /// </summary>
    public override void WriteStartAttribute(string? prefix, string localName, string? ns)
    {
        if (string.Equals(prefix, "xmlns", StringComparison.Ordinal) && localName.Length > 0)
        {
            _declaredPrefix = localName;
            _declarationValue.Clear();
            return;
        }

        // c 的型別屬性 t 沒有命名空間；記下它的值，寫完時才知道這一格是不是參照共用字串表。
        _capturingCellType = _inCellStartTag
                             && string.IsNullOrEmpty(prefix)
                             && string.IsNullOrEmpty(ns)
                             && string.Equals(localName, "t", StringComparison.Ordinal);
        _cellTypeValue.Clear();
        _inner.WriteStartAttribute(prefix, localName, ns);
    }

    public override void WriteEndAttribute()
    {
        if (_declaredPrefix is not { } prefix)
        {
            if (_capturingCellType)
            {
                _capturingCellType = false;
                if (_cellTypeValue.Length == 1 && _cellTypeValue[0] == 's')
                {
                    SharedStringReferences++;
                }
            }

            _inner.WriteEndAttribute();
            return;
        }

        _declaredPrefix = null;
        var value = _declarationValue.ToString();
        if (IsMain(value))
        {
            return;
        }

        _inner.WriteAttributeString("xmlns", prefix, XmlnsNamespace, value);
    }

    public override void WriteString(string? text)
    {
        if (_declaredPrefix is not null)
        {
            _declarationValue.Append(text);
            return;
        }

        CaptureCellType(text);
        _inner.WriteString(text);
    }

    public override void WriteChars(char[] buffer, int index, int count)
    {
        if (_declaredPrefix is not null)
        {
            _declarationValue.Append(buffer, index, count);
            return;
        }

        if (_capturingCellType)
        {
            _cellTypeValue.Append(buffer, index, count);
        }

        _inner.WriteChars(buffer, index, count);
    }

    public override void WriteWhitespace(string? ws)
    {
        if (_declaredPrefix is not null)
        {
            _declarationValue.Append(ws);
            return;
        }

        CaptureCellType(ws);
        _inner.WriteWhitespace(ws);
    }

    public override void WriteCharEntity(char ch)
    {
        if (_declaredPrefix is not null)
        {
            _declarationValue.Append(ch);
            return;
        }

        if (_capturingCellType)
        {
            _cellTypeValue.Append(ch);
        }

        _inner.WriteCharEntity(ch);
    }

    public override void WriteSurrogateCharEntity(char lowChar, char highChar)
    {
        if (_declaredPrefix is not null)
        {
            _declarationValue.Append(highChar).Append(lowChar);
            return;
        }

        if (_capturingCellType)
        {
            _cellTypeValue.Append(highChar).Append(lowChar);
        }

        _inner.WriteSurrogateCharEntity(lowChar, highChar);
    }

    public override void WriteEntityRef(string name)
    {
        if (_declaredPrefix is not null)
        {
            throw new InvalidOperationException($"命名空間宣告 xmlns:{_declaredPrefix} 的值不能含實體參照。");
        }

        if (_capturingCellType)
        {
            // 實體參照不會是 s，這一格的型別就不是共用字串。
            _cellTypeValue.Append('&').Append(name);
        }

        _inner.WriteEntityRef(name);
    }

    public override void WriteCData(string? text) => _inner.WriteCData(text);

    public override void WriteComment(string? text) => _inner.WriteComment(text);

    public override void WriteProcessingInstruction(string name, string? text) =>
        _inner.WriteProcessingInstruction(name, text);

    public override void WriteBase64(byte[] buffer, int index, int count) => _inner.WriteBase64(buffer, index, count);

    public override void WriteRaw(char[] buffer, int index, int count) => WriteRaw(new string(buffer, index, count));

    /// <summary>
    /// SDK 寫還沒解析的元素時，整段 XML 經這裡寫出，裡面的前綴沒有經過 <see cref="WriteStartElement(string?, string, string?)"/>。
    /// 所以把它當成 XML 片段解析，再逐個節點寫出；片段沒有宣告前綴 x 時，當成 SDK 的主要命名空間前綴。
    /// </summary>
    public override void WriteRaw(string data)
    {
        if (_declaredPrefix is not null)
        {
            _declarationValue.Append(data);
            return;
        }

        if (!data.Contains('<'))
        {
            CaptureCellType(data);
            _inner.WriteRaw(data);
            return;
        }

        var names = new NameTable();
        var namespaces = new XmlNamespaceManager(names);
        namespaces.AddNamespace(SdkMainPrefix, MainNamespace);
        var settings = new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            NameTable = names
        };
        using var reader = XmlReader.Create(
            new StringReader(data),
            settings,
            new XmlParserContext(names, namespaces, xmlLang: null, XmlSpace.None));
        reader.Read();
        while (!reader.EOF)
        {
            WriteNode(reader, defattr: true);
        }
    }

    public override void Flush() => _inner.Flush();

    public override void Close() => Release();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
        }

        base.Dispose(disposing);
    }

    /// <summary>關閉內層的寫出程式，連同輸出；關閉時失敗也只關一次，之後再關閉不做事。</summary>
    private void Release()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _inner.Dispose();
        _closedWithReferences?.Invoke(SharedStringReferences);
    }

    private void CaptureCellType(string? text)
    {
        if (_capturingCellType)
        {
            _cellTypeValue.Append(text);
        }
    }

    private static bool IsMain(string? ns) => string.Equals(ns, MainNamespace, StringComparison.Ordinal);
}
