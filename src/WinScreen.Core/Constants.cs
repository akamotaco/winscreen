namespace WinScreen.Core;

/// <summary>
/// 전역 상수
/// </summary>
public static class Constants
{
    /// <summary>Named Pipe 이름</summary>
    public const string PipeName = "WinScreen";
    
    /// <summary>전체 파이프 경로</summary>
    public static string PipePath => $@"\\.\pipe\{PipeName}";
    
    /// <summary>서버 프로세스 이름</summary>
    public const string ServerProcessName = "winscreen-server";
    
    /// <summary>클라이언트 프로세스 이름</summary>
    public const string ClientProcessName = "screen";
    
    /// <summary>기본 터미널 크기</summary>
    public const short DefaultCols = 120;
    public const short DefaultRows = 30;
    
    /// <summary>Detach 키 시퀀스 (Ctrl+A, D)</summary>
    public static readonly byte[] DetachSequence = new byte[] { 0x01 }; // Ctrl+A
    public const byte DetachKey = (byte)'d';
    
    /// <summary>버전</summary>
    public const string Version = "0.1.0";
}
