// =====================================================================================
//  Program.cs - entry point of the Windows application.
// =====================================================================================

using DmxController.Core.Storage;

namespace DmxController;

/// <summary>Application entry point.</summary>
static class Program
{
    /// <summary>Starts the application: reads the language, installs the crash guards, then opens the main window.</summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // The language must be known before any window is built (windows read their texts once).
        Tr.Language = ShowStore.LoadAppSettings().Language;

        // During a show, an unexpected error in the UI must not close the program (and cut
        // the light): it is shown and the program keeps running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(Tr.F("Erreur inattendue :\n{0}\n\nLe programme continue de fonctionner.", e.Exception.Message),
                Tr.T("Contrôleur DMX"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // A failed background task nobody waits for (dead Bluetooth port...) must not crash
        // the program.
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        Application.Run(new UI.MainForm());
    }
}
