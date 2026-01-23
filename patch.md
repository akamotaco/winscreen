# WinScreen 버그 분석 및 개선 계획

## 요약

WinScreen은 ConPTY + Named Pipe 기반의 터미널 멀티플렉서로, 전체적인 구조는 적절하지만 몇 가지 구조적 문제와 버그가 있습니다.

---

## 1. 서버 이중 생성 문제

### 현상
- 여러 클라이언트가 동시에 시작되면 서버가 2개 이상 실행될 수 있음
- `screen -ls` 실행 시 번갈아가면서 응답함

### 원인 분석

**[WinScreen.Client/Program.cs:79-150]** `EnsureServerRunning()` 메서드:

```csharp
// 1. 서버 연결 시도 (500ms timeout)
await _pipe.ConnectAsync(500);

// 2. 실패하면 서버 시작
Process.Start(startInfo);

// 3. 연결 재시도 (100ms * 30회)
for (int i = 0; i < 30; i++)
{
    await Task.Delay(100);
    await _pipe.ConnectAsync(500);
}
```

**문제점:**
- 클라이언트 A와 B가 동시에 실행되면:
  1. A: 서버 연결 실패 → 서버 시작
  2. B: 서버 연결 실패 → 서버 시작 (A의 서버가 아직 준비 안됨)
  3. 결과: 서버 2개 실행

- Named Pipe는 `NamedPipeServerStream.MaxAllowedServerInstances`를 사용하고 있어 **동일한 파이프 이름으로 여러 서버 인스턴스가 존재 가능**

### 해결 방안

**A. 시스템 전역 Mutex 사용 (권장)**
```csharp
private static Mutex? _serverStartMutex;

private async Task EnsureServerRunning()
{
    // 전역 뮤텍스로 서버 시작 직렬화
    _serverStartMutex = new Mutex(false, "Global\\WinScreenServerStart");

    try
    {
        _serverStartMutex.WaitOne();

        // 서버 연결 시도
        if (await TryConnectToServer())
            return;

        // 서버 시작
        StartServerProcess();

        // 서버 준비 대기
        await WaitForServerReady();
    }
    finally
    {
        _serverStartMutex.ReleaseMutex();
    }
}
```

**B. 서버 측에서 단일 인스턴스 보장**
```csharp
// WinScreen.Server/Program.cs
static async Task Main(string[] args)
{
    using var mutex = new Mutex(true, "Global\\WinScreenServer", out var createdNew);
    if (!createdNew)
    {
        Console.WriteLine("Server is already running.");
        return;
    }
    // ... 서버 시작
}
```

**C. Named Pipe 인스턴스 수 제한**
```csharp
var pipeServer = new NamedPipeServerStream(
    Constants.PipeName,
    PipeDirection.InOut,
    1,  // MaxAllowedServerInstances → 1로 변경
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);
```

### 권장 조합
- 서버: 단일 인스턴스 Mutex (B)
- 클라이언트: 서버 시작 Mutex (A)

---

## 2. async/delay 과다 사용 분석

### 현재 delay 목록

| 위치 | delay | 목적 | 평가 |
|------|-------|------|------|
| Client:131 | 100ms * 30회 | 서버 시작 대기 | **적절** - 외부 프로세스 대기 |
| Client:229 | 500ms | 서버 종료 확인 | **적절** - 프로세스 종료 대기 |
| Client:606 | 50ms | ConPTY 초기화 대기 | **불필요** - 동기화 부재 |
| Client:849 | 50ms | readTask가 먼저 DeserializeAsync 진입 | **불필요** - race condition 해킹 |
| Client:856 | 10ms | Console.KeyAvailable 폴링 | **필수** - 비동기 API 없음 |
| Client:933 | 500ms | 창 크기 변경 감시 | **개선 가능** - 이벤트 기반 |
| Session:102 | 100ms | 프로세스 종료 폴링 | **개선 가능** - WaitForSingleObject |
| Session:108 | 100ms | 마지막 출력 대기 | **불필요** - 출력 완료 보장 없음 |

### 구조적 문제 분석

#### 2.1. ConPTY 초기화 대기 (Client:606)
```csharp
// 현재 코드
if (response is SessionCreatedMessage created)
{
    await Task.Delay(50);  // 왜 50ms?
    return await AttachToSessionInternal(created.Session.Id);
}
```
**문제:** 50ms가 충분한지 보장 없음. ConPTY가 이미 준비됐을 수 있고, 아닐 수도 있음.

**해결:** 서버에서 세션 생성 완료 후 응답하므로 delay 불필요. 제거 가능.

#### 2.2. readTask/writeTask race condition (Client:849)
```csharp
var readTask = Task.Run(async () => {
    while (_isAttached)
    {
        var msg = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);
        // ...
    }
});

var writeTask = Task.Run(async () => {
    await Task.Delay(50, _cts.Token);  // readTask가 먼저 진입하도록
    // ...
});
```
**문제:** 이 delay는 두 태스크 간의 race condition을 해킹으로 해결. 구조적 문제.

**해결:** 동기화 메커니즘 사용
```csharp
var readySignal = new TaskCompletionSource();

var readTask = Task.Run(async () => {
    readySignal.SetResult();  // 준비 완료 신호
    while (_isAttached) { /* ... */ }
});

var writeTask = Task.Run(async () => {
    await readySignal.Task;  // 신호 대기 (race condition 없음)
    // ...
});
```

#### 2.3. Console Input 폴링 (Client:856)
```csharp
while (_isAttached)
{
    if (!Console.KeyAvailable)
    {
        await Task.Delay(10, _cts.Token);  // 폴링
        continue;
    }
    var keyInfo = Console.ReadKey(intercept: true);
}
```
**문제:** `Console.ReadKey`는 블로킹이라 비동기 취소 불가. 10ms 폴링은 CPU 낭비.

**대안:**
1. **WaitOne with timeout:** `Console.ReadKey`를 별도 스레드에서 호출
2. **ReadConsoleInput API:** 네이티브 API로 비동기 읽기
3. **현행 유지:** 10ms 폴링은 CPU 부담이 크지 않음 (1% 미만)

#### 2.4. 창 크기 변경 감시 (Client:933)
```csharp
while (_isAttached)
{
    await Task.Delay(500, _cts.Token);
    if (width != lastWidth || height != lastHeight)
        await ProtocolSerializer.SendAsync(_pipe!, new ResizeMessage { ... }, _cts.Token);
}
```
**문제:** 500ms마다 폴링. Windows에서는 이벤트 기반 감지 불가 (Linux의 SIGWINCH 같은 것 없음).

**대안:** `Console.WindowWidth/Height` 변경 감지는 폴링이 유일한 방법. 500ms는 적절.

