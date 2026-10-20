using System.IO;
using System.Media;

namespace FocusYourself.Services;

public enum Chime
{
    /// <summary>专注开始：上行的明亮和弦。</summary>
    FocusStart,
    /// <summary>休息开始：下行的柔和和弦。</summary>
    BreakStart,
    /// <summary>程序被拦截：短促的双音。</summary>
    Blocked,
    /// <summary>番茄钟停止。</summary>
    Stop,
}

/// <summary>运行时合成提示音（类似木琴/颂钵的泛音衰减），不需要任何音频资源文件。</summary>
public static class ChimePlayer
{
    const int SampleRate = 44100;
    static readonly Dictionary<Chime, byte[]> Cache = [];

    public static void Play(Chime chime)
    {
        byte[] wav;
        lock (Cache)
        {
            if (!Cache.TryGetValue(chime, out wav!))
                Cache[chime] = wav = Build(chime);
        }
        // SoundPlayer.Play 是异步的，但会在内部持有流，所以每次给一个新流
        Task.Run(() =>
        {
            try
            {
                using var player = new SoundPlayer(new MemoryStream(wav));
                player.PlaySync();
            }
            catch (InvalidOperationException) { }
        });
    }

    static byte[] Build(Chime chime)
    {
        // (频率 Hz, 起始秒, 时长秒, 音量)
        (double f, double t, double d, double a)[] notes = chime switch
        {
            Chime.FocusStart => [(523.25, 0, 1.2, 0.5), (659.25, 0.12, 1.2, 0.45), (783.99, 0.24, 1.5, 0.45), (1046.5, 0.36, 1.6, 0.3)],
            Chime.BreakStart => [(783.99, 0, 1.4, 0.45), (659.25, 0.16, 1.4, 0.45), (523.25, 0.32, 1.8, 0.5)],
            Chime.Blocked => [(880, 0, 0.35, 0.4), (659.25, 0.13, 0.5, 0.4)],
            _ => [(659.25, 0, 0.9, 0.4), (493.88, 0.18, 1.2, 0.4)],
        };
        var length = notes.Max(n => n.t + n.d) + 0.1;
        var samples = new float[(int)(length * SampleRate)];

        foreach (var (f, t, d, a) in notes)
        {
            int start = (int)(t * SampleRate), count = (int)(d * SampleRate);
            for (int i = 0; i < count && start + i < samples.Length; i++)
            {
                double x = (double)i / SampleRate;
                double attack = Math.Min(1, x / 0.008);           // 8ms 起音，避免爆音
                double decay = Math.Exp(-x * 3.2 / d * 2);        // 指数衰减
                double tone = Math.Sin(2 * Math.PI * f * x)
                              + 0.35 * Math.Sin(2 * Math.PI * f * 2 * x) * Math.Exp(-x * 6)   // 二次泛音，衰减更快
                              + 0.12 * Math.Sin(2 * Math.PI * f * 3.01 * x) * Math.Exp(-x * 9);
                samples[start + i] += (float)(tone * attack * decay * a * 0.5);
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
