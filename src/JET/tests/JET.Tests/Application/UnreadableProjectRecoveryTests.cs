using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UnreadableProjectRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultWithUnreadableGeneratedTime_DoesNotBlockOpening_AndCanBeRerun(bool prescreen)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        if (prescreen) await host.DispatchAsync("prescreen.run");
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var kind = prescreen ? ResultKind.Prescreen : ResultKind.Validation;
        var retained = await new LocalResultStore(database).FindLatestAsync(context.ProjectId, kind, CancellationToken.None);
        Assert.NotNull(retained);
        await host.DispatchAsync(prescreen ? "prescreen.run" : "validate.run");
        var previous = await new LocalResultStore(database).FindLatestAsync(context.ProjectId, kind, CancellationToken.None);
        Assert.NotNull(previous);
        await host.DispatchAsync(prescreen ? "export.prescreenReport" : "export.validationReports",
            JsonSerializer.Serialize(new { resultId = previous.ResultId }));
        var table = prescreen ? "prescreen_result" : "validation_result";
        await using (var connection = database.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET generated_utc = 'broken' WHERE result_id = @resultId;";
            command.AddWithValue("@resultId", previous.ResultId);
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        Assert.Equal(JsonValueKind.Null, loaded.GetProperty(prescreen ? "prescreenResult" : "validationResult").ValueKind);
        Assert.True(loaded.GetProperty("staleState").GetProperty(prescreen ? "prescreen" : "validation").GetBoolean());
        var current = await WorkflowResultStateSupport.ReadCurrentAsync(context.ProjectId,
            new LocalResultStore(database), new LocalResultStaleStateStore(database), new LocalFilterScenarioStore(database), CancellationToken.None);
        Assert.Null(prescreen ? current.PrescreenRecord : current.ValidationRecord);
        Assert.True(prescreen ? current.StaleState.Prescreen : current.StaleState.Validation);
        var reports = loaded.GetProperty("reports").EnumerateArray().ToArray();
        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.True(report.GetProperty("stale").GetBoolean()));
        var stale = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            prescreen ? "export.prescreenReport" : "export.validationReports",
            JsonSerializer.Serialize(new { resultId = previous.ResultId })));
        Assert.Equal(JetErrorCodes.StaleResult, stale.Code);

        await host.DispatchAsync(prescreen ? "prescreen.run" : "validate.run");

        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var result = reopened.GetProperty(prescreen ? "prescreenResult" : "validationResult");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(reopened.GetProperty("staleState").GetProperty(prescreen ? "prescreen" : "validation").GetBoolean());
        Assert.NotEqual(previous.ResultId, result.GetProperty("resultRef").GetProperty("resultId").GetString());
        await using (var connection = database.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE result_id = @resultId;";
            command.AddWithValue("@resultId", retained.ResultId);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task InvalidResultShapes_MarkReportsStaleWithoutRewritingExportedFiles()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var validation = JsonNode.Parse(await ReadSummaryAsync(host, context.ProjectId, "validation_result"))!.AsObject();
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new
        {
            resultId = validation["resultRef"]!["resultId"]!.GetValue<string>()
        }));
        var prescreenResult = await host.DispatchAsync("prescreen.run");
        await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new
        {
            resultId = prescreenResult.GetProperty("resultRef").GetProperty("resultId").GetString()
        }));
        var before = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.All(before.GetProperty("reports").EnumerateArray(), report => Assert.False(report.GetProperty("stale").GetBoolean()));
        var originalFiles = before.GetProperty("reports").EnumerateArray().ToDictionary(
            report => report.GetProperty("fullPath").GetString()!,
            report => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(report.GetProperty("fullPath").GetString()!)));
        validation["infSamplingTest"] = null;
        var prescreen = JsonNode.Parse(await ReadSummaryAsync(host, context.ProjectId, "prescreen_result"))!.AsObject();
        prescreen["trailingZeros"]!.AsObject().Remove("zerosThreshold");
        await WriteSummaryAsync(host, context.ProjectId, "validation_result", validation.ToJsonString());
        await WriteSummaryAsync(host, context.ProjectId, "prescreen_result", prescreen.ToJsonString());

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        var reports = loaded.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Equal(3, reports.Length);
        Assert.All(reports, report => Assert.True(report.GetProperty("stale").GetBoolean()));
        Assert.Equal(before.GetProperty("reports").EnumerateArray().Select(report => report.GetProperty("fileName").GetString()),
            reports.Select(report => report.GetProperty("fileName").GetString()));
        Assert.All(originalFiles, file => Assert.Equal(file.Value, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file.Key))));
    }

    [Theory]
    [InlineData("sectionArray")]
    [InlineData("sectionNull")]
    [InlineData("missingThreshold")]
    [InlineData("thresholdText")]
    [InlineData("thresholdTooLow")]
    [InlineData("thresholdTooHigh")]
    [InlineData("duplicateRootProperty")]
    public async Task PrescreenSummaryWithInvalidShape_IsOutdatedUntilRerun(string damage)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("prescreen.run");
        var summary = JsonNode.Parse(await ReadSummaryAsync(host, context.ProjectId, "prescreen_result"))!.AsObject();
        switch (damage)
        {
            case "sectionArray": summary["suspiciousKeywords"] = new JsonArray(); break;
            case "sectionNull": summary["blankDescription"] = null; break;
            case "missingThreshold": summary["trailingZeros"]!.AsObject().Remove("zerosThreshold"); break;
            case "thresholdText": summary["trailingZeros"]!["zerosThreshold"] = "1"; break;
            case "thresholdTooLow": summary["trailingZeros"]!["zerosThreshold"] = 0; break;
            case "thresholdTooHigh": summary["trailingZeros"]!["zerosThreshold"] = 13; break;
        }
        var damaged = summary.ToJsonString();
        if (damage == "duplicateRootProperty") damaged = "{\"blankDescription\":{}," + damaged[1..];
        await WriteSummaryAsync(host, context.ProjectId, "prescreen_result", damaged);

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("prescreenResult").ValueKind);
        Assert.True(loaded.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
        var unusable = Assert.Throws<JetActionException>(() => PrescreenReportProjectionParser.Parse(damaged));
        Assert.Equal(JetErrorCodes.StaleResult, unusable.Code);
        await host.DispatchAsync("prescreen.run");
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.Equal(JsonValueKind.Object, reopened.GetProperty("prescreenResult").ValueKind);
        Assert.False(reopened.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
    }

    [Theory]
    [InlineData("project.listLocal")]
    public async Task CorruptLocalDocument_RemainsVisibleWithoutInventingMetadata_AndHealthyCasesStillLoad(string action)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"projectName":"broken","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        await host.DispatchAsync("project.create", """{"projectName":"healthy","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var path = Path.Combine(host.ProjectsRoot, "broken", "project.json");
        const string corrupt = "{not-valid-synthetic-json";
        await File.WriteAllTextAsync(path, corrupt);
        var response = await host.DispatchAsync(action);
        var rows = response.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var broken = Assert.Single(rows, row => row.GetProperty("projectId").GetString() == "broken");
        Assert.Equal(JsonValueKind.Null, broken.GetProperty("databaseProvider").ValueKind);
        Assert.Equal(JetErrorCodes.FileReadError, broken.GetProperty("loadError").GetProperty("code").GetString());
        var message = broken.GetProperty("loadError").GetProperty("message").GetString()!;
        // 句子不寫檔名，寫「設定檔」。
        Assert.Contains("設定檔無法讀取", message);
        Assert.DoesNotContain("project.json", message);
        Assert.Contains("備份", message);
        Assert.DoesNotContain(host.ProjectsRoot, message);
        Assert.DoesNotContain(corrupt, message);
        Assert.False(broken.TryGetProperty("periodStart", out _));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        var healthy = await host.DispatchAsync("project.load", """{"projectId":"healthy"}""");
        Assert.Equal("healthy", healthy.GetProperty("project").GetProperty("projectId").GetString());
    }

    // 設定檔損壞、舊版或期間無效的本機案件，畫面請審計員從備份還原或另建案件，
    // 舊的這份也要能從案件清單刪掉；其他案件不受影響。
    [Theory]
    [InlineData("sqlite", "corrupt")]
    [InlineData("sqlite", "legacy")]
    [InlineData("sqlite", "invalidPeriod")]
    public async Task UnreadableLocalDocument_CanStillBeDeleted(string provider, string damage)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "delete-broken", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        await host.DispatchAsync("project.create", """{"projectName":"delete-healthy","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var directory = Path.Combine(host.ProjectsRoot, "delete-broken");
        var path = Path.Combine(directory, "project.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        if (damage == "legacy") { document.Remove("databaseProvider"); }
        if (damage == "invalidPeriod") { document["periodStart"] = "2026-01-01"; }
        await File.WriteAllTextAsync(path, damage == "corrupt" ? "{not-valid-synthetic-json" : document.ToJsonString());
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load", """{"projectId":"delete-broken"}"""));

        var deleted = await host.DispatchAsync("project.delete", """{"projectId":"delete-broken"}""");

        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(directory));
        var healthy = await host.DispatchAsync("project.load", """{"projectId":"delete-healthy"}""");
        Assert.Equal("delete-healthy", healthy.GetProperty("project").GetProperty("projectId").GetString());
    }

    /// <summary>
    /// 設定檔的資料庫種類或根層欄名寫成不成對的代理字元（\ud800 這種跳脫寫法）時，也和其他損壞一樣：
    /// 案件清單照常列出、標出這個案件讀不出來，其他案件照常開啟；用同一個名稱建案時說這個案件讀不出來，
    /// 這個案件也能刪掉。JET 自己寫設定檔時會把不成對的代理字元換成 U+FFFD，所以直接改原文。
    /// </summary>
    [Theory]
    [InlineData("databaseProvider")]
    [InlineData("propertyName")]
    public async Task DocumentWithUnreadableText_IsListedAsUnreadable_AndCanBeDeleted(string damage)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"projectName":"unreadable-text","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        await host.DispatchAsync("project.create", """{"projectName":"unreadable-text-healthy","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var directory = Path.Combine(host.ProjectsRoot, "unreadable-text");
        var path = Path.Combine(directory, "project.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        string damaged;
        if (damage == "databaseProvider")
        {
            document["databaseProvider"] = "placeholder-provider";
            damaged = document.ToJsonString().Replace("placeholder-provider", @"\ud800", StringComparison.Ordinal);
        }
        else
        {
            damaged = """{"\udc00":1,""" + document.ToJsonString()[1..];
        }
        await File.WriteAllTextAsync(path, damaged);

        var listed = await host.DispatchAsync("project.listLocal");

        var rows = listed.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var broken = Assert.Single(rows, row => row.GetProperty("projectId").GetString() == "unreadable-text");
        Assert.Equal(JetErrorCodes.FileReadError, broken.GetProperty("loadError").GetProperty("code").GetString());
        var healthy = await host.DispatchAsync("project.load", """{"projectId":"unreadable-text-healthy"}""");
        Assert.Equal("unreadable-text-healthy", healthy.GetProperty("project").GetProperty("projectId").GetString());

        var duplicate = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "project.create",
            """{"projectName":"unreadable-text","periodStart":"2025-01-01","periodEnd":"2025-12-31"}"""));
        Assert.Equal(JetErrorCodes.FileReadError, duplicate.Code);

        var deleted = await host.DispatchAsync("project.delete", """{"projectId":"unreadable-text"}""");

        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// 已存的驗證結果摘要讀不出來時不擋住開案；中間結果能重算就不擋住案件載入。
    /// 前三列是形狀不對：抽樣測試那一段不是物件；開案時不帶結果並標成過期。
    /// 後兩列是有讀不出來的文字（不成對代理字元的跳脫寫法），一個在形狀檢查會讀的欄位，
    /// 一個在形狀檢查不讀、開案時原樣轉寫的清單裡。這兩列和摘要不是合法 JSON 一樣，當成不是目前的結果：
    /// 開案時不帶驗證結果，並標成過期。
    /// 五列的開案都照常；後續步驟回「結果不完整，請重新執行」；產生報告用的解析也回結果不完整；重新執行資料驗證後恢復正常。
    /// JET 自己寫不出這種內容，所以直接改資料庫裡的原文。
    /// </summary>
    [Theory]
    [InlineData("infSamplingTestIsArray")]
    [InlineData("infSamplingTestIsNumber")]
    [InlineData("infSamplingTestIsNull")]
    [InlineData("textReadByShapeCheck")]
    [InlineData("textCopiedOnLoad")]
    public async Task UnreadableValidationSummary_IsTreatedAsNoResult_AndCanBeRerun(string damage)
    {
        const string marker = "synthetic-unreadable-marker";
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var summary = JsonNode.Parse(await ReadSummaryAsync(host, context.ProjectId, "validation_result"))!.AsObject();
        switch (damage)
        {
            case "infSamplingTestIsArray":
                summary["infSamplingTest"] = new JsonArray();
                break;
            case "infSamplingTestIsNumber":
                summary["infSamplingTest"] = 0;
                break;
            case "infSamplingTestIsNull":
                summary["infSamplingTest"] = null;
                break;
            case "textReadByShapeCheck":
                summary["stats"]!["periodStart"] = marker;
                break;
            case "textCopiedOnLoad":
                summary["voucherBalanceTest"]!["unbalancedVouchers"] = new JsonArray(new JsonObject { ["voucherNumber"] = marker });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
        var damaged = summary.ToJsonString().Replace(marker, @"\ud800", StringComparison.Ordinal);
        Assert.Equal(damage.StartsWith("text", StringComparison.Ordinal), damaged.Contains(@"\ud800", StringComparison.Ordinal));
        await WriteSummaryAsync(host, context.ProjectId, "validation_result", damaged);

        Assert.False(ValidationSummaryShapeValidator.IsValid(damaged));
        // 產生驗證報告時的解析排在「是不是目前結果」的檢查後面，現在的流程走不到；直接呼叫，確認它自己也回結果不完整。
        var unusable = Assert.Throws<JetActionException>(() => ValidationReportProjectionParser.Parse(damaged));
        Assert.Equal(JetErrorCodes.StaleResult, unusable.Code);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("validationResult").ValueKind);
        // 形狀不完整也標成過期。
        Assert.True(loaded.GetProperty("staleState").GetProperty("validation").GetBoolean());

        var blocked = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("prescreen.run"));
        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, blocked.Code);
        Assert.Equal("目前完整性驗證結果不完整，請重新執行資料驗證。", blocked.Message);

        await host.DispatchAsync("validate.run");

        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.Equal(JsonValueKind.Object, reopened.GetProperty("validationResult").ValueKind);
        Assert.False(reopened.GetProperty("staleState").GetProperty("validation").GetBoolean());
    }

    /// <summary>
    /// 已存的預篩選結果摘要有讀不出來的文字時，和摘要不是合法 JSON 一樣當成不是目前的結果：開案照常、不帶預篩選結果、
    /// 標成需要重新執行；重新執行預篩選後恢復正常。在摘要加一個含這種文字的欄位，代表任何一處被改壞。
    /// </summary>
    [Fact]
    public async Task PrescreenSummaryWithUnreadableText_IsTreatedAsOutdated_AndCanBeRerun()
    {
        const string marker = "synthetic-unreadable-marker";
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("prescreen.run");
        var summary = JsonNode.Parse(await ReadSummaryAsync(host, context.ProjectId, "prescreen_result"))!.AsObject();
        summary["syntheticNote"] = marker;
        var damaged = summary.ToJsonString().Replace(marker, @"\ud800", StringComparison.Ordinal);
        Assert.Contains(@"\ud800", damaged);
        await WriteSummaryAsync(host, context.ProjectId, "prescreen_result", damaged);

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("prescreenResult").ValueKind);
        Assert.True(loaded.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
        Assert.Equal(JsonValueKind.Object, loaded.GetProperty("validationResult").ValueKind);

        await host.DispatchAsync("prescreen.run");

        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.Equal(JsonValueKind.Object, reopened.GetProperty("prescreenResult").ValueKind);
        Assert.False(reopened.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
    }

    // 只有一筆結果；table 只來自上面固定的兩個表名。
    private static async Task<string> ReadSummaryAsync(HandlerTestHost host, string projectId, string table)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT summary_json FROM {table};";
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task WriteSummaryAsync(HandlerTestHost host, string projectId, string table, string summaryJson)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET summary_json = @summary;";
        command.AddWithValue("@summary", summaryJson);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("2026-01-01", "2025-12-31")]
    [InlineData("not-a-date", "2025-12-31")]
    public async Task ExistingInvalidPeriod_HasARecoveryMessageBeforeChangingSession(string start, string end)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"projectName":"invalid-period","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var path = Path.Combine(host.ProjectsRoot, "invalid-period", "project.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["periodStart"] = start; document["periodEnd"] = end;
        var saved = document.ToJsonString();
        await File.WriteAllTextAsync(path, saved);
        await host.DispatchAsync("project.create", """{"projectName":"keep-active","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load", """{"projectId":"invalid-period"}"""));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        Assert.Contains("查核期間", exception.Message);
        Assert.Contains("另建案件", exception.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        var unchangedSession = await host.DispatchAsync("project.update");
        Assert.Equal("keep-active", unchangedSession.GetProperty("project").GetProperty("projectId").GetString());
    }
}
