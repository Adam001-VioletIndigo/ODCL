using Microsoft.UI.Xaml;

namespace ODCL;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cla = Environment.GetCommandLineArgs();
        if (Array.Exists(cla, a => a == "--selftest"))
        {
            try
            {
                DiskStrategy.SelfCheck();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "odcl-selftest.txt"), "PASS");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "odcl-selftest.txt"), "FAIL: " + ex.Message);
                Environment.Exit(1);
            }
        }
        if (Array.Exists(cla, a => a == "--dbselftest"))
        {
            var outPath = Path.Combine(AppContext.BaseDirectory, "odcl-dbtest.txt");
            try
            {
                var db = new DbService(DbPaths.DefaultDbPath());
                var stats = db.GetStats();
                var sessions = db.GetSessions();
                var L = new List<string>
                {
                    $"PASS db={db.DbPath} exists={db.Exists()}",
                    $"stats event={stats.EventBytes} related={stats.RelatedBytes} freelist={stats.FreelistBytes} total={stats.TotalFileSize} free={stats.FreeDisk}",
                    $"sessions={sessions.Count} orphans={db.OrphanEvents}/{db.OrphanBytes}",
                };
                if (sessions.Count > 0)
                {
                    var ms = db.GetMessages(sessions[0].Id);
                    var pt = db.GetParts(sessions[0].Id);
                    L.Add($"first session msgs={ms.Count} parts={pt.Count}");
                    if (ms.Count > 0)
                        L.Add("msg json: " + ms[0].Json[..Math.Min(80, ms[0].Json.Length)]);
                }
                File.WriteAllLines(outPath, L);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(outPath, "FAIL: " + ex);
                Environment.Exit(1);
            }
        }

        _window = new MainWindow();
        _window.Activate();
    }
}