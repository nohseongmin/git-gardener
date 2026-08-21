using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Win32;

namespace GitGardener;

/// <summary>
/// 설정·실행·로그를 한 화면에 담은 트레이 상주 창.
/// Visual Studio가 없는 환경을 전제로 Designer/resx 없이 코드로만 UI를 구성한다(PLAN.md).
/// </summary>
sealed class MainForm : Form
{
    /// 예전 방식. 로그온 때 조용히 씹히는 일이 있어 시작 폴더로 옮겼고, 남아 있으면 지운다.
    const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string LegacyRunValueName = "GitGardener";

    const string StartupLinkName = "GitGardener.lnk";

    /// 창 제목과 알림에 쓰는 표시 이름. 레지스트리 값 이름과 달리 사람이 읽는 쪽이다.
    const string AppTitle = "git gardener";

    const int RepoPanelWidth = 240;
    const int SchedulerTickMs = 30_000;
    const int BalloonMs = 10_000;
    const int MaxLogChars = 200_000;
    const int MaxBalloonChars = 200;
    const int RetryCooldownMinutes = 30;

    static readonly (string Key, string Label)[] ImprovementTypes =
    [
        ("auto", "자동 판단"),
        ("docs", "문서·주석"),
        ("refactor", "리팩토링"),
        ("bugfix", "버그 수정"),
        ("tests", "테스트 추가"),
    ];

    static readonly string[] Models = ["sonnet", "opus", "haiku"];

    static readonly (IssueMode Mode, string Label)[] IssueModes =
    [
        (IssueMode.Prefer, "이슈 우선 (없으면 생성)"),
        (IssueMode.Only, "이슈 있을 때만"),
        (IssueMode.None, "이슈 없이"),
    ];

    readonly Config _cfg;
    readonly CancellationTokenSource _cts = new();
    readonly DateTime _startedAt = DateTime.Now;

