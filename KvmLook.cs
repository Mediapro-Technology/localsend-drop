namespace LocalSendDrop;

/// <summary>Edge glow, arrival effect and idle-pointer hiding. Stub: T-006 fills it in.</summary>
static class KvmLook
{
    static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
    }
}
