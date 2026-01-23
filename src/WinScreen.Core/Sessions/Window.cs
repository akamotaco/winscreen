using WinScreen.Core.Protocol;
using WinScreen.Core.Terminal;

namespace WinScreen.Core.Sessions;

/// <summary>
/// 개별 터미널 윈도우 (ConPTY 래퍼)
/// </summary>
public sealed class Window : IDisposable
{
    private const int DefaultMaxScrollbackSize = 1024 * 1024; // 1MB default

    private readonly ConPty _pty;
    private readonly CancellationTokenSource _cts = new();
    private readonly MemoryStream _scrollbackBuffer = new();
    private readonly object _scrollbackLock = new();
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly int _maxScrollbackSize;

    private Task? _readTask;
    private bool _disposed;

    /// <summary>윈도우 인덱스 (세션 내에서 유일)</summary>
    public int Index { get; }

    /// <summary>윈도우 이름</summary>
    public string Name { get; set; }

    /// <summary>생성 시간</summary>
    public DateTime CreatedAt { get; }

    /// <summary>사용된 프로필 이름</summary>
    public string? ProfileName { get; }

    /// <summary>프로세스 종료 여부</summary>
    public bool HasExited => _pty.HasExited;

    /// <summary>종료 코드</summary>
    public int ExitCode => _pty.GetExitCode();

    /// <summary>출력 데이터 수신 이벤트</summary>
    public event Action<byte[]>? OutputReceived;

    /// <summary>윈도우 종료 이벤트</summary>
    public event Action<int>? WindowEnded;

    private Window(
        int index,
        string name,
        ConPty pty,
        string? profileName,
        int maxScrollbackSize)
    {
        Index = index;
        Name = name;
        _pty = pty;
        CreatedAt = DateTime.UtcNow;
        ProfileName = profileName;
        _maxScrollbackSize = maxScrollbackSize > 0 ? maxScrollbackSize : DefaultMaxScrollbackSize;

        // 파이프 스트림 생성 (anonymous pipe는 overlapped I/O 미지원)
        _inputStream = new FileStream(_pty.PipeIn!, FileAccess.Write, 4096, false);
        _outputStream = new FileStream(_pty.PipeOut!, FileAccess.Read, 4096, false);
    }

    /// <summary>
    /// 새 윈도우 생성
    /// </summary>
    /// <param name="index">윈도우 인덱스</param>
    /// <param name="name">윈도우 이름</param>
    /// <param name="commandLine">실행할 명령어</param>
    /// <param name="workingDirectory">작업 디렉토리</param>
    /// <param name="profileName">프로필 이름</param>
    /// <param name="environment">환경 변수</param>
    /// <param name="cols">터미널 너비</param>
    /// <param name="rows">터미널 높이</param>
    /// <param name="maxScrollbackSize">스크롤백 버퍼 최대 크기 (바이트). 0이면 기본값 1MB</param>
    public static Window Create(
        int index,
        string? name,
        string commandLine,
        string? workingDirectory = null,
        string? profileName = null,
        Dictionary<string, string>? environment = null,
        short cols = 120,
        short rows = 30,
        int maxScrollbackSize = 0)
    {
        name ??= $"window-{index}";

        var pty = ConPty.Create(
            commandLine,
            workingDirectory,
            environment,
            cols,
            rows);

        var window = new Window(index, name, pty, profileName, maxScrollbackSize);
        window.StartReading();
        return window;
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
                Console.Error.WriteLine($"[Window {Index}] Read error: {ex.Message}");
            }
            finally
            {
                var exitCode = _pty.GetExitCode();
                Console.WriteLine($"[Window {Index}] Ended with exit code {exitCode}");
                WindowEnded?.Invoke(exitCode);
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
                var currentLength = (int)_scrollbackBuffer.Length;
                var keepLength = currentLength - excess;

                if (keepLength > 0)
                {
                    // GetBuffer()로 내부 버퍼 직접 접근 (복사 없음)
                    var buffer = _scrollbackBuffer.GetBuffer();
                    // 유지할 부분을 버퍼 앞쪽으로 이동
                    Buffer.BlockCopy(buffer, excess, buffer, 0, keepLength);
                    _scrollbackBuffer.SetLength(keepLength);
                    _scrollbackBuffer.Position = keepLength;
                }
                else
                {
                    _scrollbackBuffer.SetLength(0);
                    _scrollbackBuffer.Position = 0;
                }
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
            // 윈도우가 dispose된 경우 무시
        }
    }

    /// <summary>
    /// 윈도우 정보 반환
    /// </summary>
    public WindowInfo ToInfo(bool isActive = false) => new()
    {
        Index = Index,
        Name = Name,
        CreatedAt = CreatedAt,
        IsActive = isActive,
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
