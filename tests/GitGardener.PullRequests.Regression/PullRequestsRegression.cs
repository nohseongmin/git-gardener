using System.Diagnostics;
using System.Reflection;

namespace GitGardener;

static class Program
{
    public static Icon AppIcon => SystemIcons.Application;

    [STAThread]
    static void Main()
    {
        // 실제 폼을 사용하되 승인과 GitHub 응답만 대체해 외부 PR을 건드리지 않는다.
        using var form = new PullRequestsForm();
        // 비동기 작업이 UI 스레드로 돌아올 수 있도록 숨겨진 창 핸들을 만든다.
        _ = form.Handle;
        var list = Field<CheckedListBox>(form, "_list");
        var status = Field<Label>(form, "_status");
        var mergeable = new OpenPr("owner/repo", 1, "머지 가능", "", false);
        var conflicting = new OpenPr("owner/repo", 2, "충돌", "", true);
        Runner.Open.AddRange([mergeable, conflicting]);
        list.Items.Add(mergeable, true);
        list.Items.Add(conflicting, true);

        CallAndWait(form, "MergeChecked");

        const string expected = "머지 1건, 실패 1건 (로그 확인)";
        if (MessageBox.Calls != 1 || !Runner.Attempted.SequenceEqual([mergeable, conflicting]))
            throw new InvalidOperationException("선택한 PR 두 건이 승인 후 각각 처리되어야 합니다.");
        if (Runner.Loads != 1 || list.Items.Count != 1 || !ReferenceEquals(list.Items[0], conflicting) || status.Text != "1건")
            throw new InvalidOperationException("머지 후 목록이 새로고침되어 실패한 PR만 남아야 합니다.");
        if (!HasText(form, expected))
            throw new InvalidOperationException("목록 새로고침 후 '머지 1건, 실패 1건' 결과가 유지되어야 합니다.");

        CallAndWait(form, "Load");
        if (Runner.Loads != 2 || !HasText(form, expected))
            throw new InvalidOperationException("다시 새로고침해도 머지 결과가 유지되어야 합니다.");

        Runner.FailLoad = true;
        CallAndWait(form, "Load");
        if (status.Text != "읽지 못했습니다: 목록 조회 실패" || !HasText(form, expected))
            throw new InvalidOperationException("목록 조회 실패 시 조회 오류와 이전 머지 결과를 함께 표시해야 합니다.");
    }

    static T Field<T>(PullRequestsForm form, string name) =>
        (T)(typeof(PullRequestsForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new InvalidOperationException($"필드를 찾지 못했습니다: {name}"));

    static void CallAndWait(PullRequestsForm form, string method)
    {
        var action = typeof(PullRequestsForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"메서드를 찾지 못했습니다: {method}");
        action.Invoke(form, null);
        var elapsed = Stopwatch.StartNew();
        while (Field<bool>(form, "_busy"))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException($"{method} 작업이 완료되지 않았습니다.");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    static bool HasText(Control control, string text)
    {
        if (control.Text == text) return true;
        foreach (Control child in control.Controls)
            if (HasText(child, text)) return true;
        return false;
    }
}

sealed record OpenPr(string Repo, int Number, string Title, string Url, bool Conflicting)
{
    public string Display => $"{Repo} #{Number}  {Title}";
}

static class Runner
{
    public static readonly List<OpenPr> Open = [];
    public static readonly List<OpenPr> Attempted = [];
    public static int Loads;
    public static bool FailLoad;

    public static Task MergePullRequestAsync(OpenPr pr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attempted.Add(pr);
        if (pr.Conflicting) return Task.FromException(new InvalidOperationException("충돌로 머지할 수 없습니다."));
        Open.Remove(pr);
        return Task.CompletedTask;
    }

    public static Task<IReadOnlyList<OpenPr>> ListOpenPullRequestsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Loads++;
        return FailLoad
            ? Task.FromException<IReadOnlyList<OpenPr>>(new InvalidOperationException("목록 조회 실패"))
            : Task.FromResult<IReadOnlyList<OpenPr>>(Open.ToList());
    }
}

static class MessageBox
{
    public static int Calls;

    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons,
        MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
    {
        Calls++;
        return DialogResult.OK;
    }
}

static class Log
{
    public static readonly List<string> Messages = [];
    public static void Write(string message) => Messages.Add(message);
}
