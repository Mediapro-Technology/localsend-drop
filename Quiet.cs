using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// Do not disturb: switched on by hand, or automatically while Windows itself holds
/// back notifications (presentation mode, a fullscreen app or game).
/// </summary>
static class Quiet
{
    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Presenting or fullscreen right now, and the user lets that count.</summary>
    public static bool Auto
    {
        get
        {
            if (!Settings.Current.AutoQuiet) return false;
            try
            {
                // QUNS_BUSY (fullscreen app), QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
                return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
            }
            catch { return false; }
        }
    }

    public static bool Active => Settings.Current.DoNotDisturb || Auto;
}
