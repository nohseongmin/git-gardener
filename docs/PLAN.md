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

**규칙 주입**: `RULES.md`를 `raw.githubusercontent.com`에서 받아 `%LOCALAPPDATA%\GrassKeeper\rules\RULES.md`에 캐시하고(하루 1회 갱신, 실패 시 캐시 사용) `--append-system-prompt`로 넘긴다. 대상 레포에 `CLAUDE.md`를 심는 방식은 커밋에 섞일 위험이 있어 쓰지 않는다.

`--output-format json`의 `result` 텍스트는 PR 본문으로 재활용한다.

### 4. 변경 검사
`git status --porcelain`이 비어 있으면 브랜치를 지우고 스킵한다. **빈 PR을 만들지 않는다.**

### 5. 커밋
Conventional Commits. 제목은 claude 결과 요약의 첫 줄에서 뽑고, 못 뽑으면 `chore: automated maintenance`로 폴백.

### 6. push + PR
`git push -u origin <branch>` → `gh pr create`. PR URL을 캡처해 알림과 로그에 남긴다.

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
  "catchUpDelayMinutes": 5
}
```

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
- **소재 고갈.** 계정에 레포는 충분히 있지만(36개), 활성 레포 몇 개만 켜두면 몇 주 안에 개선할 게 마른다. 대상을 넓게 켜고 하루 1건 페이스를 권한다.
