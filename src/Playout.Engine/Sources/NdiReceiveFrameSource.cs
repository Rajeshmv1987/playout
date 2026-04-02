using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Playout.Core.Models;
using Playout.Engine.Abstractions;
using Playout.Engine.Types;

namespace Playout.Engine.Sources;

public sealed class NdiReceiveFrameSource : IFrameSource, IDisposable
{
    readonly int _width;
    readonly int _height;
    readonly Rational _fps;
    nint _recv;
    static bool _initialized;
    static bool _initTried;
    static Exception? _initError;
    static readonly object _lock = new();
    static readonly int FourCC_BGRA = MakeFourCC('B', 'G', 'R', 'A');
    static readonly int FourCC_BGRX = MakeFourCC('B', 'G', 'R', 'X');

    public NdiReceiveFrameSource(int width, int height, Rational fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
        EnsureInit();
        if (!_initialized) throw _initError ?? new InvalidOperationException("NDI SDK failed to initialize.");
    }

    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(PlaylistItem item, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReceiver(item.MediaPath);
        if (_recv == nint.Zero) yield break;

        long index = 0;
        var timeoutMs = 500;

        while (!ct.IsCancellationRequested)
        {
            var video = new NDIlib_video_frame_v2_t();
            var audio = new NDIlib_audio_frame_v2_t();
            var meta = new NDIlib_metadata_frame_t();

            var type = NDIlib_recv_capture_v2(_recv, ref video, ref audio, ref meta, timeoutMs);
            if (type == (int)frame_type_e.video)
            {
                try
                {
                    var frameBytes = CopyAndScaleBgra(video);
                    yield return new VideoFrame(_width, _height, Playout.Engine.Types.PixelFormat.BGRA, frameBytes, index++);
                }
                finally
                {
                    NDIlib_recv_free_video_v2(_recv, ref video);
                }
            }
            else if (type == (int)frame_type_e.error)
            {
                await Task.Delay(10, ct);
            }
            else if (type == (int)frame_type_e.audio)
            {
                NDIlib_recv_free_audio_v2(_recv, ref audio);
            }
            else if (type == (int)frame_type_e.metadata)
            {
                NDIlib_recv_free_metadata(_recv, ref meta);
            }

            await Task.Yield();
        }
    }

