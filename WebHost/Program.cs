// TixlWebHost - out-of-process Chromium (CEF, off-screen) renderer for TiXL's Web operators.
//
// Why a separate process: TiXL loads every operator package into its own unloadable assembly load context,
// and CEF can only be initialized once per process and ships native binaries and a subprocess. Running the
// browser here keeps all of that away from TiXL, and a browser crash can never take the editor down.
//
// Control channel: a named pipe that the TiXL operator creates (this process connects to it).
// UTF-8 JSON, one object per line.
//   TiXL -> host  {"cmd":"open","url":"file:///...","width":1280,"height":720,"fps":60,"transparent":false,"paused":false}
//                 {"cmd":"reload"}  {"cmd":"resize","width":..,"height":..}  {"cmd":"fps","value":..}
//                 {"cmd":"pause","value":true}  {"cmd":"close"}
//   host -> TiXL  {"evt":"ready"}
//                 {"evt":"frames","name":"..","width":..,"height":..,"slotSize":..}   (new shared frame memory)
//                 {"evt":"loading","isLoading":true}  {"evt":"loadError","code":..,"text":"..","url":".."}
//                 {"evt":"console","level":"..","message":"..","source":"..","line":..}  {"evt":"error","message":".."}
//
// Frame channel: a named memory-mapped file, re-created whenever the frame size changes.
//   [0]  int64 frameCounter (0 = no frame yet; published last, so readers can detect new frames)
//   [8]  int32 width   [12] int32 height   [16] int32 stride (bytes)
//   [64] slot 0, then slot 1 (each width*height*4 bytes, BGRA, premultiplied alpha as delivered by CEF)
// Frame n is written to slot (n & 1). A reader that sees counter >= n + 2 after copying slot (n & 1) knows the
// slot may have been overwritten while it was copying and must treat the copy as torn.

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

    private static readonly object _gate = new(); // guards browser + shared memory state
    private static readonly object _writeLock = new(); // guards the pipe writer
    private static readonly ManualResetEventSlim _exit = new(false);

    private static StreamWriter? _writer;
    private static string _pipeName = "";

    private static ChromiumWebBrowser? _browser;
    private static volatile bool _paused;

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
            try
            {
                Shutdown();
            }
            catch
            {
                // ignore
            }

            return 1;
        }
    }

    private static bool InitializeCef()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "TixlWebHost");
        Directory.CreateDirectory(logDir);

        // No CachePath: CEF then uses an in-memory profile, so several host processes can run side by side.
        var settings = new CefSettings
                           {
                               LogSeverity = LogSeverity.Warning,
                               LogFile = Path.Combine(logDir, $"cef-{Environment.ProcessId}.log"),
                           };

        // Local project folders: let pages load sibling files (fetch/XHR of local JSON, fonts, ...).
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
                                    catch
                                    {
                                        // parent already gone
                                    }

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

                try
                {
                    Handle(line);
                }
                catch (Exception e)
                {
                    Send(new { evt = "error", message = e.Message });
                }
            }
        }
        catch (IOException)
        {
            // pipe broken
        }
        finally
        {
            _exit.Set(); // pipe closed: TiXL is gone or disposed the operator
        }
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
                     GetBool(root, "paused", false));
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

            case "close":
                _exit.Set();
                break;
        }
    }

    private static void Open(string url, int width, int height, int fps, bool transparent, bool paused)
    {
        lock (_gate)
        {
            DisposeBrowser();
            _paused = paused;

            var browserSettings = new BrowserSettings
                                      {
                                          WindowlessFrameRate = fps,
                                          BackgroundColor = transparent
                                                                ? Cef.ColorSetARGB(0, 0, 0, 0)
                                                                : Cef.ColorSetARGB(255, 255, 255, 255),
                                      };

            var browser = new ChromiumWebBrowser(url, browserSettings);
            browser.Size = new Size(width, height);
            browser.Paint += OnPaint;
            browser.LoadingStateChanged += (_, e) => Send(new { evt = "loading", isLoading = e.IsLoading });
            browser.LoadError += (_, e) =>
                                 {
                                     if (e.ErrorCode == CefErrorCode.Aborted) // superseded navigation, not an error
                                         return;

                                     Send(new { evt = "loadError", code = (int)e.ErrorCode, text = e.ErrorText, url = e.FailedUrl });
                                 };
            browser.ConsoleMessage += (_, e) =>
                                          Send(new { evt = "console", level = e.Level.ToString(), message = e.Message, source = e.Source, line = e.Line });

            _browser = browser;
        }
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
                host.Invalidate(PaintElementType.View); // force a fresh frame after resuming
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
                return; // frame from a browser that was already replaced

            if (_basePtr == null || width != _frameWidth || height != _frameHeight)
                AllocateSharedMemory(width, height);

            var counter = _frameCounter + 1;
            var destination = _basePtr + HeaderSize + (counter & 1) * _slotSize;
            Buffer.MemoryCopy((void*)e.BufferHandle, destination, _slotSize, _slotSize);

            _frameCounter = counter;
            Volatile.Write(ref *(long*)_basePtr, counter); // publish last
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

        // The client opens the memory only after this message, so header and counter are already valid.
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

    private static void DisposeBrowser()
    {
        var browser = _browser;
        _browser = null;
        browser?.Dispose();
    }

    private static void Shutdown()
    {
        lock (_gate)
        {
            DisposeBrowser();
            ReleaseSharedMemory();
        }

        try
        {
            Cef.Shutdown();
        }
        catch
        {
            // best effort
        }
    }

    private static void Send(object message)
    {
        var json = JsonSerializer.Serialize(message);
        lock (_writeLock)
        {
            try
            {
                _writer?.WriteLine(json);
            }
            catch (IOException)
            {
                _exit.Set();
            }
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
