using JET.Application.Contracts;

namespace JET.Application.DemoData
{
    public sealed class DeterministicDemoProjectDataGenerator : IDemoProjectDataGenerator
    {
        private const string DocNumber = "Document Number";
        private const string LineItem = "Line Item";
        private const string PostDate = "Post Date";
        private const string ApprovalDate = "Approval Date";
        private const string AccountNumber = "Account Number";
        private const string AccountName = "Account Name";
        private const string Description = "Description";
        private const string SourceModule = "Source Module";
        private const string CreatedBy = "Created By";
        private const string ApprovedBy = "Approved By";
        private const string Manual = "Manual";
        private const string Amount = "Amount";
        private const string ChangeAmount = "Change Amount";

        public DemoProjectDataBundle Generate()
        {
            var projectCode = "JET-DEMO-2024-LARGE";
            var entityName = "JET Demo Manufacturing Group";
            var operatorId = "demo.agent";
            var industry = "製造業";
            var periodStart = new DateTime(2024, 1, 1);
            var periodEnd = new DateTime(2024, 12, 31);
            var lastPeriodStart = new DateTime(2024, 12, 20);
            var random = new Random(240101);

            var accounts = BuildAccounts();
            var validGlRows = BuildBalancedGlRows(accounts, periodStart, periodEnd, random);
            var invalidGlRows = BuildInvalidGlRows(accounts, periodStart, periodEnd, random);
            var allGlRows = validGlRows.Concat(invalidGlRows).ToList();
            var tbRows = BuildTbRows(accounts, validGlRows);
            var accountMappingRows = BuildAccountMappingRows(accounts);

            var project = new DemoProjectDto(
                projectCode,
                entityName,
                operatorId,
                industry,
                periodStart.ToString("yyyy-MM-dd"),
                periodEnd.ToString("yyyy-MM-dd"),
                lastPeriodStart.ToString("yyyy-MM-dd"),
                "demo-large-gl.xlsx",
                "demo-large-tb.xlsx",
                "demo-account-mapping.xlsx",
                new Dictionary<string, string>
                {
                    ["docNum"] = DocNumber,
                    ["lineID"] = LineItem,
                    ["postDate"] = PostDate,
                    ["docDate"] = ApprovalDate,
                    ["accNum"] = AccountNumber,
                    ["accName"] = AccountName,
                    ["description"] = Description,
                    ["jeSource"] = SourceModule,
                    ["createBy"] = CreatedBy,
                    ["approveBy"] = ApprovedBy,
                    ["manual"] = Manual,
                    ["amount"] = Amount
                },
                new Dictionary<string, string>
                {
                    ["accNum"] = AccountNumber,
                    ["accName"] = AccountName,
                    ["amount"] = ChangeAmount
                },
                new List<string> { "2024-02-08", "2024-02-09", "2024-04-04", "2024-05-01", "2024-09-17", "2024-10-10" },
                new List<string> { "2024-02-17" },
                new List<int> { 6, 0 });

            var glColumns = new List<string> { DocNumber, LineItem, PostDate, ApprovalDate, AccountNumber, AccountName, Description, SourceModule, CreatedBy, ApprovedBy, Manual, Amount };
            var tbColumns = new List<string> { AccountNumber, AccountName, ChangeAmount };

            var gl = new DemoGlRowsDto(project.GlFileName, allGlRows, glColumns);
            var tb = new DemoTbRowsDto(project.TbFileName, tbRows, tbColumns);
            var accountMapping = new DemoAccountMappingRowsDto(project.AccountMappingFileName, accountMappingRows);

            return new DemoProjectDataBundle(project, gl, tb, accountMapping, invalidGlRows);
        }

        private static List<AccountSeed> BuildAccounts()
        {
            var accounts = new List<AccountSeed>();

            accounts.AddRange(CreateRange("100", 1001, 1010, "現金及約當現金", "Cash"));
            accounts.AddRange(CreateRange("110", 1101, 1120, "應收帳款", "Receivables"));
            accounts.AddRange(CreateRange("120", 1201, 1215, "存貨", "Others"));
            accounts.AddRange(CreateRange("130", 1301, 1310, "預付款項", "Others"));
            accounts.AddRange(CreateRange("210", 2101, 2115, "應付帳款", "Others"));
            accounts.AddRange(CreateRange("220", 2201, 2210, "預收款項", "Receipt in advance"));
            accounts.AddRange(CreateRange("310", 3101, 3110, "股本及資本公積", "Others"));
            accounts.AddRange(CreateRange("400", 4001, 4030, "營業收入", "Revenue"));
            accounts.AddRange(CreateRange("500", 5001, 5015, "營業成本", "Others"));
            accounts.AddRange(CreateRange("610", 6101, 6130, "營業費用", "Others"));
            accounts.AddRange(CreateRange("650", 6501, 6510, "其他費用", "Others"));

            return accounts;
        }

