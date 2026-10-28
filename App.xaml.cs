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





