# 개발 환경 세팅

## PC에서 확인할 것

PowerShell에서 순서대로 확인하고, 없는 것만 설치한다.

| 확인 명령 | 없을 때 |
|---|---|
| `dotnet --list-sdks` | `winget install Microsoft.DotNet.SDK.9` |
| `gh --version` | `winget install --id GitHub.cli -e` |
| `gh auth status` | `gh auth login` (아래 참고) |
| `claude --version` | `npm i -g @anthropic-ai/claude-code` |
| `git --version` | `winget install Git.Git` |

Visual Studio는 **필요 없다.** UI를 코드로 구성하므로 `dotnet` CLI만으로 빌드된다.

### claude CLI 로그인

`claude --version`이 떠도 그것만으로는 부족하다. **CLI 자체가 로그인되어 있어야** 헤드리스 세션이 돈다. 확인:

```bash
claude -p "reply with OK only" --output-format json
```

`is_error: false`에 `result: "OK"`면 통과다. 아니고 아래 문장이 나오면 로그인이 안 된 것이다:

```
Your account does not have access to Claude Code. Please run /login.
```

대화형으로 `claude`를 띄워 `/login`을 마치면 풀린다. 로그인 전에는 앱이 돌아도 claude 단계에서 매번 실패하고, 로그에 위 문장이 그대로 찍힌다.

### claude 설치 형태가 바뀐다

**PATH에 있는 `claude`는 실행 파일이 아니라 심(shim)이다.** 그리고 그 심이 가리키는 곳이 배포마다 바뀐다. 실제로 이 PC에서 관측된 것만 세 가지다.

| 배포 | 실제 실행 파일 |
|---|---|
| 요즘 npm | `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe` |
| 예전 npm | 같은 경로의 `cli.js` (node로 실행) |
| 네이티브 설치 | `%USERPROFILE%\.local\bin\claude.exe` (PATH에 없을 수 있음) |

Runner는 이 순서로 찾는다. 그래도 못 찾으면 `config.json`의 `claudePath`에 직접 지정한다.

> 오래된 네이티브 설치본이 `~/.local/bin`에 남아 있을 수 있다. 이 PC에도 `2.0.42`짜리가 남아 있는데 npm 쪽은 `2.1.233`이다. 그래서 npm 패키지를 먼저 본다.

### gh 인증

```bash
gh auth login --hostname github.com --git-protocol https --web --scopes "repo,workflow,read:org"
```

일회용 코드가 뜨면 https://github.com/login/device 에서 입력한다. 필요한 scope는 `repo`(PR 생성), `workflow`, `read:org`.

그리고 **git 자격증명 헬퍼를 반드시 붙인다:**

```bash
gh auth setup-git --hostname github.com
```

이게 없으면 전역 설정에는 Git Credential Manager만 남아, 헤드리스로 도는 `git push`가 GUI 인증창을 띄우고 **타임아웃까지 멈춘다.** 붙으면 `git push`가 gh 토큰을 그대로 써서 조용히 통과한다. 확인:

```bash
git config --global --get-regexp credential
```

`credential.https://github.com.helper` 에 `gh.exe auth git-credential` 이 보이면 된다.

커밋 작성자도 필요하다 — `git config --global user.name` / `user.email` 이 비어 있으면 커밋 단계에서 실패한다.

### 레포 클론

```bash
git clone https://github.com/nohseongmin/GrassKeeper
```

## ponytail

### 알려진 설치 실패 (Windows)

```
claude plugin marketplace add DietrichGebert/ponytail   # ← 성공
claude plugin install ponytail@ponytail                 # ← 실패
```

```
EPERM: operation not permitted, rename
'...\.claude\plugins\cache\ponytail' -> '...\.claude\plugins\cache\ponytail\ponytail\4.9.0'
```

마켓플레이스 이름과 플러그인 이름이 둘 다 `ponytail`이라, 마켓플레이스 클론 경로를 **자기 자신의 하위 경로로** rename하려다 Windows가 거부한다. 캐시(`.claude\plugins\cache\ponytail`)를 지우고 재시도해도 똑같이 재현된다.

마켓플레이스 등록은 성공하므로 소스는 이미 로컬에 있다:

```
C:\Users\<user>\.claude\plugins\marketplaces\ponytail\
```

### 우회

**A. 자동화(Runner)에서 — 설치 자체가 불필요하다.** ★

Runner는 어차피 `--append-system-prompt`로 코딩 규칙을 주입한다. ponytail 룰 본문도 2.5KB짜리 파일 하나뿐이므로 같이 붙이면 끝이다:

```
marketplaces\ponytail\.agents\rules\ponytail.md   (2,525B)
```

설치 상태에 의존하지 않게 되므로 **이 방식을 기본으로 한다.** 플러그인 설치 버그가 고쳐지든 말든 파이프라인은 영향받지 않는다.

**B. 대화형 세션에서 — 세션 한정 로드.** (PC에서 검증 필요)

```bash
claude --plugin-dir "C:\Users\<user>\.claude\plugins\marketplaces\ponytail"
```

`--plugin-dir`은 설치를 거치지 않고 디렉토리에서 플러그인을 직접 로드한다. 이게 동작하면 `/ponytail`, `/ponytail-review` 같은 명령도 쓸 수 있다. **아직 실제로 확인하지 않았다** — PC에서 먼저 테스트할 것.

**C. 근본 해결**

업스트림 이슈다 (마켓플레이스명 = 플러그인명일 때 경로 충돌). Claude Code 업데이트 후 재시도해보면 된다.

## 코딩 규칙

자동 세션에 주입되는 규칙은 별도 레포에 있다:

```
https://github.com/nohseongmin/coding-rules   →  RULES.md (15KB)
```

Runner가 `gh api`로 받아 `%LOCALAPPDATA%\GrassKeeper\rules\`에 캐시한다(하루 1회 갱신, 실패 시 캐시). 규칙을 고치려면 GrassKeeper가 아니라 **coding-rules 레포를 고친다.**

> `raw.githubusercontent.com`은 쓰지 않는다. 비인증 요청이라 `HTTP 429`에 걸린다 — 구현 중 실제로 재현됐다. ponytail(2.5KB)까지 붙여 총 17.6KB가 `--append-system-prompt`로 들어간다.

PC에 글로벌 `CLAUDE.md`(`~\.claude\CLAUDE.md`)가 따로 있다면 랩탑과 내용을 맞춰두는 게 좋다. 현재 랩탑 쪽은 두 줄뿐이다:

```
- 항상 한국어로 대답해라
- 빌드 테스트는 하지마 내가 할라니까
```

> 두 번째 줄이 자동화에 그대로 영향을 준다. 자동 세션은 빌드 검증을 하지 않으므로 **깨진 코드가 PR로 올라올 수 있다**는 걸 전제로 리뷰해야 한다.

## 빌드

```bash
dotnet publish src/GrassKeeper -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

산출물은 `src/GrassKeeper/bin/Release/net9.0-windows/win-x64/publish/GrassKeeper.exe`.

## 첫 실행 순서

1. exe 실행 → 레포 목록이 GitHub 계정에서 채워지는지 확인
2. 대상 레포를 **1개만** 체크
3. **Dry-run** — 편집 내역과 `git diff`가 로그에 찍히는지, PR이 안 만들어지는지 확인
4. `지금 1회 실행` — PR이 실제로 올라오고 트레이 알림에 링크가 뜨는지 확인
5. PR 내용이 납득되면 → 대상 레포를 늘리고 `시작프로그램 등록`
6. 재부팅 → 트레이에 자동으로 떠 있는지 확인

**3번을 건너뛰지 말 것.** 자동 push가 붙어 있어서 첫 실행부터 실제 PR이 올라간다.

## 이미 끝난 것

**랩탑**

- gh CLI 2.97.0 설치 + `nohseongmin` 인증 완료 (scope: `gist,read:org,repo,workflow`)
- ponytail 마켓플레이스 등록 (플러그인 설치는 위 버그로 실패)
- GrassKeeper 레포 생성 + 설계 문서 커밋
- 환경 확인: .NET 9 SDK 9.0.315, Node v22.17.1, claude 2.0.64, git 2.51.0

**데스크탑 (구현한 곳)**

- 환경: .NET 9 SDK 9.0.304, gh 2.97.0, git 2.48.1, claude 2.1.233
- `src/GrassKeeper` 전체 구현 + `dotnet publish` 단일 exe 검증
- 실행 검증: 트레이 기동 → 레포 37개 자동 로드 → UI 정상 렌더
- gh 인증 + `gh auth setup-git` 완료 (헤드리스 push 통과 확인)
- claude CLI 로그인 완료
- **Dry-run 파이프라인 전체 통과** — `daily-tangle` 대상으로 규칙 수신 → 동기화 → claude(49초) → 응답 파싱 → 템플릿 채움까지 돌았고, 이슈도 PR도 만들지 않는 것까지 확인했다.

**아직 안 해본 것**

실제 PR을 올리는 경로(`지금 1회 실행`). Dry-run까지는 검증했으니 다음은 이거다.
