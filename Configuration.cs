using Newtonsoft.Json;
using System;
using System.IO;
using TShockAPI;

namespace SSCManager;

internal class Configuration
{
    private const string PluginFolderName = "SSCManager";

    #region 原有【清理SSC存档】配置
    public static readonly string ConfigDir = Path.Combine(TShock.SavePath, PluginFolderName);
    public static readonly string ConfigFilePath = Path.Combine(ConfigDir, "配置文件.json");
    public static readonly string ExportDir = Path.Combine(ConfigDir, "已清理存档");

    [JsonProperty("插件开关(清理SSC)", Order = 0)]
    public bool Enabled { get; set; } = false;

    [JsonProperty("检查分钟(清理SSC)", Order = 1)]
    public int CheckInt { get; set; } = 1440;

    [JsonProperty("清理分钟阈值(清理SSC)", Order = 2)]
    public int CleanMins { get; set; } = 21600;

    [JsonProperty("下次清理时间", Order = 3)]
    public DateTime LastClean { get; set; } = DateTime.UtcNow;
    #endregion

    #region 新增【定时备份】独立配置
    [JsonProperty("开启自动备份", Order = 10)]
    public bool BackupEnable { get; set; } = true;

    [JsonProperty("备份近期活跃玩家存档", Order = 11)]
    public bool BackupPlayerRecent { get; set; } = true;

    [JsonProperty("玩家活跃判定时长(分钟)", Order = 12)]
    public int BackupActiveMinute { get; set; } = 1440;

    [JsonProperty("备份世界地图文件", Order = 13)]
    public bool BackupWorld { get; set; } = true;

    [JsonProperty("备份tshock.sqlite数据库", Order = 14)]
    public bool BackupSqlite { get; set; } = true;

    [JsonProperty("备份间隔(分钟)", Order = 15)]
    public int BackupIntervalMin { get; set; } = 15;

    [JsonProperty("备份文件保留天数", Order = 16)]
    public int BackupKeepDays { get; set; } = 7;

    public static readonly string BackupRootDir = Path.Combine(ConfigDir, "WorldBackup");
    #endregion

    #region 读写方法（static）
    public static Configuration Read()
    {
        if (!Directory.Exists(ConfigDir))
            Directory.CreateDirectory(ConfigDir);
        if (!Directory.Exists(ExportDir))
            Directory.CreateDirectory(ExportDir);
        if (!Directory.Exists(BackupRootDir))
            Directory.CreateDirectory(BackupRootDir);

        if (!File.Exists(ConfigFilePath))
        {
            var cfg = new Configuration();
            cfg.Write();
            return cfg;
        }
        string json = File.ReadAllText(ConfigFilePath);
        return JsonConvert.DeserializeObject<Configuration>(json)!;
    }

    public void Write()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(ConfigFilePath, json);
    }
    #endregion
}
