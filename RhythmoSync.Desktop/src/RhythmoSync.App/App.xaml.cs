using System.Windows;

namespace RhythmoSync.App;

public partial class App : Application
{
    /// <summary>Fichier (.rsp ou vidéo) passé en argument de ligne de commande.</summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && System.IO.File.Exists(e.Args[0]))
            StartupFile = e.Args[0];
        base.OnStartup(e);
    }
}
