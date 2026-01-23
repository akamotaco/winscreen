using System.Collections.Concurrent;
using System.IO.Pipes;
using WinScreen.Core;
using WinScreen.Core.Profiles;
using WinScreen.Core.Protocol;
using WinScreen.Core.Sessions;

namespace WinScreen.Server;

class Program
{
    private const string MutexName = "Global\\WinScreenServer_SingleInstance";

    private static readonly SessionManager _sessionManager = new();
    private static readonly ProfileStore _profileStore = new();
    private static readonly ConcurrentDictionary<string, ClientHandler> _clients = new();
    private static readonly CancellationTokenSource _cts = new();

    static async Task Main(string[] args)
    {
        // 단일 인스턴스 보장을 위한 Mutex
        using var mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            Console.WriteLine("WinScreen Server is already running.");
            return;
        }

        Console.WriteLine($"WinScreen Server v{Constants.Version} starting...");

        // Ctrl+C 핸들러
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            _cts.Cancel();
        };

        // 세션 이벤트 등록
        _sessionManager.SessionEnded += OnSessionEnded;

        try
        {
            await RunServerAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        finally
        {
            _sessionManager.Dispose();
            Console.WriteLine("Server stopped.");
        }
    }

    private static async Task RunServerAsync(CancellationToken ct)
    {
        Console.WriteLine($"Listening on pipe: {Constants.PipeName}");

        while (!ct.IsCancellationRequested)
        {
            var pipeServer = new NamedPipeServerStream(
                Constants.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipeServer.WaitForConnectionAsync(ct);
                
                var clientId = Guid.NewGuid().ToString("N");
                Console.WriteLine($"[{clientId[..8]}] Client connected");
                
                var handler = new ClientHandler(clientId, pipeServer, _sessionManager, _profileStore);
                _clients[clientId] = handler;
                
                // 비동기로 클라이언트 처리
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler.RunAsync(ct);
                    }
                    finally
                    {
                        _clients.TryRemove(clientId, out _);
                        Console.WriteLine($"[{clientId[..8]}] Client disconnected");
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                pipeServer.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error accepting client: {ex.Message}");
                pipeServer.Dispose();
            }
        }
    }

    private static void OnSessionEnded(string sessionId, int exitCode)
    {
        Console.WriteLine($"Session {sessionId[..8]} ended with exit code {exitCode}");
        
        // 연결된 클라이언트들에게 알림
        var message = new SessionEndedMessage 
        { 
            SessionId = sessionId, 
            ExitCode = exitCode 
        };
        
        foreach (var client in _clients.Values)
        {
            try
            {
                client.SendNotification(message);
            }
            catch
            {
                // 전송 실패 무시
            }
        }
    }
}

/// <summary>
/// 개별 클라이언트 연결 처리
/// </summary>
class ClientHandler
{
    private readonly string _clientId;
    private readonly NamedPipeServerStream _pipe;
    private readonly SessionManager _sessionManager;
    private readonly ProfileStore _profileStore;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Attach 중 출력 버퍼링을 위한 필드
    private const int MaxAttachingOutputQueueSize = 1000;
    private readonly ConcurrentQueue<byte[]> _attachingOutput = new();
    private volatile bool _isAttaching;

    private Session? _attachedSession;

    // 현재 클라이언트 터미널 크기 (윈도우 전환 시 리사이즈용)
    private short _terminalCols;
    private short _terminalRows;

