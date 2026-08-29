using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GitGardener;

sealed class GhBranch
{
    public string Name { get; set; } = "";
}

sealed class GhRepo
{
    public string Name { get; set; } = "";
    public string NameWithOwner { get; set; } = "";
    public bool IsArchived { get; set; }
    public bool IsFork { get; set; }
    public DateTime UpdatedAt { get; set; }
    public GhBranch? DefaultBranchRef { get; set; }

    /// CheckedListBox가 DisplayMember로 읽는다.
    [JsonIgnore]
    public string Display => IsArchived ? $"{Name}  (archived)" : IsFork ? $"{Name}  (fork)" : Name;
}

sealed class GhIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

sealed class GhPrRef
{
    public string HeadRefName { get; set; } = "";
}

/// 계정 전체에서 모아 본 열린 PR 하나.
sealed class OpenPr(string repo, int number, string title, string url, bool conflicting)
{
    public string Repo { get; } = repo;
    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Url { get; } = url;

    /// 기본 브랜치와 충돌해 이대로는 머지되지 않는다.
    public bool Conflicting { get; } = conflicting;

    /// CheckedListBox가 DisplayMember로 읽는다.
    /// 충돌은 목록에서 바로 보여야 한다. 안 그러면 머지가 조용히 실패하고 줄만 계속 남는다.
    public string Display => $"{Repo.Split('/')[^1]} #{Number}  {Title}{(Conflicting ? "   [충돌]" : "")}";
}

/// <summary>
/// 파이프라인 본체. 레포 1개당 이슈 선택 → 동기화 → 브랜치 → claude → 변경 검사 → 커밋 → push → PR.
/// 실패해도 브랜치는 남긴다(원인 추적용). 변경이 없으면 브랜치를 지우고 PR을 만들지 않는다.
/// </summary>
sealed partial class Runner(Config cfg)
{
    const string AllowedTools = "Read,Edit,Write,Glob,Grep";
    const string DisallowedTools = "Bash";
    const string RulesFile = "RULES.md";
    const int MaxRepoListSize = 200;
    const int RulesCacheHours = 24;

    /// claude 응답에서 PR 정보를 떼어낼 표식.
    const string TitleMarker = "TITLE:";
    const string BranchMarker = "BRANCH:";
    const string IssueMarker = "ISSUE:";
    const string BodyMarker = "BODY:";

    const string FallbackTitle = "Chore: 저장소 정리";

    /// 브랜치 이름이 겹칠 때 붙일 접미사의 최대 시도 횟수.
    const int MaxBranchSuffix = 20;

    /// 대상 레포에 PR 템플릿이 없을 때 쓸 기본 양식.
    const string DefaultPrTemplate = """
        ## 📌 Summary

        > - #

        ## 📚 Tasks

        -

        ## 👀 To Reviewer
        """;

    /// gh가 PR 템플릿을 찾는 위치. 위에서부터 먼저 있는 것을 쓴다.
    static readonly string[] PrTemplatePaths =
    [
        ".github/pull_request_template.md",
        ".github/PULL_REQUEST_TEMPLATE.md",
        "docs/pull_request_template.md",
        "pull_request_template.md",
    ];

    static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan GhTimeout = TimeSpan.FromMinutes(3);
    static readonly TimeSpan ClaudeTimeout = TimeSpan.FromMinutes(20);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// 개선 유형 → 프롬프트에 넣을 초점 한 줄.
    static readonly Dictionary<string, string> Focus = new()
    {
        ["auto"] = "무엇을 고칠지는 저장소 상태를 보고 스스로 판단하라.",
        ["docs"] = "문서·주석·명명만 손대라. 동작을 바꾸는 변경은 금지한다.",
        ["refactor"] = "동작을 그대로 둔 채 가독성이나 중복만 개선하라.",
        ["bugfix"] = "명백한 버그 하나만 고쳐라. 확실하지 않으면 손대지 말고 이유를 답하라.",
        ["tests"] = "기존 테스트 관행을 따라 빠진 테스트를 추가하라. 테스트 인프라가 없으면 손대지 말고 그렇게 답하라.",
    };

    /// 팀 컨벤션의 제목 형식. 예: "Fix: Safari input 포커스 시 자동 확대 방지"
    [GeneratedRegex(@"^(Build|Chore|Ci|Deploy|Docs|Feat|Fix|Perf|Refactor|Style|Test)(\([^)]+\))?: .{2,80}$")]
    private static partial Regex TitleFormat { get; }

