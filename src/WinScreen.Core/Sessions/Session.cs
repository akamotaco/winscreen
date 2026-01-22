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
    private const int MaxScrollbackSize = 1024 * 1024; // 1MB
    
    private readonly ConPty _pty;
    private readonly CancellationTokenSource _cts = new();
    private readonly MemoryStream _scrollbackBuffer = new();
    private readonly object _scrollbackLock = new();
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    
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
        string? profileName)
    {
        Id = id;
        Name = name;
        _pty = pty;
        CreatedAt = DateTime.UtcNow;
        WorkingDirectory = workingDirectory;
        ProfileName = profileName;

        // 파이프 스트림 생성
        _inputStream = new FileStream(_pty.PipeIn!, FileAccess.Write, 4096, false);
        _outputStream = new FileStream(_pty.PipeOut!, FileAccess.Read, 4096, false);
    }

    /// <summary>
    /// 새 세션 생성
    /// </summary>
    public static Session Create(
        string? name,
        string commandLine,
        string? workingDirectory = null,
        string? profileName = null,
        Dictionary<string, string>? environment = null,
        short cols = 120,
        short rows = 30)
    {
        var id = GenerateId();
        name ??= $"session-{id[..8]}";

        var pty = ConPty.Create(
            commandLine,
            workingDirectory,
            environment,
            cols,
            rows);

        var session = new Session(id, name, pty, workingDirectory, profileName);
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
                    var readTask = _outputStream.ReadAsync(buffer, _cts.Token).AsTask();
                    var completed = await Task.WhenAny(readTask, Task.Delay(500, _cts.Token));

                    if (completed != readTask)
                    {
                        // 타임아웃 - 프로세스 종료 확인
                        if (_pty.HasExited) break;
                        continue;
                    }

                    var read = await readTask;
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
            if (_scrollbackBuffer.Length + data.Length > MaxScrollbackSize)
            {
                var excess = (int)(_scrollbackBuffer.Length + data.Length - MaxScrollbackSize);
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
    public bool Attach(string clientId, short cols, short rows)
    {
        if (IsAttached && AttachedClientId != clientId)
        {
            // 이미 다른 클라이언트가 연결됨
            // 기존 연결을 강제로 끊을 수도 있음
            return false;
        }

        AttachedClientId = clientId;
        
        try
        {
            _pty.Resize(cols, rows);
        }
        catch
        {
            // 리사이즈 실패해도 연결은 유지
        }

        return true;
    }

    /// <summary>
    /// 클라이언트 분리
    /// </summary>
    public void Detach(string? clientId = null)
    {
        if (clientId == null || AttachedClientId == clientId)
        {
            AttachedClientId = null;
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
        _pty.Resize(cols, rows);
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
        short rows = 30)
    {
        var session = Session.Create(name, commandLine, workingDirectory, profileName, environment, cols, rows);
        
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
