namespace LocalSendDrop;

/// <summary>Do not disturb: only ever the switch in the panel or Settings, never automatic.</summary>
static class Quiet
{
    public static bool Active => Settings.Current.DoNotDisturb;

    /// <summary>423 with the reason, so the sender can say which kind of quiet it is.</summary>
    public static Microsoft.AspNetCore.Http.IResult Refusal() =>
        Microsoft.AspNetCore.Http.Results.Text("dnd", statusCode: 423);

    /// <summary>Sending side: why they answered 423. Versions before 2.6.3 send no reason and also went quiet when full screen.</summary>
    public static async Task<string> WhyAsync(HttpResponseMessage resp)
    {
        string body;
        try { body = (await resp.Content.ReadAsStringAsync()).Trim(); } catch { body = ""; }
        return body switch
        {
            "dnd" => "they have Do not disturb on",
            _ => "they have Do not disturb on, or they're full screen (they need the latest LocalSend Drop)",
        };
    }
}
