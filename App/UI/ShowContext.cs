// =====================================================================================
//  ShowContext.cs
// -------------------------------------------------------------------------------------
//  Objet partage entre tous les panneaux de l'IHM : le moteur, la liaison, la
//  configuration ouverte, et quelques evenements pour que les onglets restent
//  synchronises entre eux (ex. rappel d'une scene dans l'onglet Soiree -> les
//  curseurs des onglets d'appareils se mettent a jour).
// =====================================================================================

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.UI;

internal sealed class ShowContext
{
    public ShowContext(ShowEngine engine, DmxLink link, ShowConfig show)
    {
        Engine = engine;
        Link = link;
        Show = show;
    }

    public ShowEngine Engine { get; }
    public DmxLink Link { get; }

    /// <summary>Configuration ouverte. Toute modification de structure passe par Engine.EditShow().</summary>
    public ShowConfig Show { get; set; }

    /// <summary>true si la configuration a des modifications non enregistrees.</summary>
    public bool IsDirty { get; private set; }

    public event Action? DirtyChanged;

    /// <summary>Valeurs / effets / selection changes par un panneau : les autres se rafraichissent.</summary>
    public event Action? LiveStateChanged;

    /// <summary>Presets ou cases "verifie" d'un profil modifies (sans changer le nombre de canaux).</summary>
    public event Action? ProfileDetailsChanged;

    public event Action? DebugModeChanged;

    /// <summary>Message court pour la barre d'etat et le journal de l'onglet Debug.</summary>
    public event Action<string>? StatusMessage;

    public void MarkDirty()
    {
        if (!IsDirty)
        {
            IsDirty = true;
            DirtyChanged?.Invoke();
        }
    }

    public void ClearDirty()
    {
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke();
        }
    }

    public void NotifyLiveStateChanged() => LiveStateChanged?.Invoke();
    public void NotifyProfileDetailsChanged() => ProfileDetailsChanged?.Invoke();
    public void NotifyDebugModeChanged() => DebugModeChanged?.Invoke();
    public void Status(string message) => StatusMessage?.Invoke(message);

    public FixtureProfile? ProfileOf(FixtureInstance fixture) => Show.FindProfile(fixture.ProfileId);
}
