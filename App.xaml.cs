using System.IO;
using System.Text;
using System.Windows;
namespace SuiteRedes;

public partial class App : Application
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuiteRedes", "erros.log");

    public App()
    {
        // threads de trabalho: valores moderados (rajadas enormes de threads de rede lembram varredura e pesam na heurística de antivírus)
        int cores = Environment.ProcessorCount;
        ThreadPool.SetMinThreads(Math.Max(16, cores * 2), Math.Max(32, cores * 4));
        // .NET Framework: habilita TLS 1.2/1.3 nas conexões HTTPS (Windows 7/8.1 vêm com padrões antigos)
        try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288; }
        catch { try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { } }
        // Erros não tratados: registra em log e avisa, sem derrubar o programa quando for possível continuar.
        DispatcherUnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; ShowError(e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    static void ShowError(Exception ex)
    {
        try { MessageBox.Show($"Ocorreu um erro inesperado, mas o programa continua aberto.\n\n{ex.Message}\n\nDetalhes gravados em:\n{LogPath}", "Suíte de Redes", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
    }
}
