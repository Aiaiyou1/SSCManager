using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.IO;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace SSCManager;

[ApiVersion(2, 1)]
public class Plugin(Main game) : TerrariaPlugin(game)
{
    #region 插件基础信息（改名区分字符串与方法）
    public override string Name => SSCManager;
    public const string SSCManager = "SSCManager";
    public override string Author => "羽学、唉唉有";
    public override Version Version => new(1, 1, 0);
    public override string Description => "清理长期未登录SSC数据；附加备份活跃玩家存档+地图+数据库";
    public static readonly Color color = new(240, 250, 150);
    public static readonly Color BackupColor = new(120, 220, 255);
    #endregion

    #region 全局静态配置 + 双定时器
    internal static Configuration Config = new();
    private static Timer? _cleanTimer;
    private Thread? _backupThread;
    private readonly CancellationTokenSource _cts = new();
    #endregion

    #region 注册释放
    public override void Initialize()
    {
        LoadConfig();
        GeneralHooks.ReloadEvent += ReloadConfig;
        ServerApi.Hooks.GamePostInitialize.Register(this, PostInit);
        Commands.ChatCommands.Add(new Command("backup.admin", BackupCommand, "backup"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 取消备份线程等待，优雅关闭
            _cts.Cancel();
            _backupThread?.Join(1000);
            _cts.Dispose();
            _cleanTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
    #endregion

    #region 重载 & 静态读写
    private static void ReloadConfig(ReloadEventArgs args)
    {
        LoadConfig();
        ManageCleanTimer();
        args.Player.SendMessage($"[{SSCManager}] 配置重载完成", color);
    }

    private static void LoadConfig()
    {
        try
        {
            Config = Configuration.Read();
            Config.LastClean = DateTime.UtcNow.AddMinutes(Config.CheckInt);
            Config.Write();
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[{SSCManager}] 配置加载失败：\n{ex.Message}");
        }
    }
    #endregion

    #region 初始化启动双任务
    private void PostInit(EventArgs args)
    {
        ManageCleanTimer();
        StartBackupThread();
    }
    #endregion

    #region ======【原有功能：SSC清理定时器逻辑】======
    private static void ManageCleanTimer()
    {
        if (!Config.Enabled || !Main.ServerSideCharacter)
        {
            _cleanTimer?.Dispose();
            _cleanTimer = null;
            return;
        }
        if (_cleanTimer == null)
        {
            int ms = Config.CheckInt * 60 * 1000;
            _cleanTimer = new Timer(CleanCheck, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(ms));
        }
    }

    private static void CleanCheck(object state)
    {
        if (!Config.Enabled || !Main.ServerSideCharacter)
        {
            ManageCleanTimer();
            return;
        }

        var users = TShock.UserAccounts.GetUserAccounts();
        if (users == null || users.Count == 0) return;

        foreach (var user in users)
        {
            bool online = TShock.Players.Any(p => p != null && p.Active && p.Account?.ID == user.ID);
            if (online) continue;

            if (string.IsNullOrEmpty(user.LastAccessed)) continue;
            if (!DateTime.TryParse(user.LastAccessed, out DateTime loginUtc)) continue;

            if ((DateTime.UtcNow - loginUtc).TotalMinutes < Config.CleanMins) continue;

            var fake = new TSPlayer(byte.MaxValue - 1);
            fake.Account = user;
            var data = TShock.CharacterDB.GetPlayerData(fake, user.ID);
            if (data == null || !data.exists) continue;

            try
            {
                ExportPlrFile(user, data, Configuration.ExportDir);
                TShock.DB.Query("DELETE FROM tsCharacter WHERE Account = @0", user.ID);
                TShock.Log.ConsoleInfo($"[{SSCManager}] 已清理过期角色：{user.Name}");
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[{SSCManager}] 清理{user.Name}失败:{ex.Message}");
            }
        }
    }

    /// <summary>导出玩家plr文件（原有成熟反射逻辑，备份模块直接复用）</summary>
    private static void ExportPlrFile(UserAccount user, PlayerData data, string exportPath)
    {
        try
        {
            var plr = new Player();
            var tsplr = new TSPlayer(byte.MaxValue - 1);
            typeof(TSPlayer).GetField("FakePlayer", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(tsplr, plr);
            tsplr.Account = user;
            data.RestoreCharacter(tsplr);

            if (!Directory.Exists(exportPath)) Directory.CreateDirectory(exportPath);
            string savePath = Path.Combine(exportPath, $"{user.Name}.plr");
            var fileData = new PlayerFileData
            {
                Metadata = FileMetadata.FromCurrentSettings(FileType.Player),
                Player = plr,
                _isCloudSave = false
            };
            typeof(FileData).GetField("_path", BindingFlags.Public | BindingFlags.Instance)?.SetValue(fileData, savePath);
            Player.InternalSavePlayerFile(fileData);
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[{SSCManager}] 导出{user.Name}失败: {ex.Message}");
        }
    }
    #endregion

    #region ======【新增功能：活跃玩家备份后台线程】======
    private void StartBackupThread()
    {
        _backupThread = new Thread(AutoBackupLoop)
        {
            IsBackground = true,
            Name = "ActiveBackupThread"
        };
        _backupThread.Start();
    }

    private void AutoBackupLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            int sleepMs = Config.BackupIntervalMin * 60 * 1000;
            if (!_cts.Token.WaitHandle.WaitOne(sleepMs))
            {
                if (Config.BackupEnable)
                {
                    RunBackupJob();
                    CleanExpiredBackupFolder();
                }
            }
        }
    }

    /// <summary>执行一次完整备份</summary>
    private void RunBackupJob()
    {
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string currentBackupFolder = Path.Combine(Configuration.BackupRootDir, timeStamp);
        Directory.CreateDirectory(currentBackupFolder);

        // ========== 1.备份世界地图 ==========
        if (Config.BackupWorld)
        {
            try
            {
                string srcWorld = Terraria.Main.worldPathName;
                if (File.Exists(srcWorld))
                {
                    string destWorld = Path.Combine(currentBackupFolder, $"{timeStamp}_world.wld");
                    File.Copy(srcWorld, destWorld, true);
                    TShock.Log.ConsoleInfo($"[SSCManager][备份] 地图备份完成");
                }
                else
                {
                    TShock.Log.ConsoleWarn($"[SSCManager][备份] 世界文件不存在，跳过地图备份");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[SSCManager][备份] 地图备份异常：{ex.Message}");
            }
        }

        // ========== 2.备份SQLite数据库 ==========
        if (Config.BackupSqlite)
        {
            try
            {
                string srcDb = Path.Combine(TShock.SavePath, "tshock.sqlite");
                if (File.Exists(srcDb))
                {
                    string destDb = Path.Combine(currentBackupFolder, $"{timeStamp}_tshock.sqlite");
                    File.Copy(srcDb, destDb, true);
                    TShock.Log.ConsoleInfo($"[SSCManager][备份] 数据库备份完成");
                }
                else
                {
                    TShock.Log.ConsoleWarn($"[SSCManager][备份] tshock.sqlite不存在");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[SSCManager][备份] 数据库备份异常：{ex.Message}");
            }
        }

        // ========== 3.活跃玩家plr备份【遍历数据库User，判断LastAccessed】 ==========
        if (Config.BackupPlayerRecent)
        {
            string playerBackupDir = Path.Combine(currentBackupFolder, "Players");
            Directory.CreateDirectory(playerBackupDir);

            List<UserAccount> allUsers = TShock.UserAccounts.GetUserAccounts();
            int successCount = 0;
            int failCount = 0;
            int skipByTime = 0;
            int parseFail = 0;

            foreach (var user in allUsers)
            {
                string timeText = user.LastAccessed;
                if (string.IsNullOrWhiteSpace(timeText))
                {
                    parseFail++;
                    continue;
                }
                if (!DateTime.TryParse(timeText, out DateTime loginUtc))
                {
                    parseFail++;
                    continue;
                }

                TimeSpan diff = DateTime.UtcNow - loginUtc;
                if (diff.TotalMinutes > Config.BackupActiveMinute)
                {
                    skipByTime++;
                    continue;
                }

                // 复用现成导出方法，无外部依赖
                try
                {
                    var fakeTs = new TSPlayer(byte.MaxValue - 1);
                    fakeTs.Account = user;
                    var playerData = TShock.CharacterDB.GetPlayerData(fakeTs, user.ID);
                    if (playerData != null && playerData.exists)
                    {
                        ExportPlrFile(user, playerData, playerBackupDir);
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch
                {
                    failCount++;
                }
            }
            TShock.Log.ConsoleInfo($"[SSCManager备份]活跃玩家导出｜成功:{successCount}｜失败:{failCount}｜长期离线跳过:{skipByTime}｜时间解析异常:{parseFail}");
        }

        // 后台线程禁止发送游戏内消息，改用控制台日志
        TShock.Log.ConsoleInfo($"[SSCManager][备份完成] {timeStamp}");
    }

    /// <summary>自动清理过期备份文件夹</summary>
    private void CleanExpiredBackupFolder()
    {
        string root = Configuration.BackupRootDir;
        if (!Directory.Exists(root)) return;
        DateTime expirePoint = DateTime.Now.AddDays(-Config.BackupKeepDays);
        foreach (var dir in Directory.GetDirectories(root))
        {
            var info = new DirectoryInfo(dir);
            if (info.CreationTime < expirePoint)
            {
                info.Delete(true);
                TShock.Log.ConsoleInfo($"[{SSCManager}][备份] 清理过期备份：{info.Name}");
            }
        }
    }
    #endregion

    #region Backup管理指令（独立命名BackupCommand）
    private void BackupCommand(CommandArgs args)
    {
        var plr = args.Player;
        if (args.Parameters.Count == 0)
        {
            plr.SendMessage($"===== {SSCManager} 备份管理 =====", BackupColor);
            plr.SendMessage("/backup run —— 立即手动执行一次备份", Color.White);
            plr.SendMessage("/backup on/off —— 开关自动备份", Color.White);
            plr.SendMessage("/backup min <数值> —— 修改备份间隔分钟", Color.White);
            plr.SendMessage("/backup keep <天数> —— 修改备份保留天数", Color.White);
            plr.SendMessage("/backup active <分钟> —— 修改活跃玩家判定时长", Color.White);
            plr.SendMessage($"状态：自动备份:{Config.BackupEnable} | 活跃判定:{Config.BackupActiveMinute}min | 间隔:{Config.BackupIntervalMin}min | 保留:{Config.BackupKeepDays}天", Color.Gray);
            return;
        }
        switch (args.Parameters[0].ToLower())
        {
            case "run":
                RunBackupJob();
                CleanExpiredBackupFolder();
                plr.SendMessage($"[{SSCManager}] ✅手动备份完成", BackupColor);
                break;
            case "on":
                Config.BackupEnable = true;
                Config.Write();
                plr.SendMessage($"[{SSCManager}] ✅自动备份已开启", Color.Lime);
                break;
            case "off":
                Config.BackupEnable = false;
                Config.Write();
                plr.SendMessage($"[{SSCManager}] ❌自动备份已关闭", Color.Red);
                break;
            case "min":
                if (args.Parameters.Count >= 2 && int.TryParse(args.Parameters[1], out int m) && m > 0)
                {
                    Config.BackupIntervalMin = m;
                    Config.Write();
                    plr.SendMessage($"[{SSCManager}] ⏱备份间隔设置为 {m} 分钟", BackupColor);
                }
                break;
            case "keep":
                if (args.Parameters.Count >= 2 && int.TryParse(args.Parameters[1], out int d) && d > 0)
                {
                    Config.BackupKeepDays = d;
                    Config.Write();
                    plr.SendMessage($"[{SSCManager}] 📅备份保留 {d} 天", BackupColor);
                }
                break;
            case "active":
                if (args.Parameters.Count >= 2 && int.TryParse(args.Parameters[1], out int activeMin) && activeMin > 0)
                {
                    Config.BackupActiveMinute = activeMin;
                    Config.Write();
                    plr.SendMessage($"[{SSCManager}] ⏱玩家活跃判定设置为 {activeMin} 分钟", BackupColor);
                }
                else
                {
                    plr.SendMessage($"用法：/backup active <分钟数>，当前值：{Config.BackupActiveMinute}分钟", Color.Gray);
                }
                break;
        }
    }
    #endregion
}
