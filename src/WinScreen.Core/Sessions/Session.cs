using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using WinScreen.Core.Protocol;
using WinScreen.Core.Terminal;

namespace WinScreen.Core.Sessions;

/// <summary>
/// 터미널 세션
/// </summary>
public sealed class Session : IDisposable
{
    private const int DefaultMaxScrollbackSize = 1024 * 1024; // 1MB default

    private readonly ConPty _pty;
    private readonly CancellationTokenSource _cts = new();
    private readonly MemoryStream _scrollbackBuffer = new();
    private readonly object _scrollbackLock = new();
    private readonly object _attachLock = new();
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly int _maxScrollbackSize;

    private Task? _readTask;
    private bool _disposed;

    public string Id { get; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; }
    public string? WorkingDirectory { get; }
    public string? ProfileName { get; }
    
    /// <summary>현재 연결된 클라이언트 ID (null이면 detached)</summary>
    public string? AttachedClientId { get; private set; }
    
    public bool IsAttached => AttachedClientId != null;
    public bool HasExited => _pty.HasExited;
    public int ExitCode => _pty.GetExitCode();

    /// <summary>출력 데이터 수신 이벤트</summary>
    public event Action<byte[]>? OutputReceived;
    
    /// <summary>세션 종료 이벤트</summary>
    public event Action<int>? SessionEnded;

    private Session(
        string id,
        string name,
        ConPty pty,
        string? workingDirectory,
        string? profileName,
        int maxScrollbackSize)
    {
        Id = id;
        Name = name;
        _pty = pty;
        CreatedAt = DateTime.UtcNow;
        WorkingDirectory = workingDirectory;
        ProfileName = profileName;
        _maxScrollbackSize = maxScrollbackSize > 0 ? maxScrollbackSize : DefaultMaxScrollbackSize;

        // 파이프 스트림 생성 (anonymous pipe는 overlapped I/O 미지원)
        _inputStream = new FileStream(_pty.PipeIn!, FileAccess.Write, 4096, false);
        _outputStream = new FileStream(_pty.PipeOut!, FileAccess.Read, 4096, false);
    }

    /// <summary>
    /// 새 세션 생성
    /// </summary>
    /// <param name="maxScrollbackSize">스크롤백 버퍼 최대 크기 (바이트). 0이면 기본값 1MB</param>
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

        var pty = ConPty.Create(
            commandLine,
            workingDirectory,
            env,
            cols,
            rows);

        var session = new Session(id, name, pty, workingDirectory, profileName, maxScrollbackSize);
        session.StartReading();
        return session;
    }

    private static string GenerateId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private void StartReading()
    {
        // 프로세스 종료 모니터링 태스크
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested && !_pty.HasExited)
            {
                await Task.Delay(100, _cts.Token).ConfigureAwait(false);
            }

            if (_pty.HasExited && !_cts.Token.IsCancellationRequested)
            {
                // 잠시 대기 후 종료 (마지막 출력 처리를 위해)
                await Task.Delay(100).ConfigureAwait(false);
                _cts.Cancel();
            }
        });

        _readTask = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    // 단순히 ReadAsync로 블로킹 - 프로세스 종료 모니터링은 별도 태스크에서 처리
                    var read = await _outputStream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                    {
                        // 스트림 종료 = 프로세스 종료
                        break;
                    }

                    var data = buffer[..read];

                    // 스크롤백 버퍼에 저장
                    AppendToScrollback(data);

                    // 이벤트 발생
                    OutputReceived?.Invoke(data);
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Session {Id[..8]}] Read error: {ex.Message}");
            }
            finally
            {
                var exitCode = _pty.GetExitCode();
                Console.WriteLine($"[Session {Id[..8]}] Ended with exit code {exitCode}");
                SessionEnded?.Invoke(exitCode);
            }
        });
    }

    private void AppendToScrollback(byte[] data)
    {
        lock (_scrollbackLock)
        {
            // 버퍼가 너무 크면 앞부분 제거
            if (_scrollbackBuffer.Length + data.Length > _maxScrollbackSize)
            {
                var excess = (int)(_scrollbackBuffer.Length + data.Length - _maxScrollbackSize);
                var existing = _scrollbackBuffer.ToArray();
                _scrollbackBuffer.SetLength(0);
                _scrollbackBuffer.Write(existing, excess, existing.Length - excess);
            }
            
            _scrollbackBuffer.Write(data);
        }
    }

    /// <summary>
    /// 현재 스크롤백 버퍼 내용 가져오기
    /// </summary>
    public byte[] GetScrollbackBuffer()
    {
        lock (_scrollbackLock)
        {
            return _scrollbackBuffer.ToArray();
        }
    }

    /// <summary>
    /// 클라이언트 연결
    /// </summary>
    /// <param name="clientId">연결할 클라이언트 ID</param>
    /// <param name="cols">터미널 너비</param>
    /// <param name="rows">터미널 높이</param>
    /// <param name="forceDetach">true면 기존 연결을 강제로 분리 (GNU screen -d -r)</param>
    /// <returns>성공 여부. forceDetach=false이고 다른 클라이언트가 연결 중이면 false</returns>
    public bool Attach(string clientId, short cols, short rows, bool forceDetach = false)
    {
        string? previousClientId = null;

        lock (_attachLock)
        {
            if (IsAttached && AttachedClientId != clientId)
            {
                if (!forceDetach)
                {
                    // 이미 다른 클라이언트가 연결됨
                    return false;
                }
                // 강제 분리
                previousClientId = AttachedClientId;
            }

            AttachedClientId = clientId;

            // Resize를 lock 안에서 수행하여 Detach와의 race condition 방지
            try
            {
                _pty.Resize(cols, rows);
            }
            catch
            {
                // 리사이즈 실패해도 연결은 유지
            }
        }

        // 강제 분리된 경우 로그 (lock 밖에서 출력)
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
    /// 터미널에 입력 전송
    /// </summary>
    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        await _inputStream.WriteAsync(data, ct);
        await _inputStream.FlushAsync(ct);
    }

    /// <summary>
    /// 터미널 크기 조정
    /// </summary>
    public void Resize(short cols, short rows)
    {
        if (_disposed) return;
        try
        {
            _pty.Resize(cols, rows);
        }
        catch (ObjectDisposedException)
        {
            // 세션이 dispose된 경우 무시
        }
    }

    /// <summary>
    /// 세션 정보 반환
    /// </summary>
    public SessionInfo ToInfo() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        IsAttached = IsAttached,
        WorkingDirectory = WorkingDirectory,
        ProfileName = ProfileName
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        
        try { _readTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        
        _inputStream.Dispose();
        _outputStream.Dispose();
        _pty.Dispose();
        _scrollbackBuffer.Dispose();
        _cts.Dispose();
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