#### 2.5. 프로세스 종료 모니터링 (Session:102)
```csharp
while (!_cts.Token.IsCancellationRequested && !_pty.HasExited)
{
    await Task.Delay(100, _cts.Token);
}
```
**해결:** `WaitForSingleObject`를 비동기로 래핑
```csharp
// 프로세스 종료 대기 (폴링 없이)
await Task.Run(() => {
    NativeMethods.WaitForSingleObject(_processHandle.DangerousGetHandle(), INFINITE);
}, _cts.Token);
```

### 요약: delay 정리

| 제거 가능 | 유지 | 개선 가능 |
|-----------|------|-----------|
| Client:606 (50ms) | Client:131 (서버 대기) | Client:849 → 동기화 사용 |
| Session:108 (100ms) | Client:856 (10ms 폴링) | Session:102 → WaitForSingleObject |
|  | Client:933 (500ms 폴링) |  |

---

## 3. 옵션 필터 강화

### 현상
- `screen -lx` 처럼 잘못된 옵션을 입력해도 에러 없이 실행됨
- 오타를 치면 의도치 않은 동작 발생 가능

### 원인 분석

**[WinScreen.Client/Program.cs:1321-1331]**
```csharp
default:
    // 위치 인자로 세션 이름 또는 ID
    if (!arg.StartsWith("-") && result.SessionId == null)
    {
        if (result.Command == Command.None)
            result.Command = Command.Attach;
        result.SessionId = arg;
    }
    // -로 시작하는 알 수 없는 옵션은 무시됨!
    break;
```

**문제:** `-`로 시작하는 알 수 없는 옵션이 조용히 무시됨.

### 해결 방안

```csharp
default:
    if (arg.StartsWith("-"))
    {
        Console.Error.WriteLine($"Error: Unknown option '{arg}'");
        Console.Error.WriteLine("Use 'screen --help' for usage information.");
        Environment.Exit(1);
    }

    // 위치 인자로 세션 이름 또는 ID
    if (result.SessionId == null)
    {
        if (result.Command == Command.None)
            result.Command = Command.Attach;
        result.SessionId = arg;
    }
    break;
```

### 추가 개선: 유사 옵션 제안
```csharp
private static string? FindSimilarOption(string arg)
{
    var knownOptions = new[] { "-ls", "-list", "-r", "-R", "-S", "-p", "-d", "-X", "-wipe", "--help" };
    // Levenshtein distance로 유사 옵션 찾기
    return knownOptions
        .Select(opt => (opt, LevenshteinDistance(arg.ToLower(), opt.ToLower())))
        .Where(x => x.Item2 <= 2)
        .OrderBy(x => x.Item2)
        .FirstOrDefault().opt;
}

// 사용 예
if (arg.StartsWith("-"))
{
    var similar = FindSimilarOption(arg);
    Console.Error.WriteLine($"Error: Unknown option '{arg}'");
    if (similar != null)
        Console.Error.WriteLine($"Did you mean '{similar}'?");
    Environment.Exit(1);
}
```

---

## 4. 신호 누락 문제

### 현상
- detach 시 `[Detached]` 메시지가 출력되지 않을 때가 있음
- screen 시작 시 기존 화면이 클리어되지 않음

### 원인 분석

#### 4.1. Fire-and-Forget 패턴

**[WinScreen.Server/Program.cs:464-467]**
```csharp
private void OnSessionOutput(byte[] data)
{
    _ = SendAsync(new OutputMessage { Data = data }, CancellationToken.None);
}
```

**문제:**
- `SendAsync`가 실패해도 알 방법이 없음
- 파이프가 끊어진 상태에서도 호출됨
- 예외가 발생해도 무시됨

**해결:**
```csharp
private async void OnSessionOutput(byte[] data)
{
    try
    {
        await SendAsync(new OutputMessage { Data = data }, CancellationToken.None);
    }
    catch (IOException)
    {
        // 파이프 끊김 - 클라이언트 연결 해제
        _attachedSession?.Detach(_clientId);
    }
}
```

#### 4.2. Attach 시 이벤트 구독 타이밍

**[WinScreen.Server/Program.cs:309-319]**
```csharp
// 스크롤백 버퍼와 함께 응답
await SendAsync(new AttachedMessage { ... }, ct);

// 이벤트 구독 (응답 후!)
session.OutputReceived += OnSessionOutput;
```

**시간 차이 문제:**
1. AttachedMessage 전송
2. (세션이 출력 생성 → 이벤트 발생 → 수신자 없음 → **누락**)
3. OutputReceived 이벤트 구독

최근 커밋 `f63e55b`에서 수정됐다고 되어 있으나, 여전히 race condition 존재.

**해결:** 이벤트를 먼저 구독하고, 버퍼링 후 전송
```csharp
// 이벤트 먼저 구독 (버퍼링)
var outputBuffer = new List<byte[]>();
void BufferOutput(byte[] data) => outputBuffer.Add(data);
session.OutputReceived += BufferOutput;

try
{
    // 스크롤백 전송
    await SendAsync(new AttachedMessage { ... }, ct);

    // 버퍼링된 출력 전송
    foreach (var data in outputBuffer)
        await SendAsync(new OutputMessage { Data = data }, ct);
}
finally
{
    // 실제 핸들러로 교체
    session.OutputReceived -= BufferOutput;
    session.OutputReceived += OnSessionOutput;
}
```

#### 4.3. 화면 클리어 누락

**현상:** `screen -r`로 attach할 때 기존 화면이 남아있음

**원인:** 클라이언트에서 화면 클리어를 하지 않음

**해결:** Attach 시 화면 클리어
```csharp
private async Task<int> AttachToSessionInternal(string sessionId)
{
    // ... attach 로직 ...

    if (response is AttachedMessage attached)
    {
        _isAttached = true;
        _attachedSessionId = attached.Session.Id;

        // 화면 클리어 후 스크롤백 출력
        Console.Write("\x1b[2J\x1b[H");  // Clear screen + cursor home

        return await RunTerminalLoop(attached.ScrollbackBuffer);
    }
}
```

---

## 5. /K 옵션 README 추가

### 문제
Anaconda의 `activate.bat`는 `cmd /K`로 실행해야 정상 동작함

### README 추가 내용

```markdown
## Conda/Anaconda 프로필 설정

Anaconda의 activate.bat를 사용하려면 `cmd /K` 옵션이 필요합니다:

### 방법 1: 프로필 JSON 직접 편집
`%LOCALAPPDATA%\WinScreen\profiles.json`:
```json
{
  "name": "conda",
  "description": "Anaconda with base environment",
  "shell": "cmd.exe",
  "arguments": "/K",
  "startupCommand": "C:\\Users\\yourname\\miniconda3\\Scripts\\activate.bat"
}
```

### 방법 2: CLI로 프로필 추가
```batch
screen --profile-add myconda --shell cmd.exe --args "/K" --startup "C:\path\to\activate.bat && conda activate myenv"
```

### /K 옵션 설명
- `/K`: 명령 실행 후 cmd.exe 유지 (Keep)
- `/C`: 명령 실행 후 cmd.exe 종료 (Close)

activate.bat는 환경을 설정한 후 종료되므로, `/K` 없이 실행하면 세션이 즉시 종료됩니다.
```

