using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinScreen.Core.Protocol;

/// <summary>
/// 클라이언트 → 서버 메시지 타입
/// </summary>
public enum ClientMessageType
{
    /// <summary>세션 목록 요청</summary>
    ListSessions,
    /// <summary>새 세션 생성</summary>
    CreateSession,
    /// <summary>세션에 연결</summary>
    Attach,
    /// <summary>세션에서 분리</summary>
    Detach,
    /// <summary>터미널 입력</summary>
    Input,
    /// <summary>터미널 크기 변경</summary>
    Resize,
    /// <summary>세션 종료</summary>
    KillSession,
    /// <summary>프로필 목록 요청</summary>
    ListProfiles,
    /// <summary>프로필 추가/수정</summary>
    AddProfile,
    /// <summary>프로필 삭제</summary>
    RemoveProfile,
    /// <summary>프로필 상세 조회</summary>
    GetProfile,
    /// <summary>프로필 초기화</summary>
    ResetProfiles,
    /// <summary>기본 프로필 조회</summary>
    GetDefaultProfile,
    /// <summary>기본 프로필 설정</summary>
    SetDefaultProfile,
    /// <summary>서버 종료</summary>
    Shutdown
}

/// <summary>
/// 서버 → 클라이언트 메시지 타입
/// </summary>
public enum ServerMessageType
{
    /// <summary>세션 목록</summary>
    SessionList,
    /// <summary>세션 생성됨</summary>
    SessionCreated,
    /// <summary>세션 연결됨</summary>
    Attached,
    /// <summary>세션 분리됨</summary>
    Detached,
    /// <summary>터미널 출력</summary>
    Output,
    /// <summary>세션 종료됨</summary>
    SessionEnded,
    /// <summary>오류</summary>
    Error,
    /// <summary>프로필 목록</summary>
    ProfileList,
    /// <summary>프로필 상세</summary>
    ProfileDetail,
    /// <summary>프로필 작업 성공</summary>
    ProfileOk,
    /// <summary>기본 프로필</summary>
    DefaultProfile
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ListSessionsMessage), "list")]
[JsonDerivedType(typeof(CreateSessionMessage), "create")]
[JsonDerivedType(typeof(AttachMessage), "attach")]
[JsonDerivedType(typeof(DetachMessage), "detach")]
[JsonDerivedType(typeof(InputMessage), "input")]
[JsonDerivedType(typeof(ResizeMessage), "resize")]
[JsonDerivedType(typeof(KillSessionMessage), "kill")]
[JsonDerivedType(typeof(ListProfilesMessage), "profiles")]
[JsonDerivedType(typeof(AddProfileMessage), "addProfile")]
[JsonDerivedType(typeof(RemoveProfileMessage), "removeProfile")]
[JsonDerivedType(typeof(GetProfileMessage), "getProfile")]
[JsonDerivedType(typeof(ResetProfilesMessage), "resetProfiles")]
[JsonDerivedType(typeof(GetDefaultProfileMessage), "getDefault")]
[JsonDerivedType(typeof(SetDefaultProfileMessage), "setDefault")]
[JsonDerivedType(typeof(ShutdownMessage), "shutdown")]
public abstract class ClientMessage
{
    public abstract ClientMessageType Type { get; }
}

public class ListSessionsMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.ListSessions;
}

public class CreateSessionMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.CreateSession;
    public string? SessionName { get; set; }
    public string? ProfileName { get; set; }
    public string? WorkingDirectory { get; set; }
    public short Rows { get; set; } = 24;
    public short Cols { get; set; } = 80;
}

public class AttachMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.Attach;
    public required string SessionId { get; set; }
    public short Rows { get; set; } = 24;
    public short Cols { get; set; } = 80;
}

public class DetachMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.Detach;
}

public class InputMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.Input;
    public required byte[] Data { get; set; }
}

public class ResizeMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.Resize;
    public short Rows { get; set; }
    public short Cols { get; set; }
}

public class KillSessionMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.KillSession;
    public required string SessionId { get; set; }
}

public class ListProfilesMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.ListProfiles;
}

public class ShutdownMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.Shutdown;
}

public class AddProfileMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.AddProfile;
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Shell { get; set; }
    public string? Arguments { get; set; }
    public string? StartupCommand { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
}

public class RemoveProfileMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.RemoveProfile;
    public required string Name { get; set; }
}

public class GetProfileMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.GetProfile;
    public required string Name { get; set; }
}

