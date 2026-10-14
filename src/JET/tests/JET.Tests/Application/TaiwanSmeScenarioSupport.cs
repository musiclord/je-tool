using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 台灣中小企業 GL 與 TB 查核情境測試共用的小工具：組條件情境、儲存後逐筆讀回命中分錄，
/// 以及匯入科目配對與授權人員名單。預期答案一律寫在各測試裡，這裡不計算任何預期值。
/// 命中分錄以「傳票號碼|傳票項次」表示，空白傳票號碼寫成「-」。
/// </summary>
internal static class TaiwanSmeScenarioSupport
{
    internal const string BlankDocument = "-";

    /// <summary>把「V1:1,2;V2:1」展開成排序後的「V1|1、V1|2、V2|1」。</summary>
    internal static string[] Keys(string compact) => compact.Length == 0
        ? []
        : compact.Split(';').SelectMany(part =>
        {
            var pieces = part.Split(':');
            return pieces[1].Split(',').Select(line => pieces[0] + "|" + line);
        }).Order(StringComparer.Ordinal).ToArray();

    internal static JsonObject Scenario(string name, string groupsJson) => new()
    {
        ["name"] = name,
        ["rationale"] = "台灣中小企業查核情境的手算固定答案",
        ["groups"] = JsonNode.Parse(groupsJson)
    };

    /// <summary>只有一組、一條條件的情境。</summary>
    internal static JsonObject OneRule(string name, string ruleJson) =>
        Scenario(name, "[{\"join\":\"AND\",\"matchScope\":\"row\",\"rules\":[" + ruleJson + "]}]");

    internal static async Task<(long Count, long VoucherCount)> PreviewAsync(HandlerTestHost host, JsonObject scenario)
    {
        var preview = await host.DispatchAsync("filter.preview",
            new JsonObject { ["scenario"] = scenario.DeepClone() }.ToJsonString());
        var result = preview.GetProperty("scenario");
        return (result.GetProperty("count").GetInt64(), result.GetProperty("voucherCount").GetInt64());
    }

    /// <summary>一次儲存全部情境（最多十個），再逐一以已儲存情境讀回命中分錄。</summary>
    internal static async Task<IReadOnlyList<string[]>> CommitAndReadHitsAsync(
        HandlerTestHost host, params JsonObject[] scenarios)
    {
        var committed = await CommitAsync(host, scenarios);
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var result = new List<string[]>();
        for (var position = 1; position <= scenarios.Length; position++)
        {
            result.Add(await ReadHitsAsync(host, position, revision));
        }

        return result;
    }

    internal static Task<JsonElement> CommitAsync(HandlerTestHost host, params JsonObject[] scenarios) =>
        host.DispatchAsync("filter.commit", new JsonObject
        {
            ["scenarios"] = new JsonArray(scenarios.Select(s => (JsonNode)s.DeepClone()).ToArray())
        }.ToJsonString());

    internal static async Task<string[]> ReadHitsAsync(HandlerTestHost host, int position, string revision)
    {
        var keys = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            Assert.True(++pages <= 100, "命中分頁讀了 100 頁仍未結束。");
            var request = new JsonObject
            {
                ["scenarioPosition"] = position,
                ["scenarioRevision"] = revision,
                ["pageSize"] = 500
            };
            if (cursor is not null)
            {
                request["cursor"] = cursor;
            }

            var page = await host.DispatchAsync("query.filterHitsPage", request.ToJsonString());
            keys.AddRange(page.GetProperty("rows").EnumerateArray().Select(row =>
                (row.GetProperty("documentNumber").GetString() ?? BlankDocument) + "|" + row.GetProperty("lineItem").GetString()));
            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (cursor is not null);

        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    internal static async Task ImportAccountMappingAsync(
        HandlerTestHost host, params (string Code, string Name, string Category)[] rows)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號";
            sheet.Cell(1, 2).Value = "科目名稱";
            sheet.Cell(1, 3).Value = "標準化分類";
            for (var i = 0; i < rows.Length; i++)
            {
                sheet.Cell(i + 2, 1).Value = rows[i].Code;
                sheet.Cell(i + 2, 2).Value = rows[i].Name;
                sheet.Cell(i + 2, 3).Value = rows[i].Category;
            }
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    /// <summary>匯入授權編製人員名單，識別欄名固定為「員工代碼」；null 代表空白儲存格。</summary>
    internal static async Task<JsonElement> ImportAuthorizedPreparersAsync(HandlerTestHost host, params string?[] identifiers)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "姓名";
            sheet.Cell(1, 2).Value = "員工代碼";
            for (var i = 0; i < identifiers.Length; i++)
            {
                sheet.Cell(i + 2, 1).Value = "合成人員";
                if (identifiers[i] is { } identifier)
                {
                    sheet.Cell(i + 2, 2).Value = identifier;
                }
            }
        });
        try
        {
            return await host.DispatchAsync("import.authorizedPreparer.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sourceColumn = "員工代碼" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }
}