---

## 구조적 문제 요약

### 현재 아키텍처의 장점
1. ConPTY + Named Pipe 조합은 적절
2. 세션 관리/프로필 시스템 잘 분리됨
3. 비동기 I/O 기반으로 확장성 좋음

### 개선이 필요한 부분

| 문제 | 심각도 | 복잡도 | 우선순위 |
|------|--------|--------|----------|
| 서버 이중 생성 | 높음 | 낮음 | **1** |
| **Nested session 미감지** | **높음** | **낮음** | **2** |
| 옵션 필터링 | 중간 | 낮음 | **3** |
| 화면 클리어 누락 | 중간 | 낮음 | **4** |
| Fire-and-forget 에러 처리 | 중간 | 중간 | **5** |
| README /K 옵션 | 낮음 | 낮음 | **6** |
| race condition delay 제거 | 낮음 | 중간 | **7** |
| 프로세스 종료 폴링 개선 | 낮음 | 중간 | **8** |

---

## 권장 구현 순서

### Phase 1: 빠른 수정 (1-4번)
1. 서버 Mutex로 단일 인스턴스 보장
2. **Nested session 감지 (`$WINSCREEN` 환경 변수)**
3. 옵션 파서에 에러 출력 추가
4. Attach 시 화면 클리어 추가

### Phase 2: 안정성 개선 (5-6번)
5. Fire-and-forget에 에러 핸들링 추가
6. README에 /K 옵션 및 nested session 설명 추가

### Phase 3: 최적화 (7-8번)
7. delay 해킹 대신 동기화 메커니즘 사용
8. WaitForSingleObject로 폴링 제거

---

## 부록: 간단한 개선 vs 복잡한 개선

### 간단한 개선 (권장)
현재 구조를 유지하면서 버그만 수정:
- Mutex 추가
- 에러 메시지 추가
- 화면 클리어 추가

### 복잡한 개선 (선택적)
구조 변경이 필요한 개선:
- 이벤트 기반 입력 처리 (ReadConsoleInput API)
- 비동기 프로세스 종료 대기 (WaitForSingleObject 래핑)
- 메시지 버퍼링으로 race condition 완전 해결

**권장:** 간단한 개선만으로 대부분의 문제가 해결됨. 복잡한 개선은 실제 문제가 발생할 때 진행.

---

## 6. Nested Session (세션 내 screen 누적 실행) 문제

### GNU Screen의 원본 동작 분석

