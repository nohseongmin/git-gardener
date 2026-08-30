using System.Text.RegularExpressions;

namespace GitGardener;

/// <summary>
/// 한 줄짜리 아이디어를 저장소로 만든다. 기획서를 쓰고, 비공개 저장소를 만들어 첫 커밋을 올리고,
/// 기획서의 남은 작업을 이슈로 옮긴다. 그다음은 평소의 일과가 그 이슈를 하나씩 처리한다.
/// </summary>
sealed partial class Runner
{
    /// 기획서에서 이슈로 옮길 작업의 최대 개수. 더 열어봤자 며칠씩 밀린 채로 쌓인다.
    const int MaxSeedIssues = 8;

    /// 깃허브가 받아주는 길이는 100자지만, 그보다 길면 이름이 아니라 문장이다.
    const int MaxRepoNameLength = 40;

    const string DefaultBranch = "main";
    const string PlanFile = "docs/PLAN.md";

    const string NameMarker = "NAME:";
    const string DescMarker = "DESC:";

    /// 저장소 이름에 못 쓰는 글자.
    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonRepoName { get; }

    /// 기획서의 남은 작업 줄. 예: "- [ ] 스와이프 화면"
    [GeneratedRegex(@"^[ \t]*[-*][ \t]*\[ \][ \t]*(.+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Todo { get; }

    /// <returns>만들어진 저장소의 URL.</returns>
    public async Task<string> CreateFromIdeaAsync(string idea, CancellationToken ct)
    {
        if (cfg.GithubUser.Length == 0) cfg.GithubUser = await CurrentUserAsync(ct);

        var rules = await LoadRulesAsync(ct);

        // 이름은 기획을 마쳐야 정해진다. 임시 폴더에서 쓰고 나중에 옮긴다.
        var dir = Path.Combine(Paths.ReposDir, $"_new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            Log.Write($"기획 시작 (최대 {ClaudeTimeout.TotalMinutes:0}분) — {idea}");
            var (name, description) = ParsePlan(await PlanAsync(dir, rules, idea, ct));

            await GitAsync(dir, ["init", "-b", DefaultBranch], ct);
            await GitAsync(dir, ["add", "-A"], ct);
            await GitAsync(dir, ["commit", "-m", $"Docs: {name} 기획 초안"], ct);

            var created = await Proc.RunAsync("gh",
                ["repo", "create", name,
                 "--private", "--source", ".", "--push", "--description", description],
                dir, GhTimeout, ct);
            if (!created.Ok) throw new InvalidOperationException($"gh repo create 실패: {created.Message}");

            var repo = new GhRepo { Name = name, NameWithOwner = $"{cfg.GithubUser}/{name}" };
            Log.Write($"[{name}] 저장소 생성 — {description}");

            await SeedIssuesAsync(repo, dir, ct);
            Enable(name);

            Move(dir, Path.Combine(Paths.ReposDir, name));
            return created.Stdout.Trim();
        }
        catch
        {
            // 저장소를 만들기 전에 엎어지면 작업 폴더만 남는다. 이름도 없는 폴더라 다시 못 쓴다.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            throw;
        }
    }

    async Task<string> PlanAsync(string dir, string rules, string idea, CancellationToken ct)
    {
        var launcher = Proc.ResolveClaude(cfg.ClaudePath);
        List<string> args = [
            .. launcher.Prefix,
            "-p", IdeaPrompt(idea),
            "--output-format", "json",
            "--permission-mode", "acceptEdits",
            "--allowedTools", AllowedTools,
            "--disallowedTools", DisallowedTools,
            "--append-system-prompt", rules,
            "--model", cfg.Model,
        ];

        var result = await Proc.RunAsync(launcher.Exe, args, dir, ClaudeTimeout, ct, ClaudeEnv());

        var (text, failed) = ParseResult(result.Stdout);
        if (!result.Ok || failed)
        {
            var reason = text.Length > 0 ? text : result.Message;
            throw new InvalidOperationException(
                $"claude 실행 실패 (exit {result.ExitCode}): {reason}{LoginHint()}");
        }
        return text;
    }

    /// <summary>기획서의 남은 작업을 이슈로 옮긴다. 이게 다음 일과의 작업 목록이 된다.</summary>
    async Task SeedIssuesAsync(GhRepo repo, string dir, CancellationToken ct)
    {
        var plan = Path.Combine(dir, PlanFile);
        if (!File.Exists(plan))
        {
            Log.Write($"[{repo.Name}] {PlanFile}이 없어 이슈를 만들지 않습니다.");
            return;
        }

        var tasks = Todo.Matches(await File.ReadAllTextAsync(plan, ct))
            .Select(m => m.Groups[1].Value)
            .Take(MaxSeedIssues)
            .ToList();

        if (tasks.Count == 0)
        {
            Log.Write($"[{repo.Name}] 기획서에서 할 일을 찾지 못해 이슈를 만들지 않습니다.");
            return;
        }

        foreach (var task in tasks)
        {
            var number = await CreateIssueAsync(repo, task, $"기획서의 MVP 작업 하나입니다. 배경은 `{PlanFile}`에 있습니다.", ct);
            Log.Write($"[{repo.Name}] 이슈 #{number} — {task}");
        }
    }

    /// 만든 저장소를 대상에 넣는다. 안 넣으면 기껏 만든 이슈를 아무도 안 가져간다.
    void Enable(string name)
    {
        if (cfg.EnabledRepos.Contains(name)) return;
        cfg.EnabledRepos.Add(name);
        cfg.Save();
    }

    /// 같은 이름의 낡은 작업 사본이 있으면 버린다. 언제든 다시 클론할 수 있는 폴더다.
    static void Move(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.Move(from, to);
    }

    static (string Name, string Description) ParsePlan(string summary)
    {
        string name = "", description = "";
        foreach (var raw in summary.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(NameMarker, StringComparison.Ordinal))
                name = line[NameMarker.Length..].Trim();
            else if (line.StartsWith(DescMarker, StringComparison.Ordinal))
                description = line[DescMarker.Length..].Trim();
        }

        name = NonRepoName.Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (name.Length is 0 or > MaxRepoNameLength)
            throw new InvalidOperationException(
                $"쓸 수 있는 저장소 이름을 받지 못했습니다: {(name.Length > 0 ? name : "(없음)")}");

        // 설명이 비어도 저장소는 만들 수 있다. 막을 이유가 없다.
        return (name, description);
    }

    string IdeaPrompt(string idea) => $"""
        아래 한 줄짜리 아이디어를 기획서로 옮기고, 새 저장소의 첫 커밋에 들어갈 파일을 써라.

        ----- 아이디어 -----
        {idea}
        ----- 끝 -----

        쓸 파일
        - README.md — 무엇을 만드는 것인지 15줄 이내. 아직 코드가 없다는 것도 적는다.
        - {PlanFile} — 기획서. 아래 목차를 순서대로 쓴다.
        - .gitignore — 아래에서 고른 스택에 맞는 것
        - LICENSE — MIT. 저작권자는 "{cfg.GithubUser}", 연도는 {DateTime.Now.Year}

        기획서 목차
        ## 문제
        ## 쓸 사람
        ## 이미 있는 것과 무엇이 다른가
        ## 수익 모델
        ## 기술 스택
        ## 보안에서 놓치면 안 되는 것
        ## MVP 범위
        ## 안 할 것
        ## MVP 작업

        "## MVP 작업"에는 "- [ ] 한 줄 작업" 형식의 체크박스만 5~8줄 쓴다.
        한 줄이 하루치 분량이어야 하고, 위에서부터 순서대로 하면 돌아가는 것이 나와야 한다.
        각 줄은 그대로 이슈 제목이 되므로 그 줄만 읽고 무엇을 만드는지 알 수 있게 써라.

        범위
        - 기획서와 위 네 파일만 쓴다. 코드는 한 줄도 쓰지 마라.
        - 스택은 이 아이디어에 가장 흔하고 지루한 것을 골라라. 새 프레임워크를 구경시키는 자리가 아니다.
        - MVP 범위는 혼자 2주 안에 끝낼 수 있는 크기로 잘라라.

        금지
        - 빌드·테스트 실행 금지. 셸 도구가 막혀 있다.
        - git 명령, 커밋, 저장소 조작 금지. 형상 관리는 호출한 앱이 한다.

        작성 규칙
        - 존댓말 평문으로 쓴다. 표와 이모지를 쓰지 마라.
        - 모르는 숫자를 지어내지 마라. 시장 규모나 사용자 수를 아는 척하지 마라.
        - 스스로를 도구나 자동화로 지칭하지 마라.

        파일을 다 쓴 뒤 마지막에 아래 두 줄을 그대로 출력하라. 표식의 철자를 바꾸지 마라.

        NAME: 영문 소문자 kebab-case 저장소 이름. 단어 2~4개, {MaxRepoNameLength}자 이내
        DESC: 저장소 설명 한 줄. 한국어 80자 이내
        """;
}
