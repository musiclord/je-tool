using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jet.ExcelDriver;

internal sealed class LateBoundExcelAutomation : IExcelAutomationSession
{
    private const int MsoAutomationSecurityForceDisable = 3;
    private const int XlNormalLoad = 0;
    private const int XlExcelLinks = 1;
    private const int XlOleLinks = 2;
    private const int XlCellTypeFormulas = -4123;
    private const int XlErrors = 16;
    private const int XlTypePdf = 0;
    private const int XlQualityStandard = 0;
    private const int NoCellsFoundHresult = unchecked((int)0x800A03EC);

    private object? _application;
    private object? _workbooks;
    private object? _workbook;
    private bool _applicationQuit;
    private bool _disposed;

    internal LateBoundExcelAutomation()
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application", throwOnError: false)
            ?? throw new ExcelUnavailableException();
        try
        {
            _application = Activator.CreateInstance(excelType)
                ?? throw new ExcelUnavailableException();
            dynamic application = _application;
            application.Visible = false;
            application.DisplayAlerts = false;
            application.EnableEvents = false;
            application.ScreenUpdating = false;
            application.AskToUpdateLinks = false;
            _workbooks = application.Workbooks;
        }
        catch (Exception exception) when (exception is COMException
            or InvalidOperationException
            or TargetInvocationException)
        {
            Dispose();
            throw new ExcelUnavailableException(exception);
        }
    }

    public long ApplicationHwnd
    {
        get
        {
            dynamic application = RequireApplication();
            return Convert.ToInt64(application.Hwnd, CultureInfo.InvariantCulture);
        }
    }

    public string? OfficeVersion
    {
        get
        {
            dynamic application = RequireApplication();
            return Convert.ToString(application.Version, CultureInfo.InvariantCulture);
        }
    }

    public void OpenWorkbook(string path, WorkbookOpenPolicy policy)
    {
        if (policy != WorkbookOpenPolicy.SafeReadOnly
            || _workbook is not null
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Excel workbook open policy was rejected.");
        }

        dynamic application = RequireApplication();
        dynamic workbooks = _workbooks
            ?? throw new InvalidOperationException("Excel workbooks collection is unavailable.");
        object? previousAutomationSecurity = null;
        try
        {
            previousAutomationSecurity = application.AutomationSecurity;
            application.AutomationSecurity = MsoAutomationSecurityForceDisable;
            _workbook = workbooks.Open(
                path,
                policy.UpdateLinks,
                policy.ReadOnly,
                Missing.Value,
                Missing.Value,
                Missing.Value,
                policy.IgnoreReadOnlyRecommended,
                Missing.Value,
                Missing.Value,
                policy.Editable,
                policy.Notify,
                Missing.Value,
                policy.AddToMru,
                policy.Local,
                XlNormalLoad);
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or TargetInvocationException)
        {
            throw new ExcelWorkbookOpenException(exception);
        }
        finally
        {
            if (previousAutomationSecurity is not null)
            {
                application.AutomationSecurity = previousAutomationSecurity;
            }
        }
    }

    public bool IsWorkbookReadOnly
    {
        get
        {
            dynamic workbook = RequireWorkbook();
            return Convert.ToBoolean(workbook.ReadOnly, CultureInfo.InvariantCulture);
        }
    }

    public int ExternalLinkCount => checked(CountLinks(XlExcelLinks) + CountLinks(XlOleLinks));

    public long CountFormulaErrors()
    {
        dynamic workbook = RequireWorkbook();
        object? worksheets = null;
        long total = 0;
        try
        {
            worksheets = workbook.Worksheets;
            dynamic worksheetCollection = worksheets;
            var count = Convert.ToInt32(worksheetCollection.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= count; index++)
            {
                object? worksheet = null;
                object? usedRange = null;
                object? errorCells = null;
                try
                {
                    worksheet = worksheetCollection.Item[index];
                    dynamic sheet = worksheet;
                    usedRange = sheet.UsedRange;
                    dynamic cells = usedRange;
                    errorCells = cells.SpecialCells(XlCellTypeFormulas, XlErrors);
                    dynamic errors = errorCells;
                    total = checked(total + Convert.ToInt64(errors.CountLarge, CultureInfo.InvariantCulture));
                }
                catch (COMException exception) when (exception.HResult == NoCellsFoundHresult)
                {
                }
                finally
                {
                    ReleaseCom(ref errorCells);
                    ReleaseCom(ref usedRange);
                    ReleaseCom(ref worksheet);
                }
            }
        }
        finally
        {
            ReleaseCom(ref worksheets);
        }

        return total;
    }

    public void CalculateFullRebuild()
    {
        dynamic application = RequireApplication();
        application.CalculateFullRebuild();
    }

    public void SaveCopyAs(string path)
    {
        EnsureOutputPath(path, ".xlsx");
        dynamic workbook = RequireWorkbook();
        workbook.SaveCopyAs(path);
    }

    public void ExportFirstPagePdf(string path)
    {
        EnsureOutputPath(path, ".pdf");
        dynamic workbook = RequireWorkbook();
        workbook.ExportAsFixedFormat(
            XlTypePdf,
            path,
            XlQualityStandard,
            true,
            true,
            1,
            1,
            false,
            Missing.Value);
    }

    public void CloseWorkbook()
    {
        if (_workbook is null)
        {
            return;
        }

        try
        {
            dynamic workbook = _workbook;
            workbook.Close(false, Missing.Value, Missing.Value);
        }
        finally
        {
            ReleaseCom(ref _workbook);
        }
    }

    public void Quit()
    {
        if (_application is null || _applicationQuit)
        {
            return;
        }

        dynamic application = _application;
        application.Quit();
        _applicationQuit = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseCom(ref _workbook);
        ReleaseCom(ref _workbooks);
        ReleaseCom(ref _application);
    }

    private int CountLinks(int linkType)
    {
        dynamic workbook = RequireWorkbook();
        object? links = workbook.LinkSources(linkType);
        return links switch
        {
            null => 0,
            Array array => array.Length,
            _ => throw new InvalidDataException("Excel returned an unexpected link inventory.")
        };
    }

    private static void EnsureOutputPath(string path, string extension)
    {
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
            || File.Exists(path))
        {
            throw new InvalidDataException("Excel output path was rejected.");
        }
    }

    private object RequireApplication() => _application
        ?? throw new InvalidOperationException("Excel automation is unavailable.");

    private object RequireWorkbook() => _workbook
        ?? throw new InvalidOperationException("Excel workbook is not open.");

    private static void ReleaseCom(ref object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                _ = Marshal.FinalReleaseComObject(value);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or COMException
            or InvalidComObjectException)
        {
        }
        finally
        {
            value = null;
        }
    }
}
