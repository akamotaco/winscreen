using System.Collections.Concurrent;
using WinScreen.Core.Protocol;
using WinScreen.Core.Terminal;

namespace WinScreen.Core.Sessions;

/// <summary>
/// 터미널 세션 (윈도우 그룹)
/// </summary>
public sealed class Session : IDisposable
{
    private readonly List<Window> _windows = new();
    private readonly object _windowsLock = new();
    private readonly object _attachLock = new();
    private readonly int _maxScrollbackSize;

    private int _activeWindowIndex = 0;
    private bool _disposed;

    public string Id { get; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; }
    public string? WorkingDirectory { get; }
    public string? ProfileName { get; }

    /// <summary>현재 연결된 클라이언트 ID (null이면 detached)</summary>
    public string? AttachedClientId { get; private set; }

    public bool IsAttached => AttachedClientId != null;

    /// <summary>활성 윈도우 인덱스</summary>
    public int ActiveWindowIndex
    {
        get
        {
            lock (_windowsLock)
            {
                return _activeWindowIndex;
            }
        }
    }

    /// <summary>활성 윈도우</summary>
    public Window? ActiveWindow
    {
        get
        {
            lock (_windowsLock)
            {
                return _windows.FirstOrDefault(w => w.Index == _activeWindowIndex);
            }
        }
    }

    /// <summary>윈도우 개수</summary>
    public int WindowCount
    {
        get
        {
            lock (_windowsLock)
            {
                return _windows.Count;
            }
        }
    }