    /// 브랜치 형식. 예: "fix/input-focus-auto-zoom"
    [GeneratedRegex(@"^[a-z]+/[a-z0-9]([a-z0-9._-]*[a-z0-9])?$")]
    private static partial Regex BranchFormat { get; }

    /// 템플릿에 있는 빈 이슈 참조 줄. 예: "> - #"
    [GeneratedRegex(@"^\s*(>\s*)?-\s*#\s*$")]
    private static partial Regex EmptyIssueRef { get; }

    /// <summary>
    /// claude 자식 프로세스에 줄 환경변수.
    ///
    /// 사용자의 ~/.claude 를 그대로 쓰면 자동 세션이 OAuth 토큰을 갱신하면서 자격증명 파일을
    /// 덮어쓰고, 옛 토큰을 들고 있던 대화형 Claude Code 가 401 을 맞는다. 저장소를 나눠 갖는다.
    /// </summary>
    IReadOnlyDictionary<string, string>? ClaudeEnv()
    {
        if (!cfg.SeparateClaudeConfig) return null;
        Directory.CreateDirectory(Paths.ClaudeConfig);
        return new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = Paths.ClaudeConfig };
    }

    /// 제목, 본문. 트레이 풍선으로 띄운다.
    public event Action<string, string>? Notify;

    public async Task<IReadOnlyList<GhRepo>> ListReposAsync(CancellationToken ct)
    {
        if (cfg.GithubUser.Length == 0) cfg.GithubUser = await CurrentUserAsync(ct);

        var result = await Proc.RunAsync("gh",
            ["repo", "list", cfg.GithubUser,
             "--limit", MaxRepoListSize.ToString(),
             "--json", "name,nameWithOwner,isArchived,isFork,updatedAt,defaultBranchRef"],
            Paths.Roaming, GhTimeout, ct);

        if (!result.Ok) throw new InvalidOperationException($"gh repo list 실패: {result.Message}");
        return JsonSerializer.Deserialize<List<GhRepo>>(result.Stdout, JsonOpts) ?? [];
    }

    /// <returns>생성한 PR 개수.</returns>
    public async Task<int> RunAsync(bool dryRun, CancellationToken ct)
    {
        var repos = await ListReposAsync(ct);
        var enabled = repos.Where(r => cfg.EnabledRepos.Contains(r.Name)).ToList();

        // 0건을 정상 종료로 처리하면 그날이 "완료"로 찍혀 다음 날까지 안 돈다.
        // 부팅 직후 목록 로드가 실패한 날이 통째로 비는 걸 막으려고 실패로 올린다.
        if (enabled.Count == 0)
            throw new InvalidOperationException(
                $"처리할 대상 레포가 없습니다. 설정에 {cfg.EnabledRepos.Count}개가 선택되어 있고 계정에서 {repos.Count}개를 읽었습니다.");

        // 아직 열린 PR이 있는 레포는 건너뛴다. 그 수정이 기본 브랜치에 안 들어간 상태라
        // 다시 돌리면 같은 자리를 또 고쳐 이슈와 PR이 겹치고, 먼저 머지된 쪽 때문에
        // 나중 PR이 충돌로 막혀 목록에 영영 남는다.
        var pending = (await ListOpenPullRequestsAsync(ct))
            .Select(p => p.Repo)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var waiting = enabled.Where(r => pending.Contains(r.NameWithOwner)).Select(r => r.Name).ToList();
        if (waiting.Count > 0)
            Log.Write($"열린 PR이 남아 있어 {waiting.Count}개를 건너뜁니다: {string.Join(", ", waiting)}");

        // 가장 오래 안 건드린 레포부터. 하루 처리량만큼만.
        var targets = enabled
            .Where(r => !pending.Contains(r.NameWithOwner))
            .OrderBy(r => r.UpdatedAt)
            .Take(DailyQuota())
            .ToList();

        if (targets.Count == 0)
        {
            Log.Write("고른 레포가 전부 머지를 기다리는 중입니다. 오늘은 손댈 곳이 없습니다.");
            return 0;
        }

        var rules = await LoadRulesAsync(ct);
        var created = 0;

        foreach (var repo in targets)
        {
            try
            {
                var prUrl = await ProcessAsync(repo, rules, dryRun, ct);
                if (prUrl is null) continue;
                created++;
                Log.Write($"[{repo.Name}] PR 생성: {prUrl}");
                Notify?.Invoke($"{repo.Name} — PR 생성", prUrl);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 한 레포가 실패해도 나머지는 계속 돈다. 대신 조용히 넘기지 않는다.
                Log.Write($"[{repo.Name}] 실패: {ex.Message}");
                Notify?.Invoke($"{repo.Name} — 실패", ex.Message);
            }
        }
        return created;
    }

    /// <summary>
    /// 그날 손볼 레포 수. 매일 같은 양을 처리하면 실제 작업 리듬과 동떨어져서 범위 안에서 새로 뽑는다.
    /// 실제로 몇 건이 나오는지는 별개다 — 고칠 게 없는 레포는 그대로 건너뛰므로 이 수보다 적을 수 있다.
    /// </summary>
    int DailyQuota()
    {
        if (!cfg.VaryDailyLoad) return cfg.ReposPerDay;

        var quota = Random.Shared.Next(1, cfg.MaxReposPerDay + 1);
        Log.Write($"오늘 처리량: {quota}개 (1~{cfg.MaxReposPerDay})");
        return quota;
    }

    /// <returns>PR URL. 변경이 없거나 dry-run이면 null.</returns>
    async Task<string?> ProcessAsync(GhRepo repo, string rules, bool dryRun, CancellationToken ct)
    {
        var baseBranch = repo.DefaultBranchRef?.Name
            ?? throw new InvalidOperationException("기본 브랜치를 알 수 없습니다. 빈 레포일 수 있습니다.");
        var dir = Path.Combine(Paths.ReposDir, repo.Name);

        var issue = cfg.IssueMode == IssueMode.None ? null : await FindIssueAsync(repo, ct);
        if (issue is null && cfg.IssueMode == IssueMode.Only)
        {
            Log.Write($"[{repo.Name}] 처리할 열린 이슈가 없어 건너뜁니다.");
            return null;
        }
        if (issue is not null) Log.Write($"[{repo.Name}] 이슈 #{issue.Number} — {issue.Title}");

        // 편집을 하려면 브랜치가 먼저 필요한데, 브랜치 이름은 무엇을 고쳤는지 알아야 정해진다.
        // 그래서 임시 이름으로 만들고 커밋 직전에 실제 이름으로 바꾼다.
        var branch = $"wip/{Guid.NewGuid():N}"[..16];

        Log.Write($"[{repo.Name}] 작업 사본 동기화");
        await SyncAsync(repo, dir, baseBranch, ct);
        await GitAsync(dir, ["checkout", "-b", branch], ct);

        var template = await PrTemplateAsync(dir, ct);

        Log.Write($"[{repo.Name}] claude 실행 (최대 {ClaudeTimeout.TotalMinutes:0}분)");
        var summary = await ImproveAsync(dir, rules, template, issue, ct);

        var status = await GitAsync(dir, ["status", "--porcelain"], ct);
        if (status.Stdout.Trim().Length == 0)
        {
            Log.Write($"[{repo.Name}] 변경 없음 → 건너뜀. 응답: {FirstLine(summary)}");
            await GitAsync(dir, ["checkout", "-f", baseBranch], ct);
            await GitAsync(dir, ["branch", "-D", branch], ct);
            return null;
        }

        await GitAsync(dir, ["add", "-A"], ct);

        var pr = ParsePr(summary);

        if (dryRun)
        {
            var stat = await GitAsync(dir, ["diff", "--cached", "--stat"], ct);
            var diff = await GitAsync(dir, ["diff", "--cached"], ct);
            Log.Write($"""
                [{repo.Name}] DRY-RUN — 이슈도 PR도 만들지 않습니다.
                제목  : {pr.Title}
                브랜치: {pr.Branch}
                이슈  : {(issue is not null ? $"#{issue.Number} (기존)" : $"새로 생성 예정 — {pr.IssueTitle}")}

                {LinkIssue(pr.Body, issue?.Number)}

                {stat.Stdout}
                {diff.Stdout}
                """);
            return null;
        }

        // 이슈가 없으면 여기서 만든다. 무엇을 고쳤는지 알아야 이슈를 제대로 쓸 수 있어서 뒤로 미뤘다.
        if (issue is null && cfg.IssueMode == IssueMode.Prefer)
        {
            var number = await CreateIssueAsync(repo, pr.IssueTitle, pr.IssueBody, ct);
            issue = new GhIssue { Number = number, Title = pr.IssueTitle };
            Log.Write($"[{repo.Name}] 이슈 #{number} 생성 — {pr.IssueTitle}");
        }

        var named = issue is null ? pr.Branch : $"{pr.Branch}/#{issue.Number}";
        branch = await UniqueBranchAsync(dir, named, ct);
        await GitAsync(dir, ["branch", "-m", branch], ct);
        Log.Write($"[{repo.Name}] {branch} — {pr.Title}");

        await GitAsync(dir, ["commit", "-m", pr.Title], ct);
        await GitAsync(dir, ["push", "-u", "origin", branch], ct);

        var body = LinkIssue(pr.Body, issue?.Number);
        return await CreatePullRequestAsync(repo, dir, baseBranch, branch, pr.Title, body, ct);
    }

    /// <summary>
    /// 계정이 소유한 모든 저장소의 열린 PR을 한 번에 모은다.
    /// 검색 인덱스는 갱신이 늦어 방금 만든 PR이 빠지므로 GraphQL로 직접 훑는다.
    /// 저장소는 한 번에 100개까지만 오므로 커서를 따라 끝까지 읽는다.
    /// </summary>
    public static async Task<IReadOnlyList<OpenPr>> ListOpenPullRequestsAsync(CancellationToken ct)
    {
        var found = new List<OpenPr>();
        string? cursor = null;

        do
        {
            var query =
                "{ viewer { repositories(first: 100, ownerAffiliations: OWNER, after: " + Cursor(cursor) + ") { " +
                "pageInfo { hasNextPage endCursor } nodes { nameWithOwner " +
                "pullRequests(states: OPEN, first: 30) { nodes { number title url mergeable } } } } } }";

            var result = await Proc.RunAsync("gh", ["api", "graphql", "-f", $"query={query}"],
                Paths.Roaming, GhTimeout, ct);
            if (!result.Ok) throw new InvalidOperationException($"PR 목록을 읽지 못했습니다: {result.Message}");

            using var doc = JsonDocument.Parse(result.Stdout);
            var repositories = doc.RootElement
                .GetProperty("data").GetProperty("viewer").GetProperty("repositories");

            foreach (var repo in repositories.GetProperty("nodes").EnumerateArray())
            {
                var name = repo.GetProperty("nameWithOwner").GetString() ?? "";
                foreach (var pr in repo.GetProperty("pullRequests").GetProperty("nodes").EnumerateArray())
                {
                    found.Add(new OpenPr(
                        name,
                        pr.GetProperty("number").GetInt32(),
                        pr.GetProperty("title").GetString() ?? "",
                        pr.GetProperty("url").GetString() ?? "",
                        // UNKNOWN은 깃허브가 아직 계산 중인 상태다. 확정된 충돌만 표시한다.
                        pr.GetProperty("mergeable").GetString() == "CONFLICTING"));
                }
            }

            var page = repositories.GetProperty("pageInfo");
            cursor = page.GetProperty("hasNextPage").GetBoolean()
                ? page.GetProperty("endCursor").GetString()
                : null;
        }
        while (cursor is not null);

        return found.OrderBy(p => p.Repo, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Number).ToList();
    }

    /// GraphQL after 인자. 첫 페이지는 null 리터럴이다.
    static string Cursor(string? cursor) => cursor is null ? "null" : $"\"{cursor}\"";

    /// <summary>고른 PR 하나를 머지한다. 한 커밋짜리가 대부분이라 squash가 기본 브랜치를 깨끗하게 둔다.</summary>
    public static async Task MergePullRequestAsync(OpenPr pr, CancellationToken ct)
    {
        var result = await Proc.RunAsync("gh",
            ["pr", "merge", pr.Number.ToString(), "--repo", pr.Repo, "--squash", "--delete-branch"],
            Paths.Roaming, GhTimeout, ct);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
    }

    /// <summary>
    /// 손대도 될 열린 이슈 하나를 고른다. 오래된 것부터.
    /// 이미 열린 PR이 물고 있는 이슈는 건너뛴다 — 같은 이슈로 매일 PR을 쌓지 않기 위해서다.
    /// </summary>
    async Task<GhIssue?> FindIssueAsync(GhRepo repo, CancellationToken ct)
    {
        List<string> args = ["issue", "list", "--repo", repo.NameWithOwner,
            "--state", "open", "--json", "number,title,body"];
        if (cfg.IssueLabel.Length > 0) args.AddRange(["--label", cfg.IssueLabel]);

        var listed = await Proc.RunAsync("gh", args, Paths.Roaming, GhTimeout, ct);
        if (!listed.Ok) throw new InvalidOperationException($"gh issue list 실패: {listed.Message}");

        var issues = JsonSerializer.Deserialize<List<GhIssue>>(listed.Stdout, JsonOpts) ?? [];
        if (issues.Count == 0) return null;

        var openPrs = await Proc.RunAsync("gh",
            ["pr", "list", "--repo", repo.NameWithOwner, "--state", "open", "--json", "headRefName"],
            Paths.Roaming, GhTimeout, ct);
        if (!openPrs.Ok) throw new InvalidOperationException($"gh pr list 실패: {openPrs.Message}");

        var taken = (JsonSerializer.Deserialize<List<GhPrRef>>(openPrs.Stdout, JsonOpts) ?? [])
            .Select(p => p.HeadRefName)
            .ToList();

        return issues
            .OrderBy(i => i.Number)
            .FirstOrDefault(i => !taken.Any(b => b.EndsWith($"/#{i.Number}", StringComparison.Ordinal)));
    }

    static async Task<int> CreateIssueAsync(GhRepo repo, string title, string body, CancellationToken ct)
    {
        var bodyFile = Path.Combine(Path.GetTempPath(), $"gitgardener-issue-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(bodyFile, body, ct);
        try
        {
            var created = await Proc.RunAsync("gh",
                ["issue", "create", "--repo", repo.NameWithOwner, "--title", title, "--body-file", bodyFile],
                Paths.Roaming, GhTimeout, ct);
            if (!created.Ok) throw new InvalidOperationException($"gh issue create 실패: {created.Message}");

            // 출력은 이슈 URL이다. 끝의 번호만 떼어 쓴다.
            var tail = created.Stdout.Trim().Split('/')[^1];
            return int.TryParse(tail, out var number)
                ? number
                : throw new InvalidOperationException($"이슈 번호를 읽지 못했습니다: {created.Stdout.Trim()}");
        }
        finally
        {
            File.Delete(bodyFile);
        }
    }

    /// <summary>
    /// 대상 레포의 PR 템플릿을 그대로 쓴다. 레포마다 팀 양식이 다르니 형식을 앱이 정하지 않는다.
    /// 이미 동기화해둔 작업 사본에서 읽으므로 API를 더 부르지 않는다.
    /// </summary>
    static async Task<string> PrTemplateAsync(string dir, CancellationToken ct)
    {
        foreach (var path in PrTemplatePaths)
        {
            var file = Path.Combine(dir, path);
            if (!File.Exists(file)) continue;

            var text = (await File.ReadAllTextAsync(file, ct)).Trim();
            if (text.Length > 0) return text;
        }
        return DefaultPrTemplate;
    }

    /// 같은 이름이 원격에 이미 있으면 push가 실패한다. 비어 있는 이름을 찾아 돌려준다.
    static async Task<string> UniqueBranchAsync(string dir, string branch, CancellationToken ct)
    {
        for (var suffix = 1; suffix <= MaxBranchSuffix; suffix++)
        {
            var candidate = suffix == 1 ? branch : $"{branch}-{suffix}";
            var exists = await Proc.RunAsync("git", ["rev-parse", "--verify", $"origin/{candidate}"],
                dir, GitTimeout, ct);
            if (!exists.Ok) return candidate;
        }
        throw new InvalidOperationException($"쓸 수 있는 브랜치 이름을 찾지 못했습니다: {branch}");
    }

    async Task SyncAsync(GhRepo repo, string dir, string baseBranch, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            if (Directory.Exists(dir))
            {
                Log.Write($"[{repo.Name}] 온전하지 않은 작업 사본을 지우고 새로 클론합니다: {dir}");
                Directory.Delete(dir, recursive: true);
            }
            Directory.CreateDirectory(Paths.ReposDir);
            var clone = await Proc.RunAsync("gh", ["repo", "clone", repo.NameWithOwner, dir],
                Paths.ReposDir, GitTimeout, ct);
            if (!clone.Ok) throw new InvalidOperationException($"clone 실패: {clone.Message}");
            return;
        }

        await GitAsync(dir, ["fetch", "origin", "--prune"], ct);
        await GitAsync(dir, ["checkout", "-f", "-B", baseBranch, $"origin/{baseBranch}"], ct);
        await GitAsync(dir, ["clean", "-fd"], ct);
    }

    async Task<string> ImproveAsync(
        string dir, string rules, string template, GhIssue? issue, CancellationToken ct)
    {
        var launcher = Proc.ResolveClaude(cfg.ClaudePath);
        List<string> args = [
            .. launcher.Prefix,
            "-p", Prompt(template, issue),
            "--output-format", "json",
            "--permission-mode", "acceptEdits",
            "--allowedTools", AllowedTools,
            "--disallowedTools", DisallowedTools,
            "--append-system-prompt", rules,
            "--model", cfg.Model,
        ];

        var result = await Proc.RunAsync(launcher.Exe, args, dir, ClaudeTimeout, ct, ClaudeEnv());

        // 실패해도 응답 JSON에 사람이 읽을 이유가 들어 있다("...Please run /login." 등).
        // 원문 JSON을 그대로 뱉지 말고 그 문장을 꺼내 보여준다.
        var (text, failed) = ParseResult(result.Stdout);
        if (!result.Ok || failed)
        {
            var reason = text.Length > 0 ? text : result.Message;
            throw new InvalidOperationException(
                $"claude 실행 실패 (exit {result.ExitCode}): {reason}{LoginHint()}");
        }
        return text;
    }

    static async Task<string> CreatePullRequestAsync(
        GhRepo repo, string dir, string baseBranch, string branch, string title, string body, CancellationToken ct)
    {
        // 본문이 명령줄 상한에 걸리지 않게 파일로 넘긴다.
        var bodyFile = Path.Combine(Path.GetTempPath(), $"gitgardener-pr-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(bodyFile, body, ct);
        try
        {
            var pr = await Proc.RunAsync("gh",
                ["pr", "create",
                 "--repo", repo.NameWithOwner,
                 "--base", baseBranch,
                 "--head", branch,
                 "--title", title,
                 "--body-file", bodyFile],
                dir, GhTimeout, ct);
            if (!pr.Ok) throw new InvalidOperationException($"gh pr create 실패: {pr.Message}");
            return pr.Stdout.Trim();
        }
        finally
        {
            File.Delete(bodyFile);
        }
    }

    /// <summary>
    /// RULES.md를 받아 캐시하고 ponytail 룰셋을 덧붙인다.
    /// 갱신에 실패하면 캐시로 버티되, 캐시조차 없으면 규칙 없는 세션을 돌리지 않고 중단한다.
    /// raw.githubusercontent.com은 비인증 요청이라 429에 걸리므로 인증된 gh api로 받는다.
    /// </summary>
    async Task<string> LoadRulesAsync(CancellationToken ct)
    {
        var cache = Paths.RulesCache;
        var fresh = File.Exists(cache)
            && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromHours(RulesCacheHours);

        if (!fresh)
        {
            var repo = cfg.ResolveRulesRepo();
            var result = await Proc.RunAsync("gh",
                ["api", $"repos/{repo}/contents/{RulesFile}", "-H", "Accept: application/vnd.github.raw"],
                Paths.Roaming, GhTimeout, ct);

            if (result.Ok)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllTextAsync(cache, result.Stdout, ct);
                Log.Write($"코딩 규칙 갱신: {repo}/{RulesFile}");
            }
            else if (File.Exists(cache))
            {
                Log.Write($"코딩 규칙 갱신 실패, 캐시를 씁니다: {result.Message}");
            }
            else
            {
                throw new InvalidOperationException(
                    $"코딩 규칙을 받지 못했고 캐시도 없습니다 ({repo}/{RulesFile}): {result.Message}");
            }
        }

        var rules = await File.ReadAllTextAsync(cache, ct);
        if (!File.Exists(Paths.PonytailRules))
        {
            Log.Write($"ponytail 룰셋이 없어 건너뜁니다: {Paths.PonytailRules}");
            return rules;
        }
        return rules + "\n\n" + await File.ReadAllTextAsync(Paths.PonytailRules, ct);
    }

    /// <summary>
    /// claude를 실제로 띄울 수 있는지만 확인한다. API를 부르지 않아 공짜다.
    /// 설치 경로는 배포마다 바뀌는데, 그걸 예약 시각에 실패 로그로 알면 그날은 이미 빈다.
    /// </summary>
    public async Task<string> ClaudeVersionAsync(CancellationToken ct)
    {
        var launcher = Proc.ResolveClaude(cfg.ClaudePath);
        var result = await Proc.RunAsync(
            launcher.Exe, [.. launcher.Prefix, "--version"], Paths.Roaming, GhTimeout, ct, ClaudeEnv());
        if (!result.Ok)
            throw new InvalidOperationException($"claude를 실행하지 못했습니다: {result.Message}");
        return result.Stdout.Trim();
    }

    /// REST의 /user가 계속 503을 돌려주는 걸 확인했다. 레포 목록도 어차피 GraphQL이라 API 표면을 하나로 맞춘다.
    /// 일시적 장애로 실패해도 예약 실행이 쿨다운 뒤 다시 시도하므로 여기서 재시도하지 않는다.
    static async Task<string> CurrentUserAsync(CancellationToken ct)
    {
        var result = await Proc.RunAsync("gh",
            ["api", "graphql", "-f", "query={viewer{login}}", "--jq", ".data.viewer.login"],
            Paths.Roaming, GhTimeout, ct);
        if (!result.Ok) throw new InvalidOperationException($"gh 인증을 확인하지 못했습니다: {result.Message}");
        return result.Stdout.Trim();
    }

    static async Task<ProcResult> GitAsync(string dir, string[] args, CancellationToken ct)
    {
        var result = await Proc.RunAsync("git", args, dir, GitTimeout, ct);
        if (!result.Ok) throw new InvalidOperationException($"git {args[0]} 실패: {result.Message}");
        return result;
    }

    /// 규칙(RULES.md + ponytail)은 시스템 프롬프트로 이미 주입되므로 여기엔 범위만 담는다.
    string Prompt(string template, GhIssue? issue) => $"""
        {Task(issue)}

        범위
        - 개선은 정확히 1건. 여러 파일에 걸친 대공사는 금지한다.
        - {Focus.GetValueOrDefault(cfg.ImprovementType, Focus["auto"])}
        - 테스트가 없는 저장소라면 구조 리팩토링 대신 문서·주석·명명 개선을 택하라.
        - 주변 코드의 스타일과 표현을 그대로 따라가라.

        금지
        - 빌드·테스트 실행 금지. 셸 도구가 막혀 있다.
        - git 명령, 커밋, 브랜치, PR 조작 금지. 형상 관리는 호출한 앱이 한다.
        - 새 의존성 추가 금지.

        고칠 만한 것을 못 찾았으면 아무 파일도 수정하지 말고 그 이유를 한 줄로 답하고 끝내라.

        고쳤다면 마지막에 아래 형식을 그대로 출력하라. 표식 네 줄의 철자를 바꾸지 마라.

        TITLE: <Feat|Fix|Docs|Refactor|Test|Chore> 중 하나 + ": " + 한국어 한 줄 요약(80자 이내)
        BRANCH: 위 타입의 소문자 + "/" + 영문 소문자 kebab-case 단어 2~4개
        ISSUE: 고친 문제를 이슈 제목처럼 한국어 한 줄로. 해결책이 아니라 문제를 쓴다
        <ISSUE 줄 아래에는 그 문제를 설명하는 짧은 이슈 본문을 쓴다. 어떤 상태였고 왜 문제인지>
        BODY:
        <아래 템플릿을 채운 PR 본문>

        BODY에 쓸 템플릿은 이 저장소의 것이다. 섹션 제목과 순서를 그대로 유지하고 내용만 채워라.
        HTML 주석(<!-- -->)은 작성 안내이므로 읽고 지운다. 체크박스는 사실인 것만 켠다.

        ----- 템플릿 시작 -----
        {template}
        ----- 템플릿 끝 -----

        작성 규칙
        - 존댓말 평문으로 쓴다. 굵은 글씨와 이모지를 본문에 뿌리지 마라(템플릿의 이모지는 그대로 둔다).
        - 표를 만들지 마라. 문단과 목록으로 쓴다.
        - 없는 내용을 지어내지 마라. 쓸 게 없는 섹션은 통째로 지운다.
        - 스스로를 도구나 자동화로 지칭하지 마라.
        """;

    static string Task(GhIssue? issue) => issue is null
        ? "이 저장소를 딱 한 가지만 개선하라."
        : $"""
            아래 이슈를 해결하라.

            ----- 이슈 #{issue.Number} -----
            제목: {issue.Title}

            {issue.Body}
            ----- 이슈 끝 -----

            이슈가 한 번에 끝낼 수 없을 만큼 크면, 그중 독립적으로 의미 있는 한 조각만 처리하고
            무엇을 남겼는지 PR 본문에 적어라.
            """;

    /// 전용 설정 폴더는 따로 로그인해야 한다. 실패했을 때 무엇을 하라는 건지 바로 알 수 있게 붙인다.
    string LoginHint()
    {
        if (!cfg.SeparateClaudeConfig) return "";

        return $"""


            자동 세션은 전용 설정 폴더를 씁니다. 아직 로그인하지 않았다면 아래를 한 번 실행하고 /login 하세요.
              $env:CLAUDE_CONFIG_DIR = '{Paths.ClaudeConfig}'
              claude
            """;
    }

    /// claude --output-format json 응답에서 result 텍스트와 오류 여부를 뽑는다.
    static (string Text, bool Failed) ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("result", out var result) ? result.GetString() ?? "" : "";
            var failed = doc.RootElement.TryGetProperty("is_error", out var isError) && isError.GetBoolean();
            return (text, failed);
        }
        catch (JsonException)
        {
            Log.Write("claude 응답을 JSON으로 읽지 못해 원문을 그대로 씁니다.");
            return (json.Trim(), false);
        }
    }

    /// <summary>
    /// claude 응답 끝의 TITLE / BRANCH / BODY 블록을 떼어낸다.
    /// 형식이 어긋나면 막지 말고 안전한 기본값으로 메운다 — 개선 자체는 이미 끝난 뒤다.
    /// </summary>
    static (string Title, string Branch, string IssueTitle, string IssueBody, string Body) ParsePr(string summary)
    {
        var lines = summary.Split('\n');
        string title = "", branch = "", issueTitle = "", issueBody = "", body = "";
        var issueBodyStart = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(TitleMarker, StringComparison.Ordinal))
                title = line[TitleMarker.Length..].Trim();
            else if (line.StartsWith(BranchMarker, StringComparison.Ordinal))
                branch = line[BranchMarker.Length..].Trim();
            else if (line.StartsWith(IssueMarker, StringComparison.Ordinal))
            {
                issueTitle = line[IssueMarker.Length..].Trim();
                issueBodyStart = i + 1;
            }
            else if (line.StartsWith(BodyMarker, StringComparison.Ordinal))
            {
                // ISSUE: 줄과 BODY: 사이는 이슈 설명이다.
                if (issueBodyStart >= 0) issueBody = string.Join('\n', lines[issueBodyStart..i]).Trim();
                body = string.Join('\n', lines.Skip(i + 1)).Trim();
                break;
            }
        }

        if (!TitleFormat.IsMatch(title))
        {
            Log.Write($"제목 형식이 어긋나 기본값을 씁니다: {(title.Length > 0 ? title : "(없음)")}");
            title = FallbackTitle;
        }

        var type = title[..title.IndexOf(':')].Split('(')[0].ToLowerInvariant();
        if (!BranchFormat.IsMatch(branch) || !branch.StartsWith($"{type}/", StringComparison.Ordinal))
        {
            branch = $"{type}/{DateTime.Now:yyyyMMdd}-improve";
            Log.Write($"브랜치 형식이 어긋나 기본값을 씁니다: {branch}");
        }

        if (body.Length == 0)
        {
            Log.Write("PR 본문을 받지 못해 제목만으로 채웁니다.");
            body = $"## 📌 Summary\n\n{title}";
        }

        // 이슈 제목이 없으면 PR 제목에서 타입 접두사만 떼어 쓴다.
        if (issueTitle.Length == 0) issueTitle = title[(title.IndexOf(':') + 1)..].Trim();
        if (issueBody.Length == 0) issueBody = body;

        return (title, branch, issueTitle, issueBody, body);
    }

    /// <summary>
    /// 템플릿의 빈 이슈 참조 줄("> - #")을 실제 번호로 채운다.
    /// 그 줄이 없으면 본문 끝에 붙이고, 걸 이슈가 없으면 빈 줄째로 지운다.
    /// </summary>
    static string LinkIssue(string body, int? number)
    {
        var lines = body.Split('\n');
        var slot = Array.FindIndex(lines, EmptyIssueRef.IsMatch);

        if (number is null)
            return slot < 0 ? body : string.Join('\n', lines.Where((_, i) => i != slot)).Trim();

        if (slot < 0) return $"{body}\n\nCloses #{number}";

        lines[slot] = $"> - Closes #{number}";
        return string.Join('\n', lines).Trim();
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 0 ? line : "(응답 없음)";
    }
}