        private static IEnumerable<AccountSeed> CreateRange(string prefix, int start, int end, string baseName, string category)
        {
            for (var code = start; code <= end; code++)
            {
                yield return new AccountSeed(code.ToString(), $"{baseName}{code - start + 1:00}", category);
            }
        }

        private static List<Dictionary<string, object?>> BuildBalancedGlRows(
            IReadOnlyList<AccountSeed> accounts,
            DateTime periodStart,
            DateTime periodEnd,
            Random random)
        {
            var receivables = accounts.Where(x => x.Category == "Receivables").ToList();
            var revenue = accounts.Where(x => x.Category == "Revenue").ToList();
            var cash = accounts.Where(x => x.Category == "Cash").ToList();
            var expense = accounts.Where(x => x.Code.StartsWith("61") || x.Code.StartsWith("65")).ToList();
            var payable = accounts.Where(x => x.Code.StartsWith("21")).ToList();
            var advance = accounts.Where(x => x.Category == "Receipt in advance").ToList();
            var inventory = accounts.Where(x => x.Code.StartsWith("12")).ToList();

            var descriptions = new[]
            {
                "Monthly revenue recognition",
                "Collection of receivables",
                "Vendor payment",
                "Inventory procurement",
                "Payroll accrual",
                "Expense reimbursement",
                "Adj year-end reserve",
                "Reclass operating expense",
                "Routine sales invoice",
                "Manual correction entry"
            };
            var creators = new[] { "amy.lin", "bob.wu", "carol.tsai", "david.chen", "eva.huang", "frank.liu" };
            var approvers = new[] { "manager.chen", "controller.lin", "director.wang" };
            var modules = new[] { "AR", "AP", "GL", "INV" };

            var rows = new List<Dictionary<string, object?>>(2220);
            var voucherCount = 1100;
            for (var i = 1; i <= voucherCount; i++)
            {
                var postDate = periodStart.AddDays(random.Next((periodEnd - periodStart).Days + 1));
                var approvalDate = postDate.AddDays(random.Next(0, 4));
                if (approvalDate > periodEnd.AddDays(10)) approvalDate = periodEnd;
                var creator = creators[random.Next(creators.Length)];
                var approver = approvers[random.Next(approvers.Length)];
                var module = modules[random.Next(modules.Length)];
                var manualFlag = random.NextDouble() < 0.38 ? 1 : 0;
                var amount = Math.Round((decimal)(random.Next(8_000, 900_000) / 100.0) * 100m, 0);
                var docNum = $"JE-2024-{i:0000}";
                var description = descriptions[random.Next(descriptions.Length)];

                AccountSeed debit;
                AccountSeed credit;
                switch (i % 5)
                {
                    case 0:
                        debit = expense[random.Next(expense.Count)];
                        credit = cash[random.Next(cash.Count)];
                        break;
                    case 1:
                        debit = receivables[random.Next(receivables.Count)];
                        credit = revenue[random.Next(revenue.Count)];
                        break;
                    case 2:
                        debit = inventory[random.Next(inventory.Count)];
                        credit = payable[random.Next(payable.Count)];
                        break;
                    case 3:
                        debit = cash[random.Next(cash.Count)];
                        credit = receivables[random.Next(receivables.Count)];
                        break;
                    default:
                        debit = cash[random.Next(cash.Count)];
                        credit = advance[random.Next(advance.Count)];
                        break;
                }

                rows.Add(CreateGlRow(docNum, 1, postDate, approvalDate, debit, description, module, creator, approver, manualFlag, amount));
                rows.Add(CreateGlRow(docNum, 2, postDate, approvalDate, credit, description, module, creator, approver, manualFlag, -amount));
            }

            return rows;
        }

