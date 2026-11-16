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
    private static volatile bool _syncTime;
    private static double _lastTimeMs;

    // DOM changes requested by TiXL (see DomHelper). Kept here so they can be applied again after every page load.
    private static readonly object _domLock = new();
    private static readonly Dictionary<string, string> _domState = new(); // key -> op as JSON array text
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
                     GetBool(root, "syncTime", true));
                break;

            case "reload":
                _pageReady = false;
                lock (_gate)
                    _browser?.Reload(true);
                break;

            case "dom":
                HandleDom(root.GetProperty("ops"));
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
                lock (_gate)
                {
                    var time = root.TryGetProperty("value", out var p) ? p.GetDouble() : 0.0;
                    SetTime(time);
                }
                break;

            case "close":
                _exit.Set();
                break;
        }
    }

    private static void Open(string url, int width, int height, int fps, bool transparent, bool paused, bool syncTime)
    {
        ChromiumWebBrowser? previous;
        lock (_gate)
        {
            previous = DetachBrowserLocked();
            _paused = paused;
            _syncTime = syncTime;
        }

        // A new browser starts without any DOM changes; TiXL sends the full set again after the open command.
        _pageReady = false;
        lock (_domLock)
        {
            _domState.Clear();
            _lastMissing = "[]";
        }

        // Dispose outside _gate to avoid blocking CEF's UI thread while OnPaint waits on _gate.
        previous?.Dispose();

        var browserSettings = new BrowserSettings
                                  {
                                      WindowlessFrameRate = fps,
                                      BackgroundColor = transparent
                                                            ? Cef.ColorSetARGB(0, 0, 0, 0)
                                                            : Cef.ColorSetARGB(255, 255, 255, 255),
                                  };

        var browser = new ChromiumWebBrowser("about:blank", browserSettings);
        browser.Size = new Size(width, height);
        browser.Paint += OnPaint;

        var nav = new NavState();
        browser.LoadingStateChanged += (_, e) =>
                                       {
                                           if (!nav.Started)
                                               return; // still on about:blank

                                           Send(new { evt = "loading", isLoading = e.IsLoading });

                                           if (e.IsLoading)
                                           {
                                               _pageReady = false;
                                               return;
                                           }

                                           PageLoaded(browser);

                                           // Run the page's first animation callbacks even if TiXL's time is standing still.
                                           ApplyTime(browser);
                                       };
        browser.LoadError += (_, e) =>
                             {
                                 if (e.ErrorCode == CefErrorCode.Aborted)
                                     return;

                                 Send(new { evt = "loadError", code = (int)e.ErrorCode, text = e.ErrorText, url = e.FailedUrl });
                             };

        // Inject the clock-shimming engine before the page runs its scripts
        browser.FrameLoadStart += (_, e) =>
        {
            if (e.Frame.IsMain)
            {
                e.Browser.ExecuteScriptAsync(@"
                    window.__tixlTimeMs = 0;
                    window.__tixlRafCallbacks = [];
                    
                    // Override requestAnimationFrame to be driven solely by TiXL time ticks
                    window.requestAnimationFrame = function(cb) {
                        window.__tixlRafCallbacks.push(cb);
                        return window.__tixlRafCallbacks.length;
                    };
                    
                    // Override performance.now and Date.now for time-dependent JS libraries (GSAP, Three.js, etc.)
                    performance.now = function() { return window.__tixlTimeMs; };
                    Date.now = function() { return window.__tixlTimeMs; };

                    // Master clock advance function called by TiXL on every frame update
                    window.__advanceTiXLClock = function(tSec) {
                        window.__tixlTimeMs = tSec * 1000.0;
                        
                        // 1. Call user's setTiXLTime if defined
                        if (typeof window.setTiXLTime === 'function') {
                            window.setTiXLTime(tSec);
                        }

                        // 2. Dispatch pending requestAnimationFrame callbacks with virtual timestamp
                        const cbs = window.__tixlRafCallbacks.splice(0);
                        for (const cb of cbs) {
                            try { cb(window.__tixlTimeMs); } catch(err) { console.error(err); }
                        }
                    };
                ");
            }
        };

        browser.FrameLoadEnd += (_, e) =>
                                {
                                    if (e.Frame.IsMain)
                                    {
                                        e.Browser.ExecuteScriptAsync(
                                            "if (document.readyState === 'complete') { console.log('[TIXL_READY]'); }" +
                                            "else { window.addEventListener('load', () => console.log('[TIXL_READY]')); }"
                                        );
                                    }
                                };
        browser.ConsoleMessage += (_, e) =>
                                  {
                                      if (e.Message.Contains("[TIXL_READY]"))
                                      {
                                          Send(new { evt = "fullyLoaded" });
                                          return;
                                      }

                                      var now = Environment.TickCount64;
                                      var prev = Interlocked.Read(ref _lastConsoleSendTicks);
                                      if (now - prev < 50 || Interlocked.CompareExchange(ref _lastConsoleSendTicks, now, prev) != prev)
                                          return;

                                      Send(new { evt = "console", level = e.Level.ToString(), message = e.Message, source = e.Source, line = e.Line });
                                  };

        lock (_gate)
        {
            _browser = browser;
        }

        if (syncTime)
            _ = StartWithTimeShimAsync(browser, url, nav);
    }

    private static async Task StartWithTimeShimAsync(ChromiumWebBrowser browser, string url, NavState nav)
    {
        try
        {
            await browser.WaitForInitialLoadAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Send(new { evt = "error", message = "Time sync setup failed: " + e.Message });
        }

        if (!ReferenceEquals(browser, _browser))
            return; // replaced in the meantime

        nav.Started = true;
        browser.LoadUrl(url);
    }

    private static void HandleDom(JsonElement ops)
    {
        // ops: [[kind, selector, name, value-or-null], ...]
        lock (_domLock)
        {
            foreach (var op in ops.EnumerateArray())
            {
                var key = op[0].GetString() + "\u0001" + op[1].GetString() + "\u0001" + op[2].GetString();
                if (op[3].ValueKind == JsonValueKind.Null)
                    _domState.Remove(key);
                else
                    _domState[key] = op.GetRawText();
            }
        }

        var browser = _browser;
        if (browser != null && _pageReady)
            _ = ApplyDomAsync(browser, ops.GetRawText());
    }

    private static void PageLoaded(ChromiumWebBrowser browser)
    {
        var frame = browser.GetMainFrame();
        if (frame is not { IsValid: true })
            return;

        frame.ExecuteJavaScriptAsync(DomHelper.Source);
        _pageReady = true;

        string all;
        lock (_domLock)
            all = _domState.Count == 0 ? "" : "[" + string.Join(",", _domState.Values) + "]";

        if (all.Length > 0)
            _ = ApplyDomAsync(browser, all);
    }

    private static async Task ApplyDomAsync(ChromiumWebBrowser browser, string opsJson)
    {
        try
        {
            var frame = browser.GetMainFrame();
            if (frame is not { IsValid: true })
                return;

            var response = await frame.EvaluateScriptAsync("window.__tixlDom?window.__tixlDom.apply(" + opsJson + "):'[]'").ConfigureAwait(false);
            if (!response.Success || response.Result is not string missing)
                return;

            // Report only changes, so TiXL's status does not flicker and the pipe is not flooded.
            bool changed;
            lock (_domLock)
            {
                changed = missing != _lastMissing;
                _lastMissing = missing;
            }

            if (changed && ReferenceEquals(browser, _browser))
                Send(new { evt = "domMissing", missing });
        }
        catch (Exception e)
        {
            Send(new { evt = "error", message = "DOM update failed: " + e.Message });
        }
    }

    private static void SetTime(double milliseconds)
    {
        _lastTimeMs = milliseconds;
        ApplyTime(_browser);
    }

    private static void ApplyTime(ChromiumWebBrowser? browser)
    {
        if (browser == null || !_syncTime)
            return;

        var frame = browser.GetMainFrame();
        if (frame is not { IsValid: true })
            return;

        frame.ExecuteJavaScriptAsync("window.__advanceTiXLClock && window.__advanceTiXLClock(" + (_lastTimeMs / 1000.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    private sealed class NavState
    {
        public volatile bool Started;
    }

    private static void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            var host = _browser?.GetBrowserHost();
            if (host == null)
                return;

            host.WasHidden(paused);
            if (!paused)
                host.Invalidate(PaintElementType.View);
        }
    }

    private static void OnPaint(object? sender, OnPaintEventArgs e)
    {
        if (e.IsPopup || _paused)
            return;

        var width = e.Width;
        var height = e.Height;
        if (width <= 0 || height <= 0)
            return;

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _browser))
                return;

            if (_basePtr == null || width != _frameWidth || height != _frameHeight)
                AllocateSharedMemory(width, height);

            var counter = _frameCounter + 1;
            var destination = _basePtr + HeaderSize + (counter & 1) * _slotSize;
            Buffer.MemoryCopy((void*)e.BufferHandle, destination, _slotSize, _slotSize);

            _frameCounter = counter;
            Volatile.Write(ref *(long*)_basePtr, counter);
        }
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

        Send(new { evt = "frames", name, width, height, slotSize = _slotSize });
    }

    private static void ReleaseSharedMemory()
    {
        if (_view != null)
        {
            if (_basePtr != null)
                _view.SafeMemoryMappedViewHandle.ReleasePointer();

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
        ChromiumWebBrowser? browser;
        lock (_gate)
        {
            browser = DetachBrowserLocked();
        }

        browser?.Dispose();

        lock (_gate)
        {
            ReleaseSharedMemory();
        }

        try { Cef.Shutdown(); } catch { }
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

    private static int Dimension(int value) => Math.Clamp(value, 1, MaxDimension);

    private static int GetInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;

    private static bool GetBool(JsonElement element, string name, bool fallback)
        => element.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
               ? p.GetBoolean()
               : fallback;
}