    public ClientHandler(
        string clientId,
        NamedPipeServerStream pipe,
        SessionManager sessionManager,
        ProfileStore profileStore)
    {
        _clientId = clientId;
        _pipe = pipe;
        _sessionManager = sessionManager;
        _profileStore = profileStore;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (_pipe.IsConnected && !ct.IsCancellationRequested)
            {
                Console.WriteLine($"[{_clientId[..8]}] Waiting for message...");
                var message = await ProtocolSerializer.DeserializeAsync<ClientMessage>(_pipe, ct);
                Console.WriteLine($"[{_clientId[..8]}] Got message: {message?.GetType().Name ?? "null"}");
                if (message == null) break;

                await HandleMessageAsync(message, ct);
            }
        }
        catch (EndOfStreamException ex)
        {
            Console.WriteLine($"[{_clientId[..8]}] EndOfStream: {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[{_clientId[..8]}] IOException: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_clientId[..8]}] Error: {ex.Message}");
            Console.WriteLine($"[{_clientId[..8]}] Stack: {ex.StackTrace}");
        }
        finally
        {
            // 이벤트 핸들러 해제 후 세션에서 detach
            if (_attachedSession != null)
            {
                _attachedSession.OutputReceived -= OnSessionOutput;
                _attachedSession.SessionEnded -= OnSessionEndedWhileAttached;
                _attachedSession.WindowEnded -= OnWindowEndedWhileAttached;
                _attachedSession.ActiveWindowChanged -= OnActiveWindowChanged;
                _attachedSession.Detach(_clientId);
            }
            _pipe.Dispose();
        }
    }

    private async Task HandleMessageAsync(ClientMessage message, CancellationToken ct)
    {
        try
        {
            switch (message)
            {
                case ListSessionsMessage:
                    await HandleListSessions(ct);
                    break;
                    
                case CreateSessionMessage create:
                    await HandleCreateSession(create, ct);
                    break;
                    
                case AttachMessage attach:
                    await HandleAttach(attach, ct);
                    break;
                    
                case DetachMessage:
                    await HandleDetach(ct);
                    break;
                    
                case InputMessage input:
                    await HandleInput(input, ct);
                    break;
                    
                case ResizeMessage resize:
                    HandleResize(resize);
                    break;
                    
                case KillSessionMessage kill:
                    await HandleKillSession(kill, ct);
                    break;
                    
                case ListProfilesMessage:
                    await HandleListProfiles(ct);
                    break;

                case AddProfileMessage addProfile:
                    await HandleAddProfile(addProfile, ct);
                    break;

                case RemoveProfileMessage removeProfile:
                    await HandleRemoveProfile(removeProfile, ct);
                    break;

                case GetProfileMessage getProfile:
                    await HandleGetProfile(getProfile, ct);
                    break;

                case ResetProfilesMessage:
                    await HandleResetProfiles(ct);
                    break;

                case GetDefaultProfileMessage:
                    await HandleGetDefaultProfile(ct);
                    break;

                case SetDefaultProfileMessage setDefault:
                    await HandleSetDefaultProfile(setDefault, ct);
                    break;

                case ShutdownMessage:
                    Environment.Exit(0);
                    break;

                // 윈도우 관련 메시지 처리
                case CreateWindowMessage createWindow:
                    await HandleCreateWindow(createWindow, ct);
                    break;

                case KillWindowMessage killWindow:
                    await HandleKillWindow(killWindow, ct);
                    break;

                case SwitchWindowMessage switchWindow:
                    await HandleSwitchWindow(switchWindow, ct);
                    break;

                case NextWindowMessage:
                    await HandleNextWindow(ct);
                    break;

                case PreviousWindowMessage:
                    await HandlePreviousWindow(ct);
                    break;

                case ListWindowsMessage:
                    await HandleListWindows(ct);
                    break;

                case RenameWindowMessage renameWindow:
                    await HandleRenameWindow(renameWindow, ct);
                    break;

                case RenameSessionMessage renameSession:
                    await HandleRenameSession(renameSession, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendAsync(new ErrorMessage { Message = ex.Message }, ct);
        }
    }

    private async Task HandleListSessions(CancellationToken ct)
    {
        var sessions = _sessionManager.GetAllInfo().ToList();
        await SendAsync(new SessionListMessage { Sessions = sessions }, ct);
    }

    private async Task HandleCreateSession(CreateSessionMessage msg, CancellationToken ct)
    {
        var profile = _profileStore.GetOrDefault(msg.ProfileName);
        var workingDir = msg.WorkingDirectory ?? profile.WorkingDirectory ?? Environment.CurrentDirectory;

        // 세션 이름 중복 시 자동으로 고유 이름 생성
        string? warning = null;
        var sessionName = msg.SessionName;
        if (!string.IsNullOrEmpty(sessionName) && _sessionManager.ExistsByName(sessionName))
        {
            var uniqueName = _sessionManager.GetUniqueSessionName(sessionName);
            warning = $"Session name '{sessionName}' already exists. Created as '{uniqueName}' instead.";
            Console.WriteLine($"[{_clientId[..8]}] {warning}");
            sessionName = uniqueName;
        }

        var session = _sessionManager.Create(
            sessionName,
            profile.GetCommandLine(),
            workingDir,
            profile.Name,
            profile.Environment,
            msg.Cols,
            msg.Rows,
            _profileStore.MaxScrollbackSize);

        Console.WriteLine($"[{_clientId[..8]}] Created session: {session.Name} ({session.Id[..8]})");

        await SendAsync(new SessionCreatedMessage { Session = session.ToInfo(), Warning = warning }, ct);
    }

    private async Task HandleAttach(AttachMessage msg, CancellationToken ct)
    {
        // 기존 세션에서 detach
        if (_attachedSession != null)
        {
            _attachedSession.OutputReceived -= OnSessionOutput;
            _attachedSession.SessionEnded -= OnSessionEndedWhileAttached;
            _attachedSession.WindowEnded -= OnWindowEndedWhileAttached;
            _attachedSession.ActiveWindowChanged -= OnActiveWindowChanged;
            _attachedSession.Detach(_clientId);
        }

        var session = _sessionManager.Get(msg.SessionId) 
                      ?? _sessionManager.GetByName(msg.SessionId);
        
        if (session == null)
        {
            await SendAsync(new ErrorMessage { Message = $"Session not found: {msg.SessionId}" }, ct);
            return;
        }

        if (!session.Attach(_clientId, msg.Cols, msg.Rows, msg.ForceDetach))
        {
            await SendAsync(new ErrorMessage
            {
                Message = $"Session '{session.Name}' is already attached by another client.\nUse 'screen -d -r {session.Name}' to force detach and reattach."
            }, ct);
            return;
        }

        _attachedSession = session;
        _terminalCols = msg.Cols;
        _terminalRows = msg.Rows;

        Console.WriteLine($"[{_clientId[..8]}] Attached to session: {session.Name}");

        // Race condition 방지: 이벤트 구독 -> 스크롤백 -> 전송 -> 버퍼 flush
        // 1. 버퍼링 모드 시작
        _isAttaching = true;

        // 2. 이벤트 먼저 구독 (이 시점부터 출력은 _attachingOutput에 큐잉됨)
        session.OutputReceived += OnSessionOutput;
        session.SessionEnded += OnSessionEndedWhileAttached;
        session.WindowEnded += OnWindowEndedWhileAttached;
        session.ActiveWindowChanged += OnActiveWindowChanged;

        // 3. 스크롤백 버퍼 가져오기 (이벤트 구독 후이므로 새 출력은 큐에 들어감)
        var scrollback = session.GetScrollbackBuffer();

        // 4. AttachedMessage 전송
        await SendAsync(new AttachedMessage
        {
            Session = session.ToInfo(),
            ScrollbackBuffer = scrollback.Length > 0 ? scrollback : null
        }, ct);

        // 5. 버퍼링 모드 종료 및 큐잉된 출력 전송
        _isAttaching = false;
        while (_attachingOutput.TryDequeue(out var bufferedData))
        {
            await SendAsync(new OutputMessage { Data = bufferedData }, ct);
        }
    }

    private async Task HandleDetach(CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        var sessionId = _attachedSession.Id;
        _attachedSession.OutputReceived -= OnSessionOutput;
        _attachedSession.SessionEnded -= OnSessionEndedWhileAttached;
        _attachedSession.WindowEnded -= OnWindowEndedWhileAttached;
        _attachedSession.ActiveWindowChanged -= OnActiveWindowChanged;
        _attachedSession.Detach(_clientId);
        _attachedSession = null;

        Console.WriteLine($"[{_clientId[..8]}] Detached from session: {sessionId[..8]}");
        
        await SendAsync(new DetachedMessage { SessionId = sessionId }, ct);
    }

    private async Task HandleInput(InputMessage msg, CancellationToken ct)
    {
        if (_attachedSession == null) return;
        await _attachedSession.WriteAsync(msg.Data, ct);
    }

    private void HandleResize(ResizeMessage msg)
    {
        _terminalCols = msg.Cols;
        _terminalRows = msg.Rows;
        _attachedSession?.Resize(msg.Cols, msg.Rows);
    }

    private async Task HandleKillSession(KillSessionMessage msg, CancellationToken ct)
    {
        var session = _sessionManager.Get(msg.SessionId) 
                      ?? _sessionManager.GetByName(msg.SessionId);
        
        if (session == null)
        {
            await SendAsync(new ErrorMessage { Message = $"Session not found: {msg.SessionId}" }, ct);
            return;
        }

        // 현재 attach된 세션이면 detach
        if (_attachedSession?.Id == session.Id)
        {
            _attachedSession.OutputReceived -= OnSessionOutput;
            _attachedSession.SessionEnded -= OnSessionEndedWhileAttached;
            _attachedSession.WindowEnded -= OnWindowEndedWhileAttached;
            _attachedSession.ActiveWindowChanged -= OnActiveWindowChanged;
            _attachedSession = null;
        }

        _sessionManager.Kill(session.Id);
        
        await SendAsync(new SessionEndedMessage { SessionId = session.Id, ExitCode = 0 }, ct);
    }

    private async Task HandleListProfiles(CancellationToken ct)
    {
        var profiles = _profileStore.GetAllInfo().ToList();
        await SendAsync(new ProfileListMessage
        {
            Profiles = profiles,
            DefaultProfile = _profileStore.DefaultProfileName
        }, ct);
    }

    private async Task HandleAddProfile(AddProfileMessage msg, CancellationToken ct)
    {
        var profile = new Profile
        {
            Name = msg.Name,
            Description = msg.Description,
            Shell = msg.Shell,
            Arguments = msg.Arguments,
            StartupCommand = msg.StartupCommand,
            WorkingDirectory = msg.WorkingDirectory,
            Environment = msg.Environment
        };

        _profileStore.Add(profile);
        Console.WriteLine($"[{_clientId[..8]}] Added/Updated profile: {msg.Name}");

        await SendAsync(new ProfileOkMessage { Message = $"Profile '{msg.Name}' saved successfully." }, ct);
    }

    private async Task HandleRemoveProfile(RemoveProfileMessage msg, CancellationToken ct)
    {
        try
        {
            if (_profileStore.Remove(msg.Name))
            {
                Console.WriteLine($"[{_clientId[..8]}] Removed profile: {msg.Name}");
                await SendAsync(new ProfileOkMessage { Message = $"Profile '{msg.Name}' removed." }, ct);
            }
            else
            {
                await SendAsync(new ErrorMessage { Message = $"Profile '{msg.Name}' not found." }, ct);
            }
        }
        catch (InvalidOperationException ex)
        {
            await SendAsync(new ErrorMessage { Message = ex.Message }, ct);
        }
    }

    private async Task HandleGetProfile(GetProfileMessage msg, CancellationToken ct)
    {
        var profile = _profileStore.Get(msg.Name);
        if (profile != null)
        {
            await SendAsync(new ProfileDetailMessage { Profile = profile.ToInfo() }, ct);
        }
        else
        {
            await SendAsync(new ErrorMessage { Message = $"Profile '{msg.Name}' not found." }, ct);
        }
    }

    private async Task HandleResetProfiles(CancellationToken ct)
    {
        _profileStore.Reset();
        Console.WriteLine($"[{_clientId[..8]}] Reset profiles to defaults");
        await SendAsync(new ProfileOkMessage { Message = "Profiles reset to defaults." }, ct);
    }

    private async Task HandleGetDefaultProfile(CancellationToken ct)
    {
        await SendAsync(new DefaultProfileMessage { Name = _profileStore.DefaultProfileName }, ct);
    }

    private async Task HandleSetDefaultProfile(SetDefaultProfileMessage msg, CancellationToken ct)
    {
        try
        {
            _profileStore.SetDefaultProfile(msg.Name);
            Console.WriteLine($"[{_clientId[..8]}] Set default profile: {msg.Name}");
            await SendAsync(new ProfileOkMessage { Message = $"Default profile set to '{msg.Name}'." }, ct);
        }
        catch (ArgumentException ex)
        {
            await SendAsync(new ErrorMessage { Message = ex.Message }, ct);
        }
    }

    // 윈도우 관련 핸들러

    private async Task HandleCreateWindow(CreateWindowMessage msg, CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        var profile = _profileStore.GetOrDefault(msg.ProfileName ?? _attachedSession.ProfileName);
        var workingDir = profile.WorkingDirectory ?? _attachedSession.WorkingDirectory ?? Environment.CurrentDirectory;

        var window = _attachedSession.CreateWindow(
            profile.GetCommandLine(),
            msg.WindowName,
            workingDir,
            profile.Name,
            profile.Environment,
            _terminalCols,
            _terminalRows);

        // 새 윈도우로 자동 전환
        _attachedSession.SwitchWindow(window.Index);

        Console.WriteLine($"[{_clientId[..8]}] Created window {window.Index} in session {_attachedSession.Id[..8]}");

        // 윈도우 생성 메시지 전송
        await SendAsync(new WindowCreatedMessage { Window = window.ToInfo(true) }, ct);

        // 윈도우 전환 메시지 전송 (스크롤백 포함)
        await SendAsync(new WindowSwitchedMessage
        {
            WindowIndex = window.Index,
            Window = window.ToInfo(true),
            ScrollbackBuffer = window.GetScrollbackBuffer()
        }, ct);
    }

    private async Task HandleKillWindow(KillWindowMessage msg, CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        var windowIndex = msg.WindowIndex ?? _attachedSession.ActiveWindowIndex;

        if (!_attachedSession.KillWindow(windowIndex))
        {
            await SendAsync(new ErrorMessage { Message = $"Window {windowIndex} not found" }, ct);
            return;
        }

        Console.WriteLine($"[{_clientId[..8]}] Killed window {windowIndex} in session {_attachedSession.Id[..8]}");

        // WindowEnded 이벤트가 발생하면 OnWindowEndedWhileAttached에서 메시지 전송
    }

    private async Task HandleSwitchWindow(SwitchWindowMessage msg, CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        if (!_attachedSession.SwitchWindow(msg.WindowIndex))
        {
            await SendAsync(new ErrorMessage { Message = $"Window {msg.WindowIndex} not found" }, ct);
            return;
        }

        var window = _attachedSession.ActiveWindow;
        if (window == null) return;

        // 윈도우 전환 시 현재 터미널 크기로 리사이즈
        window.Resize(_terminalCols, _terminalRows);

        Console.WriteLine($"[{_clientId[..8]}] Switched to window {msg.WindowIndex} in session {_attachedSession.Id[..8]}");

        await SendAsync(new WindowSwitchedMessage
        {
            WindowIndex = window.Index,
            Window = window.ToInfo(true),
            ScrollbackBuffer = window.GetScrollbackBuffer()
        }, ct);
    }

    private async Task HandleNextWindow(CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        if (!_attachedSession.NextWindow())
        {
            // 윈도우가 하나뿐이면 무시
            return;
        }

        var window = _attachedSession.ActiveWindow;
        if (window == null) return;

        // 윈도우 전환 시 현재 터미널 크기로 리사이즈
        window.Resize(_terminalCols, _terminalRows);

        Console.WriteLine($"[{_clientId[..8]}] Switched to next window {window.Index}");

        await SendAsync(new WindowSwitchedMessage
        {
            WindowIndex = window.Index,
            Window = window.ToInfo(true),
            ScrollbackBuffer = window.GetScrollbackBuffer()
        }, ct);
    }

    private async Task HandlePreviousWindow(CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        if (!_attachedSession.PreviousWindow())
        {
            // 윈도우가 하나뿐이면 무시
            return;
        }

        var window = _attachedSession.ActiveWindow;
        if (window == null) return;

        // 윈도우 전환 시 현재 터미널 크기로 리사이즈
        window.Resize(_terminalCols, _terminalRows);

        Console.WriteLine($"[{_clientId[..8]}] Switched to previous window {window.Index}");

        await SendAsync(new WindowSwitchedMessage
        {
            WindowIndex = window.Index,
            Window = window.ToInfo(true),
            ScrollbackBuffer = window.GetScrollbackBuffer()
        }, ct);
    }

    private async Task HandleListWindows(CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        var windows = _attachedSession.GetWindowsInfo();

        await SendAsync(new WindowListMessage
        {
            Windows = windows,
            ActiveWindowIndex = _attachedSession.ActiveWindowIndex
        }, ct);
    }

    private async Task HandleRenameWindow(RenameWindowMessage msg, CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        var windowIndex = msg.WindowIndex ?? _attachedSession.ActiveWindowIndex;

        if (!_attachedSession.RenameWindow(windowIndex, msg.NewName))
        {
            await SendAsync(new ErrorMessage { Message = $"Window {windowIndex} not found" }, ct);
            return;
        }

        Console.WriteLine($"[{_clientId[..8]}] Renamed window {windowIndex} to '{msg.NewName}'");

        await SendAsync(new WindowRenamedMessage
        {
            WindowIndex = windowIndex,
            NewName = msg.NewName
        }, ct);
    }

    private async Task HandleRenameSession(RenameSessionMessage msg, CancellationToken ct)
    {
        if (_attachedSession == null)
        {
            await SendAsync(new ErrorMessage { Message = "Not attached to any session" }, ct);
            return;
        }

        var oldName = _attachedSession.Name;
        var newName = msg.NewName;

        // 중복 이름 체크 (자기 자신 제외)
        if (_sessionManager.ExistsByName(newName) &&
            !newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            newName = _sessionManager.GetUniqueSessionName(newName);
        }

        _attachedSession.Name = newName;

        Console.WriteLine($"[{_clientId[..8]}] Renamed session '{oldName}' to '{newName}'");

        await SendAsync(new SessionRenamedMessage { NewName = newName }, ct);
    }

    private async void OnWindowEndedWhileAttached(int windowIndex, int exitCode)
    {
        if (_attachedSession == null) return;

        try
        {
            int? newActiveIndex = null;
            if (_attachedSession.WindowCount > 0)
            {
                newActiveIndex = _attachedSession.ActiveWindowIndex;
            }

            await SendAsync(new WindowEndedMessage
            {
                WindowIndex = windowIndex,
                ExitCode = exitCode,
                NewActiveWindowIndex = newActiveIndex
            }, CancellationToken.None);

            // 새 활성 윈도우가 있으면 전환 메시지 전송
            if (newActiveIndex != null)
            {
                var window = _attachedSession.ActiveWindow;
                if (window != null)
                {
                    // 윈도우 전환 시 현재 터미널 크기로 리사이즈
                    window.Resize(_terminalCols, _terminalRows);

                    await SendAsync(new WindowSwitchedMessage
                    {
                        WindowIndex = window.Index,
                        Window = window.ToInfo(true),
                        ScrollbackBuffer = window.GetScrollbackBuffer()
                    }, CancellationToken.None);
                }
            }
        }
        catch (IOException)
        {
            // 파이프 끊김 - 정상적인 연결 해제
        }
        catch (ObjectDisposedException)
        {
            // 이미 dispose됨 - 무시
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_clientId[..8]}] Window ended notification error: {ex.Message}");
        }
    }

    private async void OnActiveWindowChanged(int newWindowIndex)
    {
        if (_attachedSession == null) return;

        try
        {
            var window = _attachedSession.GetWindow(newWindowIndex);
            if (window != null)
            {
                // 윈도우 전환 시 현재 터미널 크기로 리사이즈
                window.Resize(_terminalCols, _terminalRows);

                await SendAsync(new WindowSwitchedMessage
                {
                    WindowIndex = window.Index,
                    Window = window.ToInfo(true),
                    ScrollbackBuffer = window.GetScrollbackBuffer()
                }, CancellationToken.None);
            }
        }
        catch (IOException)
        {
            // 파이프 끊김
        }
        catch (ObjectDisposedException)
        {
            // 이미 dispose됨
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_clientId[..8]}] Active window changed notification error: {ex.Message}");
        }
    }

    private async void OnSessionOutput(byte[] data)
    {
        // Attach 진행 중이면 버퍼에 큐잉 (race condition 방지)
        if (_isAttaching)
        {
            // 큐 사이즈 제한: 너무 많으면 오래된 항목 제거
            while (_attachingOutput.Count >= MaxAttachingOutputQueueSize)
            {
                _attachingOutput.TryDequeue(out _);
            }
            _attachingOutput.Enqueue(data);
            return;
        }

        try
        {
            await SendAsync(new OutputMessage { Data = data }, CancellationToken.None);
        }
        catch (IOException)
        {
            // 파이프 끊김 - 정상적인 연결 해제
        }
        catch (ObjectDisposedException)
        {
            // 이미 dispose됨 - 무시
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_clientId[..8]}] Output send error: {ex.Message}");
        }
    }

    private async void OnSessionEndedWhileAttached(int exitCode)
    {
        if (_attachedSession == null) return;

        var sessionId = _attachedSession.Id;
        _attachedSession = null;

        try
        {
            await SendAsync(new SessionEndedMessage { SessionId = sessionId, ExitCode = exitCode }, CancellationToken.None);
        }
        catch (IOException)
        {
            // 파이프 끊김 - 정상적인 연결 해제
        }
        catch (ObjectDisposedException)
        {
            // 이미 dispose됨 - 무시
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_clientId[..8]}] Session ended notification error: {ex.Message}");
        }
    }

    public async void SendNotification(ServerMessage message)
    {
        try
        {
            await SendAsync(message, CancellationToken.None);
        }
        catch (IOException)
        {
            // 파이프 끊김 - 무시
        }
        catch (ObjectDisposedException)
        {
            // 이미 dispose됨 - 무시
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_clientId[..8]}] Notification send error: {ex.Message}");
        }
    }

    private async Task SendAsync(ServerMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_pipe.IsConnected)
            {
                await ProtocolSerializer.SendAsync(_pipe, message, ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
