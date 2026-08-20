namespace GitGardener;

static class Program
{
    public const string TrayArg = "--tray";

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
        Application.Run(new MainForm(Config.Load(), startHidden, showRequested));
    }
}
