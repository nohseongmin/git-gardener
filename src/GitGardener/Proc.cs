using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitGardener;

readonly record struct ProcResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;

    /// 실패 원인을 한 덩어리로. stderr가 비면 stdout이 이유를 담고 있는 경우가 많다.
    public string Message => Stderr.Trim().Length > 0 ? Stderr.Trim() : Stdout.Trim();
}

/// <summary>claude를 띄우는 방법. npm 설치본은 exe가 아니라 node가 실행하는 스크립트다.</summary>
readonly record struct Launcher(string Exe, IReadOnlyList<string> Prefix);

/// <summary>
/// 외부 CLI 실행 래퍼. 출력 인코딩을 UTF-8로 못박아 콘솔 코드페이지와 무관하게 한글을 살린다(PLAN.md).
/// </summary>
static class Proc
{
    /// CreateProcess의 명령줄 상한은 32767자다. RULES.md 주입이 여기 걸릴 수 있어 미리 막는다.
    const int MaxCommandLineChars = 30000;

    public static async Task<ProcResult> RunAsync(
        string exe, IReadOnlyList<string> args, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var length = exe.Length + args.Sum(a => a.Length + 3);
        if (length > MaxCommandLineChars)
            throw new InvalidOperationException(
                $"명령줄이 {length}자로 상한 {MaxCommandLineChars}자를 넘었습니다: {Path.GetFileName(exe)}");

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"프로세스를 시작하지 못했습니다: {exe}");

        // 입력을 기다리다 멈추는 일이 없게 stdin을 즉시 닫는다.
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            await Task.WhenAll(stdout, stderr);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException(
                $"{Path.GetFileName(exe)} 실행이 {timeout.TotalMinutes:0}분을 넘겨 중단했습니다.");
        }

        return new ProcResult(proc.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// CreateProcess는 PATHEXT를 적용하지 않고 .exe만 붙여본다. 그래서 확장자를 직접 붙여 PATH를 훑는다.
    /// </summary>
    public static string? Find(string name, params string[] extensions)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim('"'), name + ext);
                }
                catch (ArgumentException)
                {
                    break; // PATH 항목에 경로로 못 쓰는 문자가 섞인 경우
                }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// claude를 어떻게 띄울지 정한다. 설치 방식마다 실행 파일 자리가 달라 순서대로 훑는다.
    ///
    /// npm 전역 설치는 PATH에 claude.cmd / claude.ps1 심만 남긴다. CreateProcess는 PATHEXT를
    /// 적용하지 않아 심을 찾지 못하고, cmd.exe로 우회하면 주입할 RULES.md의 `|`·`%`·`"` 가
    /// 셸에 먹혀버린다. 그래서 심을 거치지 않고 실제 실행 파일을 직접 찾아 띄운다.
    /// </summary>
    public static Launcher ResolveClaude(string configuredPath)
    {
        if (configuredPath.Length > 0)
        {
            if (!File.Exists(configuredPath))
                throw new FileNotFoundException($"설정한 claudePath에 파일이 없습니다: {configuredPath}");
            return new Launcher(configuredPath, []);
        }

        if (Find("claude", ".exe") is { } onPath) return new Launcher(onPath, []);

        if (Find("claude", ".cmd", ".ps1") is { } shim)
        {
            var package = Path.Combine(
                Path.GetDirectoryName(shim)!, "node_modules", "@anthropic-ai", "claude-code");

            // 요즘 배포판은 패키지 안에 네이티브 바이너리를 담고 있다.
            var packaged = Path.Combine(package, "bin", "claude.exe");
            if (File.Exists(packaged)) return new Launcher(packaged, []);

            // 예전 배포판은 node로 돌리는 스크립트였다.
            var cli = Path.Combine(package, "cli.js");
            if (File.Exists(cli) && Find("node", ".exe") is { } node) return new Launcher(node, [cli]);
        }

        // 네이티브 설치본. PATH에 없는 경우가 있어 기본 위치를 직접 본다.
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(installed)) return new Launcher(installed, []);

        throw new FileNotFoundException(
            "claude CLI를 찾지 못했습니다. `npm i -g @anthropic-ai/claude-code`로 설치하거나 config.json의 claudePath를 지정하세요.");
    }

    static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // 이미 끝났거나 접근이 막힌 경우. 종료가 목적이므로 더 할 일이 없다.
        }
    }
}
