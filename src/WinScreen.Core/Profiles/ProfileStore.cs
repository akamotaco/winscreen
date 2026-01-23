using System.Text.Json;
using WinScreen.Core.Protocol;

namespace WinScreen.Core.Profiles;

/// <summary>
/// 프로필 설정
/// </summary>
public class Profile
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    
    /// <summary>실행할 쉘 (기본: cmd.exe)</summary>
    public string Shell { get; set; } = "cmd.exe";
    
    /// <summary>쉘 인자</summary>
    public string? Arguments { get; set; }
    
    /// <summary>시작 시 실행할 명령어 (예: conda activate myenv)</summary>
    public string? StartupCommand { get; set; }
    
    /// <summary>작업 디렉토리</summary>
    public string? WorkingDirectory { get; set; }
    
    /// <summary>환경 변수</summary>
    public Dictionary<string, string>? Environment { get; set; }

    public ProfileInfo ToInfo() => new()
    {
        Name = Name,
        Description = Description,
        Shell = Shell,
        Arguments = Arguments,
        StartupCommand = StartupCommand,
        WorkingDirectory = WorkingDirectory,
        Environment = Environment
    };

    /// <summary>
    /// 전체 커맨드 라인 생성
    /// </summary>
    public string GetCommandLine()
    {
        var cmdLine = Shell;
        
        if (!string.IsNullOrEmpty(Arguments))
        {
            cmdLine += " " + Arguments;
        }
        
        // PowerShell이면서 StartupCommand가 있으면
        if (!string.IsNullOrEmpty(StartupCommand))
        {
            var shellLower = Shell.ToLowerInvariant();
            
            if (shellLower.Contains("powershell") || shellLower.Contains("pwsh"))
            {
                // PowerShell: -NoExit -Command "명령어"
                cmdLine += $" -NoExit -Command \"{StartupCommand.Replace("\"", "\\\"")}\"";
            }
            else if (shellLower.Contains("cmd"))
            {
                // CMD: /K "명령어"
                cmdLine += $" /K \"{StartupCommand}\"";
            }
            // 그 외 쉘은 StartupCommand 무시 (또는 별도 처리 필요)
        }
        
        return cmdLine;
    }
}

/// <summary>
/// 프로필 설정 파일 구조
/// </summary>
internal class ProfileConfig
{
    public string DefaultProfile { get; set; } = "cmd";
    public List<Profile> Profiles { get; set; } = new();
    /// <summary>스크롤백 버퍼 최대 크기 (KB). 기본 1024KB (1MB)</summary>
    public int MaxScrollbackSizeKB { get; set; } = 1024;
}

/// <summary>
/// 프로필 저장소
/// </summary>
public class ProfileStore
{
    private readonly string _configPath;
    private Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private string _defaultProfileName = "cmd";
    private int _maxScrollbackSizeKB = 1024; // 1MB default

    /// <summary>스크롤백 버퍼 최대 크기 (바이트)</summary>
    public int MaxScrollbackSize => _maxScrollbackSizeKB * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>기본 프로필 이름</summary>
    public string DefaultProfileName
    {
        get => _defaultProfileName;
        set
        {
            _defaultProfileName = value;
            Save();
        }
    }

    public ProfileStore(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
        Load();
    }