    void EnsureReceiver(string sourceName)
    {
        if (_recv != nint.Zero) return;

        var settings = new NDIlib_recv_create_v3_t
        {
            source_to_connect_to = new NDIlib_source_t { p_ndi_name = nint.Zero, p_url_address = nint.Zero },
            color_format = (int)recv_color_format_e.BGRA,
            bandwidth = (int)recv_bandwidth_e.highest,
            allow_video_fields = true,
            p_ndi_recv_name = nint.Zero
        };

        _recv = NDIlib_recv_create_v3(ref settings);
        if (_recv == nint.Zero) return;

        string connectName = sourceName;
        if (string.IsNullOrWhiteSpace(connectName))
        {
            var list = DiscoverSources(1500);
            if (list.Count > 0) connectName = list[0];
            else return;
        }

        if (connectName.Contains("://"))
        {
            var urlPtr = Marshal.StringToHGlobalAnsi(connectName);
            try
            {
                var source = new NDIlib_source_t { p_ndi_name = nint.Zero, p_url_address = urlPtr };
                NDIlib_recv_connect(_recv, ref source);
            }
            finally
            {
                Marshal.FreeHGlobal(urlPtr);
            }
            return;
        }

        var namePtr = Marshal.StringToHGlobalAnsi(connectName);
        try
        {
            var source = new NDIlib_source_t { p_ndi_name = namePtr, p_url_address = nint.Zero };
            NDIlib_recv_connect(_recv, ref source);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    byte[] CopyAndScaleBgra(NDIlib_video_frame_v2_t vf)
    {
        if (vf.FourCC != 0 && vf.FourCC != FourCC_BGRA && vf.FourCC != FourCC_BGRX)
        {
            var blank = new byte[_width * _height * 4];
            ForceOpaque(blank);
            return blank;
        }

        if (vf.p_data == nint.Zero || vf.xres <= 0 || vf.yres <= 0)
        {
            var blank = new byte[_width * _height * 4];
            ForceOpaque(blank);
            return blank;
        }

        var srcW = vf.xres;
        var srcH = vf.yres;
        var strideSigned = vf.line_stride_in_bytes;
        var strideAbs = Math.Abs(strideSigned);
        if (strideAbs <= 0) strideAbs = srcW * 4;
        if (strideAbs < srcW * 4) strideAbs = srcW * 4;
        var raw = new byte[strideAbs * srcH];
        try
        {
            Marshal.Copy(vf.p_data, raw, 0, raw.Length);
        }
        catch
        {
            Array.Clear(raw);
        }
        ForceOpaque(raw);

        if (srcW == _width && srcH == _height)
        {
            var buffer = new byte[_width * _height * 4];
            for (int y = 0; y < srcH; y++)
            {
                int srcY = strideSigned < 0 ? (srcH - 1 - y) : y;
                int srcOffset = srcY * strideAbs;
                int destOffset = y * _width * 4;
                Buffer.BlockCopy(raw, srcOffset, buffer, destOffset, _width * 4);
            }
            return buffer;
        }

        // Scale using nearest-neighbor with aspect-preserving letterbox
        var dstBuffer = new byte[_width * _height * 4];
        for (int i = 3; i < dstBuffer.Length; i += 4) dstBuffer[i] = 255;

        double srcAspect = (double)srcW / srcH;
        double dstAspect = (double)_width / _height;
        int drawW, drawH, offX, offY;
        if (srcAspect > dstAspect)
        {
            drawW = _width;
            drawH = Math.Max(1, (int)Math.Round(_width / srcAspect));
            offX = 0;
            offY = (_height - drawH) / 2;
        }
        else
        {
            drawH = _height;
            drawW = Math.Max(1, (int)Math.Round(_height * srcAspect));
            offY = 0;
            offX = (_width - drawW) / 2;
        }

        for (int y = 0; y < drawH; y++)
        {
            int srcY = y * srcH / drawH;
            srcY = strideSigned < 0 ? (srcH - 1 - srcY) : srcY;
            int dstY = y + offY;
            if ((uint)dstY >= (uint)_height) continue;
            int srcBase = srcY * strideAbs;
            int dstBase = dstY * _width * 4;
            for (int x = 0; x < drawW; x++)
            {
                int srcX = x * srcW / drawW;
                int dstX = x + offX;
                if ((uint)dstX >= (uint)_width) continue;
                int s = srcBase + srcX * 4;
                int d = dstBase + dstX * 4;
                dstBuffer[d + 0] = raw[s + 0];
                dstBuffer[d + 1] = raw[s + 1];
                dstBuffer[d + 2] = raw[s + 2];
                dstBuffer[d + 3] = 255;
            }
        }

        return dstBuffer;
    }

    static int MakeFourCC(char a, char b, char c, char d)
    {
        return (byte)a | ((byte)b << 8) | ((byte)c << 16) | ((byte)d << 24);
    }

    static void ForceOpaque(byte[] buffer)
    {
        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
    }

    static void EnsureInit()
    {
        lock (_lock)
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                // Pre-load NDI native library from common locations
                nint handle = nint.Zero;
                bool ok = NativeLibrary.TryLoad("Processing.NDI.Lib.x64", out handle);
                if (!ok || handle == nint.Zero)
                {
                    var paths = new[]
                    {
                        @"C:\Program Files\NDI\NDI 5 Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                        @"C:\Program Files\NDI\NDI 6 Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                        @"C:\Program Files\NDI\NDI 5 Tools\Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                        @"C:\Program Files\NDI\NDI 6 Tools\Runtime\bin\x64\Processing.NDI.Lib.x64.dll"
                    };
                    foreach (var p in paths)
                    {
                        if (System.IO.File.Exists(p))
                        {
                            ok = NativeLibrary.TryLoad(p, out handle);
                            if (ok && handle != nint.Zero) break;
                        }
                    }
                }
                if (handle != nint.Zero)
                {
                    // free immediately; P/Invoke will bind when called
                    NativeLibrary.Free(handle);
                }
                _initialized = NDIlib_initialize();
            }
            catch (Exception ex)
            {
                _initialized = false;
                _initError = ex;
            }
        }
    }

    public void Dispose()
    {
        if (_recv != nint.Zero)
        {
            try { NDIlib_recv_destroy(_recv); } catch { }
            _recv = nint.Zero;
        }
    }

