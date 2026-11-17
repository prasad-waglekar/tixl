namespace TixlWebHost;

/// <summary>
/// Centralized IPC protocol constants shared between TiXL operators and TixlWebHost.
/// </summary>
internal static class WebProtocol
{
    public static class Commands
    {
        public const string Open = "open";
        public const string Reload = "reload";
        public const string Dom = "dom";
        public const string Resize = "resize";
        public const string Fps = "fps";
        public const string Pause = "pause";
        public const string Time = "time";
        public const string Close = "close";
    }

    public static class Events
    {
        public const string Ready = "ready";
        public const string Loading = "loading";
        public const string LoadError = "loadError";
        public const string Console = "console";
        public const string DomMissing = "domMissing";
        public const string Frames = "frames";
        public const string Error = "error";
    }
}
