using Avalonia;
using Fst.App;

namespace Fst.Linux;
internal static class Program
{
    private static FileStream? instance;
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return SelfTests.Run();
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This build targets Linux.");
            return 1;
        }
        try
        {
            Directory.CreateDirectory(Settings.DataRoot);
            try
            {
                instance = new FileStream(Path.Combine(Settings.DataRoot, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { if (!args.Contains("--background")) File.WriteAllText(Path.Combine(Settings.DataRoot, "activate"), Guid.NewGuid().ToString()); return 0; }
            AppBuilder.Configure<DesktopApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e) { JobService.Log("Сбой FST: {0}", e.GetType().Name + ": " + L.T(e.Message)); return 1; }
        finally { instance?.Dispose(); }
    }
}