        private static List<Dictionary<string, object?>> BuildInvalidGlRows(
            IReadOnlyList<AccountSeed> accounts,
            DateTime periodStart,
            DateTime periodEnd,
            Random random)
        {
            var rows = new List<Dictionary<string, object?>>(20);
            var miscExpense = accounts.First(x => x.Code.StartsWith("61"));
            var revenue = accounts.First(x => x.Category == "Revenue");
            var cash = accounts.First(x => x.Category == "Cash");
            var receivable = accounts.First(x => x.Category == "Receivables");

            for (var i = 1; i <= 5; i++)
            {
                rows.Add(CreateGlRow($"BAD-NULL-ACC-{i:00}", 1, periodStart.AddDays(i), periodStart.AddDays(i + 1), miscExpense, "Missing account test", "GL", "qa.user", "qa.manager", 1, 1000m, overrideAccountCode: string.Empty));
            }

            for (var i = 1; i <= 5; i++)
            {
                rows.Add(CreateGlRow(string.Empty, 1, periodStart.AddDays(10 + i), periodStart.AddDays(11 + i), cash, "Missing voucher number", "GL", "qa.user", "qa.manager", 1, 1200m));
            }

            for (var i = 1; i <= 5; i++)
            {
                rows.Add(CreateGlRow($"BAD-NULL-DESC-{i:00}", 1, periodStart.AddDays(20 + i), periodStart.AddDays(21 + i), receivable, string.Empty, "AR", "qa.user", "qa.manager", 0, 1300m));
            }

            rows.Add(CreateGlRow("BAD-OOB-01", 1, periodEnd.AddDays(3), periodEnd.AddDays(4), revenue, "Out of audit period", "GL", "qa.user", "qa.manager", 1, -5000m));
            rows.Add(CreateGlRow("BAD-OOB-02", 1, periodEnd.AddDays(5), periodEnd.AddDays(6), cash, "Out of audit period", "GL", "qa.user", "qa.manager", 1, 5000m));

            rows.Add(CreateGlRow("BAD-UNBAL-01", 1, periodStart.AddDays(60), periodStart.AddDays(61), cash, "Unbalanced voucher debit", "GL", "qa.user", "qa.manager", 1, 9000m));
            rows.Add(CreateGlRow("BAD-UNBAL-01", 2, periodStart.AddDays(60), periodStart.AddDays(61), revenue, "Unbalanced voucher credit", "GL", "qa.user", "qa.manager", 1, -7000m));
            rows.Add(CreateGlRow("BAD-UNBAL-02", 1, periodStart.AddDays(75), periodStart.AddDays(76), miscExpense, "Unbalanced voucher debit", "GL", "qa.user", "qa.manager", 1, 8000m));

            return rows;
        }

        private static List<Dictionary<string, object?>> BuildTbRows(
            IReadOnlyList<AccountSeed> accounts,
            IReadOnlyList<Dictionary<string, object?>> validGlRows)
        {
            var sums = validGlRows
                .GroupBy(row => row[AccountNumber]?.ToString() ?? string.Empty)
                .ToDictionary(group => group.Key, group => group.Sum(row => Convert.ToDecimal(row[Amount] ?? 0m)));

            return accounts
                .Select(account => new Dictionary<string, object?>
                {
                    [AccountNumber] = account.Code,
                    [AccountName] = account.Name,
                    [ChangeAmount] = sums.TryGetValue(account.Code, out var amount) ? amount : 0m
                })
                .ToList();
        }

        private static List<Dictionary<string, object?>> BuildAccountMappingRows(IReadOnlyList<AccountSeed> accounts)
        {
            return accounts
                .Select(account => new Dictionary<string, object?>
                {
                    ["Account Code"] = account.Code,
                    ["Account Name"] = account.Name,
                    ["Category"] = account.Category
                })
                .ToList();
        }

        private static Dictionary<string, object?> CreateGlRow(
            string documentNumber,
            int lineItem,
            DateTime postDate,
            DateTime approvalDate,
            AccountSeed account,
            string description,
            string sourceModule,
            string createdBy,
            string approvedBy,
            int manual,
            decimal amount,
            string? overrideAccountCode = null)
        {
            return new Dictionary<string, object?>
            {
                [DocNumber] = documentNumber,
                [LineItem] = lineItem,
                [PostDate] = postDate.ToString("yyyy-MM-dd"),
                [ApprovalDate] = approvalDate.ToString("yyyy-MM-dd"),
                [AccountNumber] = overrideAccountCode ?? account.Code,
                [AccountName] = account.Name,
                [Description] = description,
                [SourceModule] = sourceModule,
                [CreatedBy] = createdBy,
                [ApprovedBy] = approvedBy,
                [Manual] = manual,
                [Amount] = amount
            };
        }

        private sealed record AccountSeed(string Code, string Name, string Category);
    }
}
