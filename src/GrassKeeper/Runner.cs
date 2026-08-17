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
    const string AllowedTools = "Read,Edit,Write,Glob,Grep";
    const string DisallowedTools = "Bash";
    const string RulesFile = "RULES.md";
    const int MaxRepoListSize = 200;
    const int RulesCacheHours = 24;

    /// claude 응답에서 PR 정보를 떼어낼 표식.
    const string TitleMarker = "TITLE:";
    const string BranchMarker = "BRANCH:";
    const string BodyMarker = "BODY:";

    const string FallbackTitle = "Chore: 저장소 정리";
    const string FallbackBranchType = "chore";

    /// 브랜치 이름이 겹칠 때 붙일 접미사의 최대 시도 횟수.
    const int MaxBranchSuffix = 20;

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

        // 편집을 하려면 브랜치가 먼저 필요한데, 브랜치 이름은 무엇을 고쳤는지 알아야 정해진다.
        // 그래서 임시 이름으로 만들고 커밋 직전에 실제 이름으로 바꾼다.
        var branch = $"wip/{Guid.NewGuid():N}"[..16];

        Log.Write($"[{repo.Name}] 작업 사본 동기화");
        await SyncAsync(repo, dir, baseBranch, ct);
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

        var pr = ParsePr(summary);
        branch = await UniqueBranchAsync(dir, pr.Branch, ct);
        await GitAsync(dir, ["branch", "-m", branch], ct);
        Log.Write($"[{repo.Name}] {branch} — {pr.Title}");

        await GitAsync(dir, ["commit", "-m", pr.Title], ct);
        await GitAsync(dir, ["push", "-u", "origin", branch], ct);
        return await CreatePullRequestAsync(repo, dir, baseBranch, branch, pr.Title, pr.Body, ct);
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

    static async Task<string> CreatePullRequestAsync(
        GhRepo repo, string dir, string baseBranch, string branch, string title, string body, CancellationToken ct)
    {
        // 본문이 명령줄 상한에 걸리지 않게 파일로 넘긴다.
        var bodyFile = Path.Combine(Path.GetTempPath(), $"grasskeeper-pr-{Guid.NewGuid():N}.md");
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

        고칠 만한 것을 못 찾았으면 아무 파일도 수정하지 말고 그 이유를 한 줄로 답하고 끝내라.

        고쳤다면 마지막에 아래 형식을 그대로 출력하라. 표식 세 줄의 철자를 바꾸지 마라.

        TITLE: <Feat|Fix|Docs|Refactor|Test|Chore> 중 하나 + ": " + 한국어 한 줄 요약(80자 이내)
        BRANCH: 위 타입의 소문자 + "/" + 영문 소문자 kebab-case 단어 2~4개
        BODY:
        ## 📌 Summary

        무엇을 왜 고쳤는지 한두 문단. 배경(어떤 상태였는지)부터 쓰고, 그래서 무엇을 했는지로 잇는다.

        ## 📚 Tasks

        - 변경한 것을 항목으로. 파일 나열이 아니라 한 일을 쓴다.

        ## 👀 To Reviewer

        판단이 갈릴 만한 지점, 일부러 안 건드린 것, 리뷰어가 봐줬으면 하는 부분.

        작성 규칙
        - 존댓말 평문으로 쓴다. 굵은 글씨와 이모지를 본문에 뿌리지 마라(섹션 제목의 이모지는 그대로 둔다).
        - 표를 만들지 마라. 문단과 목록으로 쓴다.
        - 없는 내용을 채우지 마라. 쓸 게 없는 섹션은 통째로 지운다.
        - 스스로를 도구나 자동화로 지칭하지 마라.
        """;

    const string ReviewerHeader = "## 👀 To Reviewer";

    /// 문체는 사람이 쓴 PR을 따르되, 검증을 안 거쳤다는 사실만은 리뷰어에게 반드시 남긴다.
    const string ReviewerNote =
        "빌드와 테스트는 돌리지 않았습니다. 머지 전에 확인 부탁드립니다.";

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
    static (string Title, string Branch, string Body) ParsePr(string summary)
    {
        var lines = summary.Split('\n');
        string title = "", branch = "", body = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(TitleMarker, StringComparison.Ordinal))
                title = line[TitleMarker.Length..].Trim();
            else if (line.StartsWith(BranchMarker, StringComparison.Ordinal))
                branch = line[BranchMarker.Length..].Trim();
            else if (line.StartsWith(BodyMarker, StringComparison.Ordinal))
            {
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

        // To Reviewer는 템플릿의 마지막 섹션이다. 이미 있으면 그 아래에 덧붙이고, 없으면 섹션째로 만든다.
        var note = body.Contains(ReviewerHeader, StringComparison.Ordinal)
            ? $"{body}\n\n{ReviewerNote}"
            : $"{body}\n\n{ReviewerHeader}\n\n{ReviewerNote}";
        return (title, branch, note);
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 0 ? line : "(응답 없음)";
    }
}
