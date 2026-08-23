using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GitGardener;

/// <summary>바로가기 만들기. 설치 마법사와 창의 토글이 같은 자리를 쓰도록 한곳에 둔다.</summary>
static class Shortcuts
{
    const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string LegacyRunValueName = "GitGardener";

    public static string StartupLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "GitGardener.lnk");

    public static string DesktopLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "git gardener.lnk");

    /// <summary>.lnk를 만드는 관리되는 API가 없어 셸의 COM 객체를 늦은 바인딩으로 쓴다.</summary>
    public static void Write(string linkPath, string exe, string arguments)
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
            Set("Arguments", arguments);
            Set("WorkingDirectory", Path.GetDirectoryName(exe)!);
            Set("Description", "git gardener");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// 예전에는 Run 키에 등록했다. 남아 있으면 로그온 때 두 번 뜬다.
    public static void RemoveLegacyRunEntry()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
        key?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
    }
}

/// <summary>자동 세션 전용 설정 폴더에 로그인시키는 창을 띄운다.</summary>
static class ClaudeLogin
{
    /// PowerShell 로 여는 이유는 cmd 의 set 문법을 안내했다가 PowerShell 에 붙여넣어
    /// 아무 일도 일어나지 않는 일이 실제로 있었기 때문이다. 사람이 칠 것을 남기지 않는다.
    public static void Open()
    {
        Directory.CreateDirectory(Paths.ClaudeConfig);
        var command =
            $"$env:CLAUDE_CONFIG_DIR='{Paths.ClaudeConfig}'; " +
            "Write-Host 'git gardener 전용 로그인 - 이 창에서 /login 을 입력하세요' -ForegroundColor Cyan; " +
            "claude";
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoExit", "-NoProfile", "-Command", command },
            UseShellExecute = true,
        });
    }
}

/// <summary>이전 설치본을 치운다. 남겨두면 그쪽을 실행했을 때 옛 버전이 도는 것처럼 보인다.</summary>
static class OldInstall
{
    /// 예전 기본 위치. 여기에 두던 시절의 복사본이 남아 있을 수 있다.
    static string LegacyExe =>
        Path.Combine(Paths.Local, "bin", "GitGardener.exe");

    /// <summary>파일을 잠그고 있는 인스턴스를 끊는다. 실행 중이면 덮어쓰지도 지우지도 못한다.</summary>
    public static void StopRunning()
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("GitGardener"))
        {
            using (p)
            {
                if (p.Id == self) continue;
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 이미 끝났거나 손댈 수 없다. 지우기를 시도해보면 어차피 드러난다.
                }
            }
        }
    }

    /// <returns>지금 지우지 못해 다음 실행으로 미룬 경로. 없으면 빈 문자열.</returns>
    public static string RemoveExcept(string keep, params string[] candidates)
    {
        var pending = "";
        foreach (var path in candidates.Append(LegacyExe).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (path.Length == 0) continue;
            if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(path)) continue;

            try
            {
                File.Delete(path);
                Log.Write($"이전 설치본을 지웠습니다: {path}");
                RemoveEmptyFolder(Path.GetDirectoryName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 대개 지금 이 프로세스가 그 파일이다. 다음 실행 때 치운다.
                pending = path;
                Log.Write($"지금은 지우지 못해 다음 실행으로 미룹니다: {path}");
            }
        }
        return pending;
    }

    /// 데이터 폴더는 건드리지 않는다. 실행 파일만 있던 빈 폴더만 치운다.
    static void RemoveEmptyFolder(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
            Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 남아도 해가 없다.
        }
    }
}

/// <summary>필요한 도구 하나. 실제로 명령을 돌려 확인하고, 안 되면 무엇을 치면 되는지 알려준다.</summary>
sealed record Requirement(string Name, string Fix, Func<CancellationToken, Task<string?>> Check);

