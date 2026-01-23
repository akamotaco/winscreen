# WinScreen

Windows용 GNU Screen 스타일 터미널 멀티플렉서

## 개요

WinScreen은 Linux의 `screen` 명령어와 유사한 UX를 Windows에서 제공합니다.
세션을 생성하고, detach하고, 나중에 다른 터미널에서 다시 attach할 수 있습니다.

## 특징

- **screen 스타일 UX**: `-r`로 세션 선택, `Ctrl+A, D`로 detach
- **멀티 윈도우**: 한 세션 내에서 여러 독립적인 터미널 윈도우 지원 (`Ctrl+A, C`로 생성, `Ctrl+A, N/P`로 전환)
- **세션 유지**: 터미널을 닫아도 세션이 유지됨
- **프로필 시스템**: CMD, PowerShell, Conda, Git Bash, WSL 등 다양한 쉘 지원
- **스크롤백 버퍼**: 재연결 시 이전 출력 복원
- **자동 서버 시작**: 클라이언트 실행 시 서버 자동 시작
- **서버 관리**: 서버 상태 확인 및 종료 기능

## 아키텍처

```
┌─────────────────────────────────────────────────────────────────┐
│                        WinScreen Server                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐             │
│  │  Session 1  │  │  Session 2  │  │  Session 3  │             │
│  │   (ConPTY)  │  │   (ConPTY)  │  │   (ConPTY)  │             │
│  │  cmd.exe    │  │ powershell  │  │  conda env  │             │
│  └─────────────┘  └─────────────┘  └─────────────┘             │
│              │              │              │                    │
│              └──────────────┴──────────────┘                    │
│                          │                                      │
│                   Named Pipe (IPC)                              │
└─────────────────────────│───────────────────────────────────────┘
                          │
          ┌───────────────┼───────────────┐
          │               │               │
    ┌─────┴─────┐   ┌─────┴─────┐   ┌─────┴─────┐
    │ Terminal 1│   │ Terminal 2│   │ Terminal 3│
    │  (client) │   │  (client) │   │  (client) │
    └───────────┘   └───────────┘   └───────────┘
```

## 빌드

### 요구사항
- .NET 8.0 SDK
- Windows 10 1809 이상 (ConPTY 지원)

### 빌드 방법

```batch
cd WinScreen
build.bat
```

또는 수동으로:

```batch
dotnet publish src\WinScreen.Server -c Release -r win-x64 --self-contained -o publish\win-x64
dotnet publish src\WinScreen.Client -c Release -r win-x64 --self-contained -o publish\win-x64
```

### 설치

빌드 후 `publish\win-x64` 폴더에 `screen.exe`와 `winscreen-server.exe`가 생성됩니다.

#### 방법 1: PATH 환경 변수에 추가 (권장)

시스템 어디서나 `screen` 명령어를 사용하려면 PATH에 추가하세요:

1. **Windows 설정** → **시스템** → **정보** → **고급 시스템 설정**
2. **환경 변수** 클릭
3. **사용자 변수** 또는 **시스템 변수**에서 `Path` 선택 후 **편집**
4. **새로 만들기** 클릭 후 `publish\win-x64` 폴더의 전체 경로 입력
   - 예: `C:\Tools\WinScreen\publish\win-x64`
5. **확인**으로 저장 후 새 터미널 창 열기

또는 PowerShell에서 사용자 PATH에 추가:
```powershell
$path = "C:\Tools\WinScreen\publish\win-x64"
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";$path", "User")
```

#### 방법 2: 원하는 위치로 복사

`screen.exe`와 `winscreen-server.exe`를 이미 PATH에 등록된 폴더로 복사합니다.

## 사용법

### 기본 명령어

```batch
# 새 세션 생성 및 연결
screen

# 이름 있는 세션 생성
screen -S mywork

# 특정 프로필로 세션 생성
screen -p powershell
screen -p conda

# 세션 목록 보기
screen -ls

# 세션에 연결 (이름 또는 ID)
screen -r mywork
screen -r abc12345

# detached 세션이 하나면 자동 연결
screen -r

# 세션에 연결 또는 없으면 새로 생성 (-R)
screen -R
screen -R mywork

# 다른 클라이언트가 연결 중인 세션 강제 재연결 (-d -r)
screen -d -r mywork

# 세션 종료
screen -X kill mywork

# 모든 세션 종료
screen -X kill-all

# 사용 가능한 프로필 보기
screen --profiles
```

### 서버 관리

```batch
# 서버 상태 확인
screen --server

# 서버 (재)시작 (실행 중이면 종료 후 시작)
screen --server-start

# 서버 종료 (모든 세션도 종료됨)
screen --server-stop
```

