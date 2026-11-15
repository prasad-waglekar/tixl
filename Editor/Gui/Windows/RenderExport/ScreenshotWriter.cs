#nullable enable

using SharpDX;
using SharpDX.IO;
using SharpDX.WIC;
using System.Runtime.InteropServices;
using T3.Core.Animation;
using T3.Core.DataTypes;
using T3.Core.Resource;

namespace T3.Editor.Gui.Windows.RenderExport;

internal static class ScreenshotWriter
{
    internal enum FileFormats
    {
        Png,
        Jpg,

        /// <summary>16-bit float, linear, with alpha. Keeps HDR values above 1.0.</summary>
        Exr,
    }

    private static TextureBgraReadAccess? _textureBgraReadAccess;

    // EXR needs the unclamped float values, so it reads back through its own half-float staging textures.
    // Created lazily so PNG/JPG users don't pay for the extra GPU memory.
    private static TextureBgraReadAccess? _textureHalfFloatReadAccess;
    internal static string? LastFilename { get; private set; }

    //private static TextureBgraReadAccess? _textureBgraReadAccess;
    private static int _lastUpdateFrame = 0;

    internal static void ClearQueue()
    {
        _textureBgraReadAccess?.ClearQueue();
        _textureHalfFloatReadAccess?.ClearQueue();
    }

    internal static void Update()
    {
        if (Playback.FrameCount == _lastUpdateFrame) 
            return;
        
        _textureBgraReadAccess?.Update();
        _textureHalfFloatReadAccess?.Update();
        _lastUpdateFrame = Playback.FrameCount;
    }

    internal static bool InitiateConvertAndReadBack2(Texture2D gpuTexture, TextureBgraReadAccess.OnReadComplete saveSampleAfterReadback)
    {
        if (_textureBgraReadAccess == null)
            _textureBgraReadAccess = new TextureBgraReadAccess();
        
        _useFormats = FileFormats.Png;
        _forceOpaque = false;
        return _textureBgraReadAccess.InitiateConvertAndReadBack(gpuTexture, saveSampleAfterReadback);        
    }

    /// <param name="opaque">Write alpha as fully opaque — for a UI back-buffer capture, whose alpha is
    /// render residue that makes the PNG see-through in viewers.</param>
    internal static bool StartSavingToFile(Texture2D gpuTexture, string filepath, FileFormats format, Action<string?>? onComplete = null, bool logErrors=true, bool opaque = false)
    {
        if (format == FileFormats.Exr)
        {
            // Deliberately doesn't touch _useFormats/_forceOpaque: those are read later by the PNG/JPG callback,
            // so an in-flight screenshot must not be re-interpreted by a subsequent EXR request.
            _textureHalfFloatReadAccess ??= new TextureBgraReadAccess(targetFormat: SharpDX.DXGI.Format.R16G16B16A16_Float);
            return _textureHalfFloatReadAccess.InitiateConvertAndReadBack(gpuTexture, request => 
                                                                                      {
                                                                                          OnReadCompleteExr(request, logErrors, opaque);
                                                                                          onComplete?.Invoke(request.Filepath);
                                                                                      }, filepath);
        }

        _textureBgraReadAccess ??= new TextureBgraReadAccess();
        _useFormats = format;
        _forceOpaque = opaque;
        
        return _textureBgraReadAccess.InitiateConvertAndReadBack(gpuTexture, request => 
                                                                             {
                                                                                 OnReadComplete(request, logErrors);
                                                                                 onComplete?.Invoke(request.Filepath);
                                                                             }, filepath);        
    }
    
    
    private static void OnReadComplete(TextureBgraReadAccess.ReadRequestItem request, bool logErrors)
    {
        var immediateContext = ResourceManager.Device.ImmediateContext;
        if (request.CpuAccessTexture.IsDisposed)
        {
            if(logErrors)
                Log.Debug("ScreenshotWriter: Texture was disposed before readback was complete");
            return;
        }
        
        var dataBox = immediateContext.MapSubresource(request.CpuAccessTexture,
                                                      0,
                                                      0,
                                                      SharpDX.Direct3D11.MapMode.Read,
                                                      SharpDX.Direct3D11.MapFlags.None,
                                                      out var imageStream);
        using var dataStream = imageStream;
        
        var width = request.CpuAccessTexture.Description.Width;
        var height = request.CpuAccessTexture.Description.Height;
        var factory = new ImagingFactory();
        
        WICStream stream;
        try
        {
            stream = new WICStream(factory, request.Filepath, NativeFileAccess.Write);
        }
        catch (Exception e)
        {
            if(logErrors)
                Log.Warning("Failed to export image: " + e.Message);
            
            return;
        }

        // Initialize a Jpeg encoder with this stream
        BitmapEncoder encoder = _useFormats == FileFormats.Png
                                    ? new PngBitmapEncoder(factory)
                                    : new JpegBitmapEncoder(factory);
        encoder.Initialize(stream);

        // Create a Frame encoder
        var bitmapFrameEncode = new BitmapFrameEncode(encoder);
        bitmapFrameEncode.Initialize();
        bitmapFrameEncode.SetSize(width, height);
        var formatId = PixelFormat.Format32bppRGBA;
        bitmapFrameEncode.SetPixelFormat(ref formatId);

        var rowStride = PixelFormat.GetStride(formatId, width);
        var outBufferSize = height * rowStride;
        var outDataStream = new DataStream(outBufferSize, true, true);
        
        try
        {
            if (_useFormats == FileFormats.Png)
            {
                // Note: dataBox.RowPitch and outputStream.RowPitch can diverge if width is not divisible by 16.
                for (var loopY = 0; loopY < height; loopY++)
                {
                    imageStream.Position = (long)(loopY) * dataBox.RowPitch;
                    var row = imageStream.ReadRange<byte>(rowStride);
                    if (_forceOpaque)
                    {
                        for (var i = 3; i < row.Length; i += 4)
                            row[i] = 255;
                    }

                    outDataStream.WriteRange(row);
                }
            }
            else
            {
                // We need to skip bytes for alpha channel from stream... 
                for (var y1 = 0; y1 < height; y1++)
                {
                    imageStream.Position = (long)(y1) * dataBox.RowPitch;
                    for (var x1 = 0; x1 < width; x1++)
                    {
                        outDataStream.WriteRange(imageStream.ReadRange<byte>(3));
                        imageStream.ReadByte();
                    }
                }
            }            
            
            // Copy the pixels from the buffer to the Wic Bitmap Frame encoder
            bitmapFrameEncode.WritePixels(height, new DataRectangle(outDataStream.DataPointer, rowStride));

            // Commit changes
            bitmapFrameEncode.Commit();
            encoder.Commit();
        }
        catch (Exception e)
        {
            Log.Error($"Screenshot internal image copy failed : {e.Message}");
        }
        finally
        {
            imageStream.Dispose();
            outDataStream.Dispose();
            bitmapFrameEncode.Dispose();
            encoder.Dispose();
            stream.Dispose();
            LastFilename = request.Filepath;
        }
    }
    