/// <summary>
/// 처음 실행했을 때 뜨는 설치 마법사. 고지 → 검사 → 설치 순으로 간다.
///
/// 빠진 도구를 대신 설치하지는 않는다. 내 PC에 무엇이 깔릴지는 사용자가 정할 몫이라,
/// 무엇이 없는지와 어떤 명령을 치면 되는지까지만 보여주고 다시 검사한다.
/// </summary>
sealed class SetupForm : Form
{
    const string DefaultInstallDir = @"C:\GitGardener";
    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    const string Notice = """
        git gardener 는 방치된 저장소를 골라 이슈를 열고, 고치고, PR 까지 올리는 도구입니다.
        설치하기 전에 아래를 확인하세요.


        [1] 내 GitHub 계정으로 글을 씁니다

        정해진 시각마다 대상 저장소에 이슈와 Pull Request 를 만듭니다. 커밋 작성자는 본인입니다.
        기본 브랜치에 직접 쓰는 경로는 없고, 머지는 사람이 합니다.


        [2] 요금이 드는 Claude Code 계정이 필요합니다

        개선을 만드는 주체는 Claude Code CLI 입니다. 유료 플랜이나 API 크레딧이 있어야 하며,
        실행할 때마다 사용량을 씁니다. 하루에 몇 건을 돌릴지는 설치 후 설정에서 정합니다.


        [3] 만들어진 코드는 검증되지 않았습니다

        자동 세션에는 셸이 없어 빌드도 테스트도 돌리지 못합니다. 깨진 코드가 PR 로 올라올 수
        있다는 전제로 리뷰하세요. 승인이 유일한 관문입니다.


        [4] 보증하지 않습니다 (MIT)

        이 소프트웨어는 있는 그대로 제공되며, 사용으로 생긴 어떤 손해에 대해서도 책임지지
        않습니다. 중요한 저장소는 대상에서 빼두는 편이 안전합니다.
        """;

    readonly CancellationTokenSource _cts = new();
    readonly Panel _body = new() { Dock = DockStyle.Fill };
    readonly Button _back = new() { Text = "이전", AutoSize = true, Enabled = false };
    readonly Button _next = new() { Text = "다음", AutoSize = true, Enabled = false };
    readonly Label _step = new() { AutoSize = true, Padding = new Padding(12, 6, 0, 0) };

    readonly CheckBox _agree = new() { Text = "위 내용을 읽었고 동의합니다", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    readonly ListView _checks = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
    };
    readonly Button _recheck = new() { Text = "다시 검사", AutoSize = true };
    readonly Button _copyFix = new() { Text = "해결 명령 복사", AutoSize = true, Enabled = false };
    readonly Button _login = new() { Text = "claude 로그인 창 열기", AutoSize = true };
    readonly TextBox _fixHint = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Height = 70,
        BackColor = Color.White, Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    readonly TextBox _dir = new() { Text = DefaultInstallDir, Width = 330 };
    readonly CheckBox _desktop = new() { Text = "바탕화면에 바로가기 만들기", Checked = true, AutoSize = true };
    readonly CheckBox _startup = new() { Text = "로그온할 때 자동 실행", Checked = true, AutoSize = true };
    readonly Label _result = new() { Dock = DockStyle.Fill, AutoSize = false, Height = 80 };

    readonly Requirement[] _requirements;
    int _page;
    bool _busy;

    public string? InstalledExe { get; private set; }

