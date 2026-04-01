using System;
using System.IO;
using System.Runtime.InteropServices;
using Playout.Engine.Abstractions;
using Playout.Engine.Types;

namespace Playout.Engine.Sinks;

public sealed class NdiSink : IVideoSink, IDisposable
{
    readonly string _name;
    nint _send;
    static bool _initialized;
    static bool _initTried;
    static readonly object _lock = new();

    public NdiSink(string name)
    {
        _name = name;
        EnsureInit();
        if (!_initialized) throw new InvalidOperationException("NDI SDK failed to initialize.");

        var sc = new NDIlib_send_create_t
        {
            p_ndi_name = Marshal.StringToHGlobalAnsi(_name),
            p_groups = nint.Zero,
            clock_video = true,
            clock_audio = true
        };
        
        _send = NDIlib_send_create(ref sc);
        if (sc.p_ndi_name != nint.Zero) Marshal.FreeHGlobal(sc.p_ndi_name);

        if (_send == nint.Zero) throw new InvalidOperationException("NDIlib_send_create failed.");
    }

    public void Send(VideoFrame frame)
    {
        if (_send == nint.Zero) return;

        // Send Video
        var vf = new NDIlib_video_frame_v2_t
        {
            xres = frame.Width,
            yres = frame.Height,
            FourCC = (int)FourCC_type_e.BGRA,
            frame_rate_N = 30000,
            frame_rate_D = 1001,
            picture_aspect_ratio = (float)frame.Width / frame.Height,
            frame_format_type = (int)frame_format_type_e.progressive,
            timecode = send_timecode_synthesize,
            line_stride_in_bytes = frame.Width * 4,
            p_metadata = nint.Zero,
            timestamp = 0
        };

        var vHandle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
        try
        {
            vf.p_data = vHandle.AddrOfPinnedObject();
            NDIlib_send_send_video_v2(_send, ref vf);
        }
        finally
        {
            vHandle.Free();
        }

        // Send Audio if present
        if (frame.AudioData != null && frame.AudioData.Length > 0)
        {
            var af = new NDIlib_audio_frame_v2_t
            {
                sample_rate = frame.AudioSampleRate,
                no_channels = frame.AudioChannels,
                no_samples = frame.AudioData.Length / frame.AudioChannels,
                timecode = send_timecode_synthesize,
                p_metadata = nint.Zero,
                timestamp = 0
            };

            var aHandle = GCHandle.Alloc(frame.AudioData, GCHandleType.Pinned);
            try
            {
                af.p_data = aHandle.AddrOfPinnedObject();
                NDIlib_send_send_audio_v2(_send, ref af);
            }
            finally
            {
                aHandle.Free();
            }
        }
    }

    public void Dispose()
    {
        if (_send != nint.Zero)
        {
            NDIlib_send_destroy(_send);
            _send = nint.Zero;
        }
    }

    static void EnsureInit()
    {
        lock (_lock)
        {
            if (_initialized || _initTried) return;
            _initTried = true;

            try
            {
                // Try standard search path first
                if (!NativeLibrary.TryLoad("Processing.NDI.Lib.x64", out _))
                {
                    // Try common install paths
                    var paths = new[]
                    {
                        @"C:\Program Files\NDI\NDI 5 Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                        @"C:\Program Files\NDI\NDI 6 Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                        @"C:\Program Files\NDI\NDI 5 Tools\Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                        @"C:\Program Files\NDI\NDI 6 Tools\Runtime\bin\x64\Processing.NDI.Lib.x64.dll"
                    };

                    foreach (var p in paths)
                    {
                        if (File.Exists(p) && NativeLibrary.TryLoad(p, out _))
                        {
                            break;
                        }
                    }
                }

                _initialized = NDIlib_initialize();
            }
            catch { _initialized = false; }
        }
    }

    const long send_timecode_synthesize = -1;
    enum FourCC_type_e : int
    {
        BGRA = 0x41524742
    }
    enum frame_format_type_e : int
    {
        progressive = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NDIlib_send_create_t
    {
        public nint p_ndi_name;
        public nint p_groups;
        [MarshalAs(UnmanagedType.I1)] public bool clock_video;
        [MarshalAs(UnmanagedType.I1)] public bool clock_audio;
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

    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_initialize", CallingConvention = CallingConvention.Cdecl)]
    static extern bool NDIlib_initialize();
    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_send_create", CallingConvention = CallingConvention.Cdecl)]
    static extern nint NDIlib_send_create(ref NDIlib_send_create_t p_create_settings);
    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_send_destroy", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_send_destroy(nint p_instance);
    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_send_send_video_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_send_send_video_v2(nint p_instance, ref NDIlib_video_frame_v2_t p_video_data);
    [DllImport("Processing.NDI.Lib.x64", EntryPoint = "NDIlib_send_send_audio_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern void NDIlib_send_send_audio_v2(nint p_instance, ref NDIlib_audio_frame_v2_t p_audio_data);
}
