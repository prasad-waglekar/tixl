// TixlWebHost - out-of-process Chromium (CEF, off-screen) renderer for TiXL's Web operators.
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.OffScreen;

namespace TixlWebHost;

internal static unsafe class Program
{
    private const int HeaderSize = 64;
    private const int MaxDimension = 8192;

    private static readonly object _gate = new();
    private static readonly object _writeLock = new();
    private static readonly ManualResetEventSlim _exit = new(false);

    private static StreamWriter? _writer;
    private static string _pipeName = "";

    private static ChromiumWebBrowser? _browser;
    private static volatile bool _paused;
    private static long _lastConsoleSendTicks;
    private static volatile bool _syncTime;
    private static double _lastTimeMs;

    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _basePtr;
    private static int _frameWidth;
    private static int _frameHeight;
    private static long _slotSize;
    private static long _frameCounter;
    private static int _generation;

    private static int Main(string[] args)
    {
        var parentPid = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe")
                _pipeName = args[i + 1];
            else if (args[i] == "--parent")
                int.TryParse(args[i + 1], out parentPid);
        }

        if (_pipeName.Length == 0)
        {
            Console.Error.WriteLine("Usage: TixlWebHost --pipe <name> [--parent <pid>]");
            return 2;
        }

        try
        {
            if (!InitializeCef())
                return 3;

            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(15000);

            var utf8 = new UTF8Encoding(false);
            _writer = new StreamWriter(pipe, utf8, 1024, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true);

            Send(new { evt = "ready" });
            StartParentWatch(parentPid);

            var readerThread = new Thread(() => ReadLoop(reader)) { IsBackground = true, Name = "pipe-reader" };
            readerThread.Start();

            _exit.Wait();
            Shutdown();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            try { Shutdown(); } catch { }
            return 1;
        }
    }

    private static bool InitializeCef()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "TixlWebHost");
        Directory.CreateDirectory(logDir);

        var settings = new CefSettings
                           {
                               LogSeverity = LogSeverity.Warning,
                               LogFile = Path.Combine(logDir, $"cef-{Environment.ProcessId}.log"),
                           };

        settings.CefCommandLineArgs.Add("allow-file-access-from-files");
        settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

        if (Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null))
            return true;

        Console.Error.WriteLine("CEF initialization failed.");
        return false;
    }

    private static void StartParentWatch(int parentPid)
    {
        if (parentPid == 0)
            return;

        var thread = new Thread(() =>
                                {
                                    try
                                    {
                                        using var parent = Process.GetProcessById(parentPid);
                                        parent.WaitForExit();
                                    }
                                    catch { }

                                    _exit.Set();
                                }) { IsBackground = true, Name = "parent-watch" };
        thread.Start();
    }

    private static void ReadLoop(StreamReader reader)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;

                try { Handle(line); }
                catch (Exception e) { Send(new { evt = "error", message = e.Message }); }
            }
        }
        catch (IOException) { }
        finally { _exit.Set(); }
    }

    private static void Handle(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        switch (root.GetProperty("cmd").GetString())
        {
            case "open":
                var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "about:blank" : "about:blank";
                Open(url,
                     Dimension(GetInt(root, "width", 1280)),
                     Dimension(GetInt(root, "height", 720)),
                     Math.Clamp(GetInt(root, "fps", 60), 1, 240),
                     GetBool(root, "transparent", false),
                     GetBool(root, "paused", false),
                     GetBool(root, "sync", false),
                     GetDouble(root, "timeMs", 0));
                break;

            case "reload":
                lock (_gate)
                    _browser?.Reload(true);
                break;

            case "resize":
                lock (_gate)
                {
                    if (_browser != null)
                        _browser.Size = new Size(Dimension(GetInt(root, "width", 1280)), Dimension(GetInt(root, "height", 720)));
                }
                break;

            case "fps":
                lock (_gate)
                {
                    var host = _browser?.GetBrowserHost();
                    if (host != null)
                        host.WindowlessFrameRate = Math.Clamp(GetInt(root, "value", 60), 1, 240);
                }
                break;

            case "pause":
                SetPaused(GetBool(root, "value", false));
                break;

            case "time":
                SetTime(GetDouble(root, "ms", 0));
                break;

            case "close":
                _exit.Set();
                break;
        }
    }

    private static void Open(string url, int width, int height, int fps, bool transparent, bool paused, bool syncTime, double timeMs)
    {
        ChromiumWebBrowser? previous;
        lock (_gate)
        {
            previous = DetachBrowserLocked();
            _paused = paused;
            _syncTime = syncTime;
            _lastTimeMs = timeMs;
        }

        previous?.Dispose();

        var browserSettings = new BrowserSettings
                                  {
                                      WindowlessFrameRate = fps,
                                      BackgroundColor = transparent
                                                            ? Cef.ColorSetARGB(0, 0, 0, 0)
                                                            : Cef.ColorSetARGB(255, 255, 255, 255),
                                  };

        // With time sync the page must start only after the time script is registered, so begin on about:blank.
        var nav = new NavState { Started = !syncTime };
        var browser = new ChromiumWebBrowser(syncTime ? "about:blank" : url, browserSettings);
        browser.Size = new Size(width, height);
        browser.Paint += OnPaint;
        browser.LoadingStateChanged += (_, e) =>
                                       {
                                           if (!nav.Started)
                                               return; // still on about:blank

                                           Send(new { evt = "loading", isLoading = e.IsLoading });

                                           // Run the page's first animation callbacks even if TiXL's time is standing