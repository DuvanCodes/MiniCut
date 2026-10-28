using System.IO;
using System.Text.Json;
using System.Windows;

namespace MiniCut
{
    public partial class App : Application
    {
        // headless: MiniCut.exe --render project.minicut out.mp4   (also used to self-test the engine)
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length == 3 && e.Args[0] == "--render")
            {
                string err; try { err = Ff.Export(JsonSerializer.Deserialize<Project>(File.ReadAllText(e.Args[1])), e.Args[2]); } catch (System.Exception ex) { err = ex.ToString(); }
                File.WriteAllText(e.Args[2] + ".log", err ?? "ok");
                Shutdown(); return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--audiotest")
            {
                Directory.CreateDirectory(Ff.Cache); var sb = new System.Text.StringBuilder(); sb.AppendLine("raw ok: " + Ff.MakeRaw(e.Args[1]));
                foreach (var (vol, fi) in new[] { (1.0, 0.0), (0.5, 0.0), (2.0, 0.0), (0.0, 0.0), (1.0, 2.0) })
                {
                    var eng = new AudioEngine(); eng.SetClips(new[] { new AClip { Raw = Ff.Raw(e.Args[1]), Start = 0, End = 4, Src = 60, Volume = vol, FadeIn = fi } });
                    eng.Seek(0); var buf = new float[44100 * 2]; double[] rms = new double[4];
                    for (int sec = 0; sec < 4; sec++) { eng.Read(buf, 0, buf.Length); double s = 0; foreach (var x in buf) s += x * x; rms[sec] = System.Math.Sqrt(s / buf.Length); }
                    sb.AppendLine($"vol={vol} fadein={fi}s  RMS per second: " + string.Join("  ", System.Array.ConvertAll(rms, r => r.ToString("0.0000"))));
                }
                File.WriteAllText(e.Args[1] + ".audiotest.txt", sb.ToString()); Shutdown(); return;
            }
            if (e.Args.Length == 4 && e.Args[0] == "--frame")
            {
                var r = Ff.Frame(JsonSerializer.Deserialize<Project>(File.ReadAllText(e.Args[1])), double.Parse(e.Args[3], System.Globalization.CultureInfo.InvariantCulture), e.Args[2], null);
                File.WriteAllText(e.Args[2] + ".log", r ?? "ok"); Shutdown(); return;
            }
            DispatcherUnhandledException += (_, ex) => { MessageBox.Show(ex.Exception.Message, "MiniCut"); ex.Handled = true; };
            base.OnStartup(e);
        }
    }
}







