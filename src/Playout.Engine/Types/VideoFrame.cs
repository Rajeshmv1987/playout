namespace Playout.Engine.Types;

public enum PixelFormat
{
    BGRA
}

public readonly struct Rational
{
    public int Num { get; }
    public int Den { get; }
    public Rational(int num, int den)
    {
        Num = num;
        Den = den;
    }
    public double ToDouble() => (double)Num / Den;
}

public sealed class VideoFrame
{
    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }
    public byte[] Data { get; }
    public long FrameIndex { get; }
    
    // Audio data (interleaved float samples, e.g. 48kHz Stereo)
    public float[]? AudioData { get; init; }
    public int AudioChannels { get; init; }
    public int AudioSampleRate { get; init; }

    public VideoFrame(int width, int height, PixelFormat format, byte[] data, long frameIndex)
    {
        Width = width;
        Height = height;
        Format = format;
        Data = data;
        FrameIndex = frameIndex;
    }
}
