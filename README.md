# WinScreen

Windows용 GNU Screen 스타일 터미널 멀티플렉서

## 개요

WinScreen은 Linux의 `screen` 명령어와 유사한 UX를 Windows에서 제공합니다.
세션을 생성하고, detach하고, 나중에 다른 터미널에서 다시 attach할 수 있습니다.

## 특징

- **screen 스타일 UX**: `-r`로 세션 선택, `Ctrl+A, D`로 detach
- **세션 유지**: 터미널을 닫아도 세션이 유지됨
- **프로필 시스템**: CMD, PowerShell, Conda, Git Bash, WSL 등 다양한 쉘 지원
- **스크롤백 버퍼**: 재연결 시 이전 출력 복원
- **자동 서버 시작**: 클라이언트 실행 시 서버 자동 시작

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
dotnet publish src\WinScreen.Server -c Release -r win-x64 --self-contained -o publish
dotnet publish src\WinScreen.Client -c Release -r win-x64 --self-contained -o publish
```

### 설치

`publish\win-x64` 폴더를 PATH에 추가하거나, 원하는 위치로 복사하세요.

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

# 세션 종료
screen -X kill mywork

# 모든 세션 종료
screen -wipe

# 사용 가능한 프로필 보기
screen --profiles
```

### 키 바인딩 (세션 연결 중)

| 키 조합 | 동작 |
|---------|------|
| `Ctrl+A, D` | 세션에서 분리 (detach) |
| `Ctrl+A, K` | 세션 종료 |
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

## 프로필

프로필은 `%LOCALAPPDATA%\WinScreen\profiles.json`에 저장됩니다.

### 기본 제공 프로필

| 이름 | 설명 |
|------|------|
| `default` | Windows 명령 프롬프트 (cmd.exe) |
| `powershell` | Windows PowerShell |
| `pwsh` | PowerShell Core |
| `conda` | Miniconda/Anaconda (자동 감지) |
| `gitbash` | Git Bash (자동 감지) |
| `wsl` | Windows Subsystem for Linux |

### 커스텀 프로필 추가

`profiles.json`을 직접 편집하여 프로필을 추가할 수 있습니다:

```json
{
  "name": "myenv",
  "description": "My Python Environment",
  "shell": "cmd.exe",
  "startupCommand": "conda activate myenv",
  "workingDirectory": "C:\\Projects",
  "environment": {
    "PYTHONPATH": "C:\\mylibs"
  }
}
```

## 제한사항

- Windows 10 1809 이상 필요 (ConPTY API)
- 한 번에 하나의 클라이언트만 세션에 연결 가능
- 프로세스 fork가 불가능하므로 Linux screen의 일부 기능 미지원

## 트러블슈팅

### 서버가 시작되지 않음
- `winscreen-server.exe`가 같은 폴더에 있는지 확인
- 관리자 권한이 필요할 수 있음

### 세션이 즉시 종료됨
- 프로필의 shell 경로가 올바른지 확인
- `screen --profiles`로 감지된 프로필 확인

### Ctrl+A가 작동하지 않음
- Windows Terminal 또는 ConHost 사용 권장
- 일부 터미널 에뮬레이터는 Ctrl+A를 가로챌 수 있음

## 라이선스

MIT License

## 참고

- [Windows Pseudo Console (ConPTY)](https://devblogs.microsoft.com/commandline/windows-command-line-introducing-the-windows-pseudo-console-conpty/)
- [GNU Screen](https://www.gnu.org/software/screen/)
