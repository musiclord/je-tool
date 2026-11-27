using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

public sealed class ProjectCreateHandler : IApplicationActionHandler
{
    private readonly IProjectStore _projectStore;
    private readonly ProjectRepositoryCatalog _repositoryCatalog;
    private readonly CurrentPrincipal _principal;
    private readonly IProjectSessionPublisher _session;
    private readonly ISupportCaseHistory? _supportHistory;
    private readonly ILogger? _logger;
    private readonly ProjectSessionDeparture? _sessionDeparture;

    internal ProjectCreateHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        CurrentPrincipal principal,
        IProjectSessionPublisher session,
        ISupportCaseHistory? supportHistory = null,
        ILogger<ProjectCreateHandler>? logger = null,
        ProjectSessionDeparture? sessionDeparture = null)
    {
        this._projectStore = projectStore;
        this._repositoryCatalog = repositoryCatalog;
        this._principal = principal;
        this._session = session;
        this._supportHistory = supportHistory;
        this._logger = logger;
        this._sessionDeparture = sessionDeparture;
    }

    public string Action => "project.create";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 只建立 SQLite 案件；畫面不送 databaseProvider，送了也只接受 sqlite。
        var databaseProvider = PayloadReader.GetOptionalString(payload, "databaseProvider")
            ?? ProjectDocument.DefaultDatabaseProvider;
        if (databaseProvider != ProjectDocument.DefaultDatabaseProvider)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "database_provider_unsupported");
        }

        // 案件的資料庫組在建案時就選定：建案過程與成功後的 session 都用這一組，不另外回頭讀 project.json。
        var repositories = _repositoryCatalog.For(databaseProvider);
        var projectCreateFactsPort = repositories.ProjectCreateFacts;

        // projectName 選填。有值時先驗證名稱、確認沒有重複，再拿它當 projectId 與資料夾名稱；
        // 沒有值時用 GUID 當 projectId，這是不經畫面、由程式直接建立案件時的做法。
        var projectNameRaw = PayloadReader.GetOptionalString(payload, "projectName");
        // 畫面送來的 projectName 是空白時回錯誤，請審計員當場改正；完全沒帶 projectName 的呼叫才用 GUID。
        if (payload.TryGetProperty("projectName", out var projectNameValue)
            && projectNameValue.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(projectNameRaw))
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "案件名稱不可為空白。請輸入案件名稱後再建立。", JetErrorFields.ProjectName);
        string projectId;
        if (!string.IsNullOrWhiteSpace(projectNameRaw))
        {
            var projectName = projectNameRaw.Trim();
            var nameError = ProjectNameRules.Validate(projectName);
            if (nameError is not null)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    nameError,
                    JetErrorFields.ProjectName);
            }

            // 同名資料夾即視為重複。DuckDB 或 SQL Server 的案件不列在案件清單，
            // 撞到它時先說清楚是哪一種資料夾、審計員能怎麼做；它的設定檔讀不通時也不報那個看不到的案件的錯誤。
            SupportOperationStage.Enter("name_check");
            if (await _projectStore.IsRetiredProviderCaseAsync(projectName, cancellationToken))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    ProjectFolderMessages.RetiredProviderNameTaken(projectName),
                    JetErrorFields.ProjectName);
            }

            if (await _projectStore.FindAsync(projectName, cancellationToken) is not null)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"案件名稱「{projectName}」已存在，請改用其他名稱。",
                    JetErrorFields.ProjectName);
            }

            projectId = projectName;
        }
        else
        {
            projectId = Guid.NewGuid().ToString("N");
        }

        // 案件編號與客戶名稱是選填 metadata，沒有填時存空字串，清單與報告照樣讀得到；
        // 不能為了空白顯示資訊阻擋建案。
        var projectCode = PayloadReader.GetOptionalString(payload, "projectCode")?.Trim() ?? string.Empty;
        var clientName = PayloadReader.GetOptionalString(payload, "clientName")?.Trim() ?? string.Empty;

        // 建案者只能是程式啟動時注入的目前 Windows 身分：照舊版 JET 記 Windows 帳號的使用者全名，
        // 取不到時記帳號短名。payload 即使帶了 operatorId 也不採用，避免前端欄位或其他呼叫端冒名寫進案件設定。
        var operatorId = await _principal.ResolveCreatorNameAsync(cancellationToken);
        var periodStart = PayloadReader.GetRequiredDate(payload, "periodStart");
        var periodEnd = PayloadReader.GetRequiredDate(payload, "periodEnd");
        var prepStartDate = PayloadReader.GetOptionalDate(payload, "prepStartDate");
        ProjectMetadataRules.RequireValidPeriod(periodStart, periodEnd);
        var createdUtc = DateTimeOffset.UtcNow;
        var sampleSeed = NewSampleSeed();
        var document = ProjectDocument.CreateNew(
            projectId,
            projectCode,
            clientName,
            operatorId,
            periodStart,
            periodEnd,
            prepStartDate,
            databaseProvider,
            createdUtc,
            sampleSeed,
            JetAuditProgram.CurrentInfSamplingAlgorithmVersion);
        var plan = JetAuditProgram.Plan(
            new ProjectCreateRequest(
                document,
                HasUserSuppliedProjectName: !string.IsNullOrWhiteSpace(projectNameRaw),
                _principal.Name));

        ProjectCreateFacts facts;
        SupportOperationStage.Enter("case_create");
        try
        {
            facts = await projectCreateFactsPort.ExecuteAsync(
                plan,
                cancellationToken);
        }
        catch (ProjectCreateLockHeldException held)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectLocked,
                ProjectLockErrorMessage.Format(
                    held.LockedBy,
                    held.MachineName,
                    held.LockedUtc),
                innerException: held);
        }
        catch (ProjectCreateExistingWorkLockException exception)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectLocked,
                "同名案件目前已在 JET 中開啟，無法重新建立。",
                innerException: exception);
        }

        SupportOperationStage.Enter("case_complete");
        try
        {
            var result = JetAuditProgram.Finalize(plan, facts);
            document = result.Document;
            // 回應物件要在進入 session 之前建好；這一行之後不再做任何可能失敗的回應組裝工作。
            var response = new { projectId = document.ProjectId, ok = true,
                warnings = ProjectMetadataRules.GetWarnings(periodStart, periodEnd, prepStartDate) };
            // 新的案件建好之後、進入 session 之前，才離開目前還開著的另一個案件，放掉它的案件鎖並關閉它的資料庫。
            // 名稱、查核期間或建立案件資料夾失敗時，目前的案件照樣開著。
            if (_sessionDeparture is not null)
            {
                await _sessionDeparture.LeaveOtherProjectAsync(document.ProjectId);
            }
            _session.Enter(document.ProjectId, repositories);
            // finalize 與 session 都完成後才結算建案；結算不理會請求取消，只做一次。
            await projectCreateFactsPort.CompleteAsync(facts, CancellationToken.None);
            // 建案確定之後才建立支援紀錄資料夾並記下這次開啟；之前失敗時，回復刪除案件資料夾不受紀錄檔影響。
            SupportCaseDiagnostics.CaseOpened(
                _supportHistory, _logger, document.ProjectId, document.DatabaseProvider, createdNow: true);
            return response;
        }
        catch
        {
            try
            {
                // 不論失敗在哪一步都要離開 session：session.Enter 可能已經把這個案件設成目前案件才丟出例外。
                // Leave 只在目前案件仍是這一個時才清除。
                _ = _session.Leave(document.ProjectId);
            }
            catch (Exception)
            {
                // 離開 session 失敗時不往外丟，保留原本完成建案或結算時的例外。
            }

            // 完成建案、建立回應、進入 session、結算這四步任何一步失敗，都走同一個回復流程，而且不理會請求取消：
            // 先釋放這次取得的案件鎖，再回復資料庫，兩者都成功才刪除案件資料夾。
            await projectCreateFactsPort.RollbackAsync(facts, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 新案件的 INF 抽樣種子，只在建案時用亂數產生一次；開案補完中斷的建案時也用這裡。
    /// Domain 不產生亂數，只規定種子的範圍。
    /// </summary>
    internal static long NewSampleSeed() =>
        Random.Shared.NextInt64(1, ProjectDocument.SampleSeedExclusiveUpperBound);
}
