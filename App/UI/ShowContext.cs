// =====================================================================================
//  ShowContext.cs
// -------------------------------------------------------------------------------------
//  Object shared by every UI panel: the engine, the link, the open configuration,
//  and a few events that keep the tabs in sync with each other (e.g. recalling a
//  scene in the Show tab -> the sliders of the fixture tabs are updated).
// =====================================================================================

using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.UI;

/// <summary>Shared state of the UI (engine, link, configuration, microphone) and synchronisation events between tabs.</summary>
internal sealed class ShowContext
{
    /// <summary>Creates the context for this engine, link and open configuration.</summary>
    public ShowContext(ShowEngine engine, DmxLink link, ShowConfig show)
    {
        Engine = engine;
        Link = link;
        Show = show;
        Mic = new Audio.MicController(engine, () => Show.Settings);
    }

    /// <summary>Show engine (computes the DMX output).</summary>
    public ShowEngine Engine { get; }

    /// <summary>The microphone (tempo and volume), shared by every tab.</summary>
    public Audio.MicController Mic { get; }
    /// <summary>Link with the Bluetooth box.</summary>
    public DmxLink Link { get; }

    /// <summary>Open configuration. Any structural change goes through Engine.EditShow().</summary>
    public ShowConfig Show { get; set; }

    /// <summary>true when the configuration has unsaved changes.</summary>
    public bool IsDirty { get; private set; }

    public event Action? DirtyChanged;

    /// <summary>Values / effects / selection changed by a panel: the others refresh.</summary>
    public event Action? LiveStateChanged;

    /// <summary>Presets or "checked" boxes of a profile changed (without changing the channel count).</summary>
    public event Action? ProfileDetailsChanged;

    public event Action? DebugModeChanged;

    /// <summary>Short message for the status bar and the log of the Debug tab.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>Marks the configuration as modified (title "*", save prompt on closing).</summary>
    public void MarkDirty()
    {
        if (!IsDirty)
        {
            IsDirty = true;
            DirtyChanged?.Invoke();
        }
    }

    /// <summary>Marks the configuration as saved.</summary>
    public void ClearDirty()
    {
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke();
        }
    }

    /// <summary>Tells every tab that values, effects or selection changed.</summary>
    public void NotifyLiveStateChanged() => LiveStateChanged?.Invoke();
    /// <summary>Tells every tab that presets or checked boxes of a profile changed.</summary>
    public void NotifyProfileDetailsChanged() => ProfileDetailsChanged?.Invoke();
    /// <summary>Tells the window that debug mode was switched on or off.</summary>
    public void NotifyDebugModeChanged() => DebugModeChanged?.Invoke();
    /// <summary>Shows a short message in the status bar and the Debug log.</summary>
    public void Status(string message) => StatusMessage?.Invoke(message);

    /// <summary>Profile of a fixture in the open configuration (null when missing).</summary>
    public FixtureProfile? ProfileOf(FixtureInstance fixture) => Show.FindProfile(fixture.ProfileId);
}
