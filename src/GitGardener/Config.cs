using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitGardener;

/// <summary>앱이 쓰는 모든 경로. 로밍=사용자 설정·로그, 로컬=언제든 지워도 되는 작업 사본.</summary>
static class Paths
{
    const string AppName = "GitGardener";

    public static string Roaming { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string Local { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string ConfigFile => Path.Combine(Roaming, "config.json");
    public static string LogDir => Path.Combine(Roaming, "log");
    public static string ReposDir => Path.Combine(Local, "repos");
    public static string RulesCache => Path.Combine(Local, "rules", "RULES.md");

    /// ponytail은 Windows에서 플러그인 설치가 실패하므로(SETUP.md) 마켓플레이스 클론에서 직접 읽는다.
    public static string PonytailRules { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "plugins", "marketplaces", "ponytail", ".agents", "rules", "ponytail.md");
}

/// <summary>파일과 UI에 동시에 남기는 로그. 백그라운드 스레드에서 호출되므로 구독자가 마샬링을 책임진다.</summary>
static class Log
{
    static readonly Lock Gate = new();

    /// BOM을 붙인다. 없으면 PowerShell 5.1의 Get-Content나 옛 편집기가 한글 로그를 ANSI로 읽어 깨뜨린다.
    static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static event Action<string>? Line;

    public static void Write(string message)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogDir);
                File.AppendAllText(
                    Path.Combine(Paths.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    stamped + Environment.NewLine, FileEncoding);
            }
        }
        catch (IOException ex)
        {
            // 로그 파일이 잠겨도 실행 자체는 계속한다. 대신 화면에는 반드시 드러낸다.
            stamped += $"  (로그 파일 기록 실패: {ex.Message})";
        }
        Line?.Invoke(stamped);
    }
}

/// <summary>이슈를 어떻게 다룰지. 자동 PR도 사람 PR처럼 이슈에서 출발하게 하는 스위치다.</summary>
enum IssueMode
{
    /// 열린 이슈가 있으면 그것을 처리하고, 없으면 스스로 찾아 이슈를 만든 뒤 처리한다.
    Prefer,

    /// 열린 이슈가 있을 때만 작업한다. 없으면 그 레포는 건너뛴다.
    Only,

    /// 이슈를 보지도 만들지도 않고 곧장 PR만 올린다.
    None,
}

sealed class Config
{
    const string DefaultRulesRepoName = "coding-rules";
    const string DefaultScheduleTime = "22:00";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string GithubUser { get; set; } = "";
    public List<string> EnabledRepos { get; set; } = [];
    public string ScheduleTime { get; set; } = DefaultScheduleTime;
    public int ReposPerDay { get; set; } = 1;

    /// 켜면 하루 처리량을 reposPerDay 대신 1~maxReposPerDay 사이에서 매일 새로 뽑는다.
    public bool VaryDailyLoad { get; set; } = true;

    public int MaxReposPerDay { get; set; } = 10;
    public string ImprovementType { get; set; } = "auto";
    public string Model { get; set; } = "sonnet";
    public string LastRunDate { get; set; } = "";
    public bool RunAtStartup { get; set; }
    public int CatchUpDelayMinutes { get; set; } = 5;

    public IssueMode IssueMode { get; set; } = IssueMode.Prefer;

    /// 이 라벨이 붙은 이슈만 고른다. 비워두면 열린 이슈 전부가 대상이다.
    public string IssueLabel { get; set; } = "";

    /// 코딩 규칙을 받아올 "owner/repo". 비워두면 githubUser의 coding-rules 레포.
    public string RulesRepo { get; set; } = "";

    /// 비워두면 PATH에서 자동 탐지한다. 네이티브 설치본을 쓸 때만 지정.
    public string ClaudePath { get; set; } = "";

    /// scheduleTime에서 계산한다. 파일에 나가면 고쳐도 안 먹히는 항목이 보여 헷갈린다.
    [JsonIgnore]
    public TimeOnly Schedule => TimeOnly.Parse(ScheduleTime);

    public string ResolveRulesRepo() =>
        RulesRepo.Length > 0 ? RulesRepo : $"{GithubUser}/{DefaultRulesRepoName}";

    public static Config Load()
    {
        if (!File.Exists(Paths.ConfigFile)) return new Config();
        try
        {
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.ConfigFile), JsonOpts) ?? new Config();
            cfg.Validate();
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Write($"설정을 읽지 못해 기본값으로 시작합니다: {ex.Message}");
            return new Config();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Roaming);
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// 손으로 고친 config.json이 앱을 무너뜨리지 않게, 못 쓰는 값은 알리고 기본값으로 되돌린다.
    void Validate()
    {
        if (!TimeOnly.TryParse(ScheduleTime, out _))
        {
            Log.Write($"scheduleTime '{ScheduleTime}'을 읽지 못해 {DefaultScheduleTime}으로 되돌립니다.");
            ScheduleTime = DefaultScheduleTime;
        }
        if (ReposPerDay < 1)
        {
            Log.Write($"reposPerDay {ReposPerDay}는 1보다 작아 1로 되돌립니다.");
            ReposPerDay = 1;
        }
        if (MaxReposPerDay < 1)
        {
            Log.Write($"maxReposPerDay {MaxReposPerDay}는 1보다 작아 1로 되돌립니다.");
            MaxReposPerDay = 1;
        }
        if (CatchUpDelayMinutes < 0)
        {
            Log.Write($"catchUpDelayMinutes {CatchUpDelayMinutes}는 음수라 0으로 되돌립니다.");
            CatchUpDelayMinutes = 0;
        }
    }
}