    private static void OnReadCompleteExr(TextureBgraReadAccess.ReadRequestItem request, bool logErrors, bool forceOpaque)
    {
        if (request.CpuAccessTexture.IsDisposed)
        {
            if (logErrors)
                Log.Debug("ScreenshotWriter: Texture was disposed before readback was complete");
            return;
        }

        if (string.IsNullOrEmpty(request.Filepath))
        {
            if (logErrors)
                Log.Warning("Failed to export EXR: no target filepath");
            return;
        }

        var immediateContext = ResourceManager.Device.ImmediateContext;
        var dataBox = immediateContext.MapSubresource(request.CpuAccessTexture,
                                                      0,
                                                      0,
                                                      SharpDX.Direct3D11.MapMode.Read,
                                                      SharpDX.Direct3D11.MapFlags.None,
                                                      out var imageStream);
        using var dataStream = imageStream;

        var width = request.CpuAccessTexture.Description.Width;
        var height = request.CpuAccessTexture.Description.Height;
        var rowBytes = width * ExrWriter.SourceBytesPerPixel;
        var basePointer = dataBox.DataPointer.ToInt64();
        var rowPitch = dataBox.RowPitch; // Can be larger than rowBytes because of GPU row alignment.

        try
        {
            // The texture is R16G16B16A16_Float, which is exactly what the EXR half channels need.
            // Rows are only read here, so the writer is free to pull them from several threads.
            ExrWriter.WriteRgbaHalf(request.Filepath, width, height,
                                    (y, dest) =>
                                    {
                                        Marshal.Copy(new IntPtr(basePointer + (long)y * rowPitch), dest, 0, rowBytes);

                                        if (!forceOpaque)
                                            return;

                                        // Half-float 1.0 is 0x3C00 (little endian: 00 3C). Alpha is the 4th half of each pixel.
                                        for (var i = 6; i < rowBytes; i += ExrWriter.SourceBytesPerPixel)
                                        {
                                            dest[i] = 0x00;
                                            dest[i + 1] = 0x3C;
                                        }
                                    });
        }
        catch (Exception e)
        {
            if (logErrors)
                Log.Error($"Failed to export EXR '{request.Filepath}': {e.Message}");
        }
        finally
        {
            LastFilename = request.Filepath;
        }
    }

    /// <summary>
    /// Save the requested format for later use by callback.
    /// This is not ideal, but beats the alternative to moving file formats to
    /// <see cref="_textureBgraReadAccess"/> in core.
    /// </summary>
    private static FileFormats _useFormats = FileFormats.Png;
    private static bool _forceOpaque;


}