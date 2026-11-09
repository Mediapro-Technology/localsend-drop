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

    /// <summary>423 with the reason, so the sender can say which kind of quiet it is.</summary>
    public static Microsoft.AspNetCore.Http.IResult Refusal() =>
        Microsoft.AspNetCore.Http.Results.Text(Settings.Current.DoNotDisturb ? "dnd" : "busy", statusCode: 423);

    /// <summary>Sending side: why they answered 423. Older versions send no reason.</summary>
    public static async Task<string> WhyAsync(HttpResponseMessage resp)
    {
        string body;
        try { body = (await resp.Content.ReadAsStringAsync()).Trim(); } catch { body = ""; }
        return body switch
        {
            "dnd" => "they have Do not disturb on",
            "busy" => "they're presenting or have a full-screen app open, so LocalSend Drop is keeping quiet there",
            _ => "they have Do not disturb on, or they're presenting or full screen",
        };
    }
}