    private static string GetDefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "WinScreen", "profiles.json");
    }

    public void Load()
    {
        if (!File.Exists(_configPath))
        {
            CreateDefaultProfiles();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_configPath);

            // 새 형식 (ProfileConfig) 시도
            var config = JsonSerializer.Deserialize<ProfileConfig>(json, JsonOptions);
            if (config?.Profiles != null && config.Profiles.Count > 0)
            {
                _profiles = config.Profiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                _defaultProfileName = config.DefaultProfile ?? "cmd";
                _maxScrollbackSizeKB = config.MaxScrollbackSizeKB > 0 ? config.MaxScrollbackSizeKB : 1024;

                // 기본 프로필이 존재하지 않으면 첫 번째 프로필을 기본으로
                if (!_profiles.ContainsKey(_defaultProfileName) && _profiles.Count > 0)
                {
                    _defaultProfileName = _profiles.Keys.First();
                    Save();
                }
            }
            else
            {
                // 구 형식 (List<Profile>) 호환성
                var profiles = JsonSerializer.Deserialize<List<Profile>>(json, JsonOptions);
                _profiles = profiles?.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);

                // 구 형식에서 "default" 프로필이 있으면 마이그레이션
                if (_profiles.ContainsKey("default") && !_profiles.ContainsKey("cmd"))
                {
                    var defaultProfile = _profiles["default"];
                    defaultProfile.Name = "cmd";
                    _profiles.Remove("default");
                    _profiles["cmd"] = defaultProfile;
                    _defaultProfileName = "cmd";
                    Save(); // 새 형식으로 저장
                }
            }

            // 프로필이 비어있으면 기본 프로필 생성
            if (_profiles.Count == 0)
            {
                CreateDefaultProfiles();
                Save();
            }
        }
        catch
        {
            CreateDefaultProfiles();
            Save();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var config = new ProfileConfig
        {
            DefaultProfile = _defaultProfileName,
            Profiles = _profiles.Values.ToList(),
            MaxScrollbackSizeKB = _maxScrollbackSizeKB
        };
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private void CreateDefaultProfiles()
    {
        _defaultProfileName = "cmd";
        _profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase)
        {
            ["cmd"] = new Profile
            {
                Name = "cmd",
                Description = "Command Prompt",
                Shell = "cmd.exe"
            },
            ["powershell"] = new Profile
            {
                Name = "powershell",
                Description = "Windows PowerShell",
                Shell = "powershell.exe",
                Arguments = "-NoLogo"
            },
            ["pwsh"] = new Profile
            {
                Name = "pwsh",
                Description = "PowerShell Core",
                Shell = "pwsh.exe",
                Arguments = "-NoLogo"
            }
        };

        // Conda/Miniconda/Miniforge 감지
        var condaPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniforge3"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
            @"C:\ProgramData\miniconda3",
            @"C:\ProgramData\miniforge3",
            @"C:\ProgramData\anaconda3"
        };

        foreach (var condaPath in condaPaths)
        {
            if (Directory.Exists(condaPath))
            {
                var activateScript = Path.Combine(condaPath, "Scripts", "activate.bat");
                if (File.Exists(activateScript))
                {
                    _profiles["conda"] = new Profile
                    {
                        Name = "conda",
                        Description = $"Conda ({condaPath})",
                        Shell = "cmd.exe",
                        StartupCommand = $"\"{activateScript}\"",
                        Environment = new Dictionary<string, string>
                        {
                            ["CONDA_PREFIX"] = condaPath
                        }
                    };
                    break;
                }
            }
        }

        // Git Bash 감지
        var gitBashPaths = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe"
        };

        foreach (var gitBashPath in gitBashPaths)
        {
            if (File.Exists(gitBashPath))
            {
                _profiles["gitbash"] = new Profile
                {
                    Name = "gitbash",
                    Description = "Git Bash",
                    Shell = gitBashPath,
                    Arguments = "--login -i"
                };
                break;
            }
        }

        // WSL 감지
        if (File.Exists(@"C:\Windows\System32\wsl.exe"))
        {
            _profiles["wsl"] = new Profile
            {
                Name = "wsl",
                Description = "Windows Subsystem for Linux",
                Shell = "wsl.exe"
            };
        }
    }

    public Profile? Get(string name)
    {
        return _profiles.TryGetValue(name, out var profile) ? profile : null;
    }

    public Profile GetOrDefault(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return _profiles.TryGetValue(_defaultProfileName, out var def) ? def : CreateFallbackProfile();

        return _profiles.TryGetValue(name, out var profile) ? profile : CreateFallbackProfile();
    }

    private static Profile CreateFallbackProfile() => new()
    {
        Name = "cmd",
        Shell = "cmd.exe"
    };

    public void SetDefaultProfile(string name)
    {
        if (!_profiles.ContainsKey(name))
            throw new ArgumentException($"Profile '{name}' not found.");

        _defaultProfileName = name;
        Save();
    }

    public void Add(Profile profile)
    {
        _profiles[profile.Name] = profile;
        Save();
    }

    public bool Remove(string name)
    {
        // 기본 프로필은 삭제 불가
        if (name.Equals(_defaultProfileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot remove the default profile '{name}'. Change the default profile first.");

        if (_profiles.Remove(name))
        {
            Save();
            return true;
        }
        return false;
    }

    public void Reset()
    {
        CreateDefaultProfiles();
        Save();
    }

    public IEnumerable<Profile> GetAll() => _profiles.Values;

    public IEnumerable<ProfileInfo> GetAllInfo() => _profiles.Values.Select(p => p.ToInfo());
}
