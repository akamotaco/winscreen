using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using WinScreen.Core;
using WinScreen.Core.Protocol;

namespace WinScreen.Client;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            return await new ScreenClient().RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine($"Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
            return 1;
        }
    }
}

class ScreenClient
{
    private NamedPipeClientStream? _pipe;
    private bool _isAttached;
    private string? _attachedSessionId;
    private readonly CancellationTokenSource _cts = new();
    
    // Ctrl+A 상태 추적
    private bool _ctrlAPressed;
    private DateTime _ctrlAPressedTime;

    public async Task<int> RunAsync(string[] args)
    {
        var parsed = ParseArgs(args);

        // 서버 연결이 필요 없는 명령어 먼저 처리
        if (parsed.Command == Command.Help)
            return ShowHelp();

        if (parsed.Command == Command.ServerStatus)
            return await CheckServerStatus();

        if (parsed.Command == Command.ServerStop)
            return await StopServer();

        if (parsed.Command == Command.ServerStart)
            return await StartServer();

        // 서버 시작 확인/자동 시작
        await EnsureServerRunning();

        return parsed.Command switch
        {
            Command.List => await ListSessions(),
            Command.ListProfiles => await ListProfiles(),
            Command.Create => await CreateAndAttach(parsed),
            Command.Attach => await AttachToSession(parsed.SessionId),
            Command.AttachOrCreate => await AttachOrCreate(parsed),
            Command.Detach => await DetachSession(parsed.SessionId),
            Command.Kill => await KillSession(parsed.SessionId!),
            Command.Wipe => await WipeAllSessions(),
            Command.ProfileAdd => await AddProfile(parsed),
            Command.ProfileRemove => await RemoveProfile(parsed.ProfileName),
            Command.ProfileShow => await ShowProfile(parsed.ProfileName),
            Command.ProfileReset => await ResetProfiles(),
            Command.GetDefault => await GetDefaultProfile(),
            Command.SetDefault => await SetDefaultProfile(parsed.ProfileName),
            _ => await CreateAndAttach(parsed)
        };
    }

    private async Task EnsureServerRunning()
    {
        // 파이프 연결 시도
        try
        {
            _pipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(500);
            return;
        }
        catch (TimeoutException)
        {
            _pipe?.Dispose();
            _pipe = null;
        }

        // 서버 시작
        Console.WriteLine("Starting WinScreen server...");

        var serverPath = FindServerExecutable();
        if (serverPath == null)
        {
            Console.Error.WriteLine("Error: winscreen-server.exe not found.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Please ensure winscreen-server.exe is:");
            Console.Error.WriteLine("  1. In the same directory as screen.exe");
            Console.Error.WriteLine("  2. Or in a directory listed in your PATH");
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Current directory: {AppDomain.CurrentDomain.BaseDirectory}");
            throw new FileNotFoundException("winscreen-server.exe not found");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to start server: {ex.Message}");
            throw;
        }

        // 서버 시작 대기
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            try
            {
                _pipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(500);
                Console.WriteLine("Server started successfully.");
                return;
            }
            catch (TimeoutException)
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }

        Console.Error.WriteLine("Error: Server failed to start within timeout.");
        Console.Error.WriteLine("  Check if another instance is running or if there are permission issues.");
        Console.Error.WriteLine("  Use 'screen --server' to check server status.");
        throw new Exception("Failed to start server");
    }

    private static string? FindServerExecutable()
    {
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;
        var serverName = "winscreen-server.exe";

        // 1. 같은 디렉토리
        var path = Path.Combine(currentDir, serverName);
        if (File.Exists(path)) return path;

        // 2. PATH에서 검색
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            path = Path.Combine(dir, serverName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private async Task<int> CheckServerStatus()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500);

            // 서버에 연결 성공 - 세션 목록 요청
            await ProtocolSerializer.SendAsync(pipe, new ListSessionsMessage(), CancellationToken.None);
            var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(pipe, CancellationToken.None);

            Console.WriteLine("WinScreen server is running.");

            if (response is SessionListMessage list)
            {
                Console.WriteLine($"  Active sessions: {list.Sessions.Count}");
                if (list.Sessions.Count > 0)
                {
                    var attached = list.Sessions.Count(s => s.IsAttached);
                    var detached = list.Sessions.Count - attached;
                    Console.WriteLine($"    Attached: {attached}, Detached: {detached}");
                }
            }

            // 서버 프로세스 정보
            var serverProcesses = Process.GetProcessesByName("winscreen-server");
            if (serverProcesses.Length > 0)
            {
                var proc = serverProcesses[0];
                Console.WriteLine($"  Server PID: {proc.Id}");
                Console.WriteLine($"  Memory: {proc.WorkingSet64 / 1024 / 1024} MB");
            }

            return 0;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("WinScreen server is not running.");
            Console.WriteLine("  Use 'screen' to start the server automatically.");
            return 1;
        }
    }

    private async Task<int> StopServer()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500);

            // 서버에 종료 요청
            await ProtocolSerializer.SendAsync(pipe, new ShutdownMessage(), CancellationToken.None);

            Console.WriteLine("Shutdown signal sent to WinScreen server.");

            // 서버 종료 대기
            await Task.Delay(500);

            // 종료 확인
            try
            {
                using var checkPipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await checkPipe.ConnectAsync(200);
                Console.WriteLine("Warning: Server may still be running.");
            }
            catch
            {
                Console.WriteLine("Server stopped successfully.");
            }

            return 0;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("WinScreen server is not running.");
            return 0;
        }
    }

    private async Task<int> StartServer()
    {
        // 1. 기존 서버가 실행 중이면 먼저 종료
        try
        {
            using var pipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500);

            Console.WriteLine("Stopping existing server...");
            await ProtocolSerializer.SendAsync(pipe, new ShutdownMessage(), CancellationToken.None);

            // 서버 종료 대기
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                try
                {
                    using var checkPipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await checkPipe.ConnectAsync(100);
                    // 아직 실행 중
                }
                catch (TimeoutException)
                {
                    // 서버가 종료됨
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            // 서버가 이미 실행 중이지 않음
        }

        // 2. 새 서버 시작
        Console.WriteLine("Starting WinScreen server...");

        var serverPath = FindServerExecutable();
        if (serverPath == null)
        {
            Console.Error.WriteLine("Error: winscreen-server.exe not found.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Please ensure winscreen-server.exe is:");
            Console.Error.WriteLine("  1. In the same directory as screen.exe");
            Console.Error.WriteLine("  2. Or in a directory listed in your PATH");
            return 1;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to start server: {ex.Message}");
            return 1;
        }

        // 3. 서버 시작 확인
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            try
            {
                using var pipe = new NamedPipeClientStream(".", Constants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(500);
                Console.WriteLine("Server started successfully.");

                // 서버 프로세스 정보
                var serverProcesses = Process.GetProcessesByName("winscreen-server");
                if (serverProcesses.Length > 0)
                {
                    Console.WriteLine($"  Server PID: {serverProcesses[0].Id}");
                }

                return 0;
            }
            catch (TimeoutException)
            {
                // 아직 시작 중
            }
        }

        Console.Error.WriteLine("Error: Server failed to start within timeout.");
        return 1;
    }

    private async Task<int> ListSessions()
    {
        await ProtocolSerializer.SendAsync(_pipe!, new ListSessionsMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is SessionListMessage list)
        {
            if (list.Sessions.Count == 0)
            {
                Console.WriteLine("No sessions.");
            }
            else
            {
                Console.WriteLine("Sessions:");
                Console.WriteLine($"{"ID",-12} {"Name",-20} {"Created",-20} {"Status",-12} {"Profile",-10}");
                Console.WriteLine(new string('-', 76));
                
                foreach (var session in list.Sessions)
                {
                    var status = session.IsAttached ? "Attached" : "Detached";
                    Console.WriteLine($"{session.Id[..8],-12} {session.Name,-20} {session.CreatedAt:yyyy-MM-dd HH:mm,-20} {status,-12} {session.ProfileName ?? "-",-10}");
                }
            }
            return 0;
        }
        
        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> ListProfiles()
    {
        await ProtocolSerializer.SendAsync(_pipe!, new ListProfilesMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is ProfileListMessage list)
        {
            Console.WriteLine($"Available profiles (default: {list.DefaultProfile ?? "cmd"}):");
            Console.WriteLine($"{"Name",-15} {"Shell",-25} {"Description"}");
            Console.WriteLine(new string('-', 70));

            foreach (var profile in list.Profiles)
            {
                var name = profile.Name;
                if (list.DefaultProfile != null && name.Equals(list.DefaultProfile, StringComparison.OrdinalIgnoreCase))
                {
                    name += " *";
                }
                Console.WriteLine($"{name,-15} {profile.Shell ?? "-",-25} {profile.Description ?? ""}");
            }
            return 0;
        }

        return 1;
    }

    private async Task<int> AddProfile(ParsedArgs args)
    {
        if (string.IsNullOrEmpty(args.ProfileName))
        {
            Console.Error.WriteLine("Error: Profile name is required.");
            Console.Error.WriteLine("Usage: screen --profile-add <name> --shell <shell> [--args <args>] [--startup <cmd>] [--desc <description>]");
            return 1;
        }

        if (string.IsNullOrEmpty(args.ProfileShell))
        {
            Console.Error.WriteLine("Error: Shell is required for new profile.");
            Console.Error.WriteLine("Usage: screen --profile-add <name> --shell <shell> [--args <args>] [--startup <cmd>] [--desc <description>]");
            return 1;
        }

        var msg = new AddProfileMessage
        {
            Name = args.ProfileName,
            Shell = args.ProfileShell,
            Arguments = args.ProfileArgs,
            StartupCommand = args.ProfileStartup,
            Description = args.ProfileDescription,
            WorkingDirectory = args.WorkingDirectory
        };

        await ProtocolSerializer.SendAsync(_pipe!, msg, _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is ProfileOkMessage ok)
        {
            Console.WriteLine(ok.Message);
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> RemoveProfile(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("Error: Profile name is required.");
            Console.Error.WriteLine("Usage: screen --profile-remove <name>");
            return 1;
        }

        await ProtocolSerializer.SendAsync(_pipe!, new RemoveProfileMessage { Name = name }, _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is ProfileOkMessage ok)
        {
            Console.WriteLine(ok.Message);
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> ShowProfile(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("Error: Profile name is required.");
            Console.Error.WriteLine("Usage: screen --profile-show <name>");
            return 1;
        }

        await ProtocolSerializer.SendAsync(_pipe!, new GetProfileMessage { Name = name }, _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is ProfileDetailMessage detail)
        {
            var p = detail.Profile;
            Console.WriteLine($"Profile: {p.Name}");
            Console.WriteLine($"  Description: {p.Description ?? "-"}");
            Console.WriteLine($"  Shell:       {p.Shell ?? "-"}");
            Console.WriteLine($"  Arguments:   {p.Arguments ?? "-"}");
            Console.WriteLine($"  Startup:     {p.StartupCommand ?? "-"}");
            Console.WriteLine($"  WorkDir:     {p.WorkingDirectory ?? "-"}");
            if (p.Environment != null && p.Environment.Count > 0)
            {
                Console.WriteLine($"  Environment:");
                foreach (var kv in p.Environment)
                {
                    Console.WriteLine($"    {kv.Key}={kv.Value}");
                }
            }
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> ResetProfiles()
    {
        await ProtocolSerializer.SendAsync(_pipe!, new ResetProfilesMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is ProfileOkMessage ok)
        {
            Console.WriteLine(ok.Message);
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> GetDefaultProfile()
    {
        await ProtocolSerializer.SendAsync(_pipe!, new GetDefaultProfileMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is DefaultProfileMessage defaultProfile)
        {
            Console.WriteLine($"Default profile: {defaultProfile.Name}");
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> SetDefaultProfile(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("Error: Profile name is required.");
            Console.Error.WriteLine("Usage: screen --set-default <profile-name>");
            return 1;
        }

        await ProtocolSerializer.SendAsync(_pipe!, new SetDefaultProfileMessage { Name = name }, _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is ProfileOkMessage ok)
        {
            Console.WriteLine(ok.Message);
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> CreateAndAttach(ParsedArgs args)
    {
        try
        {
            // 세션 생성
            var createMsg = new CreateSessionMessage
            {
                SessionName = args.SessionName,
                ProfileName = args.Profile,
                WorkingDirectory = args.WorkingDirectory ?? Environment.CurrentDirectory,
                Cols = (short)Console.WindowWidth,
                Rows = (short)Console.WindowHeight
            };

            await ProtocolSerializer.SendAsync(_pipe!, createMsg, _cts.Token);
            var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

            if (response is SessionCreatedMessage created)
            {
                // 세션 생성 완료 - ConPTY 초기화 대기 후 attach
                await Task.Delay(50);
                return await AttachToSessionInternal(created.Session.Id);
            }

            if (response is ErrorMessage error)
            {
                Console.Error.WriteLine($"Error: {error.Message}");
                return 1;
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            throw;
        }
    }

    private async Task<int> AttachOrCreate(ParsedArgs args)
    {
        // 세션 목록 가져오기
        await ProtocolSerializer.SendAsync(_pipe!, new ListSessionsMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is SessionListMessage list)
        {
            // 특정 세션 ID가 지정된 경우
            if (!string.IsNullOrEmpty(args.SessionId))
            {
                var session = list.Sessions.FirstOrDefault(s =>
                    s.Id.StartsWith(args.SessionId, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals(args.SessionId, StringComparison.OrdinalIgnoreCase));

                if (session != null)
                    return await AttachToSessionInternal(session.Id);

                Console.WriteLine($"Session '{args.SessionId}' not found. Creating new session...");
                return await CreateAndAttach(args);
            }

            // 세션이 없으면 새로 생성
            if (list.Sessions.Count == 0)
            {
                Console.WriteLine("No sessions found. Creating new session...");
                return await CreateAndAttach(args);
            }

            // 세션이 하나면 자동 연결
            if (list.Sessions.Count == 1)
            {
                var session = list.Sessions[0];
                Console.WriteLine($"Attaching to session: {session.Name}");
                return await AttachToSessionInternal(session.Id);
            }

            // 세션이 여러 개면 목록 표시
            Console.WriteLine("Multiple sessions found. Please specify one:");
            Console.WriteLine($"{"ID",-12} {"Name",-20} {"Created",-20} {"Status",-12}");
            Console.WriteLine(new string('-', 66));

            foreach (var session in list.Sessions)
            {
                var status = session.IsAttached ? "Attached" : "Detached";
                Console.WriteLine($"{session.Id[..8],-12} {session.Name,-20} {session.CreatedAt:yyyy-MM-dd HH:mm,-20} {status,-12}");
            }

            Console.WriteLine("\nUse: screen -r <session-id or name>");
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> AttachToSession(string? sessionId)
    {
        // 세션 ID가 지정된 경우 바로 연결
        if (!string.IsNullOrEmpty(sessionId))
        {
            return await AttachToSessionInternal(sessionId);
        }

        // 세션 ID가 없으면 목록에서 자동 선택 (detached 세션만)
        await ProtocolSerializer.SendAsync(_pipe!, new ListSessionsMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is SessionListMessage list)
        {
            // detached 세션만 필터링
            var detachedSessions = list.Sessions.Where(s => !s.IsAttached).ToList();

            // 세션이 없으면 에러
            if (list.Sessions.Count == 0)
            {
                Console.WriteLine("No sessions to resume.");
                return 1;
            }

            // detached 세션이 없으면 에러
            if (detachedSessions.Count == 0)
            {
                Console.WriteLine("No detached sessions available.");
                Console.WriteLine("All sessions are currently attached:");
                foreach (var session in list.Sessions)
                {
                    Console.WriteLine($"  {session.Id[..8]}  {session.Name}  (Attached)");
                }
                return 1;
            }

            // detached 세션이 하나면 자동 연결
            if (detachedSessions.Count == 1)
            {
                var session = detachedSessions[0];
                Console.WriteLine($"Attaching to session: {session.Name}");
                return await AttachToSessionInternal(session.Id);
            }

            // detached 세션이 여러 개면 목록 표시
            Console.WriteLine("Multiple detached sessions found. Please specify one:");
            Console.WriteLine($"{"ID",-12} {"Name",-20} {"Created",-20}");
            Console.WriteLine(new string('-', 54));

            foreach (var session in detachedSessions)
            {
                Console.WriteLine($"{session.Id[..8],-12} {session.Name,-20} {session.CreatedAt:yyyy-MM-dd HH:mm,-20}");
            }

            Console.WriteLine("\nUse: screen -r <session-id or name>");
            return 0;
        }

        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> AttachToSessionInternal(string sessionId)
    {
        var attachMsg = new AttachMessage
        {
            SessionId = sessionId,
            Cols = (short)Console.WindowWidth,
            Rows = (short)Console.WindowHeight
        };
        
        await ProtocolSerializer.SendAsync(_pipe!, attachMsg, _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is AttachedMessage attached)
        {
            _isAttached = true;
            _attachedSessionId = attached.Session.Id;

            return await RunTerminalLoop(attached.ScrollbackBuffer);
        }
        
        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> RunTerminalLoop(byte[]? scrollbackBuffer = null)
    {
        // 콘솔 모드 설정
        EnableVirtualTerminal();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            // Ctrl+C는 터미널로 전달
        };

        // 스크롤백 버퍼 출력 (VT 모드 설정 후)
        if (scrollbackBuffer != null && scrollbackBuffer.Length > 0)
        {
            var scrollbackText = System.Text.Encoding.UTF8.GetString(scrollbackBuffer);
            Console.Write(scrollbackText);
        }

        // 읽기 태스크: 서버에서 출력 받아서 콘솔에 출력
        var readTask = Task.Run(async () =>
        {
            try
            {
                // DEBUG: readTask 시작 확인
                Console.Error.WriteLine("[readTask started]");

                while (_isAttached && !_cts.Token.IsCancellationRequested)
                {
                    var msg = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

                    switch (msg)
                    {
                        case OutputMessage output:
                            // DEBUG: 출력 수신 확인
                            Console.Error.WriteLine($"[RECV:{output.Data.Length}]");

                            // Console.Write 직접 사용 (버퍼링 이슈 방지)
                            var text = System.Text.Encoding.UTF8.GetString(output.Data);
                            Console.Write(text);
                            break;

                        case SessionEndedMessage ended:
                            Console.Write($"\r\n[Session ended with exit code {ended.ExitCode}]\r\n");
                            _isAttached = false;
                            _cts.Cancel();
                            return;

                        case DetachedMessage:
                            Console.Write("\r\n[Detached]\r\n");
                            _isAttached = false;
                            return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\r\nRead error: {ex.Message}");
                _isAttached = false;
            }
        });

        // 쓰기 태스크: 콘솔 입력을 서버로 전송
        var writeTask = Task.Run(async () =>
        {
            try
            {
                // readTask가 DeserializeAsync에 진입할 시간 확보
                await Task.Delay(50, _cts.Token);

                while (_isAttached && !_cts.Token.IsCancellationRequested)
                {
                    // Console.KeyAvailable로 블로킹 방지
                    if (!Console.KeyAvailable)
                    {
                        await Task.Delay(10, _cts.Token);
                        continue;
                    }

                    var keyInfo = Console.ReadKey(intercept: true);

                    // Ctrl+A 감지
                    if (keyInfo.Key == ConsoleKey.A && keyInfo.Modifiers == ConsoleModifiers.Control)
                    {
                        _ctrlAPressed = true;
                        _ctrlAPressedTime = DateTime.UtcNow;
                        continue;
                    }

                    // Ctrl+A 후 다른 키
                    if (_ctrlAPressed)
                    {
                        _ctrlAPressed = false;

                        // 1초 이내
                        if ((DateTime.UtcNow - _ctrlAPressedTime).TotalSeconds < 1)
                        {
                            if (keyInfo.Key == ConsoleKey.D)
                            {
                                await ProtocolSerializer.SendAsync(_pipe!, new DetachMessage(), _cts.Token);
                                _isAttached = false;
                                return;
                            }

                            if (keyInfo.Key == ConsoleKey.A)
                            {
                                await SendInput(new byte[] { 0x01 });
                                continue;
                            }

                            if (keyInfo.Key == ConsoleKey.K)
                            {
                                await ProtocolSerializer.SendAsync(_pipe!, new KillSessionMessage { SessionId = _attachedSessionId! }, _cts.Token);
                                _isAttached = false;
                                return;
                            }

                            if (keyInfo.KeyChar == '?')
                            {
                                ShowTerminalHelp();
                                continue;
                            }
                        }

                        // 그 외: Ctrl+A 전송 후 현재 키 전송
                        await SendInput(new byte[] { 0x01 });
                    }

                    // 키를 바이트로 변환
                    var bytes = KeyInfoToBytes(keyInfo);
                    if (bytes.Length > 0)
                    {
                        await SendInput(bytes);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\r\nWrite error: {ex.Message}");
                _isAttached = false;
            }
        });

        // 창 크기 변경 감시
        var resizeTask = Task.Run(async () =>
        {
            var lastWidth = Console.WindowWidth;
            var lastHeight = Console.WindowHeight;
            
            while (_isAttached && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, _cts.Token);
                
                var width = Console.WindowWidth;
                var height = Console.WindowHeight;
                
                if (width != lastWidth || height != lastHeight)
                {
                    lastWidth = width;
                    lastHeight = height;
                    
                    await ProtocolSerializer.SendAsync(_pipe!, new ResizeMessage 
                    { 
                        Cols = (short)width, 
                        Rows = (short)height 
                    }, _cts.Token);
                }
            }
        });

        await Task.WhenAny(readTask, writeTask);
        _cts.Cancel();

        try { await readTask; } catch { }
        try { await writeTask; } catch { }
        try { await resizeTask; } catch { }

        // 콘솔 모드 복원
        RestoreConsoleMode();

        return 0;
    }

    private async Task SendInput(byte[] data)
    {
        await ProtocolSerializer.SendAsync(_pipe!, new InputMessage { Data = data }, _cts.Token);
    }

    private static byte[] KeyInfoToBytes(ConsoleKeyInfo keyInfo)
    {
        // 특수키 처리 (VT 시퀀스로 변환)
        var sequence = keyInfo.Key switch
        {
            ConsoleKey.UpArrow => "\x1b[A",
            ConsoleKey.DownArrow => "\x1b[B",
            ConsoleKey.RightArrow => "\x1b[C",
            ConsoleKey.LeftArrow => "\x1b[D",
            ConsoleKey.Home => "\x1b[H",
            ConsoleKey.End => "\x1b[F",
            ConsoleKey.Insert => "\x1b[2~",
            ConsoleKey.Delete => "\x1b[3~",
            ConsoleKey.PageUp => "\x1b[5~",
            ConsoleKey.PageDown => "\x1b[6~",
            ConsoleKey.F1 => "\x1bOP",
            ConsoleKey.F2 => "\x1bOQ",
            ConsoleKey.F3 => "\x1bOR",
            ConsoleKey.F4 => "\x1bOS",
            ConsoleKey.F5 => "\x1b[15~",
            ConsoleKey.F6 => "\x1b[17~",
            ConsoleKey.F7 => "\x1b[18~",
            ConsoleKey.F8 => "\x1b[19~",
            ConsoleKey.F9 => "\x1b[20~",
            ConsoleKey.F10 => "\x1b[21~",
            ConsoleKey.F11 => "\x1b[23~",
            ConsoleKey.F12 => "\x1b[24~",
            ConsoleKey.Backspace => "\x7f",
            ConsoleKey.Tab => "\t",
            ConsoleKey.Enter => "\r",
            ConsoleKey.Escape => "\x1b",
            _ => null
        };

        if (sequence != null)
            return System.Text.Encoding.UTF8.GetBytes(sequence);

        // Ctrl 조합
        if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control) && keyInfo.Key >= ConsoleKey.A && keyInfo.Key <= ConsoleKey.Z)
        {
            return new byte[] { (byte)(keyInfo.Key - ConsoleKey.A + 1) };
        }

        // 일반 문자
        if (keyInfo.KeyChar != '\0')
        {
            return System.Text.Encoding.UTF8.GetBytes(new[] { keyInfo.KeyChar });
        }

        return Array.Empty<byte>();
    }

    private static void ShowTerminalHelp()
    {
        Console.WriteLine("\r\n--- WinScreen Key Bindings ---");
        Console.WriteLine("  Ctrl+A, D    Detach from session");
        Console.WriteLine("  Ctrl+A, K    Kill session");
        Console.WriteLine("  Ctrl+A, A    Send Ctrl+A");
        Console.WriteLine("  Ctrl+A, ?    Show this help");
        Console.WriteLine("------------------------------\r\n");
    }

    private async Task<int> DetachSession(string? sessionId)
    {
        if (sessionId == null)
        {
            Console.Error.WriteLine("No session ID specified");
            return 1;
        }
        
        // Detach는 attach된 상태에서만 가능
        Console.WriteLine("Detach command is only available while attached to a session.");
        Console.WriteLine("Use 'screen -r <session>' to attach first, then press Ctrl+A, D to detach.");
        return 1;
    }

    private async Task<int> KillSession(string sessionId)
    {
        await ProtocolSerializer.SendAsync(_pipe!, new KillSessionMessage { SessionId = sessionId }, _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is SessionEndedMessage)
        {
            Console.WriteLine($"Session {sessionId} killed.");
            return 0;
        }
        
        if (response is ErrorMessage error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }

        return 1;
    }

    private async Task<int> WipeAllSessions()
    {
        await ProtocolSerializer.SendAsync(_pipe!, new ListSessionsMessage(), _cts.Token);
        var response = await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);

        if (response is SessionListMessage list)
        {
            foreach (var session in list.Sessions)
            {
                await ProtocolSerializer.SendAsync(_pipe!, new KillSessionMessage { SessionId = session.Id }, _cts.Token);
                await ProtocolSerializer.DeserializeAsync<ServerMessage>(_pipe!, _cts.Token);
                Console.WriteLine($"Killed session: {session.Name}");
            }
            Console.WriteLine($"All {list.Sessions.Count} session(s) killed.");
            return 0;
        }

        return 1;
    }

    private static int ShowHelp()
    {
        Console.WriteLine($@"
WinScreen v{Constants.Version} - Windows Screen-like Terminal Multiplexer

Usage: screen [options] [command]

Commands:
  (no command)         Create new session and attach
  -ls, -list           List all sessions
  -r, -resume [id]     Attach to detached session (auto-select if only one)
  -R [id]              Attach or create if no session exists
  -S <name>            Create session with name
  -p <profile>         Use profile (cmd, powershell, pwsh, conda, etc.)
  -d <id>              Detach session
  -X kill <id>         Kill session
  -wipe                Kill all sessions

Server Management:
  --server             Check server status
  --server-start       Start server (restarts if already running)
  --server-stop        Stop the server (all sessions will be terminated)

Profile Management:
  --profiles           List available profiles
  --profile-add <name> Add/update profile (requires --shell)
  --profile-remove <n> Remove a profile
  --profile-show <n>   Show profile details
  --profile-reset      Reset profiles to defaults
  --default            Show current default profile
  --set-default <name> Set default profile

Profile Options (for --profile-add):
  --shell <path>       Shell executable (required)
  --args <args>        Shell arguments
  --startup <cmd>      Startup command
  --desc <text>        Profile description

  -h, --help           Show this help

Key Bindings (when attached):
  Ctrl+A, D            Detach from session
  Ctrl+A, K            Kill session
  Ctrl+A, A            Send Ctrl+A to terminal
  Ctrl+A, ?            Show key bindings

Examples:
  screen                    Create new session
  screen -S mywork          Create session named 'mywork'
  screen -p powershell      Create session using PowerShell profile
  screen -ls                List sessions
  screen -r mywork          Attach to 'mywork' session
  screen -r abc123          Attach to session by ID
");
        return 0;
    }

    private static uint _originalInputMode;
    private static uint _originalOutputMode;

    private static void EnableVirtualTerminal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // UTF-8 인코딩 설정
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 입력 모드 저장 (Console.ReadKey가 자체 관리하므로 변경하지 않음)
        var inputHandle = GetStdHandle(-10); // STD_INPUT_HANDLE
        GetConsoleMode(inputHandle, out _originalInputMode);

        // 출력 모드: VT 처리 활성화
        var outputHandle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        GetConsoleMode(outputHandle, out _originalOutputMode);
        SetConsoleMode(outputHandle, _originalOutputMode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }

    private static void RestoreConsoleMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var inputHandle = GetStdHandle(-10);
        SetConsoleMode(inputHandle, _originalInputMode);

        var outputHandle = GetStdHandle(-11);
        SetConsoleMode(outputHandle, _originalOutputMode);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    
    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static ParsedArgs ParseArgs(string[] args)
    {
        var result = new ParsedArgs();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 대소문자 구분이 필요한 옵션 먼저 처리
            if (arg == "-R")
            {
                result.Command = Command.AttachOrCreate;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    result.SessionId = args[++i];
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "-ls":
                case "-list":
                case "--list":
                    result.Command = Command.List;
                    break;
                    
                case "-r":
                case "-resume":
                case "--resume":
                    result.Command = Command.Attach;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        result.SessionId = args[++i];
                    break;
                    
                case "-s":
                    if (i + 1 < args.Length) result.SessionName = args[++i];
                    break;
                    
                case "-p":
                case "--profile":
                    if (i + 1 < args.Length) result.Profile = args[++i];
                    break;
                    
                case "-d":
                case "--detach":
                    result.Command = Command.Detach;
                    if (i + 1 < args.Length) result.SessionId = args[++i];
                    break;
                    
                case "-x":
                    if (i + 1 < args.Length && args[i + 1].ToLowerInvariant() == "kill")
                    {
                        i++;
                        result.Command = Command.Kill;
                        if (i + 1 < args.Length) result.SessionId = args[++i];
                    }
                    break;
                    
                case "-wipe":
                case "--wipe":
                    result.Command = Command.Wipe;
                    break;
                    
                case "--profiles":
                    result.Command = Command.ListProfiles;
                    break;

                case "--server":
                case "--server-status":
                    result.Command = Command.ServerStatus;
                    break;

                case "--server-stop":
                case "--quit":
                    result.Command = Command.ServerStop;
                    break;

                case "--server-start":
                    result.Command = Command.ServerStart;
                    break;

                case "--profile-add":
                    result.Command = Command.ProfileAdd;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        result.ProfileName = args[++i];
                    break;

                case "--profile-remove":
                case "--profile-delete":
                    result.Command = Command.ProfileRemove;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        result.ProfileName = args[++i];
                    break;

                case "--profile-show":
                    result.Command = Command.ProfileShow;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        result.ProfileName = args[++i];
                    break;

                case "--profile-reset":
                    result.Command = Command.ProfileReset;
                    break;

                case "--default":
                case "--get-default":
                    result.Command = Command.GetDefault;
                    break;

                case "--set-default":
                    result.Command = Command.SetDefault;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        result.ProfileName = args[++i];
                    break;

                case "--shell":
                    if (i + 1 < args.Length) result.ProfileShell = args[++i];
                    break;

                case "--args":
                    if (i + 1 < args.Length) result.ProfileArgs = args[++i];
                    break;

                case "--startup":
                    if (i + 1 < args.Length) result.ProfileStartup = args[++i];
                    break;

                case "--desc":
                case "--description":
                    if (i + 1 < args.Length) result.ProfileDescription = args[++i];
                    break;

                case "-h":
                case "--help":
                case "/?":
                    result.Command = Command.Help;
                    break;
                    
                default:
                    // 위치 인자로 세션 이름 또는 ID
                    if (!arg.StartsWith("-") && result.SessionId == null)
                    {
                        if (result.Command == Command.None)
                        {
                            result.Command = Command.Attach;
                        }
                        result.SessionId = arg;
                    }
                    break;
            }
        }
        
        return result;
    }
}

enum Command
{
    None,
    List,
    ListProfiles,
    Create,
    Attach,
    AttachOrCreate,
    Detach,
    Kill,
    Wipe,
    Help,
    ServerStatus,
    ServerStart,
    ServerStop,
    ProfileAdd,
    ProfileRemove,
    ProfileShow,
    ProfileReset,
    GetDefault,
    SetDefault
}

class ParsedArgs
{
    public Command Command { get; set; } = Command.None;
    public string? SessionId { get; set; }
    public string? SessionName { get; set; }
    public string? Profile { get; set; }
    public string? WorkingDirectory { get; set; }

    // Profile management args
    public string? ProfileName { get; set; }
    public string? ProfileShell { get; set; }
    public string? ProfileArgs { get; set; }
    public string? ProfileStartup { get; set; }
    public string? ProfileDescription { get; set; }
}
