// TixlWebHost - out-of-process Chromium (CEF, off-screen) renderer for TiXL's Web operators.
using System.Diagnostics;
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
    private static volatile bool _syncTime = true;
    private static double _lastTimeMs;

    private static readonly object _logLock = new();
    private static string _logPath = "";
    private static (string Name, int Width, int Height, long SlotSize)? _announceFrames;

    private static readonly object _domLock = new();
    private static readonly Dictionary<string, string> _domState = new();
    private static volatile bool _pageReady;
    private static string _lastMissing = "[]";

    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _basePtr;
    private static int _frameWidth;
    private static int _frameHeight;
    private static long _slotSize;
    private static long _frameCounter;
    private static int _generation;

    [STAThread]
    private static int Main(string[] args)
    {
        var parentPid = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe") _pipeName = args[i + 1];
            else if (args[i] == "--parent") int.TryParse(args[i + 1], out parentPid);
        }

        if (_pipeName.Length == 0) return 2;

        try
        {
            var logDir = Path.Combine(Path.GetTempPath(), "TixlWebHost");
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, $"host-{Environment.ProcessId}.log");
        }
        catch
        {
            // logging is best effort
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled exception: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
                                                 {
                                                     Log("Unobserved task exception: " + e.Exception);
                                                     e.SetObserved();
                                                 };
        Log($"TixlWebHost starting (pid {Environment.ProcessId}, parent {parentPid}, CefSharp {typeof(Cef).Assembly.GetName().Version})");

        try
        {
            if (!InitializeCef()) { Log("CEF initialization failed."); return 3; }
            Log("CEF initialized.");

            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
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
            Log("Fatal: " + e);
            try { Shutdown(); } catch { }
            return 1;
        }
    }

    private static bool InitializeCef()
    {
        var baseDir = AppContext.BaseDirectory;
        var logDir = Path.Combine(Path.GetTempPath(), "TixlWebHost");
        var cacheDir = Path.Combine(logDir, $"cache-{Environment.ProcessId}");
        Directory.CreateDirectory(cacheDir);

        var settings = new CefSettings
                           {
                               LogSeverity = LogSeverity.Warning,
                               LogFile = Path.Combine(logDir, $"cef-{Environment.ProcessId}.log"),
                               BrowserSubprocessPath = Path.Combine(baseDir, "CefSharp.BrowserSubprocess.exe"),
                               ResourcesDirPath = baseDir,
                               LocalesDirPath = Path.Combine(baseDir, "locales"),
                               RootCachePath = cacheDir,
                               CachePath = cacheDir,
                           };

        settings.CefCommandLineArgs.Add("allow-file-access-from-files");
        settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
        settings.CefCommandLineArgs.Add("disable-gpu-shader-disk-cache");

        return Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null);
    }

    private static void StartParentWatch(int parentPid)
    {
        if (parentPid == 0) return;
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
                if (line.Length == 0) continue;
                try { Dispatch(line); }
                catch (Exception e) { Log("Bad command: " + e.Message + " :: " + (line.Length > 200 ? line[..200] : line)); }
            }
        }
        catch (IOException) { }
        finally { Log("Pipe closed."); _exit.Set(); }
    }

    private static void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement.Clone();
        var cmd = root.GetProperty("cmd").GetString();

        if (cmd == "close")
        {
            _exit.Set();
            return;
        }

        Cef.UIThreadTaskFactory.StartNew(() =>
                                         {
                                             try { Handle(cmd, root); }
                                             catch (Exception e) { Log($"Command '{cmd}' failed: {e}"); }
                                         });
    }

    private static void Handle(string? cmd, JsonElement root)
    {
        switch (cmd)
        {
            case "open":
                var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "about:blank" : "about:blank";
                var w = Dimension(GetInt(root, "width", 1280));
                var h = Dimension(GetInt(root, "height", 720));
                var fps = Math.Clamp(GetInt(root, "fps", 60), 1, 240);
                var trans = GetBool(root, "transparent", false);
                var p = GetBool(root, "paused", false);
                var st = GetBool(root, "syncTime", true);
                Open(url, w, h, fps, trans, p, st);
                break;

            case "reload":
                _pageReady = false;
                _browser?.Reload(true);
                break;

            case "dom":
                HandleDom(root);
                break;

            case "resize":
                var rw = Dimension(GetInt(root, "width", 1280));
                var rh = Dimension(GetInt(root, "height", 720));
                if (_browser != null)
                    _browser.Size = new Size(rw, rh);
                break;

            case "fps":
                var f = Math.Clamp(GetInt(root, "value", 60), 1, 240);
                if (_browser?.GetBrowserHost() is { } host)
                    host.WindowlessFrameRate = f;
                break;

            case "pause":
                var pauseVal = GetBool(root, "value", false);
                SetPaused(pauseVal);
                break;

            case "time":
                var time = root.TryGetProperty("value", out var timeProp) ? timeProp.GetDouble() : 0.0;
                SetTime(time * 1000.0);
                break;

            case "close":
                _exit.Set();
                break;
        }
    }

    private static void SetTime(double milliseconds)
    {
        _lastTimeMs = milliseconds;
        var browser = _browser;
        if (browser != null && browser.IsBrowserInitialized)
        {
            ApplyTime(browser);
            try { browser.GetBrowserHost()?.Invalidate(PaintElementType.View); } catch { }
        }
    }

    private static void ApplyTime(ChromiumWebBrowser browser)
    {
        if (!_syncTime) return;
        try
        {
            var t = _lastTimeMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            browser.ExecuteScriptAsync($"if (typeof window.__advanceTiXLClock === 'function') window.__advanceTiXLClock({t});");
        }
        catch { }
    }

    private static void Open(string url, int width, int height, int fps, bool transparent, bool paused, bool syncTime)
    {
        _syncTime = syncTime;
        ChromiumWebBrowser? previous;
        lock (_gate)
        {
            previous = DetachBrowserLocked();
            _paused = paused;
        }

        _pageReady = false;
        lock (_domLock)
        {
            _domState.Clear();
            _lastMissing = "[]";
        }

        previous?.Dispose();

        var browserSettings = new BrowserSettings
                                  {
                                      WindowlessFrameRate = fps,
                                      BackgroundColor = transparent ? Cef.ColorSetARGB(0, 0, 0, 0) : Cef.ColorSetARGB(255, 255, 255, 255),
                                  };

        var browser = new ChromiumWebBrowser(string.Empty, browserSettings);
        browser.Size = new Size(width, height);
        browser.Paint += OnPaint;
        browser.LoadingStateChanged += (_, e) =>
                                       {
                                           Log($"loading: {e.IsLoading}");
                                           Send(new { evt = "loading", isLoading = e.IsLoading });
                                           if (e.IsLoading)
                                           {
                                               _pageReady = false;
                                               return;
                                           }
                                           PageLoaded(browser);
                                           ApplyTime(browser);
                                       };
        browser.LoadError += (_, e) =>
                             {
                                 if (e.ErrorCode == CefErrorCode.Aborted) return;
                                 Log($"load error {(int)e.ErrorCode} {e.ErrorText}: {e.FailedUrl}");
                                 Send(new { evt = "loadError", code = (int)e.ErrorCode, text = e.ErrorText, url = e.FailedUrl });
                             };
        
        browser.FrameLoadStart += (_, e) =>
        {
            if (e.Frame.IsMain)
                _pageReady = false;

            if (e.Frame.IsMain && _syncTime)
            {
                e.Browser.ExecuteScriptAsync(@"
                    window.__tixlTimeMs = 0;
                    window.__tixlRafCallbacks = [];
                    window.requestAnimationFrame = function(cb) {
                        window.__tixlRafCallbacks.push(cb);
                        return window.__tixlRafCallbacks.length;
                    };
                    performance.now = function() { return window.__tixlTimeMs; };
                    Date.now = function() { return window.__tixlTimeMs; };
                    window.__advanceTiXLClock = function(tMs) {
                        window.__tixlTimeMs = tMs;
                        if (typeof window.setTiXLTime === 'function') window.setTiXLTime(tMs / 1000.0);
                        const cbs = window.__tixlRafCallbacks.splice(0);
                        for (const cb of cbs) {
                            try { cb(window.__tixlTimeMs); } catch(err) { console.error(err); }
                        }
                    };
                ");
            }
        };

        browser.ConsoleMessage += (_, e) =>
                                  {
                                      var now = Environment.TickCount64;
                                      var prev = Interlocked.Read(ref _lastConsoleSendTicks);
                                      if (now - prev < 50 || Interlocked.CompareExchange(ref _lastConsoleSendTicks, now, prev) != prev) return;
                                      Send(new { evt = "console", level = e.Level.ToString(), message = e.Message, source = e.Source, line = e.Line });
                                  };

        lock (_gate) { _browser = browser; }
        Log($"open: {url} ({width}x{height} @ {fps} fps, transparent={transparent}, syncTime={syncTime})");
        browser.LoadUrl(url);
    }

    private static void HandleDom(JsonElement root)
    {
        if (root.TryGetProperty("ops", out var opsElement))
        {
            lock (_domLock)
            {
                foreach (var op in opsElement.EnumerateArray())
                {
                    var key = op[0].GetString() + "\u0001" + op[1].GetString() + "\u0001" + op[2].GetString();
                    if (op[3].ValueKind == JsonValueKind.Null) _domState.Remove(key);
                    else _domState[key] = op.GetRawText();
                }
            }
        }

        if (_pageReady && _browser != null)
        {
            var opsJson = root.TryGetProperty("ops", out var o) ? o.GetRawText() : "[]";
            var callsJson = root.TryGetProperty("calls", out var c) ? c.GetRawText() : "[]";
            ApplyDomAsync(_browser, opsJson, callsJson);
        }
    }

    private static void PageLoaded(ChromiumWebBrowser browser)
    {
        var frame = browser.GetMainFrame();
        if (frame is not { IsValid: true }) return;

        frame.ExecuteJavaScriptAsync(DomHelper.Source);
        _pageReady = true;

        string all;
        lock (_domLock) all = _domState.Count == 0 ? "" : "[" + string.Join(",", _domState.Values) + "]";
        if (all.Length > 0) ApplyDomAsync(browser, all, "[]");
    }

    private static void ApplyDomAsync(ChromiumWebBrowser browser, string opsJson, string callsJson)
    {
        Cef.UIThreadTaskFactory.StartNew(() =>
        {
            try
            {
                if (!ReferenceEquals(browser, _browser) || !_pageReady) return;
                
                var frame = browser.GetMainFrame();
                if (frame is not { IsValid: true }) return;

                frame.EvaluateScriptAsync("window.__tixlDom?window.__tixlDom.apply(" + opsJson + "," + callsJson + "):'[]'")
                    .ContinueWith(t =>
                    {
                        try
                        {
                            browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                            if (!t.IsCompletedSuccessfully || !t.Result.Success || t.Result.Result is not string missing) return;

                            bool changed;
                            lock (_domLock)
                            {
                                changed = missing != _lastMissing;
                                _lastMissing = missing;
                            }

                            if (changed && ReferenceEquals(browser, _browser))
                            {
                                Log("selectors without a match: " + missing);
                                Send(new { evt = "domMissing", missing });
                            }
                        }
                        catch (Exception e) { Send(new { evt = "error", message = "DOM update failed: " + e.Message }); }
                    }, TaskScheduler.Default);
            }
            catch (Exception e)
            {
                Send(new { evt = "error", message = "DOM update failed: " + e.Message });
            }
        });
    }

    private static void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            var host = _browser?.GetBrowserHost();
            if (host == null) return;
            host.WasHidden(paused);
            if (!paused) host.Invalidate(PaintElementType.View);
        }
    }

    private static void OnPaint(object? sender, OnPaintEventArgs e)
    {
        if (e.IsPopup || _paused) return;
        var width = e.Width;
        var height = e.Height;
        if (width <= 0 || height <= 0) return;

        (string Name, int Width, int Height, long SlotSize)? announce;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _browser)) return;
            if (_basePtr == null || width != _frameWidth || height != _frameHeight) AllocateSharedMemory(width, height);

            var counter = _frameCounter + 1;
            var destination = _basePtr + HeaderSize + (counter & 1) * _slotSize;
            Buffer.MemoryCopy((void*)e.BufferHandle, destination, _slotSize, _slotSize);

            _frameCounter = counter;
            Volatile.Write(ref *(long*)_basePtr, counter);

            announce = _announceFrames;
            _announceFrames = null;
        }

        if (announce is { } a)
            Send(new { evt = "frames", name = a.Name, width = a.Width, height = a.Height, slotSize = a.SlotSize });
    }

    private static void AllocateSharedMemory(int width, int height)
    {
        ReleaseSharedMemory();
        _slotSize = (long)width * height * 4;
        var capacity = HeaderSize + 2 * _slotSize;
        var name = $"{_pipeName}-f{++_generation}";

        _mmf = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);

        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _basePtr = pointer + _view.PointerOffset;

        _frameWidth = width;
        _frameHeight = height;
        _frameCounter = 0;

        *(long*)_basePtr = 0;
        *(int*)(_basePtr + 8) = width;
        *(int*)(_basePtr + 12) = height;
        *(int*)(_basePtr + 16) = width * 4;

        _announceFrames = (name, width, height, _slotSize);
    }

    private static void ReleaseSharedMemory()
    {
        if (_view != null)
        {
            if (_basePtr != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _basePtr = null;
        _frameWidth = 0;
        _frameHeight = 0;
    }

    private static ChromiumWebBrowser? DetachBrowserLocked()
    {
        var browser = _browser;
        _browser = null;
        return browser;
    }

    private static void Shutdown()
    {
        try
        {
            Cef.UIThreadTaskFactory.StartNew(() =>
            {
                ChromiumWebBrowser? browser;
                lock (_gate) browser = DetachBrowserLocked();
                try { browser?.Dispose(); } catch { }
            }).Wait(1000);
        }
        catch { }

        lock (_gate) ReleaseSharedMemory();
    }

    private static void Send(object message)
    {
        var json = JsonSerializer.Serialize(message);
        lock (_writeLock)
        {
            try { _writer?.WriteLine(json); }
            catch (IOException) { _exit.Set(); }
        }
    }

    private static void Log(string message)
    {
        if (_logPath.Length == 0)
            return;

        try
        {
            lock (_logLock)
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging is best effort
        }
    }

    private static int Dimension(int value) => Math.Clamp(value, 1, MaxDimension);

    private static int GetInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;

    private static bool GetBool(JsonElement element, string name, bool fallback)
        => element.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
               ? p.GetBoolean()
               : fallback;
}
