using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace MiniCut
{
    // Immutable snapshot of a clip for the audio thread (the UI keeps editing the real Seg).
    public class AClip
    {
        public string Raw; public double Start, End, Src, FadeIn, FadeOut, Volume; public double[] KT = Array.Empty<double>(), KV = Array.Empty<double>();
        public static AClip From(Seg s, string raw)
        {
            var k = s.Of("volume");
            return new AClip { Raw = raw, Start = s.Start, End = s.End, Src = s.Src, FadeIn = s.FadeIn, FadeOut = s.FadeOut, Volume = s.Volume, KT = k.ConvertAll(x => x.T).ToArray(), KV = k.ConvertAll(x => x.V).ToArray() };
        }
        // linear gain at time rel (seconds into the clip): keyframed/static volume x fades; same math the ffmpeg export uses
        public double Gain(double rel)
        {
            double v = Volume;
            if (KT.Length > 0)
            {
                v = KV[^1];
                if (rel <= KT[0]) v = KV[0];
                else for (int i = 0; i < KT.Length - 1; i++) if (rel < KT[i + 1]) { v = KV[i] + (rel - KT[i]) / (KT[i + 1] - KT[i]) * (KV[i + 1] - KV[i]); break; }
            }
            double dur = End - Start;
            if (FadeIn > 0) v *= Math.Clamp(rel / FadeIn, 0, 1);
            if (FadeOut > 0) v *= Math.Clamp((dur - rel) / FadeOut, 0, 1);
            return v;
        }
    }

    // Real-time mixer: reads pre-decoded 16-bit stereo 44.1k PCM, applies gain (can exceed 0 dB, unlike MediaElement.Volume), sums and clips.
    public class AudioEngine : ISampleProvider
    {
        const int Rate = 44100;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        volatile AClip[] clips = Array.Empty<AClip>();
        long pos; WaveOutEvent dev; readonly Dictionary<string, FileStream> files = new(); byte[] tmp = new byte[Rate * 4];

        public bool Ready => dev != null;
        public void SetClips(AClip[] c) => clips = c;
        public void Seek(double t) => Interlocked.Exchange(ref pos, (long)(Math.Max(0, t) * Rate));
        public void Init() { try { dev = new WaveOutEvent { DesiredLatency = 100 }; dev.Init(this.ToWaveProvider()); } catch { dev = null; } }
        public void Start(double t) { Seek(t); try { dev?.Play(); } catch { dev = null; } }
        public void Stop() { try { dev?.Pause(); } catch { } }

        public int Read(float[] buf, int off, int count)
        {
            Array.Clear(buf, off, count);
            int frames = count / 2; long p0 = Interlocked.Read(ref pos);
            foreach (var c in clips)
            {
                long cs = (long)(c.Start * Rate), ce = (long)(c.End * Rate), a = Math.Max(cs, p0), b = Math.Min(ce, p0 + frames);
                if (b <= a) continue;
                if (!files.TryGetValue(c.Raw, out var fs))
                {
                    try { files[c.Raw] = fs = new FileStream(c.Raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); } catch { continue; }
                }
                int n = (int)(b - a); if (tmp.Length < n * 4) tmp = new byte[n * 4];
                long srcFrame = (long)(c.Src * Rate) + (a - cs);
                if (srcFrame * 4 >= fs.Length) continue;
                fs.Position = srcFrame * 4; int got = 0; while (got < n * 4) { int r = fs.Read(tmp, got, n * 4 - got); if (r <= 0) break; got += r; }
                const int chunk = 441; // gain recomputed every 10 ms
                for (int i = 0; i < got / 4; i += chunk)
                {
                    float g = (float)c.Gain((a + i) / (double)Rate - c.Start); int end = Math.Min(got / 4, i + chunk);
                    for (int j = i; j < end; j++)
                    {
                        int o = off + (int)(a - p0 + j) * 2;
                        buf[o] += BitConverter.ToInt16(tmp, j * 4) / 32768f * g; buf[o + 1] += BitConverter.ToInt16(tmp, j * 4 + 2) / 32768f * g;
                    }
                }
            }
            for (int i = 0; i < count; i++) buf[off + i] = Math.Clamp(buf[off + i], -1f, 1f);
            Interlocked.Add(ref pos, frames);
            return count;
        }
    }
}

