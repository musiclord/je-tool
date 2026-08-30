using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class ProjectCreateHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ICaseCreateFactsPort caseCreateFactsPort;
    private readonly CurrentPrincipal principal;
    private readonly IProjectSessionPublisher session;

    internal ProjectCreateHandler(
        IProjectStore projectStore,
        ICaseCreateFactsPort caseCreateFactsPort,
        CurrentPrincipal principal,
        IProjectSessionPublisher session)
    {
        this.projectStore = projectStore;
        this.caseCreateFactsPort = caseCreateFactsPort;
        this.principal = principal;
        this.session = session;
    }

    public string Action => "project.create";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // provider 只在建立時選定(之後不可改);僅接受 sqlite / sqlServer / duckdb。
        var databaseProvider = PayloadReader.GetOptionalString(payload, "databaseProvider")
            ?? ProjectDocument.DefaultDatabaseProvider;
        if (databaseProvider != ProjectDocument.DefaultDatabaseProvider
            && databaseProvider != ProjectDocument.SqlServerDatabaseProvider
            && databaseProvider != ProjectDocument.DuckDbDatabaseProvider)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"未支援的 databaseProvider '{databaseProvider}'(僅接受 sqlite / sqlServer / duckdb)。");
        }

        // 選填 caseName:有值則驗證 + 唯一性檢查並作為 projectId/資料夾名;無值回退 GUID(既有程式化/測試建立行為)。
        var caseNameRaw = PayloadReader.GetOptionalString(payload, "caseName");
        string projectId;
        if (!string.IsNullOrWhiteSpace(caseNameRaw))
        {
            var caseName = caseNameRaw.Trim();
            var nameError = ProjectNameRules.Validate(caseName);
            if (nameError is not null)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    nameError,
                    JetErrorFields.CaseName);
            }

            // 同名資料夾即視為重複(provider 無關;sqlServer 的後端殘留檢查在 projectId 決定後、
            // 落檔前另行執行,見下)。
            if (await projectStore.FindAsync(caseName, cancellationToken) is not null)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"案件名稱『{caseName}』已存在，請換一個。",
                    JetErrorFields.CaseName);
            }

            projectId = caseName;
        }
        else
        {
            projectId = Guid.NewGuid().ToString("N");
        }

        // 保留既有失敗優先序：SQL orphan preflight 先於 required payload
        // 解析；技術前置檢查不屬 Domain 案件政策，也不建立第二套 audit decision。
        await caseCreateFactsPort.PreflightAsync(
            new CaseCreatePreflightRequest(
                projectId,
                databaseProvider,
                HasUserSuppliedCaseName: !string.IsNullOrWhiteSpace(caseNameRaw)),
            cancellationToken);

        var projectCode = PayloadReader.GetRequiredString(payload, "projectCode");
        var entityName = PayloadReader.GetRequiredString(payload, "entityName");
        var operatorId = PayloadReader.GetRequiredString(payload, "operatorId");
        var periodStart = PayloadReader.GetRequiredDate(payload, "periodStart");
        var periodEnd = PayloadReader.GetRequiredDate(payload, "periodEnd");
        var lastPeriodStart = PayloadReader.GetOptionalDate(payload, "lastPeriodStart");
        var createdUtc = DateTimeOffset.UtcNow;
        // INF 抽樣 per-project 種子由 Application entropy 產生一次，Domain 只套既有政策。
        var sampleSeed = Random.Shared.NextInt64(
            1,
            ProjectDocument.SampleSeedExclusiveUpperBound);
        var document = ProjectDocument.CreateNew(
            projectId,
            projectCode,
            entityName,
            operatorId,
            periodStart,
            periodEnd,
            lastPeriodStart,
            databaseProvider,
            createdUtc,
            sampleSeed,
            JetAuditProgram.CurrentInfSamplingAlgorithmVersion);
        var plan = JetAuditProgram.Plan(
            new CaseCreateRequest(
                document,
                HasUserSuppliedCaseName: !string.IsNullOrWhiteSpace(caseNameRaw),
                principal.Name));
        CaseCreateFacts facts;
        try
        {
            facts = await JetAuditProgram.ExecuteAsync(
                plan,
                caseCreateFactsPort,
                cancellationToken);
        }
        catch (CaseCreateLockHeldException held)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectLocked,
                ProjectLockErrorMessage.Format(
                    held.LockedBy,
                    held.MachineName,
                    held.LockedUtc));
        }
        catch (CaseCreateExistingWorkLockException)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectLocked,
                "案件已有目前工作階段持有的工作鎖，無法重新建立同名案件。");
        }

        try
        {
            var result = JetAuditProgram.Finalize(plan, facts);
            document = result.Document;
            // response 必須先完成物化；從這一行之後不再做可能失敗的 response parse／shape 工作。
            var response = new { projectId = document.ProjectId, ok = true };
            session.Enter(document.ProjectId);
            // SQL schema remains in an uncommitted create transaction until finalize／session are ready.
            // Final registry／access／create-audit publication ignores request cancellation and commits once.
            await caseCreateFactsPort.CompleteAsync(facts, CancellationToken.None);
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
            await caseCreateFactsPort.RollbackAsync(facts, CancellationToken.None);
            throw;
        }
    }
}
