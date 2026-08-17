# GrassKeeper

> 매일 기존 프로젝트를 자동으로 조금씩 개선하고 PR을 올리는 Windows 트레이 앱.

부팅과 동시에 트레이에 상주하다가, 정해진 시각에 대상 레포를 하나 골라 **Claude Code 헤드리스 세션**으로 작은 개선을 만들고 브랜치를 파서 **PR까지 올린다.** 사람이 하는 일은 GitHub에서 **PR 승인/머지** 하나뿐이다.

## 왜

현업과 병행하면서 매일 손으로 레포를 손보고 커밋하는 게 현실적으로 어렵다. 반복 가능한 부분(레포 고르기 → 개선 → 브랜치 → 커밋 → PR)을 자동화하고, **판단이 필요한 마지막 단계만 사람이 잡는다.**

## 원칙

- **가짜 커밋은 만들지 않는다.** 빈 커밋, 공백 수정, 타임스탬프 갱신 같은 잔디용 트릭은 쓰지 않는다. 실제 diff가 없으면 그날은 PR 없이 건너뛴다.
- **main은 건드리지 않는다.** 기본 브랜치에 직접 커밋하는 경로가 코드에 아예 없다. 항상 `auto/improve-*` 브랜치.
- **작업 사본에서만 돈다.** 로컬에서 직접 개발 중인 레포 폴더는 절대 건드리지 않고, `%LOCALAPPDATA%\GrassKeeper\repos\` 아래 별도 사본에서만 작업한다.
- **셸을 주지 않는다.** 자동 세션에는 파일 편집 도구만 허용하고 Bash는 차단한다. git/PR 조작은 전부 앱이 한다.

## 파이프라인

```
스케줄 발화 (또는 부팅 후 catch-up)
   ↓
대상 레포 선택 (가장 오래 안 건드린 것부터)
   ↓
작업 사본 동기화  git clone / fetch + reset --hard
   ↓
브랜치 생성  auto/improve-yyyyMMdd-HHmm
   ↓
Claude 헤드리스 실행  ← 코딩 규칙 + ponytail 룰셋 주입, 편집 도구만 허용
   ↓
변경 있나?  ── 없음 ──→ 브랜치 삭제, 스킵 (PR 만들지 않음)
   ↓ 있음
커밋 → push → gh pr create
   ↓
트레이 알림 + PR 링크
```

## 코딩 규칙

자동 세션은 [nohseongmin/coding-rules](https://github.com/nohseongmin/coding-rules)의 `RULES.md`를 시스템 프롬프트로 주입받는다. 특히 이 규칙들이 자동화의 핵심 제약이다:

| 규칙 | 자동화에서의 의미 |
|---|---|
| **P0-5** 최소 변경 | 요청 범위 밖 리팩토링 금지. 하루 개선 1건으로 제한하는 근거 |
| **P0-2** 코드베이스에 맞춰라 | 주변 스타일을 따라가야 PR이 튀지 않음 |
| **V-3** 브랜치 분리 | 기본 브랜치 직접 푸시 금지 |
| **R-1** 안전망 있는 리팩토링 | 테스트 없는 레포에서는 구조 변경보다 문서/주석 위주로 |
| **V-1** 작고 집중된 커밋 | Conventional Commits, 한 커밋 = 한 논리 변경 |

여기에 [ponytail](https://github.com/DietrichGebert/ponytail) 미니멀리즘 룰셋이 더해져 과잉 엔지니어링을 막는다.

## 스택

C# / .NET 9 / WinForms, **NuGet 의존성 0개**. 단일 실행 파일로 배포.

```
dotnet publish src/GrassKeeper -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

외부 도구로 `git`, [`gh`](https://cli.github.com/), [`claude`](https://claude.com/claude-code) CLI를 호출한다.

## 상태

**구현 완료 / 실사용 검증 전.**

- 앱은 빌드·기동·레포 로드·UI까지 확인했다.
- claude 개선 단계는 **아직 못 돌려봤다.** CLI 로그인이 필요하다 → [docs/SETUP.md](docs/SETUP.md)
- 첫 실행은 반드시 **Dry-run**부터. 자동 push가 붙어 있다.

설계 문서는 [docs/PLAN.md](docs/PLAN.md), 개발 환경 세팅은 [docs/SETUP.md](docs/SETUP.md) 참고.
