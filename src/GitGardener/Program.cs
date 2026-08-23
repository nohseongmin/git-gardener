using System.Diagnostics;

namespace GitGardener;

static class Program
{
    public const string TrayArg = "--tray";

    /// exe에 박아둔 아이콘. 창·트레이·바로가기가 모두 이걸 쓴다.
    public static Icon AppIcon { get; } = LoadIcon();

    static Icon LoadIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("GitGardener.app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    const string SingleInstanceMutex = @"Local\GitGardener.SingleInstance";
    const string ShowWindowEvent = @"Local\GitGardener.ShowWindow";

    [STAThread]
    static void Main(string[] args)
    {
        // 시작프로그램 등록 상태에서 사용자가 exe를 또 실행해도 트레이 아이콘이 둘로 늘지 않게 한다.
        using var single = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFirstInstance);
        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEvent);

        // 이미 떠 있으면 그냥 죽지 말고 그쪽 창을 띄워준다.
        // 아무 반응이 없으면 사용자는 실행이 실패한 줄 안다.
        if (!isFirstInstance)
        {
            showRequested.Set();
            return;
        }

        Directory.CreateDirectory(Paths.Roaming);
        Directory.CreateDirectory(Paths.Local);

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write($"처리되지 않은 UI 예외: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"처리되지 않은 예외: {e.ExceptionObject}");

        var startHidden = args.Contains(TrayArg, StringComparer.OrdinalIgnoreCase);
        var cfg = Config.Load();

        // 받아서 처음 실행한 상태면 설치부터 안내한다. 자동 실행(--tray)은 설치된 자리에서만 걸리므로 건너뛴다.
        if (!startHidden && !IsInstalled(cfg))
        {
            using var setup = new SetupForm();
            if (setup.ShowDialog() != DialogResult.OK) return;

            // 설치한 자리의 실행 파일로 넘긴다. 지금 도는 것은 받아둔 원본일 수 있다.
            if (setup.InstalledExe is { } exe && !string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return;
            }
            cfg = Config.Load();
        }

        Application.Run(new MainForm(cfg, startHidden, showRequested));
    }

    /// 설정에 적힌 자리에서 돌고 있어야 설치가 끝난 것으로 본다.
    static bool IsInstalled(Config cfg) =>
        cfg.InstallPath.Length > 0
        && string.Equals(cfg.InstallPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
}
