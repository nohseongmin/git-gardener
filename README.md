<p align="center">
  <img src="docs/assets/banner.png" alt="git gardener" width="820">
</p>

<p align="center">
  <strong>정원사는 매일 조금씩 돌본다</strong>
</p>

<p align="center">
  트레이에 상주하다가 방치된 레포를 골라 이슈를 열고, 고치고, PR까지 올린다.<br>
  사람이 하는 일은 <strong>머지 버튼 하나.</strong><br>
  빈 커밋도 공백 수정도 쓰지 않는다. 고칠 게 없으면 그날은 그냥 넘어간다.
</p>

<p align="center">
  <a href="#license"><img src="https://img.shields.io/badge/license-MIT-green?style=flat" alt="MIT"></a>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat" alt=".NET 9">
  <img src="https://img.shields.io/badge/requires-git%20%C2%B7%20gh%20%C2%B7%20claude-orange?style=flat" alt="git, gh, claude CLI 필요">
  <img src="https://img.shields.io/badge/platform-Windows-0078D4?style=flat" alt="Windows">
</p>

<p align="center">
  <a href="#see-it">See it</a> ·
  <a href="#install">Install</a> ·
  <a href="#이슈에서-출발한다">이슈</a> ·
  <a href="#안전장치">안전장치</a> ·
  <a href="#한계">한계</a> ·
  <a href="docs/PLAN.md">설계</a> ·
  <a href="docs/SETUP.md">세팅</a>
</p>

---

## See it

