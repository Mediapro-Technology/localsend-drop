using System.IO.Pipes;
using Microsoft.Win32;

namespace LocalSendDrop;

/// <summary>
/// "Send with LocalSend Drop" in Explorer's right-click menu (per user, no admin).
/// Explorer starts one process per selected item; each hands its path to the
/// running copy over a named pipe, which collects them into one send.
/// </summary>
static class Shell
{
    const string PipeName = "LocalSendDrop.share";
    const string Verb = "LocalSendDrop";

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        foreach (var cls in new[] { "*", "Directory" })
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell\{Verb}");
                key.SetValue("MUIVerb", "Send with LocalSend Drop");
                key.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = key.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" --share \"%1\"");
            }
            catch { }
        }
    }

    public static void Unregister()
    {
        foreach (var cls in new[] { "*", "Directory" })
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{cls}\shell\{Verb}", false); } catch { }
    }

    /// <summary>Second instance: pass paths to the running copy. False if none is running.</summary>
    public static bool SendToRunning(IEnumerable<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(5000); // the running copy may still be starting
            using var w = new StreamWriter(pipe);
            foreach (var p in paths) w.WriteLine(p);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Running copy: receive paths, batching ones that arrive close together.</summary>
    /// <summary>Second instance: pass shared text (from the Share sheet) to the running copy.</summary>
    public static bool SendTextToRunning(string text) =>
        SendToRunning([TextPrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))]);

    const string TextPrefix = "TEXT:";

    public static void Listen(Action<List<string>> onPaths, Action<string> onText)
    {
        var batch = new List<string>();
        System.Threading.Timer? flush = null;
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe);
                    while (r.ReadLine() is { } line)
                    {
                        if (line.StartsWith(TextPrefix))
                            onText(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line[TextPrefix.Length..])));
                        else if (line.Length > 0) lock (batch) batch.Add(line);
                    }
                    // Explorer launches the items one after another; wait for the rest.
                    flush?.Dispose();
                    flush = new System.Threading.Timer(_ =>
                    {
                        List<string> paths;
                        lock (batch) { paths = batch.Distinct().ToList(); batch.Clear(); }
                        if (paths.Count > 0) onPaths(paths);
                    }, null, 400, Timeout.Infinite);
                }
                catch { Thread.Sleep(200); }
            }
        }) { IsBackground = true }.Start();
    }
}
