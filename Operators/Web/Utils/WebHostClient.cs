#nullable enable
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Lib.io.web;

/// <summary>
/// TiXL-side client of TixlWebHost (the out-of-process Chromium renderer, see /WebHost).
/// Starts the host, communicates via an asynchronous named pipe, and reads rendered BGRA frames
/// from a double-buffered memory-mapped file without blocking TiXL's evaluation loop.
/// </summary>
internal sealed class WebHostClient : IDisposable
{
    public const string HostFileName = "TixlWebHost.exe";
    private const int HeaderSize = 64;

    public readonly record struct FrameInfo(IntPtr Data, int Width, int Height, int Stride, long Counter);

    public bool IsConnected => _isConnected;
    public bool IsLoading => _isLoading;
    public bool IsFullyLoaded => _isFullyLoaded;
    public bool HostExited => _hostExited;
    public string? ErrorMessage => _errorMessage;
    public string? LoadError => _loadError;
    public string? MissingSelectors => _missingSelectors;
    public bool HasFrame => _lastCounter > 0;

    /// <summary>Looks for the host in %TIXL_WEBHOST% (file or folder), then %LOCALAPPDATA%\TiXL\WebHost.</summary>
    public static string? FindHostExecutable()
    {
        var env = Environment.GetEnvironmentVariable("TIXL_WEBHOST");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var candidate = Directory.Exists(env) ? Path.Combine(env, HostFileName) : env;
            if (File.Exists(candidate))
                return candidate;
        }

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "TiXL", "WebHost", HostFileName);
        return File.Exists(installed) ? installed : null;
    }

    public bool TryStart(out string error)
    {
        error = "";
        var exe = FindHostExecutable();
        if (exe == null)
        {
            error = $"{HostFileName} not found. Run WebHost\\install.ps1 from the TiXL repository " +
                    "(installs to %LOCALAPPDATA%\\TiXL\\WebHost) or set the TIXL_WEBHOST environment variable.";
            return false;
        }

        try
        {
            var pipeName = $"tixl-web-{Guid.NewGuid():N}";
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var startInfo = new ProcessStartInfo(exe)
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    WorkingDirectory = Path.GetDirectoryName(exe)!
                                };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            _process = Process.Start(startInfo);
            if (_process == null)
            {
                error = "Failed to start the browser host process.";
                Dispose();
                return false;
            }

            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            _ = Task.Run(ReceiveLoop);
            return true;
        }
        catch (Exception e)
        {
            error = $"Failed to start the browser host: {e.Message}";
            Dispose();
            return false;
        }
    }

    // ---- non-blocking outbound commands ----

    public void Open(string url, int width, int height, int fps, bool transparent)
    {
        lock (_stateLock)
        {
            _url = url;
            _width = width;
            _height = height;
            _fps = fps;
            _transparent = transparent;
            _paused = false;
            _hasOpen = true;
            _isLoading = true;
            _isFullyLoaded = false;
            _loadError = null;
            _missingSelectors = null;
            EnqueueCommand(BuildOpenJsonLocked());
            ResendAllDomLocked();
        }
    }

    public void Resize(int width, int height)
    {
        lock (_stateLock)
        {
            _width = width;
            _height = height;
            EnqueueCommand(JsonSerializer.Serialize(new { cmd = "resize", width, height }));
        }
    }

    public void SetFps(int fps)
    {
        lock (_stateLock)
        {
            _fps = fps;
            EnqueueCommand(JsonSerializer.Serialize(new { cmd = "fps", value = fps }));
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_stateLock)
        {
            _paused = paused;
            EnqueueCommand(JsonSerializer.Serialize(new { cmd = "pause", value = paused }));
        }
    }

    public void SetTime(double time)
    {
        lock (_stateLock)
        {
            EnqueueCommand(JsonSerializer.Serialize(new { cmd = "time", value = time }));
        }
    }

    public void Reload()
    {
        lock (_stateLock)
        {
            _isLoading = true;
            _isFullyLoaded = false;
            _loadError = null;
            EnqueueCommand("{\"cmd\":\"reload\"}");
        }
    }

    public void SetDomOps(List<DomOp> ops)
    {
        var changed = false;
        lock (_stateLock)
        {
            _domSeen.Clear();
            foreach (var op in ops)
            {
                var key = op.Key;
                _domSeen.Add(key);
                if (_domDesired.TryGetValue(key, out var old) && old.Value == op.Value)
                    continue;

                _domDesired[key] = op;
                _domPending[key] = op;
                changed = true;
            }

            if (_domDesired.Count > _domSeen.Count)
            {
                _domRemoved.Clear();
                foreach (var key in _domDesired.Keys)
                {
                    if (!_domSeen.Contains(key))
                        _domRemoved.Add(key);
                }

                foreach (var key in _domRemoved)
                {
                    var op = _domDesired[key];
                    _domDesired.Remove(key);
                    _domPending[key] = op with { Value = null };
                    changed = true;
                }
            }

            if (changed)
                EnqueueCommand(DomFlushMarker);
        }
    }

    private void ResendAllDomLocked()
    {
        if (_domDesired.Count == 0)
            return;

        _domPending.Clear();
        foreach (var pair in _domDesired)
            _domPending[pair.Key] = pair.Value;

        EnqueueCommand(DomFlushMarker);
    }

    private string? BuildDomMessage()
    {
        lock (_stateLock)
        {
            if (_domPending.Count == 0)
                return null;

            var list = new List<object?[]>(_domPending.Count);
            foreach (var op in _domPending.Values)
                list.Add(new object?[] { op.Kind.ToString(), op.Selector, op.Name, op.Value });

            _domPending.Clear();
            return JsonSerializer.Serialize(new { cmd = "dom", ops = list });
        }
    }

    private void EnqueueCommand(string json)
    {
        if (_disposed)
            return;

        _outbound.Writer.TryWrite(json);
    }

    // ---- frames (call from the operator's update thread only) ----

    public unsafe bool TryGetFrame(out FrameInfo frame)
    {
        frame = default;

        lock (_frameLock)
        {
            if (_pendingView != null)
            {
                _currentView?.Dispose();
                _currentView = _pendingView;
                _pendingView = null;
                _lastCounter = 0;
            }
        }

        var view = _currentView;
        if (view == null)
            return false;

        var counter = Volatile.Read(ref *(long*)view.Base);
        if (counter <= _lastCounter)
            return false;

        _lastCounter = counter;
        var slot = counter & 1;
        var data = (IntPtr)(view.Base + HeaderSize + slot * view.SlotSize);
        frame = new FrameInfo(data, view.Width, view.Height, view.Stride, counter);
        return true;
    }

    public unsafe bool IsFrameStillValid(in FrameInfo frame)
    {
        var view = _currentView;
        return view != null && Volatile.Read(ref *(long*)view.Base) < frame.Counter + 2;
    }

    // ---- background send / receive loops ----

    private async Task SendLoop(StreamWriter writer, CancellationToken ct)
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var json = item == DomFlushMarker ? BuildDomMessage() : item;
                if (json == null)
                    continue;

                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on dispose
        }
        catch (Exception e) when (!_disposed)
        {
            SetError($"Could not send command to browser host: {e.Message}");
        }
    }

    private async Task ReceiveLoop()
    {
        CancellationTokenSource? sendCts = null;
        Task? sendTask = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _pipe!.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(_pipe, utf8, 1024, leaveOpen: true);

            lock (_stateLock)
            {
                while (_outbound.Reader.TryRead(out _))
                {
                }

                if (_hasOpen)
                    _outbound.Writer.TryWrite(BuildOpenJsonLocked());

                ResendAllDomLocked();
            }

            sendCts = new CancellationTokenSource();
            sendTask = Task.Run(() => SendLoop(writer, sendCts.Token));
            _isConnected = true;

            using var reader = new StreamReader(_pipe, utf8, false, 4096, leaveOpen: true);
            while (!_disposed && await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    HandleMessage(line);
                }
                catch (Exception e)
                {
                    Log.Warning($"[web] Could not handle host message: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
                SetError("The browser host did not connect in time. Check %TEMP%\\TixlWebHost for logs.");
        }
        catch (Exception e) when (!_disposed)
        {
            SetError($"Connection to the browser host lost: {e.Message}");
        }
        finally
        {
            _isConnected = false;
            if (sendCts != null)
            {
                sendCts.Cancel();
                if (sendTask != null)
                {
                    try { await sendTask.ConfigureAwait(false); } catch { }
                }
                sendCts.Dispose();
            }
        }
    }

    private void HandleMessage(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        switch (root.GetProperty("evt").GetString())
        {
            case "frames":
                OpenFrameView(root.GetProperty("name").GetString()!, root.GetProperty("slotSize").GetInt64());
                break;

            case "loading":
                _isLoading = root.GetProperty("isLoading").GetBoolean();
                if (_isLoading)
                {
                    _loadError = null;
                    _isFullyLoaded = false;
                }
                break;

            case "fullyLoaded":
                _isFullyLoaded = true;
                _isLoading = false;
                break;

            case "loadError":
                _loadError = $"{root.GetProperty("text").GetString()} ({root.GetProperty("code").GetInt32()}): {root.GetProperty("url").GetString()}";
                Log.Warning($"[web] Load error: {_loadError}");
                break;

            case "console":
                Log.Debug($"[web] {root.GetProperty("level").GetString()}: {root.GetProperty("message").GetString()} " +
                          $"({root.GetProperty("source").GetString()}:{root.GetProperty("line").GetInt32()})");
                break;

            case "domMissing":
                _missingSelectors = root.GetProperty("missing").GetString();
                break;

            case "error":
                SetError(root.GetProperty("message").GetString() ?? "Unknown browser host error.");
                break;
        }
    }

    private unsafe void OpenFrameView(string name, long slotSize)
    {
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            accessor = mmf.CreateViewAccessor(0, HeaderSize + 2 * slotSize, MemoryMappedFileAccess.Read);
        }
        catch (Exception e)
        {
            accessor?.Dispose();
            mmf?.Dispose();
            SetError($"Could not open shared frame memory: {e.Message}");
            return;
        }

        byte* pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        var view = new FrameView(mmf, accessor, pointer + accessor.PointerOffset);

        if (_disposed)
        {
            view.Dispose();
            return;
        }

        FrameView? replaced;
        lock (_frameLock)
        {
            replaced = _pendingView;
            _pendingView = view;
        }

        replaced?.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _hostExited = true;
        _isConnected = false;
        if (_disposed)
            return;

        var code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }
        SetError($"The browser host exited unexpectedly (exit code {code}). Check %TEMP%\\TixlWebHost for logs.");
    }

    private void SetError(string message)
    {
        _errorMessage = message;
        Log.Warning($"[web] {message}");
    }

    private string BuildOpenJsonLocked()
        => JsonSerializer.Serialize(new { cmd = "open", url = _url, width = _width, height = _height, fps = _fps, transparent = _transparent, paused = _paused });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _outbound.Writer.TryWrite("{\"cmd\":\"close\"}");
        _outbound.Writer.TryComplete();

        var process = _process;
        var pipe = _pipe;
        _ = Task.Run(() =>
                     {
                         try { if (process != null && !process.WaitForExit(2000)) process.Kill(true); } catch { }
                         try { process?.Dispose(); } catch { }
                         try { pipe?.Dispose(); } catch { }
                     });

        _currentView?.Dispose();
        _currentView = null;
        lock (_frameLock)
        {
            _pendingView?.Dispose();
            _pendingView = null;
        }
    }

    private sealed unsafe class FrameView : IDisposable
    {
        public FrameView(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, byte* basePointer)
        {
            _mmf = mmf;
            _accessor = accessor;
            Base = basePointer;
            Width = *(int*)(basePointer + 8);
            Height = *(int*)(basePointer + 12);
            Stride = *(int*)(basePointer + 16);
            SlotSize = (long)Width * Height * 4;
        }

        public readonly byte* Base;
        public readonly int Width;
        public readonly int Height;
        public readonly int Stride;
        public readonly long SlotSize;

        public void Dispose()
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _mmf.Dispose();
        }

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
    }

    private const string DomFlushMarker = "\u0001dom";

    private readonly object _stateLock = new();
    private readonly Dictionary<(char, string, string), DomOp> _domDesired = new();
    private readonly Dictionary<(char, string, string), DomOp> _domPending = new();
    private readonly HashSet<(char, string, string)> _domSeen = new();
    private readonly List<(char, string, string)> _domRemoved = new();
    private volatile string? _missingSelectors;
    private readonly object _frameLock = new();
    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
                                                                                 {
                                                                                     FullMode = BoundedChannelFullMode.DropOldest,
                                                                                     SingleReader = true,
                                                                                     SingleWriter = false
                                                                                 });

    private NamedPipeServerStream? _pipe;
    private Process? _process;

    private FrameView? _pendingView;
    private FrameView? _currentView;
    private long _lastCounter;

    private string _url = "about:blank";
    private int _width = 1280;
    private int _height = 720;
    private int _fps = 60;
    private bool _transparent;
    private bool _paused;
    private bool _hasOpen;

    private volatile bool _isConnected;
    private volatile bool _isLoading;
    private volatile bool _isFullyLoaded;
    private volatile bool _hostExited;
    private volatile bool _disposed;
    private volatile string? _errorMessage;
    private volatile string? _loadError;
}
