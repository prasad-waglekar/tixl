#nullable enable
using System.Diagnostics;
using T3.Core.Animation;
using T3.Core.Resource.Assets;
using T3.Core.Utils;

namespace Lib.io.web;

[Guid("85f3298e-9f3c-4a78-8678-1caa9a3e12c1")]
public sealed class HtmlRenderer : Instance<HtmlRenderer>, IStatusProvider
{
    [Output(Guid = "cd959652-45cc-44d6-b9b6-3112bd5da28c", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Texture2D?> Texture = new();

    [Output(Guid = "741e2a89-bc84-48f1-9c31-b219e48c14bc", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<bool> IsReady = new();

    public HtmlRenderer()
    {
        Texture.UpdateAction = Update;
        IsReady.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var address = Path.GetValue(context);
        var resolution = Resolution.GetValue(context);
        var fps = Math.Clamp(Fps.GetValue(context), 1, 240);
        var transparent = Transparent.GetValue(context);
        var paused = Paused.GetValue(context);
        var syncTime = SyncToTiXLTime.GetValue(context);

        var reloadTriggered = MathUtils.WasTriggered(Reload.GetValue(context), ref _reloadState);
        if (reloadTriggered)
            Reload.SetTypedInputValue(false);

        var width = PickSize(resolution.Width, context.RequestedResolution.Width);
        var height = PickSize(resolution.Height, context.RequestedResolution.Height);

        if (reloadTriggered && (_startFailed || (_client?.HostExited ?? false)))
        {
            DisposeClient();
            _startFailed = false;
        }

        var url = ResolveUrl(address, out _inputError);
        if (url == null || _startFailed)
        {
            Texture.Value = _texture;
            IsReady.Value = false;
            return;
        }

        if (_client == null)
        {
            var client = new WebHostClient();
            if (!client.TryStart(out var error))
            {
                client.Dispose();
                _startError = error;
                _startFailed = true;
                Texture.Value = _texture;
                IsReady.Value = false;
                return;
            }

            _client = client;
            _startError = null;
            _sessionUrl = null;
        }

        var nowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        if (_sessionUrl != url || _sessionTransparent != transparent || _sessionSyncTime != syncTime)
        {
            _client.Open(url, width, height, fps, transparent, syncTime);
            _sessionUrl = url;
            _sessionTransparent = transparent;
            _sessionSyncTime = syncTime;
            _appliedWidth = width;
            _appliedHeight = height;
            _appliedFps = fps;
            _appliedPaused = false;
            _lastSettingsSendMs = nowMs;
            _lastSentSeconds = double.NaN; // the new page needs the current time again
        }
        else
        {
            var canSendSettings = (nowMs - _lastSettingsSendMs) >= 50.0;

            if ((width != _appliedWidth || height != _appliedHeight) && canSendSettings)
            {
                _client.Resize(width, height);
                _appliedWidth = width;
                _appliedHeight = height;
                _lastSettingsSendMs = nowMs;
            }

            if (fps != _appliedFps && canSendSettings)
            {
                _client.SetFps(fps);
                _appliedFps = fps;
                _lastSettingsSendMs = nowMs;
            }

            if (reloadTriggered)
                _client.Reload();
        }

        if (paused != _appliedPaused)
        {
            _client.SetPaused(paused);
            _appliedPaused = paused;
        }

        if (syncTime)
        {
            // LocalFxTime (in bars) advances while TiXL plays or Idle Motion is on, stands still otherwise, and follows
            // scrubbing. Only changes are sent, so a paused TiXL costs nothing and the page stays frozen.
            var seconds = Playback.Current.SecondsFromBars(context.LocalFxTime);
            if (double.IsNaN(_lastSentSeconds) || Math.Abs(seconds - _lastSentSeconds) > 0.0005)
            {
                _client.SetTime(seconds);
                _lastSentSeconds = seconds;
            }
        }

        IsReady.Value = _client.IsFullyLoaded;

        CollectModifiers(context);
        _client.SetDomOps(_domScope.Ops, _domScope.Calls);

        if (_client.TryGetFrame(out var frame))
        {
            if (_texture == null
                || _texture.Description.Width != frame.Width
                || _texture.Description.Height != frame.Height)
            {
                _texture?.Dispose();
                _texture = null;
                _texture = Texture2D.CreateTexture2D(new Texture2DDescription
                                                         {
                                                             BindFlags = BindFlags.ShaderResource,
                                                             Format = Format.B8G8R8A8_UNorm,
                                                             Width = frame.Width,
                                                             Height = frame.Height,
                                                             MipLevels = 1,
                                                             SampleDescription = new SampleDescription(1, 0),
                                                             Usage = ResourceUsage.Default,
                                                             OptionFlags = ResourceOptionFlags.None,
                                                             CpuAccessFlags = CpuAccessFlags.None,
                                                             ArraySize = 1
                                                         });
            }

            var dataBox = new SharpDX.DataBox(frame.Data, frame.Stride, 0);
            ResourceManager.Device.ImmediateContext.UpdateSubresource(dataBox, _texture, 0);
        }

        Texture.Value = _texture;
    }

    private void CollectModifiers(EvaluationContext context)
    {
        _domScope.Ops.Clear();
        _domScope.Calls.Clear();

        context.ObjectVariables.TryGetValue(WebDomOps.ContextKey, out var previous);
        context.ObjectVariables[WebDomOps.ContextKey] = _domScope;
        try
        {
            foreach (var input in Modifiers.CollectedInputs)
                input.GetValue(context);
        }
        finally
        {
            if (previous != null)
                context.ObjectVariables[WebDomOps.ContextKey] = previous;
            else
                context.ObjectVariables.Remove(WebDomOps.ContextKey);

            Modifiers.DirtyFlag.Clear();
        }
    }

    private static int PickSize(int fromInput, int requested)
    {
        var size = fromInput > 0 ? fromInput : requested > 0 ? requested : 1280;
        return Math.Clamp(size, 16, 8192);
    }

    private string? ResolveUrl(string? address, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "No HTML file selected.";
            return null;
        }

        address = address.Trim();
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        string? fullPath = null;
        if (System.IO.Path.IsPathRooted(address))
            fullPath = address;
        else if (AssetRegistry.TryResolveAddress(address, this, out var resolvedFile, out _))
            fullPath = resolvedFile;
        else if (AssetRegistry.TryResolveAddress(address, this, out var resolvedFolder, out _, isFolder: true))
            fullPath = resolvedFolder;

        if (fullPath == null)
        {
            error = $"Could not resolve '{address}'.";
            return null;
        }

        if (Directory.Exists(fullPath))
            fullPath = System.IO.Path.Combine(fullPath, "index.html");

        if (!File.Exists(fullPath))
        {
            error = $"File not found: {fullPath}";
            return null;
        }

        return new Uri(fullPath).AbsoluteUri;
    }

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
        _sessionUrl = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing) return;
        DisposeClient();
        _texture?.Dispose();
        _texture = null;
    }

    public IStatusProvider.StatusLevel GetStatusLevel() => GetStatus().Level;
    public string? GetStatusMessage() => GetStatus().Message;

    private (IStatusProvider.StatusLevel Level, string? Message) GetStatus()
    {
        if (_inputError != null) return (IStatusProvider.StatusLevel.Warning, _inputError);
        if (_startError != null) return (IStatusProvider.StatusLevel.Error, _startError);

        var client = _client;
        if (client == null) return (IStatusProvider.StatusLevel.Success, null);

        if (client.ErrorMessage != null) return (IStatusProvider.StatusLevel.Error, client.ErrorMessage + " Toggle Reload to restart.");
        if (client.LoadError != null) return (IStatusProvider.StatusLevel.Error, client.LoadError);
        if (client.MissingSelectors is { } missing) return (IStatusProvider.StatusLevel.Warning, "No element matches: " + missing);

        if (!client.IsConnected) return (IStatusProvider.StatusLevel.Notice, "Starting browser host...");
        if (client.IsLoading) return (IStatusProvider.StatusLevel.Notice, "Loading...");
        if (!client.HasFrame) return (IStatusProvider.StatusLevel.Notice, "Waiting for the first frame...");

        return (IStatusProvider.StatusLevel.Success, null);
    }

    private WebHostClient? _client;
    private Texture2D? _texture;
    private bool _reloadState;
    private bool _startFailed;
    private string? _startError;
    private string? _inputError;
    private string? _sessionUrl;
    private bool _sessionTransparent;
    private bool _sessionSyncTime;
    private readonly WebDomOps _domScope = new();
    private int _appliedWidth;
    private int _appliedHeight;
    private int _appliedFps;
    private bool _appliedPaused;
    private double _lastSettingsSendMs;
    private double _lastSentSeconds = double.NaN;

    [Input(Guid = "3ce7bcaa-3230-41ee-a0dc-601862139ce9")] public readonly InputSlot<string> Path = new();
    [Input(Guid = "5bbf805c-c7fa-4db5-b075-ee1a48ac78d9")] public readonly InputSlot<Int2> Resolution = new();
    [Input(Guid = "c70c8d20-ef5b-4dac-b395-9f1f00dbc633")] public readonly InputSlot<int> Fps = new();
    [Input(Guid = "a6319d66-550a-4ac0-88b0-86a22ec411a4")] public readonly InputSlot<bool> Transparent = new();
    [Input(Guid = "364f1cab-d6f3-41b2-a78f-55e5c77873a2")] public readonly InputSlot<bool> Paused = new();
    [Input(Guid = "693a8174-cef1-42fc-95e2-3a8299169663")] public readonly InputSlot<bool> Reload = new();
    [Input(Guid = "17a2748d-b519-4b08-aac2-fafe6b02e89d")] public readonly InputSlot<bool> SyncToTiXLTime = new();
    [Input(Guid = "e577b060-f80d-4f22-ae82-b9a1691ce481")] public readonly MultiInputSlot<Command> Modifiers = new();
}