    public static IReadOnlyList<string> DiscoverSources(int waitMs = 1500)
    {
        EnsureInit();
        var list = new List<string>();
        if (!_initialized) throw _initError ?? new InvalidOperationException("NDI SDK failed to initialize.");

        var create = new NDIlib_find_create_t { show_local_sources = true, p_groups = nint.Zero, p_extra_ips = nint.Zero };
        var finder = NDIlib_find_create(ref create);
        if (finder == nint.Zero) return list;
        try
        {
            NDIlib_find_wait_for_sources(finder, waitMs);
            int count = 0;
            nint sourcesPtr = NDIlib_find_get_current_sources(finder, ref count);
            if (sourcesPtr != nint.Zero && count > 0)
            {
                var size = Marshal.SizeOf<NDIlib_source_t>();
                for (int i = 0; i < count; i++)
                {
                    var ptr = sourcesPtr + i * size;
                    var src = Marshal.PtrToStructure<NDIlib_source_t>(ptr);
                    var name = Marshal.PtrToStringAnsi(src.p_ndi_name);
                    if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
                }
            }
        }
        finally
        {
            try { NDIlib_find_destroy(finder); } catch { }
        }
        return list;
    }

    enum frame_type_e
    {
        none = 0,
        video = 1,
        audio = 2,
        metadata = 3,
        error = 4,
        status_change = 100
    }

    enum recv_color_format_e
    {
        BGRX_BGRA = 0,
        UYVY_BGRA = 1,
        RGBX_RGBA = 2,
        UYVY_RGBA = 3,
        BGRA = 4,
        RGBA = 5
    }

    enum recv_bandwidth_e
    {
        metadata_only = -10,
        audio_only = 10,
        lowest = 0,
        highest = 100
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_source_t
    {
        public nint p_ndi_name;
        public nint p_url_address;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_find_create_t
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool show_local_sources;
        public nint p_groups;
        public nint p_extra_ips;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_recv_create_v3_t
    {
        public NDIlib_source_t source_to_connect_to;
        public int color_format;
        public int bandwidth;
        [MarshalAs(UnmanagedType.U1)]
        public bool allow_video_fields;
        public nint p_ndi_recv_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_video_frame_v2_t
    {
        public int xres;
        public int yres;
        public int FourCC;
        public int frame_rate_N;
        public int frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public nint p_data;
        public int line_stride_in_bytes;
        public nint p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_audio_frame_v2_t
    {
        public int sample_rate;
        public int no_channels;
        public int no_samples;
        public long timecode;
        public nint p_data;
        public nint p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_metadata_frame_t
    {
        public int length;
        public nint p_data;
        public long timecode;
    }

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_initialize", CallingConvention = CallingConvention.Cdecl)]
    static extern bool NDIlib_initialize();

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_create_v3", CallingConvention = CallingConvention.Cdecl)]
    static extern nint NDIlib_recv_create_v3(ref NDIlib_recv_create_v3_t p_create_settings);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_connect", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_recv_connect(nint p_instance, ref NDIlib_source_t p_source);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_capture_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern int NDIlib_recv_capture_v2(nint p_instance, ref NDIlib_video_frame_v2_t p_video_data, ref NDIlib_audio_frame_v2_t p_audio_data, ref NDIlib_metadata_frame_t p_metadata, int timeout_in_ms);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_free_video_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_recv_free_video_v2(nint p_instance, ref NDIlib_video_frame_v2_t p_video_data);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_free_audio_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_recv_free_audio_v2(nint p_instance, ref NDIlib_audio_frame_v2_t p_audio_data);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_free_metadata", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_recv_free_metadata(nint p_instance, ref NDIlib_metadata_frame_t p_metadata);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_recv_destroy", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_recv_destroy(nint p_instance);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_find_create", CallingConvention = CallingConvention.Cdecl)]
    static extern nint NDIlib_find_create(ref NDIlib_find_create_t p_create_settings);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_find_wait_for_sources", CallingConvention = CallingConvention.Cdecl)]
    static extern bool NDIlib_find_wait_for_sources(nint p_instance, int timeout_in_ms);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_find_get_current_sources", CallingConvention = CallingConvention.Cdecl)]
    static extern nint NDIlib_find_get_current_sources(nint p_instance, ref int p_no_sources);

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_find_destroy", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_find_destroy(nint p_instance);
}
