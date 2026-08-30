using System.Security.Cryptography;
using System.Text;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class MappingMetadataCodecTests
{
    public static TheoryData<string> GlModes => new()
    {
        GlAmountModeNames.Signed,
        GlAmountModeNames.Side,
        GlAmountModeNames.Flag,
        GlAmountModeNames.Dual
    };

    public static TheoryData<string> TbModes => new()
    {
        TbChangeModeNames.Direct,
        TbChangeModeNames.DebitCredit,
        TbChangeModeNames.OpenClose,
        TbChangeModeNames.OpenCloseBySide
    };

    [Theory]
    [MemberData(nameof(GlModes))]
    public void EncodeDecode_RoundTripsEveryGlModeAndAllLogicalKeys(string mode)
    {
        var gl = AllGlMappings();
        var json = MappingMetadataCodec.Encode(
            Committed(DatasetKind.Gl, gl, mode),
            Committed(DatasetKind.Tb, AllTbMappings(), TbChangeModeNames.Direct));

        var decoded = MappingMetadataCodec.Decode(MappingMetadataFormat.CurrentVersion, json);

        Assert.Equal(MappingMetadataFormat.CurrentVersion, decoded.FormatVersion);
        Assert.Equal(mode, decoded.Gl.AmountMode);
        Assert.Equal(GlMappingKeys.All, decoded.Gl.Mapping.Keys);
        Assert.Equal(gl.Values, decoded.Gl.Mapping.Values);
        Assert.Equal("D", decoded.Gl.Mapping[GlMappingKeys.DcDebitCode]);
        Assert.Equal(ApprovalDateModeNames.Mapped, decoded.Gl.ApprovalDateMode);
        Assert.Equal(["posted", "approved"], decoded.Gl.PostingStatusPolicy!.AcceptedValues);
        Assert.True(decoded.Gl.PostingStatusPolicy.IncludeBlank);
        Assert.Equal(["M"], decoded.Gl.ManualAutoPolicy.ManualValues);
        Assert.Equal(["A"], decoded.Gl.ManualAutoPolicy.AutomaticValues);
        var rde = Assert.Single(decoded.Gl.RdeFields);
        Assert.Equal("rde.0123456789abcdef0123456789abcdef", rde.FieldId);
        Assert.Equal(RdeFieldValueTypeNames.Money, rde.ValueType);
    }

    [Theory]
    [MemberData(nameof(TbModes))]
    public void EncodeDecode_RoundTripsEveryTbModeAndAllLogicalKeys(string mode)
    {
        var tb = AllTbMappings();
        var json = MappingMetadataCodec.Encode(
            Committed(DatasetKind.Gl, AllGlMappings(), GlAmountModeNames.Signed),
            Committed(DatasetKind.Tb, tb, mode));

        var decoded = MappingMetadataCodec.Decode(MappingMetadataFormat.CurrentVersion, json);

        Assert.Equal(mode, decoded.Tb.ChangeMode);
        Assert.Equal(TbMappingKeys.All, decoded.Tb.Mapping.Keys);
        Assert.Equal(tb.Values, decoded.Tb.Mapping.Values);
    }

    [Fact]
    public void Encode_IsDeterministicAndUsesCatalogOrder()
    {
        var reverseGl = AllGlMappings().Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        var reverseTb = AllTbMappings().Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        var first = MappingMetadataCodec.Encode(
            Committed(DatasetKind.Gl, reverseGl, GlAmountModeNames.Flag),
            Committed(DatasetKind.Tb, reverseTb, TbChangeModeNames.OpenCloseBySide));
        var second = MappingMetadataCodec.Encode(
            Committed(DatasetKind.Gl, AllGlMappings(), GlAmountModeNames.Flag),
            Committed(DatasetKind.Tb, AllTbMappings(), TbChangeModeNames.OpenCloseBySide));

        Assert.Equal(first, second);
        Assert.True(first.IndexOf("\"docNum\"", StringComparison.Ordinal)
                    < first.IndexOf("\"dcDebitCode\"", StringComparison.Ordinal));
        Assert.True(first.IndexOf("\"openingBalance\"", StringComparison.Ordinal)
                    < first.IndexOf("\"openingDebit\"", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeDecodeEncode_PreservesCanonicalPayloadHash()
    {
        var json = MappingMetadataCodec.Encode(
            Committed(DatasetKind.Gl, AllGlMappings(), GlAmountModeNames.Flag),
            Committed(DatasetKind.Tb, AllTbMappings(), TbChangeModeNames.OpenCloseBySide));
        var decoded = MappingMetadataCodec.Decode(MappingMetadataFormat.CurrentVersion, json);
        var reencoded = MappingMetadataCodec.Encode(
            new CommittedMapping(
                DatasetKind.Gl,
                decoded.Gl.Mapping,
                decoded.Gl.AmountMode,
                "batch",
                DateTimeOffset.UnixEpoch)
            {
                GlOptions = decoded.Gl.ToOptions()
            },
            new CommittedMapping(
                DatasetKind.Tb,
                decoded.Tb.Mapping,
                decoded.Tb.ChangeMode,
                "batch",
                DateTimeOffset.UnixEpoch));

        var originalHash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var roundTripHash = SHA256.HashData(Encoding.UTF8.GetBytes(reencoded));

        Assert.Equal(json, reencoded);
        Assert.Equal(originalHash, roundTripHash);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"gl\":{},\"tb\":{}}")]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"SIGNED\"},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{\"unknown\":\"x\"},\"amountMode\":\"signed\"},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{\"docNum\":1},\"amountMode\":\"signed\"},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\",\"extra\":1},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{\"docNum\":\"a\",\"docNum\":\"b\"},\"amountMode\":\"signed\"},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    public void Decode_RejectsMalformedOrAmbiguousV1Payload(string json)
    {
        Assert.Throws<MappingMetadataFormatException>(() =>
            MappingMetadataCodec.Decode(MappingMetadataFormat.LegacyVersion, json));
    }

    [Fact]
    public void Decode_V1_NormalizesToPendingV2DraftWithoutInventingPostingOrRdeFields()
    {
        const string json =
            "{\"gl\":{\"mapping\":{\"docDate\":\"Approval Date\"},\"amountMode\":\"signed\"},"
            + "\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}";

        var decoded = MappingMetadataCodec.Decode(MappingMetadataFormat.LegacyVersion, json);

        Assert.Equal(MappingMetadataFormat.CurrentVersion, decoded.FormatVersion);
        Assert.Equal(ApprovalDateModeNames.Mapped, decoded.Gl.ApprovalDateMode);
        Assert.Null(decoded.Gl.PostingStatusPolicy);
        Assert.Equal(["1"], decoded.Gl.ManualAutoPolicy.ManualValues);
        Assert.Equal(["0"], decoded.Gl.ManualAutoPolicy.AutomaticValues);
        Assert.Empty(decoded.Gl.RdeFields);
    }

    [Fact]
    public void Decode_V1WithoutDocDate_NormalizesApprovalDateToUnmapped()
    {
        const string json =
            "{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\"},"
            + "\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}";

        var decoded = MappingMetadataCodec.Decode(MappingMetadataFormat.LegacyVersion, json);

        Assert.Equal(ApprovalDateModeNames.Unmapped, decoded.Gl.ApprovalDateMode);
    }

    [Theory]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\"},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\",\"approvalDateMode\":\"mapped\",\"postingStatusPolicy\":null,\"manualAutoPolicy\":{\"manualValues\":[\"1\"],\"automaticValues\":[\"0\"]},\"rdeFields\":[],\"extra\":true},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\",\"approvalDateMode\":\"Mapped\",\"postingStatusPolicy\":null,\"manualAutoPolicy\":{\"manualValues\":[\"1\"],\"automaticValues\":[\"0\"]},\"rdeFields\":[]},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\",\"approvalDateMode\":\"mapped\",\"postingStatusPolicy\":null,\"manualAutoPolicy\":{\"manualValues\":[\"1\"],\"automaticValues\":[\"0\"]},\"rdeFields\":[{\"fieldId\":\"rde.0123456789abcdef0123456789abcdef\",\"sourceColumn\":\"Risk\",\"label\":\"Risk\",\"valueType\":\"number\"}]},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{},\"amountMode\":\"signed\",\"approvalDateMode\":\"unmapped\",\"postingStatusPolicy\":{\"acceptedValues\":[\"posted\"],\"includeBlank\":false},\"manualAutoPolicy\":{\"manualValues\":[\"1\"],\"automaticValues\":[\"0\"]},\"rdeFields\":[]},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    [InlineData("{\"gl\":{\"mapping\":{\"postingStatus\":\"Status\"},\"amountMode\":\"signed\",\"approvalDateMode\":\"unmapped\",\"postingStatusPolicy\":{\"acceptedValues\":[\"POSTED\",\"posted\"],\"includeBlank\":false},\"manualAutoPolicy\":{\"manualValues\":[\"1\"],\"automaticValues\":[\"0\"]},\"rdeFields\":[]},\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}")]
    public void Decode_RejectsMalformedOrAmbiguousV2Payload(string json)
    {
        Assert.Throws<MappingMetadataFormatException>(() =>
            MappingMetadataCodec.Decode(MappingMetadataFormat.CurrentVersion, json));
    }

    [Theory]
    [InlineData(ApprovalDateModeNames.Mapped, false)]
    [InlineData(ApprovalDateModeNames.SameAsPostDate, true)]
    [InlineData(ApprovalDateModeNames.Unmapped, true)]
    public void Encode_RejectsApprovalDateModeMappingMismatch(
        string approvalDateMode,
        bool includeDocDate)
    {
        var mapping = AllGlMappings();
        if (!includeDocDate)
        {
            mapping.Remove(GlMappingKeys.DocDate);
        }

        var gl = Committed(DatasetKind.Gl, mapping, GlAmountModeNames.Signed) with
        {
            GlOptions = FullGlOptions() with { ApprovalDateMode = approvalDateMode }
        };

        Assert.Throws<MappingMetadataFormatException>(() => MappingMetadataCodec.Encode(
            gl,
            Committed(DatasetKind.Tb, AllTbMappings(), TbChangeModeNames.Direct)));
    }

    [Theory]
    [InlineData(ApprovalDateModeNames.Mapped, false)]
    [InlineData(ApprovalDateModeNames.SameAsPostDate, true)]
    [InlineData(ApprovalDateModeNames.Unmapped, true)]
    public void DecodeV2_RejectsApprovalDateModeMappingMismatch(
        string approvalDateMode,
        bool includeDocDate)
    {
        var mappingJson = includeDocDate ? "{\"docDate\":\"Approval Date\"}" : "{}";
        var json = "{\"gl\":{\"mapping\":" + mappingJson
                   + ",\"amountMode\":\"signed\",\"approvalDateMode\":\"" + approvalDateMode
                   + "\",\"postingStatusPolicy\":null,\"manualAutoPolicy\":{\"manualValues\":[\"1\"],"
                   + "\"automaticValues\":[\"0\"]},\"rdeFields\":[]},"
                   + "\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}";

        Assert.Throws<MappingMetadataFormatException>(() =>
            MappingMetadataCodec.Decode(MappingMetadataFormat.CurrentVersion, json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Decode_RejectsUnsupportedVersion(int version)
    {
        Assert.Throws<MappingMetadataFormatException>(() => MappingMetadataCodec.Decode(version, "{}"));
    }

    private static Dictionary<string, string> AllGlMappings() => GlMappingKeys.All.ToDictionary(
        key => key,
        key => key == GlMappingKeys.DcDebitCode ? "D" : $"GL_{key}",
        StringComparer.Ordinal);

    private static Dictionary<string, string> AllTbMappings() => TbMappingKeys.All.ToDictionary(
        key => key,
        key => $"TB_{key}",
        StringComparer.Ordinal);

    private static CommittedMapping Committed(
        DatasetKind kind,
        IReadOnlyDictionary<string, string> mapping,
        string mode) => new(kind, mapping, mode, "batch", DateTimeOffset.UnixEpoch)
        {
            GlOptions = kind == DatasetKind.Gl ? FullGlOptions() : null
        };

    private static GlMappingOptions FullGlOptions() => new(
        ApprovalDateModeNames.Mapped,
        new GlPostingStatusPolicy(["posted", "approved"], IncludeBlank: true),
        new GlManualAutoPolicy(["M"], ["A"]),
        [
            new GlRdeFieldMetadata(
                "rde.0123456789abcdef0123456789abcdef",
                "Risk Amount",
                "風險金額",
                RdeFieldValueTypeNames.Money)
        ]);
}

public sealed class OpenXmlMappingMetadataReaderTests
{
    [Fact]
    public async Task ReadAsync_ClosedXmlResavedWorkbook_ReadsSharedStrings()
    {
        var json = MappingMetadataCodec.Encode(
            MinimalGl(GlAmountModeNames.Flag),
            MinimalTb(TbChangeModeNames.Direct));
        var path = Workbook(
            MappingMetadataFormat.Marker,
            MappingMetadataFormat.CurrentVersion.ToString(),
            json);
        try
        {
            var result = await new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None);

            Assert.Equal(GlAmountModeNames.Flag, result.Gl.AmountMode);
            Assert.Equal("D", result.Gl.Mapping[GlMappingKeys.DcDebitCode]);
            Assert.Equal(TbChangeModeNames.Direct, result.Tb.ChangeMode);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_V1Payload_NormalizesToV2Draft()
    {
        const string payload =
            "{\"gl\":{\"mapping\":{\"docDate\":\"Approval Date\"},\"amountMode\":\"signed\"},"
            + "\"tb\":{\"mapping\":{},\"changeMode\":\"direct\"}}";
        var path = Workbook(
            MappingMetadataFormat.Marker,
            MappingMetadataFormat.LegacyVersion.ToString(),
            payload);
        try
        {
            var result = await new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None);

            Assert.Equal(MappingMetadataFormat.CurrentVersion, result.FormatVersion);
            Assert.Equal(ApprovalDateModeNames.Mapped, result.Gl.ApprovalDateMode);
            Assert.Null(result.Gl.PostingStatusPolicy);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_VisibleLegacyFieldsWithoutMarker_RejectsWithoutGuessing()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = MappingMetadataFormat.WorksheetName;
            sheet.Cell("A1").Value = "V.2019";
            sheet.Cell("A2").Value = "TB檔案配對前後欄位對照表";
            sheet.Cell("E3").Value = "配對後欄位名稱";
        });
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
            Assert.Equal(JetErrorCodes.MappingMetadataMissing, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Theory]
    [InlineData("3", "{\"gl\":{},\"tb\":{}}")] // 未知版本先擋，不嘗試猜 payload
    [InlineData("2", "not-json")]
    [InlineData("1", "{\"gl\":{},\"tb\":{}}")]
    public async Task ReadAsync_InvalidVersionOrPayload_RejectsWholeDocument(string version, string payload)
    {
        var path = Workbook(MappingMetadataFormat.Marker, version, payload);
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
            Assert.Equal(JetErrorCodes.MappingMetadataInvalid, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_MissingWorksheet_ReportsMetadataMissing()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet => sheet.Cell("A1").Value = "visible only");
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
            Assert.Equal(JetErrorCodes.MappingMetadataMissing, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_UnsupportedExtension_RejectsBeforeOpening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-metadata-{Guid.NewGuid():N}.csv");
        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, ex.Code);
    }

    [Fact]
    public async Task ReadAsync_MissingAbsoluteXlsx_ReturnsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-metadata-{Guid.NewGuid():N}.xlsx");
        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));
        Assert.Equal(JetErrorCodes.FileNotFound, ex.Code);
    }

    private static string Workbook(string marker, string version, string payload)
        => TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = MappingMetadataFormat.WorksheetName;
            sheet.Cell(MappingMetadataFormat.MarkerCell).Value = marker;
            sheet.Cell(MappingMetadataFormat.VersionCell).Value = version;
            sheet.Cell(MappingMetadataFormat.PayloadCell).Value = payload;
            sheet.Columns(6, 8).Hide();
        });

    private static CommittedMapping MinimalGl(string mode) => new(
        DatasetKind.Gl,
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "Document",
            [GlMappingKeys.PostDate] = "PostDate",
            [GlMappingKeys.AccNum] = "Account",
            [GlMappingKeys.AccName] = "AccountName",
            [GlMappingKeys.Description] = "Description",
            [GlMappingKeys.Amount] = "Amount",
            [GlMappingKeys.DcField] = "Side",
            [GlMappingKeys.DcDebitCode] = "D"
        },
        mode,
        "gl-batch",
        DateTimeOffset.UnixEpoch);

    private static CommittedMapping MinimalTb(string mode) => new(
        DatasetKind.Tb,
        new Dictionary<string, string>
        {
            [TbMappingKeys.AccNum] = "Account",
            [TbMappingKeys.AccName] = "AccountName",
            [TbMappingKeys.Amount] = "Movement"
        },
        mode,
        "tb-batch",
        DateTimeOffset.UnixEpoch);
}