    /// <summary>모든 윈도우</summary>
    public IReadOnlyList<Window> Windows
    {
        get
        {
            lock (_windowsLock)
            {
                return _windows.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>세션의 모든 윈도우가 종료되었는지</summary>
    public bool HasExited
    {
        get
        {
            lock (_windowsLock)
            {
                return _windows.Count == 0;
            }
        }
    }

    /// <summary>출력 데이터 수신 이벤트 (활성 윈도우의 출력)</summary>
    public event Action<byte[]>? OutputReceived;

    /// <summary>세션 종료 이벤트 (모든 윈도우 종료 시)</summary>
    public event Action<int>? SessionEnded;

    /// <summary>윈도우 종료 이벤트</summary>
    public event Action<int, int>? WindowEnded; // windowIndex, exitCode

    /// <summary>활성 윈도우 변경 이벤트</summary>
    public event Action<int>? ActiveWindowChanged;

    private Session(
        string id,
        string name,
        string? workingDirectory,
        string? profileName,
        int maxScrollbackSize)
    {
        Id = id;
        Name = name;
        CreatedAt = DateTime.UtcNow;
        WorkingDirectory = workingDirectory;
        ProfileName = profileName;
        _maxScrollbackSize = maxScrollbackSize > 0 ? maxScrollbackSize : 1024 * 1024; // 1MB default
    }

    /// <summary>
    /// 새 세션 생성 (첫 번째 윈도우 포함)
    /// </summary>
    public static Session Create(
        string? name,
        string commandLine,
        string? workingDirectory = null,
        string? profileName = null,
        Dictionary<string, string>? environment = null,
        short cols = 120,
        short rows = 30,
        int maxScrollbackSize = 0)
    {
        var id = GenerateId();
        name ??= $"session-{id[..8]}";

        // WINSCREEN 환경 변수 추가 (nested session 감지용)
        var env = environment != null
            ? new Dictionary<string, string>(environment)
            : new Dictionary<string, string>();
        env["WINSCREEN"] = $"{id[..8]}.{name}";

        var session = new Session(id, name, workingDirectory, profileName, maxScrollbackSize);

        // 첫 번째 윈도우 생성
        session.CreateWindowInternal(commandLine, null, workingDirectory, profileName, env, cols, rows);

        return session;
    }

    private static string GenerateId()
    {
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// 새 윈도우 생성
    /// </summary>
    public Window CreateWindow(
        string commandLine,
        string? windowName = null,
        string? workingDirectory = null,
        string? profileName = null,
        Dictionary<string, string>? environment = null,
        short cols = 120,
        short rows = 30)
    {
        // WINSCREEN 환경 변수 추가
        var env = environment != null
            ? new Dictionary<string, string>(environment)
            : new Dictionary<string, string>();
        env["WINSCREEN"] = $"{Id[..8]}.{Name}";

        return CreateWindowInternal(commandLine, windowName, workingDirectory, profileName, env, cols, rows);
    }

    /// <summary>
    /// 가장 작은 사용 가능한 윈도우 인덱스 찾기 (GNU Screen 방식)
    /// </summary>
    private int FindNextAvailableIndex()
    {
        // _windowsLock이 이미 잡혀있다고 가정
        var usedIndices = _windows.Select(w => w.Index).ToHashSet();

        // 0부터 시작해서 사용하지 않는 가장 작은 인덱스 찾기
        var index = 0;
        while (usedIndices.Contains(index))
        {
            index++;
        }
        return index;
    }

    private Window CreateWindowInternal(
        string commandLine,
        string? windowName,
        string? workingDirectory,
        string? profileName,
        Dictionary<string, string>? environment,
        short cols,
        short rows)
    {
        lock (_windowsLock)
        {
            // GNU Screen 방식: 가장 작은 사용 가능한 인덱스 찾기
            var index = FindNextAvailableIndex();
            windowName ??= $"window-{index}";

            var window = Window.Create(
                index,
                windowName,
                commandLine,
                workingDirectory,
                profileName,
                environment,
                cols,
                rows,
                _maxScrollbackSize);

            // 윈도우 이벤트 연결
            window.OutputReceived += data => OnWindowOutput(index, data);
            window.WindowEnded += exitCode => OnWindowEnded(index, exitCode);

            _windows.Add(window);

            // 첫 번째 윈도우면 활성 윈도우로 설정
            if (_windows.Count == 1)
            {
                _activeWindowIndex = index;
            }

            Console.WriteLine($"[Session {Id[..8]}] Created window {index}: {windowName}");

            return window;
        }
    }

    private void OnWindowOutput(int windowIndex, byte[] data)
    {
        // 활성 윈도우의 출력만 전달
        lock (_windowsLock)
        {
            if (windowIndex == _activeWindowIndex)
            {
                OutputReceived?.Invoke(data);
            }
        }
    }

    private void OnWindowEnded(int windowIndex, int exitCode)
    {
        bool sessionEnded = false;
        int newActiveIndex = -1;

        lock (_windowsLock)
        {
            var window = _windows.FirstOrDefault(w => w.Index == windowIndex);
            if (window != null)
            {
                _windows.Remove(window);
                window.Dispose();

                Console.WriteLine($"[Session {Id[..8]}] Window {windowIndex} ended with exit code {exitCode}");

                // 활성 윈도우가 종료된 경우 다른 윈도우로 전환
                if (windowIndex == _activeWindowIndex && _windows.Count > 0)
                {
                    var nextWindow = _windows.FirstOrDefault();
                    if (nextWindow != null)
                    {
                        _activeWindowIndex = nextWindow.Index;
                        newActiveIndex = nextWindow.Index;
                    }
                }

                sessionEnded = _windows.Count == 0;
            }
        }

        // 이벤트 발생 (lock 밖에서)
        WindowEnded?.Invoke(windowIndex, exitCode);

        if (newActiveIndex >= 0)
        {
            ActiveWindowChanged?.Invoke(newActiveIndex);
        }

        if (sessionEnded)
        {
            Console.WriteLine($"[Session {Id[..8]}] All windows closed, session ended");
            SessionEnded?.Invoke(exitCode);
        }
    }

    /// <summary>
    /// 특정 윈도우로 전환
    /// </summary>
    public bool SwitchWindow(int index)
    {
        lock (_windowsLock)
        {
            var window = _windows.FirstOrDefault(w => w.Index == index);
            if (window == null)
            {
                return false;
            }

            if (_activeWindowIndex != index)
            {
                _activeWindowIndex = index;
                Console.WriteLine($"[Session {Id[..8]}] Switched to window {index}");
            }

            return true;
        }
    }

    /// <summary>
    /// 다음 윈도우로 전환
    /// </summary>
    public bool NextWindow()
    {
        lock (_windowsLock)
        {
            if (_windows.Count <= 1) return false;

            var currentIdx = _windows.FindIndex(w => w.Index == _activeWindowIndex);
            if (currentIdx < 0) return false;

            var nextIdx = (currentIdx + 1) % _windows.Count;
            _activeWindowIndex = _windows[nextIdx].Index;

            Console.WriteLine($"[Session {Id[..8]}] Switched to window {_activeWindowIndex}");
            return true;
        }
    }

    /// <summary>
    /// 이전 윈도우로 전환
    /// </summary>
    public bool PreviousWindow()
    {
        lock (_windowsLock)
        {
            if (_windows.Count <= 1) return false;

            var currentIdx = _windows.FindIndex(w => w.Index == _activeWindowIndex);
            if (currentIdx < 0) return false;

            var prevIdx = (currentIdx - 1 + _windows.Count) % _windows.Count;
            _activeWindowIndex = _windows[prevIdx].Index;

            Console.WriteLine($"[Session {Id[..8]}] Switched to window {_activeWindowIndex}");
            return true;
        }
    }

    /// <summary>
    /// 윈도우 종료
    /// </summary>
    public bool KillWindow(int index)
    {
        lock (_windowsLock)
        {
            var window = _windows.FirstOrDefault(w => w.Index == index);
            if (window == null)
            {
                return false;
            }

            // 윈도우 dispose → WindowEnded 이벤트 발생 → OnWindowEnded에서 처리
            window.Dispose();
            return true;
        }
    }

    /// <summary>
    /// 윈도우 가져오기
    /// </summary>
    public Window? GetWindow(int index)
    {
        lock (_windowsLock)
        {
            return _windows.FirstOrDefault(w => w.Index == index);
        }
    }

    /// <summary>
    /// 현재 스크롤백 버퍼 내용 가져오기 (활성 윈도우)
    /// </summary>
    public byte[] GetScrollbackBuffer()
    {
        var window = ActiveWindow;
        return window?.GetScrollbackBuffer() ?? Array.Empty<byte>();
    }

    /// <summary>
    /// 클라이언트 연결
    /// </summary>
    public bool Attach(string clientId, short cols, short rows, bool forceDetach = false)
    {
        string? previousClientId = null;

        lock (_attachLock)
        {
            if (IsAttached && AttachedClientId != clientId)
            {
                if (!forceDetach)
                {
                    return false;
                }
                previousClientId = AttachedClientId;
            }

            AttachedClientId = clientId;
        }

        // 활성 윈도우 크기 조정
        try
        {
            ActiveWindow?.Resize(cols, rows);
        }
        catch
        {
            // 리사이즈 실패해도 연결은 유지
        }

        if (previousClientId != null)
        {
            Console.WriteLine($"[Session {Id[..8]}] Force detached client {previousClientId[..8]}");
        }

        return true;
    }

    /// <summary>
    /// 클라이언트 분리
    /// </summary>
    public void Detach(string? clientId = null)
    {
        lock (_attachLock)
        {
            if (clientId == null || AttachedClientId == clientId)
            {
                AttachedClientId = null;
            }
        }
    }

    /// <summary>
    /// 터미널에 입력 전송 (활성 윈도우)
    /// </summary>
    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        var window = ActiveWindow;
        if (window != null)
        {
            await window.WriteAsync(data, ct);
        }
    }

    /// <summary>
    /// 터미널 크기 조정 (활성 윈도우)
    /// </summary>
    public void Resize(short cols, short rows)
    {
        ActiveWindow?.Resize(cols, rows);
    }

    /// <summary>
    /// 세션 정보 반환
    /// </summary>
    public SessionInfo ToInfo()
    {
        lock (_windowsLock)
        {
            return new SessionInfo
            {
                Id = Id,
                Name = Name,
                CreatedAt = CreatedAt,
                IsAttached = IsAttached,
                WorkingDirectory = WorkingDirectory,
                ProfileName = ProfileName,
                ActiveWindowIndex = _activeWindowIndex,
                Windows = _windows.Select(w => w.ToInfo(w.Index == _activeWindowIndex)).ToList()
            };
        }
    }

    /// <summary>
    /// 윈도우 목록 정보 반환
    /// </summary>
    public List<WindowInfo> GetWindowsInfo()
    {
        lock (_windowsLock)
        {
            return _windows.Select(w => w.ToInfo(w.Index == _activeWindowIndex)).ToList();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_windowsLock)
        {
            foreach (var window in _windows)
            {
                window.Dispose();
            }
            _windows.Clear();
        }
    }
}

/// <summary>
/// 세션 관리자
/// </summary>
public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private bool _disposed;

    /// <summary>세션 생성됨</summary>
    public event Action<Session>? SessionCreated;

    /// <summary>세션 종료됨</summary>
    public event Action<string, int>? SessionEnded;

    public Session Create(
        string? name,
        string commandLine,
        string? workingDirectory = null,
        string? profileName = null,
        Dictionary<string, string>? environment = null,
        short cols = 120,
        short rows = 30,
        int maxScrollbackSize = 0)
    {
        var session = Session.Create(name, commandLine, workingDirectory, profileName, environment, cols, rows, maxScrollbackSize);

        session.SessionEnded += exitCode =>
        {
            _sessions.TryRemove(session.Id, out _);
            SessionEnded?.Invoke(session.Id, exitCode);
        };

        _sessions[session.Id] = session;
        SessionCreated?.Invoke(session);

        return session;
    }

    public Session? Get(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public Session? GetByName(string name) =>
        _sessions.Values.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 지정한 이름을 가진 세션이 존재하는지 확인
    /// </summary>
    public bool ExistsByName(string name) =>
        _sessions.Values.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Session> GetAll() => _sessions.Values;

    public IEnumerable<SessionInfo> GetAllInfo() => _sessions.Values.Select(s => s.ToInfo());

    public bool Kill(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            session.Dispose();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
