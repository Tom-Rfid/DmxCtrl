// =====================================================================================
//  Program.cs - point d'entree de l'application Windows.
// =====================================================================================

using DmxController.Core.Storage;

namespace DmxController;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // The language must be known before any window is built (windows read their texts once).
        Tr.Language = ShowStore.LoadAppSettings().Language;

        // Pendant une soiree, une erreur inattendue dans l'IHM ne doit pas fermer le
        // programme (et couper la lumiere) : on l'affiche et on continue.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(Tr.F("Erreur inattendue :\n{0}\n\nLe programme continue de fonctionner.", e.Exception.Message),
                Tr.T("Contrôleur DMX"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // Une tache de fond en erreur que personne n'attend (port Bluetooth mort...) ne doit pas
        // faire planter le programme.
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        Application.Run(new UI.MainForm());
    }
}
