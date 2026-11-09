using System.Text.RegularExpressions;

namespace GitGardener;

sealed partial class Runner
{
    // 실행: dotnet run --project tests/GitGardener.Regression
    static void Main()
    {
        foreach (var branch in new[] { "fix/foo..bar", "fix/foo.lock" })
        {
            var pr = ParsePr($"TITLE: Fix: 브랜치 검증 보완\nBRANCH: {branch}\nBODY:\n변경 내용입니다.");
            if (!Regex.IsMatch(pr.Branch, @"^fix/[0-9]{8}-improve$"))
                throw new InvalidOperationException($"잘못된 브랜치가 기본값으로 대체되지 않았습니다: {branch} → {pr.Branch}");
        }

        foreach (var branch in new[] { "fix/foo-bar", "fix/foo.bar", "fix/foo_bar", "fix/foo.lock-file", "fix/a" })
        {
            var pr = ParsePr($"TITLE: Fix: 브랜치 검증 보완\nBRANCH: {branch}\nBODY:\n변경 내용입니다.");
            if (pr.Branch != branch)
                throw new InvalidOperationException($"정상 브랜치가 변경됐습니다: {branch} → {pr.Branch}");
        }
    }
}
