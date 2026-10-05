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

/// <summary>結果失效矩陣：SQLite 提供者的九種上游改寫。</summary>
public sealed class ResultInvalidationMatrixSqliteTests
{
    public static IEnumerable<object[]> SqliteMatrixCases() => LocalMatrixCases("sqlite");

    [Theory]
    [MemberData(nameof(SqliteMatrixCases))]
    public Task MutationActionCaller_LocalProviders_PreserveExactInvalidationMatrixAndExceptions(
        string databaseProvider,
        MatrixMutation mutation,
        bool clearsValidation,
        bool clearsPrescreen,
        bool clearsFilterHits) =>
        RunInvalidationMatrixAsync(
            databaseProvider,
            sqlServerConnectionString: null,
            mutation,
            clearsValidation,
            clearsPrescreen,
            clearsFilterHits);
}