> 참고: [GNU Screen Manual](https://www.gnu.org/software/screen/manual/screen.html), [GNU Screen within Screen](https://blog.bigsmoke.us/2009/01/11/gnu-screen-within-screen), [ArchWiki](https://wiki.archlinux.org/title/GNU_Screen)

#### 6.1. `$STY` 환경 변수

GNU Screen은 세션 시작 시 `$STY` (Screen TeleType) 환경 변수를 설정합니다:

```bash
$ echo $STY
# (비어있음 - screen 밖)

$ screen -S mysession
$ echo $STY
12345.mysession
# (pid.sessionname 형식)
```

#### 6.2. Nested Session 감지 및 동작

GNU Screen이 시작될 때 `$STY`가 설정되어 있으면:

| $STY 상태 | 옵션 없음 | `-m` 옵션 |
|-----------|-----------|-----------|
| 비어있음 | 새 세션 생성 | 새 세션 생성 |
| 설정됨 | **기존 세션에 새 윈도우 추가** | 강제로 새 세션 생성 (nested) |

```bash
# screen 내부에서
$ screen           # 새 윈도우만 생성 (세션은 동일)
$ screen -m        # 진짜 nested session 생성
$ screen -m -S inner  # named nested session
```

#### 6.3. Nested Session에서의 키 바인딩

Nested screen에서는 기본 키(Ctrl+A)가 외부 screen에 먼저 캡처됩니다:

| 키 입력 | 동작 |
|---------|------|
| `Ctrl+A, d` | 외부 screen detach |
| `Ctrl+A, a, d` | 내부 screen detach |
| `Ctrl+A, a, a, d` | 3번째 레벨 detach |
| `Ctrl+A, a` | 내부 screen에 Ctrl+A 전달 |

또는 내부 screen의 escape 키를 변경할 수 있습니다:
```bash
# 내부 screen에서 escape를 Ctrl+S로 변경
Ctrl+A, a, :escape ^Ss
# 이후 내부 screen은 Ctrl+S로 제어
```

#### 6.4. 주의사항 (공식 매뉴얼)

> "Screen refuses to attach from within itself. But when cascading multiple screens, loops are not detected; take care."

- Screen은 자기 자신에게 attach하는 것은 거부
- 그러나 nested screen의 루프는 감지하지 않음 (무한 루프 위험)

---

### WinScreen에서의 현재 문제

#### 문제 1: 환경 변수 체크 없음

**현재 코드:** [WinScreen.Client/Program.cs] - 환경 변수 체크 없이 항상 서버 연결

```csharp
public async Task<int> RunAsync(string[] args)
{
    var parsed = ParseArgs(args);
    // ... 명령 처리
    await EnsureServerRunning();  // $STY 체크 없음!
    // ...
}
```

**결과:**
- screen 내부에서 `screen` 실행 시 항상 새 세션 생성
- 의도치 않은 nested session 발생
- 사용자 혼란 (세션 2개가 생김)

#### 문제 2: Ctrl+A 키 충돌

Nested WinScreen에서:
```
[외부 WinScreen]
  │
  └─[내부 WinScreen 세션]
       │
       └─ cmd.exe
```

- 외부 WinScreen이 Ctrl+A를 먼저 캡처
- 내부 WinScreen에 Ctrl+A 전달 불가
- `Ctrl+A, A`로 Ctrl+A 전송하면 **내부 WinScreen이 그것을 캡처**
- 결국 cmd.exe에 Ctrl+A 전달 불가

#### 문제 3: 무한 루프 위험

```bash
# 잘못된 사용 (무한 screen 생성)
while true; do screen; done
```

현재 WinScreen은 이를 방지하지 않음.

---

### 해결 방안

#### A. 환경 변수 기반 감지 (권장)

**1. 세션 시작 시 환경 변수 설정:**

[WinScreen.Core/Sessions/Session.cs] - ConPTY 생성 시 환경 변수 추가:
```csharp
public static Session Create(...)
{
    // 환경 변수에 세션 정보 추가
    environment ??= new Dictionary<string, string>();
    environment["WINSCREEN"] = $"{Process.GetCurrentProcess().Id}.{name}";

    var pty = ConPty.Create(commandLine, workingDirectory, environment, cols, rows);
    // ...
}
```

**2. 클라이언트에서 감지:**

[WinScreen.Client/Program.cs]:
```csharp
public async Task<int> RunAsync(string[] args)
{
    var parsed = ParseArgs(args);

    // Nested session 감지
    var winscreenEnv = Environment.GetEnvironmentVariable("WINSCREEN");
    if (!string.IsNullOrEmpty(winscreenEnv) && !parsed.ForceNewSession)
    {
        Console.WriteLine($"Already inside WinScreen session: {winscreenEnv}");
        Console.WriteLine("Use 'screen -m' to force a new nested session.");
        Console.WriteLine("Use 'Ctrl+A, A' to send Ctrl+A to the inner terminal.");
        return 1;
    }

    // ... 기존 로직
}
```

**3. `-m` 옵션 추가:**

```csharp
// ParseArgs에 추가
case "-m":
case "--force-new":
    result.ForceNewSession = true;
    break;
```

#### B. Escape 키 변경 지원

Nested session에서 키 충돌 해결을 위해 escape 키 변경 지원:

```csharp
// Constants.cs
public static readonly ConsoleKey DefaultEscapeKey = ConsoleKey.A;

// 환경 변수로 변경 가능
// WINSCREEN_ESCAPE=S -> Ctrl+S가 escape 키
```

**구현:**
```csharp
private static ConsoleKey GetEscapeKey()
{
    var escapeEnv = Environment.GetEnvironmentVariable("WINSCREEN_ESCAPE");
    if (!string.IsNullOrEmpty(escapeEnv) && escapeEnv.Length == 1)
    {
        var c = char.ToUpper(escapeEnv[0]);
        if (c >= 'A' && c <= 'Z')
            return (ConsoleKey)c;
    }
    return ConsoleKey.A;
}
```

**사용 예:**
```batch
# 외부 screen: Ctrl+A
screen -S outer

# 내부 screen: Ctrl+S로 설정
set WINSCREEN_ESCAPE=S
screen -m -S inner

# 이제:
# Ctrl+A, D -> 외부 detach
# Ctrl+S, D -> 내부 detach
```

#### C. 사용자 경고 시스템

Nested session 진입 시 명확한 경고 표시:

```csharp
if (!string.IsNullOrEmpty(winscreenEnv) && parsed.ForceNewSession)
{
    Console.WriteLine("WARNING: Starting nested WinScreen session.");
    Console.WriteLine($"  Outer session: {winscreenEnv}");
    Console.WriteLine($"  Key bindings:");
    Console.WriteLine($"    Ctrl+A, D     -> Detach from OUTER session");
    Console.WriteLine($"    Ctrl+A, A, D  -> Detach from INNER session");
    Console.WriteLine();
}
```

---

### GNU Screen과의 기능 비교

| 기능 | GNU Screen | WinScreen (현재) | WinScreen (개선 후) |
|------|------------|------------------|---------------------|
| Nested 감지 | `$STY` | 없음 | `$WINSCREEN` |
| 기본 동작 | 새 윈도우 | 새 세션 | 에러 + 안내 |
| 강제 nested | `-m` | 없음 | `-m` |
| 키 전달 | `Ctrl+A, A` | `Ctrl+A, A` | 동일 |
| Escape 변경 | `:escape ^Ss` | 없음 | `$WINSCREEN_ESCAPE` |
| 루프 감지 | 없음 | 없음 | 경고 표시 |

---

### 권장 구현 우선순위

| 순위 | 기능 | 복잡도 | 영향도 |
|------|------|--------|--------|
| 1 | `$WINSCREEN` 환경 변수 설정 | 낮음 | 높음 |
| 2 | Nested session 감지 및 에러 | 낮음 | 높음 |
| 3 | `-m` 옵션 추가 | 낮음 | 중간 |
| 4 | 진입 시 경고 메시지 | 낮음 | 중간 |
| 5 | `$WINSCREEN_ESCAPE` 지원 | 중간 | 낮음 |

---

### 코드 변경 요약

**1. WinScreen.Core/Constants.cs:**
```csharp
public const string EnvVarName = "WINSCREEN";
public const string EscapeEnvVarName = "WINSCREEN_ESCAPE";
```

**2. WinScreen.Core/Sessions/Session.cs (Create 메서드):**
```csharp
environment ??= new Dictionary<string, string>();
environment[Constants.EnvVarName] = $"{Process.GetCurrentProcess().Id}.{name}";
```

**3. WinScreen.Client/Program.cs (RunAsync 메서드 시작 부분):**
```csharp
// Nested session check
var winscreenEnv = Environment.GetEnvironmentVariable(Constants.EnvVarName);
if (!string.IsNullOrEmpty(winscreenEnv))
{
    if (parsed.Command == Command.List ||
        parsed.Command == Command.Help ||
        parsed.Command == Command.ServerStatus)
    {
        // 이런 명령은 허용
    }
    else if (!parsed.ForceNewSession)
    {
        Console.Error.WriteLine($"Error: Already inside WinScreen session ({winscreenEnv})");
        Console.Error.WriteLine("  Use 'screen -m' to force a nested session");
        Console.Error.WriteLine("  Use 'screen -ls' to list sessions");
        return 1;
    }
    else
    {
        Console.WriteLine($"WARNING: Creating nested session (outer: {winscreenEnv})");
        Console.WriteLine("  Ctrl+A, A, <cmd> to control inner session");
    }
}
```

**4. ParsedArgs 및 ParseArgs:**
```csharp
// ParsedArgs에 추가
public bool ForceNewSession { get; set; }

// ParseArgs switch에 추가
case "-m":
case "--force-new":
    result.ForceNewSession = true;
    break;
```

---

### README 추가 내용

```markdown
## Nested Sessions (중첩 세션)

WinScreen 세션 내에서 다시 screen을 실행하면 기본적으로 에러가 발생합니다:

```batch
C:\> screen -S outer
[outer session]$ screen
Error: Already inside WinScreen session (12345.outer)
  Use 'screen -m' to force a nested session
  Use 'screen -ls' to list sessions
```

### Nested Session 강제 생성

```batch
[outer session]$ screen -m -S inner
WARNING: Creating nested session (outer: 12345.outer)
  Ctrl+A, A, <cmd> to control inner session
[inner session]$
```

### 키 바인딩 (Nested 상태)

| 키 | 동작 |
|----|------|
| Ctrl+A, D | 외부 세션 detach |
| Ctrl+A, A, D | 내부 세션 detach |
| Ctrl+A, A, A | 내부 터미널에 Ctrl+A 전송 |

### Escape 키 변경

내부 세션의 escape 키를 변경하려면:

```batch
set WINSCREEN_ESCAPE=S
screen -m -S inner
# 이제 내부 세션은 Ctrl+S로 제어
```
```

---

## 7. 검토: 누락/모순 사항

### 7.1. 해결안 1C의 오류 (Named Pipe 인스턴스 제한)

**문제:** 섹션 1의 해결안 C는 **잘못된 해결책**입니다.

```csharp
// ❌ 잘못됨
var pipeServer = new NamedPipeServerStream(
    Constants.PipeName,
    PipeDirection.InOut,
    1,  // MaxAllowedServerInstances → 1로 변경
    ...);
```

**이유:**
- `MaxAllowedServerInstances = 1`이면 **한 번에 한 클라이언트만 연결 가능**
- 현재 서버는 여러 클라이언트 동시 처리를 위해 무제한 인스턴스 사용
- 이 값은 "서버 프로세스 수"가 아니라 "동시 파이프 연결 수"를 의미

**수정:** 해결안 C를 제거하고, A+B 조합만 권장.

---

### 7.2. WINSCREEN 환경 변수 형식 개선

**현재 제안:**
```csharp
environment["WINSCREEN"] = $"{Process.GetCurrentProcess().Id}.{name}";
// 결과: "12345.mysession" (서버 PID.세션이름)
```

**문제:**
- 여러 세션이 있을 때 모두 동일한 서버 PID를 가짐
- 세션 식별이 불명확

**개선:**
```csharp
environment["WINSCREEN"] = $"{session.Id[..8]}.{name}";
// 결과: "abc12345.mysession" (세션ID.세션이름)
```

또는 더 명확하게:
```csharp
environment["WINSCREEN"] = session.Id;  // 전체 세션 ID
environment["WINSCREEN_NAME"] = name;   // 세션 이름 (선택적)
```

---

### 7.3. Nested Session에서 허용할 명령 목록 누락

**현재 제안:**
```csharp
if (parsed.Command == Command.List ||
    parsed.Command == Command.Help ||
    parsed.Command == Command.ServerStatus)
{
    // 허용
}
```

**누락된 명령들:**
```csharp
// 완전한 허용 목록
var allowedInNested = new[] {
    Command.List,           // screen -ls
    Command.ListProfiles,   // screen --profiles
    Command.Help,           // screen --help
    Command.ServerStatus,   // screen --server
    Command.ServerStop,     // screen --server-stop (주의 필요)
    Command.ProfileShow,    // screen --profile-show
    Command.GetDefault,     // screen --default
};

if (allowedInNested.Contains(parsed.Command))
{
    // 허용 - 서버 연결 후 진행
}
```

---

### 7.4. `-m` 옵션과 다른 옵션의 조합 정의

**명확히 정의 필요:**

| 조합 | 의미 | 동작 |
|------|------|------|
| `screen -m` | 강제 nested 새 세션 | 허용 |
| `screen -m -S name` | 강제 nested + 이름 지정 | 허용 |
| `screen -m -p profile` | 강제 nested + 프로필 | 허용 |
| `screen -m -r sessionid` | 강제 nested + attach | **무의미** → 에러 |
| `screen -m -ls` | 강제 nested + 목록 | `-m` 무시, `-ls` 실행 |

**구현:**
```csharp
// -m은 세션 생성 명령에서만 유효
if (parsed.ForceNewSession && parsed.Command != Command.None &&
    parsed.Command != Command.Create && parsed.Command != Command.AttachOrCreate)
{
    Console.Error.WriteLine("Warning: -m option is only valid for session creation.");
    // -m 무시하고 진행
    parsed.ForceNewSession = false;
}
```

---

### 7.5. 구현 순서 의존성 주의

**문제:** 옵션 필터 강화(3번)를 먼저 구현하면 `-m` 옵션이 에러 처리됨

**올바른 순서:**
1. 서버 Mutex (독립적)
2. **`-m` 옵션 파서에 추가** (먼저!)
3. Nested session 감지 로직
4. 옵션 필터 강화 (나중에)

**또는:** 옵션 필터 강화 시 `-m`을 동시에 추가

---

### 7.6. 화면 클리어와 스크롤백의 상호작용

**현재 제안:**
```csharp
Console.Write("\x1b[2J\x1b[H");  // Clear screen
return await RunTerminalLoop(attached.ScrollbackBuffer);
```

**잠재적 문제:**
- 스크롤백 버퍼에 이미 화면 상태를 복원하는 VT 시퀀스가 포함되어 있을 수 있음
- 클리어 후 스크롤백 출력 시 예상과 다른 결과 가능

**고려사항:**
1. **클리어 → 스크롤백 출력:** 이전 히스토리 표시 (권장)
2. **클리어만:** 깨끗한 화면, 히스토리 손실
3. **클리어 없음:** 기존 화면 + 스크롤백 중첩 (현재 동작, 문제)

**권장:** 현재 제안대로 클리어 후 스크롤백 출력이 적절함.

---

### 7.7. Session:108 delay 누락

**delay 분석 테이블에서:**
```
| Session:108 | 100ms | 마지막 출력 대기 | **불필요** |
```

**하지만 권장 구현 순서에서 누락됨.**

**수정:** "제거 가능" 목록에 있으므로 Phase 3에 포함하거나 별도 언급 필요.

실제 코드 ([Session.cs:105-110](src/WinScreen.Core/Sessions/Session.cs#L105-L110)):
```csharp
if (_pty.HasExited && !_cts.Token.IsCancellationRequested)
{
    await Task.Delay(100).ConfigureAwait(false);  // 이 delay
    _cts.Cancel();
}
```

**제거 가능 이유:** 출력 읽기 태스크가 스트림 종료를 감지하면 자연스럽게 종료됨.

---

### 7.8. GNU Screen vs WinScreen 동작 차이 명시

**6번 섹션 테이블:**
| 기능 | GNU Screen | WinScreen (개선 후) |
|------|------------|---------------------|
| 기본 동작 | 새 윈도우 | 에러 + 안내 |

**이것은 의도적인 차이이지만 이유 설명 필요:**

> **참고:** GNU Screen은 하나의 세션 내에 여러 "윈도우"를 지원합니다 (Ctrl+A, C로 생성).
> WinScreen은 현재 이 기능을 지원하지 않으므로, nested session 감지 시 에러를 표시합니다.
> 향후 윈도우 기능 추가 시 GNU Screen과 동일한 동작으로 변경 가능합니다.

---

### 7.9. async void 이벤트 핸들러 주의사항

**4번 섹션 해결안:**
```csharp
private async void OnSessionOutput(byte[] data)
{
    try { await SendAsync(...); }
    catch (IOException) { ... }
}
```

**주의사항 추가 필요:**
- `async void`는 예외가 호출자에게 전파되지 않음
- 모든 예외를 try-catch로 처리해야 함
- 처리되지 않은 예외는 프로세스 크래시 유발 가능

**완전한 구현:**
```csharp
private async void OnSessionOutput(byte[] data)
{
    try
    {
        await SendAsync(new OutputMessage { Data = data }, CancellationToken.None);
    }
    catch (IOException)
    {
        // 파이프 끊김 - 정상적인 연결 해제
        _attachedSession?.Detach(_clientId);
    }
    catch (ObjectDisposedException)
    {
        // 이미 dispose됨 - 무시
    }
    catch (Exception ex)
    {
        // 예상치 못한 에러 로깅
        Console.Error.WriteLine($"[{_clientId[..8]}] Output send error: {ex.Message}");
    }
}
```

---

## 8. 수정된 권장 구현 순서

### Phase 1: 핵심 버그 수정
1. 서버 Mutex로 단일 인스턴스 보장 (해결안 B만 사용)
2. `-m` 옵션 파서에 추가 (3번보다 먼저!)
3. Nested session 감지 (`$WINSCREEN` 환경 변수)
4. 옵션 파서에 에러 출력 추가

### Phase 2: UX 개선
5. Attach 시 화면 클리어 추가
6. Fire-and-forget에 완전한 에러 핸들링 추가
7. README에 /K 옵션 및 nested session 설명 추가

### Phase 3: 최적화 (선택적)
8. delay 해킹 대신 동기화 메커니즘 사용 (Client:849)
9. Session:108의 불필요한 delay 제거
10. WaitForSingleObject로 폴링 제거 (Session:102)

---

## 9. 최종 체크리스트

적용 전 확인 사항:

- [ ] 해결안 1C (Named Pipe 인스턴스 제한) 사용하지 않음
- [ ] `-m` 옵션을 옵션 필터 강화보다 먼저 추가
- [ ] WINSCREEN 환경 변수 형식 결정 (세션ID vs 서버PID)
- [ ] Nested에서 허용할 명령 전체 목록 정의
- [ ] `-m`과 다른 옵션 조합 시 동작 정의
- [ ] async void 핸들러에 모든 예외 처리 추가
- [ ] 화면 클리어 동작 테스트 (스크롤백과 함께)
- [ ] 기존 `-S` 옵션과 `-m -S` 조합 테스트

---

## 10. 잠재적 문제 (추가 발견)

### 10.1. 동시 Attach Race Condition

**현재 코드:** [WinScreen.Server/Program.cs:299-303](src/WinScreen.Server/Program.cs#L299-L303)
```csharp
if (!session.Attach(_clientId, msg.Cols, msg.Rows))
{
    await SendAsync(new ErrorMessage { Message = "Session is already attached" }, ct);
    return;
}
```

**Session.Attach:** [Session.cs:185-206](src/WinScreen.Core/Sessions/Session.cs#L185-L206)
```csharp
public bool Attach(string clientId, short cols, short rows)
{
    if (IsAttached && AttachedClientId != clientId)
        return false;  // ← 체크

    AttachedClientId = clientId;  // ← 설정 (lock 없음!)
    // ...
}
```

**문제:**
- 두 클라이언트가 동시에 `session.Attach()` 호출
- 둘 다 `IsAttached == false` 체크 통과
- 둘 다 `AttachedClientId = clientId` 설정
- **결과:** 나중에 설정한 클라이언트만 연결, 먼저 설정한 클라이언트는 연결된 줄 알지만 이벤트 수신 불가

**해결:**
```csharp
private readonly object _attachLock = new();

public bool Attach(string clientId, short cols, short rows)
{
    lock (_attachLock)
    {
        if (IsAttached && AttachedClientId != clientId)
            return false;

        AttachedClientId = clientId;
    }
    // resize는 lock 밖에서
    try { _pty.Resize(cols, rows); } catch { }
    return true;
}
```

---

### 10.2. Ctrl+S Escape 키 문제

**제안한 기능:**
```batch
set WINSCREEN_ESCAPE=S
screen -m -S inner
```

**문제:**
- **XON/XOFF 흐름 제어:** 일부 터미널에서 Ctrl+S는 화면 정지 (XOFF)
- **Windows Terminal:** 기본적으로 비활성화되어 있지만 다른 터미널은 다를 수 있음
- **cmd.exe:** Ctrl+S는 아무 동작 안 함 (안전)

**권장 대안 키:**
| 키 | 안전성 | 비고 |
|----|--------|------|
| Ctrl+B | 안전 | tmux 기본값 |
| Ctrl+O | 주의 | 일부 에디터에서 "열기" |
| Ctrl+T | 주의 | 브라우저에서 "새 탭" |
| Ctrl+] | 안전 | telnet escape |

**해결:** 문서에 Ctrl+S 주의사항 추가, Ctrl+B를 대안으로 권장

---

### 10.3. Detach 후 세션 종료 시 알림 누락

**시나리오:**
1. 클라이언트가 세션에 attach
2. Ctrl+A, D로 detach
3. 세션 내 프로세스가 종료됨 (예: `exit` 입력 후)
4. 클라이언트가 다시 `screen -r` 실행
5. **문제:** 이미 종료된 세션에 attach 시도

**현재 동작:**
- `SessionManager`에서 세션이 자동 제거됨 ([Session.cs:291-295](src/WinScreen.Core/Sessions/Session.cs#L291-L295))
- attach 시 "Session not found" 에러 발생
- 사용자는 왜 세션이 없는지 모름

**개선:**
```csharp
// ListSessions 응답에 최근 종료된 세션 정보 추가 (선택적)
public class SessionListMessage : ServerMessage
{
    public List<SessionInfo> Sessions { get; set; }
    public List<EndedSessionInfo>? RecentlyEnded { get; set; }  // 추가
}
```

또는 단순히 에러 메시지 개선:
```
Error: Session 'abc12345' not found.
  The session may have ended. Use 'screen -ls' to see active sessions.
```

---

### 10.4. 스크롤백 버퍼 1MB 제한

**현재:** [Session.cs:13](src/WinScreen.Core/Sessions/Session.cs#L13)
```csharp
private const int MaxScrollbackSize = 1024 * 1024; // 1MB
```

**문제:**
- 긴 빌드 로그, 대용량 파일 cat 등에서 1MB 초과 가능
- 초과 시 앞부분 데이터 손실
- re-attach 시 히스토리 일부만 복원

**영향:**
- `npm install`, `cargo build` 등 긴 출력 시
- 오래 실행된 세션에서 re-attach

**완화책 (문서화 필요):**
```markdown
## 제한사항

- 스크롤백 버퍼: 최대 1MB
  - 긴 출력이 있는 세션에서 re-attach 시 최근 1MB만 복원됩니다
  - 중요한 출력은 파일로 리다이렉트하세요: `command > output.log`
```

---

### 10.5. Named Pipe 보안 (다중 사용자 환경)

**현재 코드:**
```csharp
var pipeServer = new NamedPipeServerStream(
    Constants.PipeName,  // "WinScreen"
    PipeDirection.InOut,
    NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);  // 보안 설정 없음
```

**문제:**
- 같은 시스템의 다른 사용자도 파이프에 연결 가능
- 악의적인 사용자가 세션 출력을 가로채거나 입력 주입 가능

**영향:**
- 단일 사용자 PC: 무관
- 공유 서버, 터미널 서버: 보안 위험

**해결 (선택적):**
```csharp
var pipeSecurity = new PipeSecurity();
pipeSecurity.AddAccessRule(new PipeAccessRule(
    WindowsIdentity.GetCurrent().User,
    PipeAccessRights.FullControl,
    AccessControlType.Allow));

var pipeServer = NamedPipeServerStreamAcl.Create(
    Constants.PipeName,
    PipeDirection.InOut,
    NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous,
    0, 0,
    pipeSecurity);
```

**우선순위:** 낮음 (대부분 단일 사용자 환경)

---

### 10.6. 서버 크래시 시 세션 복구 불가

**현상:**
- 서버 프로세스가 비정상 종료되면 모든 세션 손실
- ConPTY 프로세스는 서버 프로세스의 자식이므로 함께 종료

**GNU Screen과의 차이:**
- GNU Screen: 각 세션이 독립 프로세스, 서버 재시작 시 복구 가능
- WinScreen: 모든 세션이 서버 프로세스 내에서 관리

**완화책:**
- 서버 안정성 향상이 우선
- 장기적으로 세션 상태 저장/복구 기능 고려 (복잡)

**문서화:**
```markdown
## 주의사항

WinScreen 서버가 종료되면 모든 세션이 함께 종료됩니다.
- 중요한 작업은 정기적으로 저장하세요
- `screen --server-stop` 전에 세션을 확인하세요
```

---

### 10.7. 이벤트 핸들러 메모리 누수 가능성

**시나리오:**
1. 클라이언트 A가 세션에 attach
2. 네트워크 문제로 연결 끊김 (정상적인 detach 없이)
3. `ClientHandler.RunAsync`의 finally에서 `_attachedSession?.Detach()` 호출
4. **하지만** `OutputReceived -= OnSessionOutput`은?

**현재 코드:** [Program.cs:174-179](src/WinScreen.Server/Program.cs#L174-L179)
```csharp
finally
{
    // 세션에서 detach
    _attachedSession?.Detach(_clientId);  // Detach만 호출
    _pipe.Dispose();
}
```

**문제:**
- `OutputReceived` 이벤트 핸들러가 해제되지 않음
- 세션이 계속 `OnSessionOutput` 호출 시도
- `_pipe`는 dispose됐으므로 `SendAsync` 실패
- 예외 발생 (Fire-and-forget이므로 무시됨)

**해결:**
```csharp
finally
{
    if (_attachedSession != null)
    {
        _attachedSession.OutputReceived -= OnSessionOutput;
        _attachedSession.SessionEnded -= OnSessionEndedWhileAttached;
        _attachedSession.Detach(_clientId);
    }
    _pipe.Dispose();
}
```

**심각도:** 중간 - 기능적 문제는 없지만 불필요한 예외 발생

---

### 10.8. 대용량 출력 시 파이프 버퍼 오버플로우

**시나리오:**
```bash
cat very_large_file.txt  # 수백 MB 파일
```

**흐름:**
1. ConPTY가 빠르게 출력 생성
2. Session이 `OutputReceived` 이벤트 발생
3. ClientHandler가 `SendAsync`로 파이프에 전송
4. 클라이언트가 수신하여 화면에 출력

**병목:**
- Console.Write()가 느림 (특히 Windows Terminal)
- 파이프 버퍼 가득 참
- SendAsync가 블로킹됨
- 이벤트 큐잉 시작

**현재 완화:**
- `_writeLock` (SemaphoreSlim)이 동시 전송 방지
- Named Pipe는 내부 버퍼링 있음

**잠재적 문제:**
- 메모리 사용량 증가 (이벤트 큐)
- 극단적인 경우 응답 지연

**해결 (선택적):**
- 출력 throttling 또는 배치 처리
- 현재로서는 실제 문제 발생 시 대응

---

## 11. 우선순위 재정리

### 필수 수정 (버그)
| # | 항목 | 이유 |
|---|------|------|
| 1 | 서버 Mutex | 서버 이중 생성 방지 |
| 2 | Attach race condition | 동시 연결 시 데이터 손실 |
| 3 | 이벤트 핸들러 해제 누락 | 연결 끊김 시 정리 안됨 |

### 권장 수정 (안정성)
| # | 항목 | 이유 |
|---|------|------|
| 4 | Nested session 감지 | 사용자 혼란 방지 |
| 5 | 옵션 필터 강화 | 오타 방지 |
| 6 | Fire-and-forget 에러 처리 | 예외 무시 방지 |
| 7 | 화면 클리어 | UX 개선 |

### 문서화 필요
| # | 항목 |
|---|------|
| 8 | 스크롤백 1MB 제한 |
| 9 | 서버 종료 시 세션 손실 |
| 10 | Ctrl+S escape 키 주의사항 |
| 11 | /K 옵션 (Conda) |

### 향후 고려 (선택적)
| # | 항목 |
|---|------|
| 12 | Named Pipe 보안 (다중 사용자) |
| 13 | 세션 상태 복구 기능 |
| 14 | delay 최적화 |

---

## 12. 추가 잠재적 문제 (프로토콜/프로필)

### 12.1. 프로토콜 버전 관리 없음

**현재 상태:**
- 클라이언트와 서버 간 버전 체크 없음
- 새 메시지 타입 추가 시 호환성 문제

**시나리오:**
1. 서버 업데이트 (새 메시지 타입 추가)
2. 구 버전 클라이언트가 연결
3. 서버가 새 메시지 전송 → 클라이언트 역직렬화 실패

**현재 동작:** [Messages.cs:352](src/WinScreen.Core/Protocol/Messages.cs#L352)
```csharp
return JsonSerializer.Deserialize<T>(jsonBuffer, Options);
// 알 수 없는 $type → null 또는 예외
```

**해결 (선택적):**
```csharp
// 연결 시 버전 교환
public class HelloMessage : ClientMessage
{
    public string ClientVersion { get; set; }
}

public class WelcomeMessage : ServerMessage
{
    public string ServerVersion { get; set; }
    public bool Compatible { get; set; }
}
```

**우선순위:** 낮음 (현재는 단일 배포 환경)

---

### 12.2. JSON 역직렬화 실패 처리 부재

**현재 코드:** [Messages.cs:333-353](src/WinScreen.Core/Protocol/Messages.cs#L333-L353)
```csharp
public static async Task<T?> DeserializeAsync<T>(Stream stream, CancellationToken ct)
{
    // ... length 읽기 ...
    return JsonSerializer.Deserialize<T>(jsonBuffer, Options);  // 예외 가능!
}
```

**문제:**
- 잘못된 JSON → `JsonException`
- 알 수 없는 `$type` → 역직렬화 실패
- 예외가 호출자에게 전파됨

**영향:**
- 서버: 클라이언트 연결 끊김 (catch 있음)
- 클라이언트: 프로그램 종료 가능

**해결:**
```csharp
try
{
    return JsonSerializer.Deserialize<T>(jsonBuffer, Options);
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"Protocol error: {ex.Message}");
    return default;  // 또는 특수 에러 메시지 반환
}
```

---

### 12.3. 메시지 길이 검증 불일치

**현재:**
- 최대 메시지 크기: 10MB ([Messages.cs:340](src/WinScreen.Core/Protocol/Messages.cs#L340))
- 최대 스크롤백: 1MB ([Session.cs:13](src/WinScreen.Core/Sessions/Session.cs#L13))

**문제:**
- 악의적인 클라이언트가 10MB 메시지 전송 가능
- 메모리 할당 공격 (DoS)

**해결:**
```csharp
// 메시지 타입별 크기 제한
private const int MaxInputSize = 64 * 1024;        // 64KB (입력)
private const int MaxScrollbackSize = 2 * 1024 * 1024;  // 2MB (스크롤백)
private const int MaxControlSize = 4 * 1024;       // 4KB (제어 메시지)
```

**우선순위:** 낮음 (신뢰할 수 있는 환경)

---

### 12.4. 프로필 파일 동시 접근

**시나리오:**
1. 클라이언트 A: `screen --profile-add foo`
2. 클라이언트 B: `screen --profile-add bar` (동시에)
3. 둘 다 파일 읽기 → 수정 → 쓰기
4. **결과:** 한 쪽 변경사항 손실

**현재:** ProfileStore에 파일 잠금 없음

**해결:**
```csharp
private static readonly object _fileLock = new();

private void Save()
{
    lock (_fileLock)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }
}
```

**또는:** 서버에서만 프로필 수정 (현재 구조), 서버는 단일 인스턴스 → 문제 없음

**실제 영향:** 낮음 (서버 내에서만 수정)

---

### 12.5. StartupCommand 인젝션 가능성

**현재 코드:** [ProfileStore.cs:60-66](src/WinScreen.Core/Profiles/ProfileStore.cs#L60-L66)
```csharp
if (shellLower.Contains("cmd"))
{
    cmdLine += $" /K \"{StartupCommand}\"";
}
```

**문제:**
- StartupCommand에 `"` 포함 시 인젝션 가능
- 예: `foo" & del /f /q * & echo "`

**영향:**
- 사용자가 직접 프로필 설정 → 자기 자신 공격 (의미 없음)
- 신뢰할 수 없는 소스에서 프로필 로드 시 위험

**해결:**
```csharp
// StartupCommand 검증 또는 이스케이프
var escaped = StartupCommand.Replace("\"", "\\\"");
cmdLine += $" /K \"{escaped}\"";
```

**우선순위:** 낮음 (사용자 자신이 설정)

---

### 12.6. 셸 경로 검증 없음

**시나리오:**
```bash
screen --profile-add test --shell "C:\nonexistent\shell.exe"
screen -p test
```

**현재 동작:**
- 프로필 저장 성공
- 세션 생성 시 `CreateProcess` 실패 → 예외 → 에러 메시지

**문제:**
- 에러 메시지가 불명확할 수 있음
- 사용자가 왜 실패했는지 모름

**개선:**
```csharp
// 프로필 추가 시 검증
if (!File.Exists(profile.Shell) && !IsInPath(profile.Shell))
{
    Console.WriteLine($"Warning: Shell '{profile.Shell}' not found.");
}
```

---

### 12.7. read < 4 시 조용한 실패

**현재:** [Messages.cs:336-337](src/WinScreen.Core/Protocol/Messages.cs#L336-L337)
```csharp
var read = await stream.ReadAsync(lengthBuffer, ct);
if (read < 4) return default;  // null 반환
```

**호출자:** [Server/Program.cs:154-156](src/WinScreen.Server/Program.cs#L154-L156)
```csharp
var message = await ProtocolSerializer.DeserializeAsync<ClientMessage>(_pipe, ct);
if (message == null) break;  // 연결 종료로 처리
```

**문제:**
- 부분 읽기(1-3바이트)도 연결 종료로 처리됨
- 네트워크 지연 시 잘못된 종료 가능

**실제 영향:** 낮음 (Named Pipe는 로컬, 신뢰성 높음)

---

## 13. 최종 문제 요약

### 즉시 수정 필요 (버그)
| # | 문제 | 위치 | 영향 |
|---|------|------|------|
| 1 | 서버 이중 생성 | Client | 서버 2개 실행 |
| 2 | Attach race condition | Session.cs | 데이터 손실 |
| 3 | 이벤트 핸들러 해제 누락 | Server | 메모리 누수/예외 |

### 권장 수정 (안정성)
| # | 문제 | 위치 | 영향 |
|---|------|------|------|
| 4 | Nested session 미감지 | Client | 사용자 혼란 |
| 5 | 옵션 필터 부재 | Client | 오타 무시 |
| 6 | Fire-and-forget 예외 | Server | 예외 무시 |
| 7 | 화면 클리어 누락 | Client | UX |
| 8 | JSON 역직렬화 예외 | Messages.cs | 연결 끊김 |

### 문서화 필요
| # | 항목 |
|---|------|
| 9 | 스크롤백 1MB 제한 |
| 10 | 서버 종료 = 세션 종료 |
| 11 | /K 옵션 (Conda) |
| 12 | Ctrl+S escape 주의 |

### 낮은 우선순위
| # | 문제 | 이유 |
|---|------|------|
| 13 | 프로토콜 버전 | 단일 배포 환경 |
| 14 | 메시지 크기 공격 | 신뢰 환경 |
| 15 | Named Pipe 보안 | 단일 사용자 |
| 16 | 셸 경로 검증 | 에러로 실패 |
| 17 | 프로필 동시 접근 | 서버 단일 인스턴스 |

---

## 14. 결론

**현재 코드 품질:** 전체적으로 양호하나 몇 가지 동시성 버그 존재

**핵심 수정 사항 (3개):**
1. 서버 Mutex 추가
2. `Session.Attach()`에 lock 추가
3. `ClientHandler.RunAsync` finally에서 이벤트 해제

**이 3가지만 수정해도 안정성 크게 향상됨.**

나머지는 UX 개선 및 방어적 프로그래밍으로, 시간 여유가 있을 때 진행.
