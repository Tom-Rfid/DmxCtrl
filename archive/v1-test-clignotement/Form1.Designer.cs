// =====================================================================================
//  Form1.Designer.cs
// -------------------------------------------------------------------------------------
//  Partie "generee par le designer" de la fenetre. On la garde volontairement
//  minimale : la fenetre contient 43 sliders (un par canal DMX utilise), ce qui
//  serait illisible a la main dans l'editeur visuel de Visual Studio. On cree donc
//  tous ces controles PAR CODE dans Form1.cs (methode BuildUserInterface()),
//  appelee juste apres InitializeComponent(). C'est une pratique WinForms tout a
//  fait normale pour des listes de controles generees dynamiquement.
// =====================================================================================

namespace DmxController;

partial class Form1
{
    /// <summary>
    ///  Variable requise par le designer.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Libere les ressources utilisees.
    /// </summary>
    /// <param name="disposing">true si les ressources managees doivent etre liberees.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            // On s'assure de bien fermer le port COM et d'arreter le thread
            // d'envoi si la fenetre se ferme alors qu'on etait connecte.
            _dmxLink?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Code genere par le concepteur Windows Form

    /// <summary>
    ///  Methode requise pour la prise en charge du concepteur - a ne pas modifier
    ///  manuellement avec l'editeur de code.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(960, 720);
        MinimumSize = new Size(820, 560);
        Text = "Controleur DMX - Boitier RY-AI Bluetooth (reverse engineering)";
        StartPosition = FormStartPosition.CenterScreen;
    }

    #endregion
}