    public SetupForm()
    {
        Text = "git gardener 설치";
        Icon = Program.AppIcon;
        MinimumSize = new Size(780, 640);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        _requirements = BuildRequirements();

        _agree.CheckedChanged += (_, _) => _next.Enabled = _agree.Checked;
        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += (_, _) => Advance();
        _recheck.Click += (_, _) => RunChecks();
        _copyFix.Click += (_, _) => CopyFix();
        _login.Click += (_, _) =>
        {
            ClaudeLogin.Open();
            _fixHint.Text = "열린 창에서 /login 을 마친 뒤 [다시 검사] 를 누르세요.";
        };
        _checks.SelectedIndexChanged += (_, _) => ShowFix();

        _checks.Columns.Add("항목", 190);
        _checks.Columns.Add("상태", 70);
        _checks.Columns.Add("내용", 460);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_back, _next, _step]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_body, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        ShowPage(0);
    }

    static Requirement[] BuildRequirements() =>
    [
        new("git", "winget install Git.Git",
            async ct => await Works("git", ct) ? null : "설치되어 있지 않습니다."),

        new("git 커밋 작성자",
            "git config --global user.name \"이름\"\ngit config --global user.email \"메일\"",
            async ct =>
            {
                foreach (var key in new[] { "user.name", "user.email" })
                {
                    var r = await Proc.RunAsync("git", ["config", "--global", key], Paths.Roaming, CheckTimeout, ct);
                    if (r.Stdout.Trim().Length == 0) return $"{key} 이(가) 비어 있습니다.";
                }
                return null;
            }),

        new("gh", "winget install --id GitHub.cli -e",
            async ct => await Works("gh", ct) ? null : "설치되어 있지 않습니다."),

        new("gh 로그인", "gh auth login --scopes \"repo,workflow,read:org\"", async ct =>
        {
            var r = await Proc.RunAsync("gh", ["auth", "status"], Paths.Roaming, CheckTimeout, ct);
            return r.Ok ? null : "로그인되어 있지 않습니다.";
        }),

        new("git 자격증명 헬퍼", "gh auth setup-git --hostname github.com", async ct =>
        {
            var r = await Proc.RunAsync("git",
                ["config", "--global", "--get-regexp", @"^credential\..*github\.com.*helper$"],
                Paths.Roaming, CheckTimeout, ct);
            return r.Stdout.Trim().Length > 0 ? null : "없으면 자동 push 가 인증창에서 멈춥니다.";
        }),

        new("claude", "npm i -g @anthropic-ai/claude-code", ct =>
        {
            try
            {
                Proc.ResolveClaude("");
                return Task.FromResult<string?>(null);
            }
            catch (FileNotFoundException ex)
            {
                return Task.FromResult<string?>(ex.Message);
            }
        }),

        new("claude 로그인 (자동 세션 전용)",
            "아래 [claude 로그인 창 열기] 를 누르세요." + "\n\n직접 하려면 PowerShell 에서:\n" +
            $"  $env:CLAUDE_CONFIG_DIR = '{Paths.ClaudeConfig}'\n  claude", async ct =>
            {
                if (!File.Exists(Path.Combine(Paths.ClaudeConfig, ".credentials.json")))
                    return "전용 설정 폴더에 아직 로그인하지 않았습니다.";
                try
                {
                    await new Runner(new Config()).ClaudeVersionAsync(ct);
                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ex.Message;
                }
            }),
    ];

    static async Task<bool> Works(string exe, CancellationToken ct)
    {
        try
        {
            var r = await Proc.RunAsync(exe, ["--version"], Paths.Roaming, CheckTimeout, ct);
            return r.Ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    void Advance()
    {
        if (_page < 2)
        {
            ShowPage(_page + 1);
            return;
        }
        DoInstall();
    }

    void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        _body.Controls.Clear();
        _back.Enabled = _page > 0;
        _step.Text = $"{_page + 1} / 3";

        switch (_page)
        {
            case 0:
                _body.Controls.Add(NoticePage());
                _next.Text = "다음";
                _next.Enabled = _agree.Checked;
                break;
            case 1:
                _body.Controls.Add(CheckPage());
                _next.Text = "다음";
                _next.Enabled = false;
                RunChecks();
                break;
            default:
                _body.Controls.Add(InstallPage());
                _next.Text = "설치";
                _next.Enabled = true;
                break;
        }
    }

    Control NoticePage()
    {
        var text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            // WinForms 텍스트 상자는 개행 문자만으로는 줄을 바꾸지 않는다.
            // 원시 문자열을 그대로 넣으면 한 덩어리로 붙어 보인다.
            Text = Notice.ReplaceLineEndings(), BackColor = Color.White, Font = new Font("Segoe UI", 10f),
            TabStop = false,
        };
        text.GotFocus += (_, _) => text.Select(0, 0);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(text, 0, 0);
        panel.Controls.Add(_agree, 0, 1);
        return panel;
    }

    Control CheckPage()
    {
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_recheck, _copyFix, _login]);

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 6),
            Text = "빠진 것을 대신 설치하지는 않습니다. 항목을 눌러 명령을 복사하고, 터미널에서 실행한 뒤 다시 검사하세요.",
        }, 0, 0);
        panel.Controls.Add(_checks, 0, 1);
        panel.Controls.Add(_fixHint, 0, 2);
        panel.Controls.Add(buttons, 0, 3);
        return panel;
    }

    Control InstallPage()
    {
        var browse = new Button { Text = "찾아보기", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _dir.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = dlg.SelectedPath;
        };

        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        row.Controls.AddRange([_dir, browse]);

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        for (var i = 0; i < 5; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.Controls.Add(new Label { Text = "설치 위치", AutoSize = true, Padding = new Padding(0, 6, 0, 4) }, 0, 0);
        panel.Controls.Add(row, 0, 1);
        panel.Controls.Add(_desktop, 0, 2);
        panel.Controls.Add(_startup, 0, 3);
        panel.Controls.Add(new Label
        {
            AutoSize = true, Padding = new Padding(0, 14, 0, 0),
            Text = "설정과 로그, 작업 사본은 실행 파일 위치와 무관하게 사용자 폴더에 남습니다.",
        }, 0, 4);
        panel.Controls.Add(_result, 0, 5);
        return panel;
    }

    async void RunChecks()
    {
        if (_busy) return;
        SetBusy(true);
        _checks.Items.Clear();

        var allOk = true;
        foreach (var req in _requirements)
        {
            string? problem;
            try
            {
                problem = await Task.Run(() => req.Check(_cts.Token), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            _checks.Items.Add(new ListViewItem([req.Name, problem is null ? "확인" : "필요", problem ?? "준비됨"])
            {
                Tag = req,
                ForeColor = problem is null ? Color.FromArgb(0x1a, 0x7f, 0x37) : Color.FromArgb(0xa4, 0x00, 0x00),
            });
            if (problem is not null) allOk = false;
        }

        _next.Enabled = allOk;
        _fixHint.Text = allOk
            ? "전부 준비됐습니다. 다음으로 넘어가세요."
            : "빨간 항목을 눌러 해결 명령을 확인하세요.";
        SetBusy(false);
    }

    void ShowFix()
    {
        var req = _checks.SelectedItems.Count > 0 ? _checks.SelectedItems[0].Tag as Requirement : null;
        _copyFix.Enabled = req is not null;
        if (req is not null) _fixHint.Text = req.Fix.ReplaceLineEndings();
    }

    void CopyFix()
    {
        if (_checks.SelectedItems.Count == 0) return;
        if (_checks.SelectedItems[0].Tag is not Requirement req) return;
        Clipboard.SetText(req.Fix);
        _fixHint.Text = $"복사했습니다. 터미널에 붙여넣고 실행하세요.\r\n\r\n{req.Fix}";
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _recheck.Enabled = !busy;
        _back.Enabled = !busy && _page > 0;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    /// <summary>실행 파일을 자기 자리로 옮기고 바로가기를 만든다.</summary>
    void DoInstall()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var source = Environment.ProcessPath
                ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
            var target = Path.Combine(_dir.Text.Trim(), "GitGardener.exe");
            var previous = Config.Load().InstallPath;

            // 실행 중이면 덮어쓰지도 지우지도 못한다. 뮤텍스 때문에 옛 인스턴스가 남아 있으면
            // 새로 띄운 쪽이 그 창만 보여주고 끝나서, 옛 버전이 도는 것처럼 보인다.
            OldInstall.StopRunning();

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, overwrite: true);

            Shortcuts.RemoveLegacyRunEntry();
            if (_startup.Checked) Shortcuts.Write(Shortcuts.StartupLink, target, Program.TrayArg);
            else File.Delete(Shortcuts.StartupLink);

            if (_desktop.Checked) Shortcuts.Write(Shortcuts.DesktopLink, target, "");
            else File.Delete(Shortcuts.DesktopLink);

            var cfg = Config.Load();
            cfg.InstallPath = target;
            cfg.PendingCleanup = OldInstall.RemoveExcept(target, previous, source);
            cfg.Save();

            InstalledExe = target;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _result.Text = $"설치하지 못했습니다: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
