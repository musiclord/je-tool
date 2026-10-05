using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using static JET.Tests.Application.ResultInvalidationTestSupport;

namespace JET.Tests.Application;

/// <summary>結果失效矩陣與 GL 重投影：SQL Server 提供者（只在明確要求時執行）。</summary>
public sealed class ResultInvalidationSqlServerTests
{
    [SqlServerTheory]
    [MemberData(nameof(SqlServerMatrixCases), MemberType = typeof(ResultInvalidationTestSupport))]
    public async Task MutationActionCaller_SqlServer_PreservesExactInvalidationMatrixAndExceptions(
        MatrixMutation mutation,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits)
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        Assert.NotNull(connectionString);

        await RunInvalidationMatrixAsync(
            "sqlServer",
            connectionString,
            mutation,
            clearsValidation,
            clearsPrescreen,
            clearsFilterHits);
    }

    [SqlServerFact]
    public async Task GlProjection_SqlServer_UpdatesControlTotalAndPreservesExactInvalidationMatrixExceptions()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        Assert.NotNull(connectionString);

        await RunGlProjectionInvalidationAsync("sqlServer", connectionString);
    }
}
