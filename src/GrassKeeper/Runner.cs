using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GrassKeeper;

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

/// <summary>
/// 파이프라인 본체. 레포 1개당 동기화 → 브랜치 → claude → 변경 검사 → 커밋 → push → PR.
/// 실패해도 브랜치는 남긴다(원인 추적용). 변경이 없으면 브랜치를 지우고 PR을 만들지 않는다.
/// </summary>
sealed partial class Runner(Config cfg)
{
    const string ProjectUrl = "https://github.com/nohseongmin/GrassKeeper";
    const string AllowedTools = "Read,Edit,Write,Glob,Grep";
    const string DisallowedTools = "Bash";
    const string FallbackCommitTitle = "chore: automated maintenance";
    const string RulesFile = "RULES.md";
    const int MaxRepoListSize = 200;
    const int RulesCacheHours = 24;

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

    /// 커밋 제목으로 쓸 수 있는 한 줄인지 판별한다.
    [GeneratedRegex(@"^(build|chore|ci|docs|feat|fix|perf|refactor|style|test)(\([^)]+\))?!?: .{3,80}$")]
    private static partial Regex ConventionalCommit { get; }

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

        // 가장 오래 안 건드린 레포부터. 하루 처리량만큼만.
        var targets = repos
            .Where(r => cfg.EnabledRepos.Contains(r.Name))
            .OrderBy(r => r.UpdatedAt)
            .Take(cfg.ReposPerDay)
            .ToList();

        if (targets.Count == 0)
        {
            Log.Write("대상 레포가 없습니다. 목록에서 체크한 뒤 다시 실행하세요.");
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

    /// <returns>PR URL. 변경이 없거나 dry-run이면 null.</returns>
    async Task<string?> ProcessAsync(GhRepo repo, string rules, bool dryRun, CancellationToken ct)
    {
        var baseBranch = repo.DefaultBranchRef?.Name
            ?? throw new InvalidOperationException("기본 브랜치를 알 수 없습니다. 빈 레포일 수 있습니다.");
        var dir = Path.Combine(Paths.ReposDir, repo.Name);
        var branch = $"auto/improve-{DateTime.Now:yyyyMMdd-HHmm}";

        Log.Write($"[{repo.Name}] 작업 사본 동기화");
        await SyncAsync(repo, dir, baseBranch, ct);

        Log.Write($"[{repo.Name}] 브랜치 {branch}");
        await GitAsync(dir, ["checkout", "-b", branch], ct);

        Log.Write($"[{repo.Name}] claude 실행 (최대 {ClaudeTimeout.TotalMinutes:0}분)");
        var summary = await ImproveAsync(dir, rules, ct);

        var status = await GitAsync(dir, ["status", "--porcelain"], ct);
        if (status.Stdout.Trim().Length == 0)
        {
            Log.Write($"[{repo.Name}] 변경 없음 → 건너뜀. 응답: {FirstLine(summary)}");
            await GitAsync(dir, ["checkout", "-f", baseBranch], ct);
            await GitAsync(dir, ["branch", "-D", branch], ct);
            return null;
        }

        await GitAsync(dir, ["add", "-A"], ct);

        if (dryRun)
        {
            var stat = await GitAsync(dir, ["diff", "--cached", "--stat"], ct);
            var diff = await GitAsync(dir, ["diff", "--cached"], ct);
            Log.Write($"[{repo.Name}] DRY-RUN — PR을 만들지 않습니다.\n{summary}\n\n{stat.Stdout}\n{diff.Stdout}");
            return null;
        }

        var title = CommitTitle(summary);
        await GitAsync(dir, ["commit", "-m", title], ct);
        await GitAsync(dir, ["push", "-u", "origin", branch], ct);
        return await CreatePullRequestAsync(repo, dir, baseBranch, branch, title, summary, ct);
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

    async Task<string> ImproveAsync(string dir, string rules, CancellationToken ct)
    {
        var launcher = Proc.ResolveClaude(cfg.ClaudePath);
        List<string> args = [
            .. launcher.Prefix,
            "-p", Prompt(),
            "--output-format", "json",
            "--permission-mode", "acceptEdits",
            "--allowedTools", AllowedTools,
            "--disallowedTools", DisallowedTools,
            "--append-system-prompt", rules,
            "--model", cfg.Model,
        ];

        var result = await Proc.RunAsync(launcher.Exe, args, dir, ClaudeTimeout, ct);

        // 실패해도 응답 JSON에 사람이 읽을 이유가 들어 있다("...Please run /login." 등).
        // 원문 JSON을 그대로 뱉지 말고 그 문장을 꺼내 보여준다.
        var (text, failed) = ParseResult(result.Stdout);
        if (!result.Ok || failed)
            throw new InvalidOperationException(
                $"claude 실행 실패 (exit {result.ExitCode}): {(text.Length > 0 ? text : result.Message)}");
        return text;
    }

    async Task<string> CreatePullRequestAsync(
        GhRepo repo, string dir, string baseBranch, string branch, string title, string summary, CancellationToken ct)
    {
        // 본문이 명령줄 상한에 걸리지 않게 파일로 넘긴다.
        var bodyFile = Path.Combine(Path.GetTempPath(), $"grasskeeper-pr-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(bodyFile, PrBody(summary), ct);
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
    string Prompt() => $"""
        이 저장소를 딱 한 가지만 개선하라.

        범위
        - 개선은 정확히 1건. 여러 파일에 걸친 대공사는 금지한다.
        - {Focus.GetValueOrDefault(cfg.ImprovementType, Focus["auto"])}
        - 테스트가 없는 저장소라면 구조 리팩토링 대신 문서·주석·명명 개선을 택하라.
        - 주변 코드의 스타일과 표현을 그대로 따라가라.

        금지
        - 빌드·테스트 실행 금지. 셸 도구가 막혀 있다.
        - git 명령, 커밋, 브랜치, PR 조작 금지. 형상 관리는 호출한 앱이 한다.
        - 새 의존성 추가 금지.

        마무리
        - 마지막 줄에 변경 요약을 Conventional Commits 한 줄로 출력하라. 예: docs: clarify setup steps in README
        - 고칠 만한 것을 못 찾았으면 아무 파일도 수정하지 말고 그 이유를 한 줄로 답하라.
        """;

    static string PrBody(string summary) => $"""
        {summary}

        ---
        🌱 [GrassKeeper]({ProjectUrl})가 자동 생성한 PR입니다.

        - 자동 세션에는 셸이 차단되어 있어 **빌드·테스트 검증을 거치지 않았습니다.** 머지 전에 직접 확인하세요.
        - 주입된 규칙: coding-rules `RULES.md` + ponytail
        """;

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

    static string CommitTitle(string summary)
    {
        foreach (var line in summary.Split('\n'))
        {
            var trimmed = line.Trim().Trim('`', '*', '#', ' ');
            if (ConventionalCommit.IsMatch(trimmed)) return trimmed;
        }
        return FallbackCommitTitle;
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 0 ? line : "(응답 없음)";
    }
}
