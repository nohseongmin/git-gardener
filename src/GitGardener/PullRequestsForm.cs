using System.ComponentModel;
using System.Diagnostics;

namespace GitGardener;

/// <summary>
/// 계정 전체의 열린 PR을 모아 보고, 고른 것만 머지한다.
///
/// 자동 세션은 빌드도 테스트도 돌리지 못하므로 머지가 유일한 관문이다.
/// 그래서 "전부 머지"를 두지 않는다. 체크하는 행위 자체가 건별 판단이 되게 한다.
/// </summary>
sealed class PullRequestsForm : Form
{
    const int MergeGapMs = 400;

    readonly CancellationTokenSource _cts = new();

    readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
        DisplayMember = nameof(OpenPr.Display),
    };
    readonly Button _refresh = new() { Text = "새로고침", AutoSize = true };
    readonly Button _merge = new() { Text = "선택 머지", AutoSize = true };
    readonly Button _open = new() { Text = "브라우저에서 열기", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(10, 6, 0, 0) };

    bool _busy;

    public PullRequestsForm()
    {
        Text = "열린 PR";
        Icon = SystemIcons.Application;
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _refresh.Click += (_, _) => Load();
        _merge.Click += (_, _) => MergeChecked();
        _open.Click += (_, _) => OpenSelected();
        _list.DoubleClick += (_, _) => OpenSelected();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_refresh, _merge, _open, _status]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(
            new Label { Text = "머지할 PR을 체크하세요. 두 번 누르면 브라우저에서 열립니다.", Dock = DockStyle.Fill, AutoSize = true },
            0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Load();
    }

    new async void Load()
    {
        if (_busy) return;
        SetBusy(true, "목록을 읽는 중");
        try
        {
            var prs = await Task.Run(() => Runner.ListOpenPullRequestsAsync(_cts.Token), _cts.Token);
            _list.Items.Clear();
            foreach (var pr in prs) _list.Items.Add(pr);
            _status.Text = prs.Count == 0 ? "열린 PR이 없습니다." : $"{prs.Count}건";
        }
        catch (OperationCanceledException)
        {
            // 창을 닫는 중이다.
        }
        catch (Exception ex)
        {
            _status.Text = $"읽지 못했습니다: {ex.Message}";
            Log.Write($"PR 목록 실패: {ex.Message}");
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    async void MergeChecked()
    {
        if (_busy) return;

        var picked = _list.CheckedItems.Cast<OpenPr>().ToList();
        if (picked.Count == 0)
        {
            _status.Text = "체크한 PR이 없습니다.";
            return;
        }

        // 기본 버튼을 취소로 둔다. 실수로 Enter가 들어가도 머지가 아니라 취소가 되어야 한다.
        // 머지는 되돌리기가 번거롭고, 이 앱에서는 사람의 승인이 유일한 관문이다.
        var answer = MessageBox.Show(
            $"{picked.Count}건을 squash 머지하고 브랜치를 지웁니다.\n\n{string.Join("\n", picked.Select(p => p.Display))}",
            "머지", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;

        SetBusy(true, "머지 중");
        var merged = 0;
        var failed = 0;
        foreach (var pr in picked)
        {
            try
            {
                await Task.Run(() => Runner.MergePullRequestAsync(pr, _cts.Token), _cts.Token);
                merged++;
                Log.Write($"머지: {pr.Repo}#{pr.Number}");
                _status.Text = $"머지 {merged}건 / {picked.Count}건";
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 하나가 막혀도 나머지는 계속한다. 충돌이나 검사 실패로 못 머지하는 건이 섞이기 마련이다.
                failed++;
                Log.Write($"머지 실패 {pr.Repo}#{pr.Number}: {ex.Message}");
            }

            // 연달아 때리면 API가 거절한다.
            await Task.Delay(MergeGapMs, _cts.Token);
        }

        SetBusy(false, failed == 0 ? $"머지 {merged}건 완료" : $"머지 {merged}건, 실패 {failed}건 (로그 확인)");
        Load();
    }

    void OpenSelected()
    {
        if (_list.SelectedItem is not OpenPr pr) return;
        try
        {
            Process.Start(new ProcessStartInfo(pr.Url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Write($"브라우저를 열지 못했습니다. 직접 여세요: {pr.Url}  ({ex.Message})");
        }
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _status.Text = status;
        foreach (var b in new[] { _refresh, _merge, _open }) b.Enabled = !busy;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
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
