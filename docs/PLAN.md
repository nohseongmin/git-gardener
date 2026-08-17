# GrassKeeper 설계

> 이 문서만 보고 개발에 착수할 수 있는 것을 목표로 한다.

## 확정된 요구사항

| 항목 | 결정 |
|---|---|
| 대상 레포 | `gh repo list`로 계정 전체 스캔 → GUI 체크박스로 on/off |
| 코딩 규칙 | [coding-rules](https://github.com/nohseongmin/coding-rules) `RULES.md` + ponytail 룰셋 |
| PR 범위 | 브랜치 → 커밋 → push → PR 생성까지 완전 자동 |
| 알림 | 트레이 아이콘 + Windows 풍선 알림 |
| 자동 실행 | 부팅 시 트레이 상주 (`HKCU\...\Run`) |

## 파일 구조

```
GrassKeeper.sln
src/GrassKeeper/
  GrassKeeper.csproj   # net9.0-windows, UseWindowsForms, single-file self-contained
  Program.cs           # 진입점. --tray 플래그면 창 없이 트레이로 시작
  MainForm.cs          # UI 전체를 코드로 구성 (Designer/resx 없음)
  Config.cs            # 설정 모델 + %APPDATA%\GrassKeeper\config.json 로드/저장
  Proc.cs              # git/gh/claude 프로세스 실행 래퍼
  Runner.cs            # 파이프라인 본체
```

**NuGet 의존성 0개.** `System.Text.Json`, `NotifyIcon`, `Microsoft.Win32.Registry` 전부 내장이다.

> **WinForms Designer를 쓰지 않는다.** 개발 환경에 Visual Studio가 없어서 `.Designer.cs` / `.resx`를 편집할 수단이 없다. UI는 전부 C# 코드로 구성해서 `dotnet` CLI만으로 완결되게 한다.

## Proc.cs — 프로세스 래퍼

`ProcessStartInfo`로 stdout/stderr 리다이렉트, 종료 코드와 출력 문자열 반환. 타임아웃과 `CancellationToken`을 받는다.

**인코딩은 UTF-8로 고정한다.** 같은 개발자의 이전 프로젝트([GUIForGeminiCli](https://github.com/nohseongmin/GUIForGeminiCli))에서 `chcp 949`로 한글 깨짐을 다뤄야 했는데, `git`/`gh`/`claude`는 모두 UTF-8로 출력하므로 코드페이지를 따라가지 말고 `StandardOutputEncoding = Encoding.UTF8`로 못박는다.

**claude는 `node`로 띄운다.** `git`·`gh`는 `.exe`지만 npm 전역 설치된 claude는 `claude.cmd` / `claude.ps1` 심만 남는다. `CreateProcess`는 PATHEXT를 적용하지 않고 `.exe`만 붙여보므로 `claude`는 "파일 없음"으로 실패한다. 그렇다고 `cmd.exe /c`로 우회하면 주입할 `RULES.md`의 마크다운 표 `|`, `%`, `"` 가 셸에 먹혀 프롬프트가 깨진다. 그래서 심이 있는 디렉토리에서 `node_modules\@anthropic-ai\claude-code\cli.js`를 찾아 **`node.exe`로 직접 실행한다.** `ArgumentList`가 이스케이프를 처리하므로 17KB짜리 시스템 프롬프트도 무손상으로 넘어간다(검증 완료). 네이티브 설치본(`claude.exe`)이 PATH에 있으면 그쪽을 우선 쓰고, `config.json`의 `claudePath`로 직접 지정할 수도 있다.

호출할 외부 명령:

| 도구 | 용도 |
|---|---|
| `gh repo list <user> --json name,url,isArchived,isFork,updatedAt` | 대상 목록 |
| `gh pr create --title ... --body ...` | PR 생성 |
| `git clone` / `fetch` / `reset --hard` / `checkout -b` / `status --porcelain` / `add` / `commit` / `push` | 형상 관리 |
| `claude -p ...` | 개선 작업 |

## Runner.cs — 파이프라인

레포 1개당:

### 1. 작업 사본 준비
`%LOCALAPPDATA%\GrassKeeper\repos\<name>` 에 없으면 `git clone`, 있으면 `git fetch origin` + `git reset --hard origin/<기본브랜치>` + `git clean -fd`.

> 개발자가 실제로 작업 중인 로컬 레포 폴더는 **절대 건드리지 않는다.** 항상 이 전용 사본에서만 돈다.

### 2. 브랜치
`auto/improve-yyyyMMdd-HHmm`. 기본 브랜치에 직접 커밋하는 코드 경로를 만들지 않는다 (규칙 V-3).

### 3. Claude 헤드리스 실행

작업 디렉토리를 레포 사본으로 두고:

```
claude -p "<작업 프롬프트>"
       --output-format json
       --permission-mode acceptEdits
       --allowedTools Read,Edit,Write,Glob,Grep
       --disallowedTools Bash
       --append-system-prompt "<RULES.md 전문>"
       --model <설정값>
```

**`--disallowedTools Bash`가 핵심 안전장치다.** 자동 세션에 셸을 주지 않으면 예측 못 한 명령이 실행될 여지가 사라진다. 편집만 시키고 git/PR은 앱이 한다.

**규칙 주입**: `RULES.md`를 `%LOCALAPPDATA%\GrassKeeper\rules\RULES.md`에 캐시하고(하루 1회 갱신, 실패 시 캐시 사용) `--append-system-prompt`로 넘긴다. 대상 레포에 `CLAUDE.md`를 심는 방식은 커밋에 섞일 위험이 있어 쓰지 않는다.

> **`raw.githubusercontent.com`을 쓰지 않는다.** 비인증 요청이라 실제로 `HTTP 429 Too Many Requests`에 걸린다(구현 중 재현됨). 이미 인증된 `gh`가 있으므로 `gh api repos/<owner>/coding-rules/contents/RULES.md -H "Accept: application/vnd.github.raw"`로 받는다. 레이트리밋이 넉넉해지고 비공개 규칙 레포도 그대로 동작한다.

`--output-format json`의 `result` 텍스트는 PR 본문으로 재활용한다.

### 4. 변경 검사
`git status --porcelain`이 비어 있으면 브랜치를 지우고 스킵한다. **빈 PR을 만들지 않는다.**

### 5. 커밋
Conventional Commits. 제목은 claude 결과 요약의 첫 줄에서 뽑고, 못 뽑으면 `chore: automated maintenance`로 폴백.

### 6. push + PR
`git push -u origin <branch>` → `gh pr create`. PR URL을 캡처해 알림과 로그에 남긴다.

**PR은 대상 레포의 팀 컨벤션을 그대로 따른다.** 자동으로 올라간 티가 나는 PR은 리뷰가 안 된다.

양식을 앱이 정하지 않는다. **작업 사본에서 그 레포의 PR 템플릿을 읽어** claude에게 "이걸 채워라"로 넘긴다. 이미 클론해둔 디렉토리에서 읽으므로 API를 더 부르지 않는다.

```
.github/pull_request_template.md
.github/PULL_REQUEST_TEMPLATE.md
docs/pull_request_template.md
pull_request_template.md
```

먼저 찾은 것을 쓰고, 없으면 기본 양식으로 떨어진다. 그래서 가벼운 팀(`Summary`/`Tasks`/`To Reviewer`)이든 무거운 팀(`관련 이슈`/`구현 방법`/`테스트 체크리스트`/`주의사항`)이든 각자 형식대로 나간다.

| 항목 | 형식 | 예 |
|---|---|---|
| 제목 | `Type: 한국어 한 줄` | `Fix: Safari input 포커스 시 자동 확대 방지` |
| 브랜치 | `type/kebab-slug/#이슈` | `fix/input-focus-auto-zoom/#105` |
| 본문 | 대상 레포 템플릿 | 빈 섹션은 삭제, 체크박스는 사실인 것만 |

claude 응답 끝의 `TITLE:` / `BRANCH:` / `ISSUE:` / `BODY:` 표식을 파싱한다. 형식이 어긋나면 막지 않고 안전한 기본값(`Chore: 저장소 정리`, `chore/<날짜>-improve`)으로 메운다 — 개선 자체는 이미 끝난 뒤라 여기서 버리면 손해다.

브랜치 이름은 **무엇을 고쳤는지 알아야 정해지므로** 임시 이름(`wip/<난수>`)으로 만들어 작업하고 커밋 직전에 바꾼다. 같은 이름이 원격에 있으면 `-2`, `-3`을 붙인다.

## 이슈

사람이 올린 PR은 이슈에서 출발한다. 자동 PR도 그렇게 만든다. GUI 드롭다운으로 고른다.

| 모드 | 동작 |
|---|---|
| **이슈 우선** (기본) | 열린 이슈가 있으면 그것을 해결한다. 없으면 스스로 찾아 고친 뒤 **이슈를 만들고** PR을 건다 |
| **이슈 있을 때만** | 열린 이슈가 없으면 그 레포는 건너뛴다. 내가 시킨 것만 하게 하는 모드 |
| **이슈 없이** | 이슈를 보지도 만들지도 않는다 |

- 오래된 이슈부터 고른다. `issueLabel`을 설정하면 그 라벨이 붙은 것만 본다.
- **열린 PR이 이미 물고 있는 이슈는 건너뛴다.** 브랜치 끝의 `/#번호`로 판별한다. 없으면 한 이슈에 매일 PR이 쌓인다.
- 이슈 생성은 개선이 끝난 **뒤에** 한다. 무엇을 고쳤는지 알아야 이슈를 제대로 쓸 수 있다. `ISSUE:` 표식의 다음 줄부터 `BODY:` 전까지가 이슈 본문이다.
- 이슈가 너무 크면 독립적으로 의미 있는 한 조각만 처리하고 무엇을 남겼는지 PR에 적게 한다.
- **Dry-run은 이슈도 PR도 만들지 않는다.**

### 7. 알림 / 로그
트레이 풍선으로 성공·스킵·실패 + PR 링크. 로그는 `%APPDATA%\GrassKeeper\log\yyyy-MM-dd.log`.

**실패해도 브랜치를 지우지 않는다** — 원인 추적용으로 남긴다.

## 작업 프롬프트

ponytail과 `RULES.md`가 이미 주입되므로 프롬프트에는 **범위**만 담는다:

- 개선은 **딱 1건**. 여러 파일에 걸친 대공사 금지 (규칙 P0-5)
- 빌드/테스트 실행 금지 — 애초에 Bash가 막혀 있음
- git 조작 금지 — 앱이 한다
- 테스트가 없는 레포에서는 구조 리팩토링 대신 문서·주석·명명 개선 위주 (규칙 R-1: 안전망 없는 리팩토링 금지)
- 개선 유형은 GUI 드롭다운으로 선택: `자동 판단` / `문서·주석` / `리팩토링` / `버그 수정` / `테스트 추가`

## MainForm.cs — UI

한 화면에 전부 넣는다.

- **레포 목록** — `CheckedListBox`. `gh repo list` 결과를 채우고, archived/fork는 기본 해제
- **스케줄** — 실행 시각(`DateTimePicker`), 하루 처리할 레포 수(기본 1), 개선 유형, 모델 선택
- **버튼** — `지금 1회 실행` / `Dry-run` / `시작프로그램 등록·해제`
- **로그 창** — 실시간 출력. `Invoke` + `AppendText` 패턴
- **트레이** — 닫기 시 트레이로 최소화. 우클릭 메뉴: 열기 / 지금 실행 / 종료

**Dry-run**은 편집까지만 하고 `git diff`를 로그에 뿌린 뒤 push/PR을 하지 않는다. 첫 검증에 반드시 쓴다.

## 자동 실행과 catch-up

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`에 `"<exe 경로>" --tray` 등록. GUI 버튼으로 토글하고, 관리자 권한은 필요 없다.

**노트북에서는 예약 시각에 컴퓨터가 꺼져 있는 경우가 많다.** 그래서 catch-up을 넣는다: 시작 시 `config.json`의 `lastRunDate`가 오늘이 아니고 예약 시각이 이미 지났으면, 부팅 5분 뒤에 밀린 실행을 자동으로 돌린다. 이게 없으면 잔디가 그냥 빈다.

## 설정 (`%APPDATA%\GrassKeeper\config.json`)

```json
{
  "githubUser": "nohseongmin",
  "enabledRepos": ["repo-a", "repo-b"],
  "scheduleTime": "22:00",
  "reposPerDay": 1,
  "improvementType": "auto",
  "model": "sonnet",
  "lastRunDate": "2026-08-17",
  "runAtStartup": true,
  "catchUpDelayMinutes": 5,
  "issueMode": "prefer",
  "issueLabel": "",
  "rulesRepo": "",
  "claudePath": ""
}
```

`githubUser`는 비워두면 `gh`에서 자동으로 채운다. `rulesRepo`를 비우면 `<githubUser>/coding-rules`를, `claudePath`를 비우면 PATH에서 찾은 claude를 쓴다. `issueMode`는 `prefer` / `only` / `none`이고, `issueLabel`을 비우면 열린 이슈 전부가 대상이다. 손으로 고쳐 넣은 값이 못 쓰는 값이면 로그에 남기고 기본값으로 되돌린다.

## 알려진 이슈

**ponytail 설치가 Windows에서 실패한다.** `claude plugin install ponytail@ponytail` 실행 시:

```
EPERM: operation not permitted, rename
'...\.claude\plugins\cache\ponytail' -> '...\.claude\plugins\cache\ponytail\ponytail\4.9.0'
```

마켓플레이스 클론 경로(`cache\ponytail`)를 자기 하위 경로로 rename하려다 Windows가 거부한다. 마켓플레이스 등록(`claude plugin marketplace add`) 자체는 성공하므로, 플러그인 설치만 실패한 상태다. 우회 방법은 SETUP.md 참고.

## 짚고 갈 점

- **빌드 검증 없이 PR이 올라간다.** `--disallowedTools Bash` 때문에 자동 세션은 컴파일 확인을 못 한다. 이건 규칙 **G-5**("실행 가능한 검증을 선호하라")와 정면으로 충돌하는 지점이다. PR 승인이 사람 손에 있어서 기본 브랜치는 안전하지만, **깨진 코드가 PR로 올라올 수 있다는 걸 전제하고 리뷰해야 한다.** 나중에 레포별 "빌드 검증" 옵션(앱이 직접 `dotnet build` / `npm run build`를 실행하고 실패 시 PR을 만들지 않음)을 붙일 수 있도록 Runner를 열어둔다.
- **규칙 V-3의 "push only when asked"** 와 자동 push는 충돌하는 것처럼 보이지만, 이 앱을 켜는 행위 자체가 사전 승인에 해당한다. 대신 그 승인이 **브랜치까지만** 미치도록 기본 브랜치 푸시 경로를 코드에서 배제한다.
- **소재 고갈.** 계정에 레포는 충분히 있지만(37개), 활성 레포 몇 개만 켜두면 몇 주 안에 개선할 게 마른다. 대상을 넓게 켜고 하루 1건 페이스를 권한다.

## 구현하며 설계에서 바뀐 것

설계 문서만 보고 착수했을 때 실제로 막혔던 지점들. 전부 검증하고 반영했다.

| 설계 | 실제 | 이유 |
|---|---|---|
| `claude`를 그냥 실행 | `node.exe cli.js` | npm 설치본은 `.exe`가 없고, `cmd.exe` 우회는 프롬프트를 깨뜨린다 |
| `raw.githubusercontent.com`에서 규칙 수신 | `gh api ... vnd.github.raw` | 비인증 요청이 `HTTP 429`에 걸린다 |
| `gh api user`로 계정명 조회 | `gh api graphql {viewer{login}}` | 이 계정에서 REST `/user`가 `HTTP 503`을 돌려준다 |
| 파일 6개 | Config.cs가 `Paths`·`Log`도 함께 소유 | 파일 수를 늘리지 않으려고 인프라를 한 곳에 모았다 |

로그는 **UTF-8 BOM**으로 쓴다. BOM이 없으면 PowerShell 5.1의 `Get-Content`가 ANSI로 읽어 한글 로그가 깨진다(재현 확인).

## 남은 것

- **`claude` CLI 로그인.** 자동 세션이 도는 전제 조건인데 아직 안 되어 있다. SETUP.md 참고.
- **빌드 검증 옵션.** 위 "짚고 갈 점" 첫 항목. Runner는 열어뒀고 아직 안 붙였다.