public class ResetProfilesMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.ResetProfiles;
}

public class GetDefaultProfileMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.GetDefaultProfile;
}

public class SetDefaultProfileMessage : ClientMessage
{
    public override ClientMessageType Type => ClientMessageType.SetDefaultProfile;
    public required string Name { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SessionListMessage), "sessionList")]
[JsonDerivedType(typeof(SessionCreatedMessage), "created")]
[JsonDerivedType(typeof(AttachedMessage), "attached")]
[JsonDerivedType(typeof(DetachedMessage), "detached")]
[JsonDerivedType(typeof(OutputMessage), "output")]
[JsonDerivedType(typeof(SessionEndedMessage), "ended")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ProfileListMessage), "profileList")]
[JsonDerivedType(typeof(ProfileDetailMessage), "profileDetail")]
[JsonDerivedType(typeof(ProfileOkMessage), "profileOk")]
[JsonDerivedType(typeof(DefaultProfileMessage), "defaultProfile")]
public abstract class ServerMessage
{
    public abstract ServerMessageType Type { get; }
}

public class SessionListMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.SessionList;
    public required List<SessionInfo> Sessions { get; set; }
}

public class SessionCreatedMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.SessionCreated;
    public required SessionInfo Session { get; set; }
}

public class AttachedMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.Attached;
    public required SessionInfo Session { get; set; }
    /// <summary>현재까지의 스크롤백 버퍼</summary>
    public byte[]? ScrollbackBuffer { get; set; }
}

public class DetachedMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.Detached;
    public required string SessionId { get; set; }
}

public class OutputMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.Output;
    public required byte[] Data { get; set; }
}

public class SessionEndedMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.SessionEnded;
    public required string SessionId { get; set; }
    public int ExitCode { get; set; }
}

public class ErrorMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.Error;
    public required string Message { get; set; }
}

public class ProfileListMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.ProfileList;
    public required List<ProfileInfo> Profiles { get; set; }
    public string? DefaultProfile { get; set; }
}

public class ProfileDetailMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.ProfileDetail;
    public required ProfileInfo Profile { get; set; }
}

public class ProfileOkMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.ProfileOk;
    public required string Message { get; set; }
}

public class DefaultProfileMessage : ServerMessage
{
    public override ServerMessageType Type => ServerMessageType.DefaultProfile;
    public required string Name { get; set; }
}

/// <summary>
/// 세션 정보
/// </summary>
public class SessionInfo
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required DateTime CreatedAt { get; set; }
    public bool IsAttached { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? ProfileName { get; set; }
}

/// <summary>
/// 프로필 정보
/// </summary>
public class ProfileInfo
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Shell { get; set; }
    public string? Arguments { get; set; }
    public string? StartupCommand { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
}

/// <summary>
/// 프로토콜 직렬화 유틸리티
/// </summary>
public static class ProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static byte[] Serialize<T>(T message)
    {
        // 다형성을 위해 베이스 타입으로 직렬화
        byte[] json;
        if (message is ClientMessage clientMsg)
            json = JsonSerializer.SerializeToUtf8Bytes<ClientMessage>(clientMsg, Options);
        else if (message is ServerMessage serverMsg)
            json = JsonSerializer.SerializeToUtf8Bytes<ServerMessage>(serverMsg, Options);
        else
            json = JsonSerializer.SerializeToUtf8Bytes(message, Options);

        var length = BitConverter.GetBytes(json.Length);
        var result = new byte[4 + json.Length];
        Buffer.BlockCopy(length, 0, result, 0, 4);
        Buffer.BlockCopy(json, 0, result, 4, json.Length);
        return result;
    }

    public static async Task<T?> DeserializeAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var lengthBuffer = new byte[4];
        var read = await stream.ReadAsync(lengthBuffer, ct);
        if (read < 4) return default;

        var length = BitConverter.ToInt32(lengthBuffer);
        if (length <= 0 || length > 10 * 1024 * 1024) // 최대 10MB
            throw new InvalidOperationException($"Invalid message length: {length}");

        var jsonBuffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            read = await stream.ReadAsync(jsonBuffer.AsMemory(totalRead, length - totalRead), ct);
            if (read == 0) throw new EndOfStreamException();
            totalRead += read;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(jsonBuffer, Options);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Protocol error: Failed to deserialize message - {ex.Message}");
            return default;
        }
    }

    public static async Task SendAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        var data = Serialize(message);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }
}