### -r vs -R vs -d -r 옵션

| 옵션 | 세션 없음 | 세션 1개 (detached) | 세션 1개 (attached) |
|------|-----------|---------------------|---------------------|
| `-r` | 에러 | 자동 연결 | 에러 (이미 연결됨) |
| `-R` | 새로 생성 | 자동 연결 | 에러 (이미 연결됨) |
| `-d -r` | 에러 | 자동 연결 | 강제 재연결 (기존 클라이언트 분리) |

### 키 바인딩 (세션 연결 중)

| 키 조합 | 동작 |
|---------|------|
| `Ctrl+A, D` | 세션에서 분리 (detach) |
| `Ctrl+A, C` | 새 윈도우 생성 |
| `Ctrl+A, K` | 현재 윈도우 종료 |
| `Ctrl+A, N` | 다음 윈도우로 이동 |
| `Ctrl+A, P` | 이전 윈도우로 이동 |
| `Ctrl+A, W` | 윈도우 목록 표시 |
| `Ctrl+A, 0-9` | 지정 번호의 윈도우로 이동 |
| `Ctrl+A, A` | 실제 Ctrl+A 전송 |
| `Ctrl+A, ?` | 도움말 표시 |

### 일반적인 워크플로우

```batch
# 1. 작업용 세션 생성
screen -S project1

# 2. 작업 수행...
# (긴 빌드나 서버 실행 등)

# 3. Ctrl+A, D로 detach
# (터미널 닫아도 됨)

# 4. 나중에 다른 터미널에서 재연결
screen -r project1

# 5. 이전 상태 그대로 계속 작업
```

### 멀티 윈도우 워크플로우

한 세션 내에서 여러 독립적인 터미널 윈도우를 사용할 수 있습니다.

```batch
# 1. 세션 생성 (첫 번째 윈도우 자동 생성)
screen -S mywork

# 2. 새 윈도우 생성 (Ctrl+A, C)
# 이제 윈도우 0과 윈도우 1이 있음

# 3. 윈도우 간 이동
# Ctrl+A, N → 다음 윈도우
# Ctrl+A, P → 이전 윈도우
# Ctrl+A, 0 → 윈도우 0으로 이동
# Ctrl+A, 1 → 윈도우 1로 이동

# 4. 윈도우 목록 확인 (Ctrl+A, W)
# --- Windows ---
#  *0 window-0
#   1 window-1
# ---------------

# 5. 현재 윈도우 종료 (Ctrl+A, K 또는 exit)
# 마지막 윈도우가 종료되면 세션도 종료됨

# 6. Detach 후에도 모든 윈도우 상태 유지
# Ctrl+A, D로 detach 후 screen -r로 재연결하면
# 모든 윈도우가 그대로 유지됨
```

## 프로필

프로필은 `%LOCALAPPDATA%\WinScreen\profiles.json`에 저장됩니다.

### 기본 제공 프로필

| 이름 | 설명 |
|------|------|
| `cmd` | Windows 명령 프롬프트 (기본) |
| `powershell` | Windows PowerShell |
| `pwsh` | PowerShell Core |
| `conda` | Miniconda/Anaconda (자동 감지) |
| `gitbash` | Git Bash (자동 감지) |
| `wsl` | Windows Subsystem for Linux |

### 기본 프로필 설정

프로필 없이 세션을 생성하면 기본 프로필이 사용됩니다. 기본 프로필은 변경할 수 있습니다:

```batch
# 현재 기본 프로필 확인
screen --default

# 기본 프로필 변경
screen --set-default powershell
```

### 프로필 관리 명령어

```batch
# 프로필 목록 보기
screen --profiles

# 프로필 상세 정보
screen --profile-show conda

# 새 프로필 추가
screen --profile-add myenv --shell cmd.exe --startup "conda activate myenv" --desc "My Conda Environment"

# 프로필 삭제
screen --profile-remove myenv

# 프로필 초기화 (기본값으로 리셋)
screen --profile-reset
```

### 프로필 추가 옵션

| 옵션 | 설명 |
|------|------|
| `--shell <path>` | 쉘 실행 파일 경로 (필수) |
| `--args <args>` | 쉘 인자 |
| `--startup <cmd>` | 시작 시 실행할 명령어 |
| `--desc <text>` | 프로필 설명 |

> **Conda/Miniconda 사용자 참고**: `cmd.exe`에서 `conda activate`를 사용하려면
> `--args "/K"` 옵션이 필요합니다. `/K` 옵션은 명령 실행 후 쉘을 유지합니다.
>
> 예시: `screen --profile-add myconda --shell cmd.exe --args "/K" --startup "conda activate myenv"`

