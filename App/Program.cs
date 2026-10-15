// =====================================================================================
//  Program.cs - point d'entree de l'application Windows.
// =====================================================================================

namespace DmxController;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Pendant une soiree, une erreur inattendue dans l'IHM ne doit pas fermer le
        // programme (et couper la lumiere) : on l'affiche et on continue.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"Erreur inattendue :\n{e.Exception.Message}\n\nLe programme continue de fonctionner.",
                "Contrôleur DMX", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        Application.Run(new UI.MainForm());
    }
}
