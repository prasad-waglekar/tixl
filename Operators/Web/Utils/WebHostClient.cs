#nullable enable
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Lib.io.web;

/// <summary>
/// TiXL-side client of TixlWebHost (the out-of-process Chromium renderer, see /WebHost).
/// Starts the host, talks to it over a named pipe and reads rendered frames from shared memory.
/// Everything that can block (process start, pipe I/O) happens on background threads; the operator only
/// calls cheap methods from its Update().
/// </summary>
internal sealed class WebHostClient : IDisposable
{
    public const string HostFileName = "TixlWebHost.exe";
    private const int HeaderSize = 64;

    public readonly record struct FrameInfo(IntPtr Data, int Width, int Height, int Stride, long Counter);

    public bool IsConnected => _isConnected;
    public bool IsLoading => _isLoading;
    public bool HostExited => _hostExited;
    public string? ErrorMessage => _errorMessage;
    public string? LoadError => _loadError;
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

    // ---- commands (all cheap and non-blocking) ----

    public void Open(string url, int width, int height, int fps, bool transparent)
    {
        lock (_sendLock)
        {
            _url = url;
            _width = width;
            _height = height;
            _fps = fps;
            _transparent = transparent;
            _paused = false;
            _hasOpen = true;
            _isLoading = true;
            _loadError = null;
            WriteLocked(BuildOpenJson());
        }
    }

    public void Resize(int width, int height)
    {
        lock (_sendLock)
        {
            _width = width;
            _height = height;
            WriteLocked(JsonSerializer.Serialize(new { cmd = "resize", width, height }));
        }
    }

    public void SetFps(int fps)
    {
        lock (_sendLock)
        {
            _fps = fps;
            WriteLocked(JsonSerializer.Serialize(new { cmd = "fps", value = fps }));
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_sendLock)
        {
            _paused = paused;
            WriteLocked(JsonSerializer.Serialize(new { cmd = "pause", value = paused }));
        }
    }

    public void Reload()
    {
        lock (_sendLock)
        {
            _isLoading = true;
            _loadError = null;
            WriteLocked("{\"cmd\":\"reload\"}");
        }
    }

    // ---- frames (call from the operator's update thread only) ----

    /// <summary>
    /// True when a frame newer than the last delivered one is available. The pointer stays valid until the next call
    /// of this method or <see cref="Dispose"/>; copy it to the GPU immediately.
    /// </summary>
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

    /// <summary>False if the host may have overwritten the frame's slot while it was being copied (rare).</summary>
    public unsafe bool IsFrameStillValid(in FrameInfo frame)
    {
        var view = _currentView;
        return view != null && Volatile.Read(ref *(long*)view.Base) < frame.Counter + 2;
    }

    // ---- background receive ----

    private async Task ReceiveLoop()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _pipe!.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            var utf8 = new UTF8Encoding(false);
            lock (_sendLock)
            {
                _writer = new StreamWriter(_pipe, utf8, 1024, leaveOpen: true) { AutoFlush = true };
                if (_hasOpen) // commands issued before the host connected: send the latest state once
                    WriteLocked(BuildOpenJson());
            }

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

        _isConnected = false;
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
                    _loadError = null;
                break;

            case "loadError":
                _loadError = $"{root.GetProperty("text").GetString()} ({root.GetProperty("code").GetInt32()}): {root.GetProperty("url").GetString()}";
                Log.Warning($"[web] Load error: {_loadError}");
                break;

            case "console":
                Log.Debug($"[web] {root.GetProperty("level").GetString()}: {root.GetProperty("message").GetString()} " +
                          $"({root.GetProperty("source").GetString()}:{root.GetProperty("line").GetInt32()})");
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
        try
        {
            code = _process?.ExitCode ?? -1;
        }
        catch
        {
            // process object already disposed
        }

        SetError($"The browser host exited unexpectedly (exit code {code}). Check %TEMP%\\TixlWebHost for logs.");
    }

    private void SetError(string message)
    {
        _errorMessage = message;
        Log.Warning($"[web] {message}");
    }

    private string BuildOpenJson()
        => JsonSerializer.Serialize(new { cmd = "open", url = _url, width = _width, height = _height, fps = _fps, transparent = _transparent, paused = _paused });

    /// <summary>Must be called with <see cref="_sendLock"/> held. Silently dropped until the host has connected.</summary>
    private void WriteLocked(string json)
    {
        if (_writer == null)
            return;

        try
        {
            _writer.WriteLine(json);
        }
        catch (Exception e)
        {
            SetError($"Could not send a command to the browser host: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_sendLock)
        {
            WriteLocked("{\"cmd\":\"close\"}");
        }

        var process = _process;
        var pipe = _pipe;
        _ = Task.Run(() =>
                     {
                         try
                         {
                             if (process != null && !process.WaitForExit(2000))
                                 process.Kill(true);
                         }
                         catch
                         {
                             // already gone
                         }

                         try
                         {
                             process?.Dispose();
                         }
                         catch
                         {
                             // ignore
                         }

                         try
                         {
                             pipe?.Dispose();
                         }
                         catch
                         {
                             // ignore
                         }
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

    private readonly object _sendLock = new();
    private readonly object _frameLock = new();

    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private StreamWriter? _writer;

    private FrameView? _pendingView; // written by the receive thread, swapped in by TryGetFrame
    private FrameView? _currentView; // only touched from the operator's thread
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
    private volatile bool _hostExited;
    private volatile bool _disposed;
    private volatile string? _errorMessage;
    private volatile string? _loadError;
}