아무것도 안 시켰는데 [daily-tangle](https://github.com/nohseongmin/daily-tangle)에 이게 올라왔다.

<table>
<tr>
<th width="50%">📮 이슈 #1 — 문제를 먼저 연다</th>
<th width="50%">🔧 PR #2 — 그걸 닫는다</th>
</tr>
<tr>
<td valign="top">

**recompute의 지역변수 이름이 실제 값과 맞지 않음**

> `recompute()`에서 이전 교차 쌍 수를 담는 변수 이름이 `wasClear`였다. 값은 숫자(카운트)인데 이름은 불리언처럼 보여, 코드베이스의 "불리언은 is/has/should" 규칙과 어긋나고 `if (r.pairs < wasClear)` 같은 비교식을 읽을 때 오해를 유발한다.

</td>
<td valign="top">

**Refactor: 교차 수 카운트 변수의 오해 소지 있는 불리언식 이름 정정**

```diff
-  const wasClear = state.crossPairs;
+  const prevPairs = state.crossPairs;
-  else if (r.pairs < wasClear) SFX.clear();
+  else if (r.pairs < prevPairs) SFX.clear();
```

브랜치 `refactor/rename-prev-pairs/#1`

</td>
</tr>
</table>

이슈를 여는 것부터 PR이 올라가기까지 **49초.** 커밋 작성자에 봇 표시도, 본문에 "자동 생성됨" 푸터도 없다. 그 레포의 PR 템플릿을 읽어서 채우기 때문에 사람이 올린 PR과 같은 모양이 나온다.

## Install

### 먼저, 이건 혼자 도는 프로그램이 아니다

세 개의 외부 CLI를 부르는 껍데기다. 셋 다 없으면 한 줄도 못 돈다.

| 필요한 것 | 왜 | 준비 |
|---|---|---|
| `git` | 클론·커밋·푸시 | `winget install Git.Git` + `user.name` / `user.email` 설정 |
| [`gh`](https://cli.github.com/) | 레포·이슈·PR 조회와 생성 | `gh auth login` **+ `gh auth setup-git`** |
| [`claude`](https://claude.com/claude-code) | 개선을 만드는 주체 | 설치 후 **대화형으로 띄워 `/login`** |

`gh`와 `claude`는 **사람이 직접 로그인해야 한다.** 자동화할 수 없고, 이 앱이 대신 해주지도 않는다. 인증 정보는 각 CLI가 소유하고 앱은 손대지 않는다.

확인:

```bash
gh auth status
claude -p "reply with OK only" --output-format json   # is_error: false 여야 한다
```

설치 스크립트가 이 셋을 검사하고, 빠진 게 있으면 설치 명령을 알려주고 멈춘다.

### 실행 파일만 받아서 (권장)

[Releases](https://github.com/nohseongmin/git-gardener/releases/latest)에서 `GitGardener.exe`를 받는다. .NET SDK가 필요 없다.

받은 자리에서 그냥 실행해도 되지만, 로그온마다 자동으로 뜨게 하려면 같이 받은 `install.ps1`에 넘긴다.

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -SourceExe .\GitGardener.exe
```

### 소스에서 빌드해서

```bash
git clone https://github.com/nohseongmin/git-gardener && cd git-gardener
```

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

### 설치 스크립트가 하는 일

필요한 도구(`git` · [`gh`](https://cli.github.com/) 인증 · [`claude`](https://claude.com/claude-code) 로그인 · 빌드할 때만 .NET 9 SDK)를 확인하고, `%LOCALAPPDATA%\GitGardener\bin`에 넣고, 로그온 시 자동 실행을 걸고, 트레이에 띄운다.

`-NoStartup`이면 자동 실행을 건너뛰고, `-Uninstall`이면 되돌린다. 설정과 로그는 남는다.

그다음은 트레이 아이콘을 더블클릭해 대상 레포를 고르고 **Dry-run** 먼저 — 자동 push가 붙어 있어서 `지금 1회 실행`은 진짜로 이슈와 PR을 만든다.

### 자동 세션용 claude 로그인 (한 번만)

자동 세션은 **사용자의 `~/.claude` 와 따로 노는 전용 설정 폴더**를 쓴다. 그래서 거기에 한 번 로그인해줘야 한다.

```powershell
$env:CLAUDE_CONFIG_DIR = "$env:LOCALAPPDATA\GitGardener\claude"
claude          # 떠 있는 창에서 /login
```

<details>
<summary><strong>왜 따로 쓰나 — 안 그러면 쓰던 Claude Code 가 401 을 맞는다</strong></summary>

Claude Code 는 OAuth 토큰을 `~/.claude/.credentials.json` 에 두고 만료되면 갱신한다. 갱신은 옛 토큰을 무효로 만들고 파일을 덮어쓴다.

자동 세션이 같은 파일을 쓰면 이 갱신이 사용자 몫의 토큰까지 갈아치운다. 대화형 Claude Code 는 메모리에 옛 토큰을 들고 있으므로, 그다음 요청에서 `API Error 401` 을 맞는다. 자동 실행이 도는 동안 쓰던 창이 죽는 것이다.

실제로 자동 실행 시작 3초 뒤에 자격증명 파일이 다시 쓰이는 것을 확인했다. 저장소를 나눠 갖는 것으로 끊는다.

같이 쓰고 싶으면 `config.json` 의 `separateClaudeConfig` 를 `false` 로 두면 된다. 401 은 감수해야 한다.

</details>

<details>
<summary><strong>"안전하지 않은 앱" 경고가 뜬다면</strong></summary>

실행 파일에 코드 서명이 없다. 인증서는 발급 비용이 들고 개인에게는 발급 조건도 까다로워서 붙이지 않았다.

경고 자체는 서명보다 **Mark of the Web** 때문에 뜬다. 인터넷에서 받은 파일에 Windows가 붙이는 표식이고, SmartScreen이 그걸 보고 평판을 조회한 뒤 알려진 파일이 아니면 막는다. 설치 스크립트가 이 표식을 떼므로 스크립트로 설치하면 경고를 보지 않는다.

받은 실행 파일을 직접 실행하고 싶으면 표식만 떼면 된다.

```powershell
Unblock-File .\GitGardener.exe
```

파일 속성 창에서 아래쪽 "차단 해제"를 체크해도 같다.

소스에서 빌드하면 애초에 이 표식이 붙지 않아 경고가 없다.

</details>

<details>
<summary><strong>자동 실행에 시작 폴더를 쓰는 이유</strong></summary>

`HKCU\...\Run` 키를 쓰다가 옮겼다. 등록도 되어 있고 사용 안 함 플래그도 없는데 로그온 때 실행되지 않는 일이 있었다. 탐색기는 정상 시작했고 실행 파일도 멀쩡한데 프로세스만 뜨지 않았다.

예약 작업(`schtasks /SC ONLOGON`)은 관리자 권한을 요구해서 설치 과정에 넣을 수 없다. 시작 폴더는 권한 없이 되고 탐색기가 로그온마다 처리한다.

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\GitGardener.lnk
```

설치 스크립트와 앱 모두 예전 Run 키 항목이 남아 있으면 지운다. 둘 다 살아 있으면 로그온 때 두 번 뜬다.

</details>

<details>
<summary><strong>헤드리스 push가 인증창에서 멈춘다면</strong></summary>

Windows Git은 시스템 레벨에 Git Credential Manager만 깔아둔다. 트레이에서 도는 `git push`가 GUI 인증창을 띄우고 타임아웃까지 멈춘다.

```bash
gh auth setup-git --hostname github.com
```

`git config --global --get-regexp credential`에 `gh.exe auth git-credential`이 보이면 된다. 커밋 작성자(`user.name` / `user.email`)도 설정돼 있어야 한다.

</details>

## 이슈에서 출발한다

사람이 올린 PR은 이슈에서 시작한다. 자동 PR도 그렇게 만든다.

| 모드 | 동작 |
|---|---|
| **이슈 우선** (기본) | 열린 이슈가 있으면 그걸 해결한다. 없으면 스스로 찾아 고친 뒤 이슈를 만들고 `Closes`로 건다 |
| **이슈 있을 때만** | 이슈가 없으면 그 레포는 건너뛴다. 내가 시킨 것만 하게 하는 모드 |
| **이슈 없이** | 이슈를 보지도 만들지도 않는다 |

이슈를 하나 열어두면 다음 실행 때 그걸 물고 간다. 이미 열린 PR이 잡고 있는 이슈는 건너뛰므로 한 이슈에 PR이 쌓이지 않는다.

## 안전장치

- **가짜 커밋을 만들지 않는다.** 실제 diff가 없으면 브랜치를 지우고 그날은 넘어간다.
- **main을 건드리지 않는다.** 기본 브랜치에 직접 커밋하는 코드 경로가 없다.
- **작업 사본에서만 돈다.** 개발 중인 로컬 폴더는 손대지 않고 `%LOCALAPPDATA%\GitGardener\repos\`의 별도 사본에서만 작업한다.
- **셸을 주지 않는다.** 자동 세션에는 파일 편집 도구만 허용하고 Bash는 차단한다. git과 PR 조작은 전부 앱이 한다.
- **Dry-run은 아무것도 만들지 않는다.** 편집과 diff까지만 보여주고 이슈도 PR도 건드리지 않는다.

<details>
<summary><strong>주입되는 코딩 규칙</strong></summary>

자동 세션은 [coding-rules](https://github.com/nohseongmin/coding-rules)의 `RULES.md`와 [ponytail](https://github.com/DietrichGebert/ponytail) 미니멀리즘 룰셋을 시스템 프롬프트로 받는다. 이 중 넷이 자동화의 실질적 제약이다.

| 규칙 | 자동화에서의 의미 |
|---|---|
| **P0-5** 최소 변경 | 요청 범위 밖 리팩토링 금지. 한 번에 한 건으로 제한하는 근거 |
| **P0-2** 코드베이스에 맞춰라 | 주변 스타일을 따라가야 PR이 튀지 않는다 |
| **R-1** 안전망 있는 리팩토링 | 테스트 없는 레포에서는 구조 변경 대신 문서·주석 위주로 |
| **V-3** 브랜치 분리 | 기본 브랜치 직접 푸시 금지 |

규칙을 고치려면 git gardener가 아니라 coding-rules 레포를 고친다. 하루 한 번 받아서 캐시한다.

</details>

<details>
<summary><strong>파이프라인</strong></summary>

```
스케줄 발화 (또는 부팅 후 catch-up)
   ↓
대상 레포 선택 (가장 오래 안 건드린 것부터)
   ↓
열린 이슈 선택  ── 없으면 ──→ 스스로 고칠 것을 찾는다
   ↓
작업 사본 동기화  fetch + reset --hard + clean
   ↓
Claude 헤드리스 실행  ← 코딩 규칙 + 그 레포의 PR 템플릿 주입, 편집 도구만 허용
   ↓
변경 있나?  ── 없음 ──→ 브랜치 삭제, 스킵
   ↓ 있음
(이슈가 없었다면 여기서 이슈 생성)
   ↓
브랜치 확정  refactor/rename-prev-pairs/#1
   ↓
커밋 → push → PR 생성 (Closes #1)
   ↓
트레이 알림 + PR 링크
```

노트북이 꺼져 있어 예약 시각을 놓친 날은 부팅 5분 뒤에 밀린 실행을 따라잡는다. 이게 없으면 손이 아예 안 간다.

</details>

## 한계

**빌드 검증을 하지 않는다.** 자동 세션에는 셸이 없어서 컴파일도 테스트도 돌려보지 못한다. PR 승인이 사람 손에 있어 기본 브랜치는 안전하지만, **깨진 코드가 PR로 올라올 수 있다는 걸 전제로 리뷰해야 한다.** 문서나 이름 변경 같은 건 괜찮고, 로직을 건드린 PR은 머지 전에 직접 빌드해보는 게 맞다.

**소재는 마른다.** 활성 레포 몇 개만 켜두면 몇 주 안에 고칠 게 없어진다. 대상을 넓게 켜두는 편이 낫다.

## 스택

C# / .NET 9 / WinForms, 소스 6개 파일.

NuGet 패키지는 쓰지 않는다. `System.Text.Json`, `NotifyIcon`, 레지스트리 접근이 전부 런타임에 들어 있어서 복원할 것이 없고, 자체 포함 단일 실행 파일로 나간다.

다만 **패키지가 없다는 것과 의존성이 없다는 것은 다르다.** 이 앱이 하는 일의 대부분은 `git` · `gh` · `claude`를 부르는 것이고, 그중 둘은 사람이 로그인해둬야 동작한다. 무거운 쪽은 오히려 이쪽이다.

## License

[MIT](LICENSE)