### JSON으로 프로필 편집

`profiles.json`을 직접 편집하여 더 복잡한 프로필을 추가할 수 있습니다:

```json
{
  "defaultProfile": "cmd",
  "maxScrollbackSizeKB": 2048,
  "profiles": [
    {
      "name": "cmd",
      "description": "Command Prompt",
      "shell": "cmd.exe"
    },
    {
      "name": "myenv",
      "description": "My Python Environment",
      "shell": "cmd.exe",
      "arguments": "/K",
      "startupCommand": "conda activate myenv",
      "workingDirectory": "C:\\Projects",
      "environment": {
        "PYTHONPATH": "C:\\mylibs"
      }
    }
  ]
}
```

| 설정 | 기본값 | 설명 |
|------|--------|------|
| `defaultProfile` | `"cmd"` | 기본 프로필 이름 |
| `maxScrollbackSizeKB` | `1024` | 스크롤백 버퍼 최대 크기 (KB) |

## 제한사항

- Windows 10 1809 이상 필요 (ConPTY API)
- 한 번에 하나의 클라이언트만 세션에 연결 가능
- **화면 분할 미지원**: GNU Screen의 `Ctrl+a S` (수평 분할), `Ctrl+a |` (수직 분할) 등은 지원하지 않음
  - 멀티 윈도우는 지원되며, `Ctrl+a c`로 새 윈도우 생성 후 `Ctrl+a n/p`로 전환 가능
- 프로세스 fork가 불가능하므로 Linux screen의 일부 기능 미지원
- 스크롤백 버퍼: 기본 1MB (설정 가능)
  - `profiles.json`에서 `maxScrollbackSizeKB` 값 변경 가능
  - 긴 출력이 있는 세션에서 re-attach 시 최근 내용만 복원됩니다
  - 중요한 출력은 파일로 리다이렉트하세요: `command > output.log`

## 주의사항

- **서버 종료 시 세션 손실**: WinScreen 서버가 종료되면 모든 세션이 함께 종료됩니다
  - 중요한 작업은 정기적으로 저장하세요
  - `screen --server-stop` 전에 세션을 확인하세요
- **Nested Session**: 이미 screen 세션 안에서 `screen`을 실행하면 경고가 표시됩니다
  - `screen -m` 옵션으로 강제로 새 세션을 생성할 수 있습니다
- **세션 이름 중복**: 같은 이름의 세션을 생성하면 경고가 표시됩니다
  - 세션은 생성되지만 `-r` 사용 시 ID로 구분해야 할 수 있습니다

## 트러블슈팅

### 서버가 시작되지 않음
- `winscreen-server.exe`가 같은 폴더에 있는지 확인
- `screen --server`로 서버 상태 확인
- 관리자 권한이 필요할 수 있음

### 세션이 즉시 종료됨
- 프로필의 shell 경로가 올바른지 확인
- `screen --profiles`로 감지된 프로필 확인

### Ctrl+A가 작동하지 않음

일부 터미널에서는 `Ctrl+A`가 "전체 선택" 등 다른 기능에 바인딩되어 있어 WinScreen이 키 입력을 받지 못할 수 있습니다.

#### Windows Terminal

1. 설정 열기: `Ctrl+,` 또는 탭 옆 드롭다운 → 설정
2. 좌측 하단 **JSON 파일 열기** 클릭 (또는 `Ctrl+Shift+,`)
3. `actions` 배열에 다음 추가:
   ```json
   { "keys": "ctrl+a", "command": "unbound" }
   ```
4. 저장 후 Windows Terminal 재시작

#### 기본 명령 프롬프트 (ConHost)

기본 명령 프롬프트에서는 `Ctrl+A`가 별도 기능에 바인딩되어 있지 않아 정상 작동합니다.

#### 기타 터미널 에뮬레이터

- **Cmder/ConEmu**: Settings → Keys & Macro에서 Ctrl+A 바인딩 해제
- **Hyper**: `.hyper.js` 설정에서 keymaps 수정
- **Git Bash (mintty)**: 기본적으로 Ctrl+A가 readline(줄 처음 이동)에 바인딩되어 있으나, WinScreen에서는 raw 모드로 동작하여 정상 작동함

### 빠른 타이핑 시 출력 누락
- 최신 버전으로 업데이트 (stdout 동기화 개선됨)

## 라이선스

MIT License

## 참고

- [Windows Pseudo Console (ConPTY)](https://devblogs.microsoft.com/commandline/windows-command-line-introducing-the-windows-pseudo-console-conpty/)
- [GNU Screen](https://www.gnu.org/software/screen/)
