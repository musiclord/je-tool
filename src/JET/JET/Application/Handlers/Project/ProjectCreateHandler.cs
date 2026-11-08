using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

public sealed class ProjectCreateHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectRepositoryCatalog repositoryCatalog;
    private readonly CurrentPrincipal principal;
    private readonly IProjectSessionPublisher session;
    private readonly ISupportCaseHistory? supportHistory;
    private readonly ILogger? logger;

    internal ProjectCreateHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        CurrentPrincipal principal,
        IProjectSessionPublisher session,
        ISupportCaseHistory? supportHistory = null,
        ILogger<ProjectCreateHandler>? logger = null)
    {
        this.projectStore = projectStore;
        this.repositoryCatalog = repositoryCatalog;
        this.principal = principal;
        this.session = session;
        this.supportHistory = supportHistory;
        this.logger = logger;
    }

    public string Action => "project.create";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 目前只建立 SQLite 案件（裁定 2026-11-06-08）；畫面不送 databaseProvider，送了也只接受 sqlite。
        var databaseProvider = PayloadReader.GetOptionalString(payload, "databaseProvider")
            ?? ProjectDocument.DefaultDatabaseProvider;
        if (databaseProvider != ProjectDocument.DefaultDatabaseProvider)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "database_provider_unsupported");
        }

        // 案件的資料庫組在建案時就選定：建案過程與成功後的 session 都用這一組，不另外回頭讀 project.json。
        var repositories = repositoryCatalog.For(databaseProvider);
        var projectCreateFactsPort = repositories.ProjectCreateFacts;

        // 選填 projectName:有值則驗證 + 唯一性檢查並作為 projectId/資料夾名;無值回退 GUID(既有程式化/測試建立行為)。
        var projectNameRaw = PayloadReader.GetOptionalString(payload, "projectName");
        // 正式畫面明確送來的空白名稱應就地修正；未提供名稱的程式化建立仍保留既有GUID行為。
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

            // 同名資料夾即視為重複。以前建立的 DuckDB 或 SQL Server 案件不列在案件清單（裁定 2026-11-06-09），
            // 撞到它時先說清楚是哪一種資料夾、審計員能怎麼做；它的設定檔讀不通時也不報那個看不到的案件的錯誤。
            SupportOperationStage.Enter("name_check");
            if (await projectStore.IsRetiredProviderCaseAsync(projectName, cancellationToken))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    ProjectFolderMessages.RetiredProviderNameTaken(projectName),
                    JetErrorFields.ProjectName);
            }

            if (await projectStore.FindAsync(projectName, cancellationToken) is not null)
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

        // 操作人員只能來自 composition root 注入的目前 Windows 身分。舊版 payload 即使仍帶
        // operatorId 也不採用，避免前端欄位或其他 caller 冒名寫入案件 metadata。
        var operatorId = principal.ShortName;
        var periodStart = PayloadReader.GetRequiredDate(payload, "periodStart");
        var periodEnd = PayloadReader.GetRequiredDate(payload, "periodEnd");
        var prepStartDate = PayloadReader.GetOptionalDate(payload, "prepStartDate");
        ProjectMetadataRules.RequireValidPeriod(periodStart, periodEnd);
        var createdUtc = DateTimeOffset.UtcNow;
        // INF 抽樣 per-project 種子由 Application entropy 產生一次，Domain 只套既有政策。
        var sampleSeed = Random.Shared.NextInt64(
            1,
            ProjectDocument.SampleSeedExclusiveUpperBound);
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
                principal.Name));
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
            // response 必須先完成物化；從這一行之後不再做可能失敗的 response parse／shape 工作。
            var response = new { projectId = document.ProjectId, ok = true,
                warnings = ProjectMetadataRules.GetWarnings(periodStart, periodEnd, prepStartDate) };
            session.Enter(document.ProjectId, repositories);
            // finalize 與 session 都完成後才結算建案；結算不理會請求取消，只做一次。
            await projectCreateFactsPort.CompleteAsync(facts, CancellationToken.None);
            // 建案確定之後才建立支援紀錄資料夾並記下這次開啟；之前失敗時，回復刪除案件資料夾不受紀錄檔影響。
            SupportCaseDiagnostics.CaseOpened(
                supportHistory, logger, document.ProjectId, document.DatabaseProvider, createdNow: true);
            return response;
        }
        catch
        {
            try
            {
                // compare-and-clear 必須無條件執行：publisher 可能先改 session 再拋例外。
                _ = session.Leave(document.ProjectId);
            }
            catch (Exception)
            {
                // Session compensation must not replace the original finalize／commit failure.
            }

            // Finalize／response 物化／session／final commit 任一步失敗，都沿同一不可取消
            // attempt seam 先釋放本次工作鎖，再 rollback backend transaction 與本機 folder。
            await projectCreateFactsPort.RollbackAsync(facts, CancellationToken.None);
            throw;
        }
    }
}
