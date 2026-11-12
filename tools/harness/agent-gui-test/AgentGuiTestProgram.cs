namespace JET
{
    /// <summary>
    /// Gui 測試版的進入點。讀這次執行的設定，同一次執行的同一個子程序只能開一個，再用正式的啟動順序開主視窗。
    /// </summary>
    internal static class AgentGuiTestProgram
    {
        [STAThread]
        static void Main()
        {
            ProcessCulture.UseGregorianCalendar();
            var profile = AgentGuiTestProfile.LoadRequired();
            using var mutex = new Mutex(
                initiallyOwned: true,
                name: profile.MutexName,
                createdNew: out var mutexCreated);
            if (!mutexCreated)
            {
                throw new InvalidOperationException(
                    $"Another JET AgentGuiTest '{profile.ChildId}' child is already running for this run id.");
            }

            Program.Run(
                new GlobalExceptionFallbackLog(profile.DiagnosticLogDirectory),
                () => new Form1(profile));
        }
    }
}
