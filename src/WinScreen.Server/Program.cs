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
    
    private Session? _attachedSession;

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

        var session = _sessionManager.Create(
            msg.SessionName,
            profile.GetCommandLine(),
            workingDir,
            profile.Name,
            profile.Environment,
            msg.Cols,
            msg.Rows);

        Console.WriteLine($"[{_clientId[..8]}] Created session: {session.Name} ({session.Id[..8]})");
        
        await SendAsync(new SessionCreatedMessage { Session = session.ToInfo() }, ct);
    }

    private async Task HandleAttach(AttachMessage msg, CancellationToken ct)
    {
        // 기존 세션에서 detach
        if (_attachedSession != null)
        {
            _attachedSession.OutputReceived -= OnSessionOutput;
            _attachedSession.SessionEnded -= OnSessionEndedWhileAttached;
            _attachedSession.Detach(_clientId);
        }

        var session = _sessionManager.Get(msg.SessionId) 
                      ?? _sessionManager.GetByName(msg.SessionId);
        
        if (session == null)
        {
            await SendAsync(new ErrorMessage { Message = $"Session not found: {msg.SessionId}" }, ct);
            return;
        }

        if (!session.Attach(_clientId, msg.Cols, msg.Rows))
        {
            await SendAsync(new ErrorMessage { Message = "Session is already attached by another client" }, ct);
            return;
        }

        _attachedSession = session;

        Console.WriteLine($"[{_clientId[..8]}] Attached to session: {session.Name}");

        // 스크롤백 버퍼와 함께 응답 (이벤트 구독 전에 먼저 전송!)
        var scrollback = session.GetScrollbackBuffer();
        await SendAsync(new AttachedMessage
        {
            Session = session.ToInfo(),
            ScrollbackBuffer = scrollback.Length > 0 ? scrollback : null
        }, ct);

        // AttachedMessage 전송 후 이벤트 구독 (순서 중요!)
        session.OutputReceived += OnSessionOutput;
        session.SessionEnded += OnSessionEndedWhileAttached;
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

    private void OnSessionOutput(byte[] data)
    {
        _ = SendAsync(new OutputMessage { Data = data }, CancellationToken.None);
    }

    private void OnSessionEndedWhileAttached(int exitCode)
    {
        if (_attachedSession == null) return;
        
        var sessionId = _attachedSession.Id;
        _attachedSession = null;
        
        _ = SendAsync(new SessionEndedMessage { SessionId = sessionId, ExitCode = exitCode }, CancellationToken.None);
    }

    public void SendNotification(ServerMessage message)
    {
        _ = SendAsync(message, CancellationToken.None);
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