    readonly CheckedListBox _repos = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, BackColor = Color.White,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    readonly DateTimePicker _time = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 70,
    };
    readonly NumericUpDown _perDay = new() { Minimum = 1, Maximum = 20, Width = 55 };
    readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly ComboBox _issueMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly Button _refresh = new() { Text = "레포 새로고침", AutoSize = true };
    readonly Button _run = new() { Text = "지금 1회 실행", AutoSize = true };
    readonly Button _dryRun = new() { Text = "Dry-run", AutoSize = true };
    readonly Button _openPrs = new() { Text = "열린 PR", AutoSize = true };
    readonly Button _startup = new() { AutoSize = true };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Text = AppTitle, Visible = true };
    readonly System.Windows.Forms.Timer _scheduler = new() { Interval = SchedulerTickMs };

    PullRequestsForm? _prs;
    RegisteredWaitHandle? _showWait;
    bool _startHidden;
    bool _reposLoaded;
    bool _running;
    bool _exiting;
    DateTime _nextAttempt = DateTime.MinValue;

    public MainForm(Config cfg, bool startHidden, WaitHandle showRequested)
    {
        _cfg = cfg;
        _startHidden = startHidden;

        // 두 번째 실행이 신호를 보내면 이 창을 띄운다.
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            showRequested, (_, _) => OnShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);

        Text = AppTitle;
        Icon = SystemIcons.Application;
        // 설정 한 줄이 접히지 않을 만큼은 확보한다.
        MinimumSize = new Size(1010, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        LoadSettingsIntoUi();

        Log.Line += OnLogLine;
        _scheduler.Tick += (_, _) => CheckSchedule();
        _scheduler.Start();
    }

    /// 레포 목록은 핸들이 생긴 뒤에 읽는다. 생성자에서 BeginInvoke하면 핸들이 없어 터진다.
    /// --tray로 떠서 창을 한 번도 안 띄우는 경로에서도 여기는 지나간다(SetVisibleCore의 CreateHandle).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_reposLoaded) return;
        _reposLoaded = true;

        // 생성자에서 주면 SplitContainer가 아직 기본 크기(150px)라 값이 잘려 레포 이름이 안 보인다.
        _split.SplitterDistance = RepoPanelWidth;
        BeginInvoke(RefreshRepos);
    }

    void BuildUi()
    {
        _type.Items.AddRange(ImprovementTypes.Select(t => (object)t.Label).ToArray());
        _model.Items.AddRange(Models);
        _issueMode.Items.AddRange(IssueModes.Select(m => (object)m.Label).ToArray());
        _repos.DisplayMember = nameof(GhRepo.Display);

        _refresh.Click += (_, _) => RefreshRepos();
        _run.Click += (_, _) => Run(dryRun: false);
        _dryRun.Click += (_, _) => Run(dryRun: true);
        _openPrs.Click += (_, _) => OpenPullRequests();
        _startup.Click += (_, _) => ToggleStartup();

        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        settings.Controls.AddRange([
            Caption("실행 시각"), _time,
            Caption("하루 레포 수"), _perDay,
            Caption("개선 유형"), _type,
            Caption("모델"), _model,
            Caption("이슈"), _issueMode,
        ]);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_refresh, _run, _dryRun, _openPrs, _startup, _status]);

        _split.Panel1.Controls.Add(_repos);
        _split.Panel1.Controls.Add(new Label { Text = "대상 레포", Dock = DockStyle.Top, Height = 20 });
        _split.Panel2.Controls.Add(_log);
        _split.Panel2.Controls.Add(new Label { Text = "로그", Dock = DockStyle.Top, Height = 20 });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(settings, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        var menu = new ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => ShowWindow());
        menu.Items.Add("지금 실행", null, (_, _) => Run(dryRun: false));
        menu.Items.Add("열린 PR", null, (_, _) => OpenPullRequests());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(12, 6, 2, 0) };

    void LoadSettingsIntoUi()
    {
        _time.Value = DateTime.Today.Add(_cfg.Schedule.ToTimeSpan());
        _perDay.Value = Math.Min(_cfg.ReposPerDay, _perDay.Maximum);
        _type.SelectedIndex = Math.Max(0, Array.FindIndex(ImprovementTypes, t => t.Key == _cfg.ImprovementType));
        _model.SelectedIndex = Math.Max(0, Array.IndexOf(Models, _cfg.Model));
        _issueMode.SelectedIndex = Math.Max(0, Array.FindIndex(IssueModes, m => m.Mode == _cfg.IssueMode));
        UpdateStartupButton();
    }

    void SaveSettings()
    {
        if (_repos.Items.Count > 0)
            _cfg.EnabledRepos = _repos.CheckedItems.Cast<GhRepo>().Select(r => r.Name).ToList();
        _cfg.ScheduleTime = _time.Value.ToString("HH:mm");
        _cfg.ReposPerDay = (int)_perDay.Value;
        _cfg.ImprovementType = ImprovementTypes[_type.SelectedIndex].Key;
        _cfg.Model = Models[_model.SelectedIndex];
        _cfg.IssueMode = IssueModes[_issueMode.SelectedIndex].Mode;
        _cfg.RunAtStartup = IsRegisteredAtStartup();
        _cfg.Save();
    }

    async void RefreshRepos()
    {
        if (_running) return;
        SetRunning(true, "레포 목록을 읽는 중");
        try
        {
            var runner = new Runner(_cfg);
            try
            {
                Log.Write($"claude {await runner.ClaudeVersionAsync(_cts.Token)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 점검이 실패해도 목록은 읽는다. 대신 예약 시각까지 모르고 있지는 않게 한다.
                Log.Write($"claude를 쓸 수 없습니다. 이대로면 예약 실행이 실패합니다: {ex.Message}");
            }

            var repos = await runner.ListReposAsync(_cts.Token);
            _repos.Items.Clear();
            foreach (var repo in repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                // 설정에 선택 이력이 없으면(첫 실행) archived·fork는 꺼둔 채로 시작한다.
                var enabled = _cfg.EnabledRepos.Count > 0
                    ? _cfg.EnabledRepos.Contains(repo.Name)
                    : !repo.IsArchived && !repo.IsFork;
                _repos.Items.Add(repo, enabled);
            }
            // 예약 실행은 설정 파일만 보고 돈다. 창을 한 번도 안 열어도 대상이 남아 있게 여기서 저장한다.
            SaveSettings();
            Log.Write($"{_cfg.GithubUser} 레포 {repos.Count}개 로드, {_repos.CheckedItems.Count}개 선택됨");
        }
        catch (OperationCanceledException)
        {
            // 종료 중이다. 남길 것이 없다.
        }
        catch (Exception ex)
        {
            Log.Write($"레포 목록을 읽지 못했습니다: {ex.Message}");
        }
        finally
        {
            SetRunning(false, "");
        }
    }

    async void Run(bool dryRun)
    {
        if (_running) return;
        SetRunning(true, dryRun ? "Dry-run 진행 중" : "실행 중");
        SaveSettings();

        var runner = new Runner(_cfg);
        runner.Notify += Balloon;
        try
        {
            Log.Write(dryRun ? "=== Dry-run 시작 ===" : "=== 실행 시작 ===");
            // 스레드풀에서 돌린다. UI 스레드에서 그대로 await 하면 파이프라인의 모든 연속 실행이
            // UI 스레드로 돌아와 메시지 펌프를 막고, 윈도우가 "응답 없음"으로 판정해 앱을 닫는다.
            var created = await Task.Run(() => runner.RunAsync(dryRun, _cts.Token), _cts.Token);
            if (!dryRun)
            {
                _cfg.LastRunDate = Today();
                _cfg.Save();
            }
            Log.Write($"=== 완료 — PR {created}건 ===");
        }
        catch (OperationCanceledException)
        {
            Log.Write("=== 취소됨 ===");
        }
        catch (Exception ex)
        {
            // 예약 실행이 실패한 뒤 30초마다 재시도하며 두들기지 않도록 쿨다운을 건다.
            _nextAttempt = DateTime.Now.AddMinutes(RetryCooldownMinutes);
            Log.Write($"=== 중단: {ex.Message} (재시도 {RetryCooldownMinutes}분 후) ===");
            Balloon($"{AppTitle} 실패", ex.Message);
        }
        finally
        {
            SetRunning(false, "");
        }
    }

    /// <summary>
    /// 하루 한 번 돈다. 오늘 이미 돌았으면 아무것도 하지 않는다.
    ///
    /// 오늘 켠 PC라면 예약 시각을 기다리지 않고 부팅 유예만 지나면 바로 돈다.
    /// 예약 시각까지 기다리면 그 시각에 PC가 꺼져 있는 날은 통째로 비는데,
    /// 낮에만 쓰는 PC에서는 그게 매일이다.
    ///
    /// 어제부터 계속 켜져 있던 경우에만 정해둔 시각을 지킨다.
    /// </summary>
    void CheckSchedule()
    {
        if (_running || DateTime.Now < _nextAttempt || _cfg.LastRunDate == Today()) return;

        var now = DateTime.Now;
        var bootedToday = _startedAt.Date == now.Date;

        if (bootedToday)
        {
            // 부팅 직후 몰아치지 않게 유예를 둔다.
            if (now < _startedAt.AddMinutes(_cfg.CatchUpDelayMinutes)) return;
        }
        else if (TimeOnly.FromDateTime(now) < _cfg.Schedule)
        {
            return;
        }

        Log.Write(bootedToday ? "부팅 후 오늘 몫 실행" : "예약 실행");
        Run(dryRun: false);
    }

    void ToggleStartup()
    {
        try
        {
            SetStartup(!IsRegisteredAtStartup());
            UpdateStartupButton();
            SaveSettings();
            Log.Write(_cfg.RunAtStartup ? "시작프로그램에 등록했습니다." : "시작프로그램에서 해제했습니다.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"시작프로그램 설정 실패: {ex.Message}");
        }
    }

    void UpdateStartupButton() =>
        _startup.Text = IsRegisteredAtStartup() ? "시작프로그램 해제" : "시작프로그램 등록";

    static string StartupLinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLinkName);

    static bool IsRegisteredAtStartup() => File.Exists(StartupLinkPath);

    /// <summary>
    /// 시작 폴더에 바로가기를 둔다. HKCU Run 키는 로그온 때 실행되지 않는 경우가 있었고,
    /// 예약 작업은 로그온 트리거라 관리자 권한을 요구해서 설치 과정에 넣을 수 없다.
    /// 시작 폴더는 권한 없이 되고 탐색기가 로그온마다 처리한다.
    /// </summary>
    static void SetStartup(bool enabled)
    {
        RemoveLegacyRunEntry();

        if (!enabled)
        {
            File.Delete(StartupLinkPath);
            return;
        }

        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        WriteShortcut(StartupLinkPath, exe);
    }

    /// 예전 등록이 남아 있으면 지운다. 두 경로가 같이 살아 있으면 로그온 때 두 번 뜬다.
    static void RemoveLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
        key?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
    }

    /// .lnk를 만들 수 있는 관리되는 API가 없어 셸의 COM 객체를 늦은 바인딩으로 쓴다.
    static void WriteShortcut(string linkPath, string exe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell을 찾지 못했습니다.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell을 만들지 못했습니다.");
        try
        {
            var link = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [linkPath])!;
            var linkType = link.GetType();
            void Set(string name, object value) =>
                linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, [value]);

            Set("TargetPath", exe);
            Set("Arguments", Program.TrayArg);
            Set("WorkingDirectory", Path.GetDirectoryName(exe)!);
            Set("Description", $"{AppTitle} - 트레이 상주");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    void SetRunning(bool running, string status)
    {
        _running = running;
        _status.Text = status;
        foreach (var button in new[] { _refresh, _run, _dryRun }) button.Enabled = !running;
        Cursor = running ? Cursors.AppStarting : Cursors.Default;
    }

    void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => OnLogLine(line));
                return;
            }
            if (_log.TextLength > MaxLogChars) _log.Clear();
            _log.AppendText(line + Environment.NewLine);
        }
        catch (ObjectDisposedException)
        {
            // 종료 중 마지막 로그. 화면이 이미 사라졌으니 파일 기록으로 충분하다.
        }
    }

    void Balloon(string title, string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => Balloon(title, text));
            return;
        }
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text.Length > MaxBalloonChars ? text[..MaxBalloonChars] : text;
        _tray.ShowBalloonTip(BalloonMs);
    }

    /// 두 번째 인스턴스가 보낸 신호. 스레드풀에서 오므로 UI 스레드로 넘긴다.
    void OnShowRequested()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(ShowWindow);
            else ShowWindow();
        }
        catch (ObjectDisposedException)
        {
            // 종료 중이다. 띄울 창이 없다.
        }
    }

    /// <summary>만든 PR을 확인하는 동선이 앱 밖에 있으면 쌓여도 모른다. 목록을 앱 안에서 연다.</summary>
    void OpenPullRequests()
    {
        if (_prs is null || _prs.IsDisposed)
        {
            _prs = new PullRequestsForm();
            _prs.FormClosed += (_, _) => _prs = null;
        }
        _prs.Show(this);
        _prs.WindowState = FormWindowState.Normal;
        _prs.Activate();
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    /// 트레이 시작(--tray)일 때 창을 한 번도 띄우지 않되, 로그 마샬링을 위해 핸들은 만들어 둔다.
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            if (!IsHandleCreated) CreateHandle();
            _startHidden = false;
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            Balloon(AppTitle, "트레이에서 계속 실행 중입니다.");
            return;
        }

        Log.Line -= OnLogLine;
        _scheduler.Stop();
        _cts.Cancel();
        SaveSettings();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait?.Unregister(null);
            _tray.Dispose();
            _scheduler.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
