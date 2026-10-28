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
                // mixer self-check on a real file: gain 0 must be silent even into a dirty buffer, 0.5x/2x must scale exactly
                Directory.CreateDirectory(Ff.Cache); Ff.MakeRaw(e.Args[1]); var rows = new System.Collections.Generic.List<string>(); bool ok = true;
                double Rms(double vol)
                {
                    var eng = new AudioEngine(); eng.SetClips(new[] { new AClip { Key = new object(), Raw = Ff.Raw(e.Args[1]), Start = 0, End = 4, Src = 60, Volume = vol } }); eng.Seek(0);
                    var buf = new float[882]; double s = 0; int n = 0;
                    for (int blk = 0; blk < 400; blk++) { System.Array.Fill(buf, 0.9f); eng.Read(buf, 0, buf.Length); foreach (var x in buf) { s += x * x; n++; } } // buffer is reused and dirty, like the audio device's
                    return System.Math.Sqrt(s / n);
                }
                double r1 = Rms(1), r05 = Rms(0.5), r2 = Rms(2), r0 = Rms(0);
                ok &= r0 == 0 && System.Math.Abs(r05 / r1 - 0.5) < 0.01 && System.Math.Abs(r2 / r1 - 2) < 0.05;
                File.WriteAllText(e.Args[1] + ".audiotest.txt", $"{(ok ? "PASS" : "FAIL")}  rms x1={r1:0.0000}  x0.5={r05 / r1:0.000}  x2={r2 / r1:0.000}  x0={r0:0.0000}"); Shutdown(); return;
            }            if (e.Args.Length == 4 && e.Args[0] == "--frame")
            {
                var r = Ff.Frame(JsonSerializer.Deserialize<Project>(File.ReadAllText(e.Args[1])), double.Parse(e.Args[3], System.Globalization.CultureInfo.InvariantCulture), e.Args[2], null);
                File.WriteAllText(e.Args[2] + ".log", r ?? "ok"); Shutdown(); return;
            }
            DispatcherUnhandledException += (_, ex) => { MessageBox.Show(ex.Exception.Message, "MiniCut"); ex.Handled = true; };
            base.OnStartup(e);
        }
    }
}

















