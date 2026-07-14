using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace GammaPanelX
{
    /// <summary>单个颜色通道的调节参数。</summary>
    public class ChannelSettings
    {
        public int Brightness { get; set; }   // -100 .. 100
        public int Contrast { get; set; }     // -100 .. 100
        public double Gamma { get; set; }     // 0.30 .. 3.00

        public ChannelSettings()
        {
            Brightness = 0;
            Contrast = 0;
            Gamma = 1.0;
        }

        public ChannelSettings Clone()
        {
            ChannelSettings c = new ChannelSettings();
            c.Brightness = Brightness;
            c.Contrast = Contrast;
            c.Gamma = Gamma;
            return c;
        }

        public bool IsNeutral()
        {
            return Brightness == 0 && Contrast == 0 && Math.Abs(Gamma - 1.0) < 0.001;
        }
    }

    /// <summary>一台显示器的完整调节状态。</summary>
    public class MonitorConfig
    {
        public ChannelSettings Red { get; set; }
        public ChannelSettings Green { get; set; }
        public ChannelSettings Blue { get; set; }
        public bool Linked { get; set; }

        public MonitorConfig()
        {
            Red = new ChannelSettings();
            Green = new ChannelSettings();
            Blue = new ChannelSettings();
            Linked = true;
        }

        public MonitorConfig Clone()
        {
            MonitorConfig c = new MonitorConfig();
            c.Red = Red.Clone();
            c.Green = Green.Clone();
            c.Blue = Blue.Clone();
            c.Linked = Linked;
            return c;
        }

        public bool IsNeutral()
        {
            return Red.IsNeutral() && Green.IsNeutral() && Blue.IsNeutral();
        }

        public ChannelSettings GetChannel(int idx)
        {
            if (idx == 1) return Green;
            if (idx == 2) return Blue;
            return Red;
        }
    }

    /// <summary>命名配置文件：记录所有显示器的状态，可绑定全局热键。</summary>
    public class Profile
    {
        public string Name { get; set; }
        public int HotKey { get; set; }   // System.Windows.Forms.Keys 的整数值（含修饰键），0 表示未绑定
        public Dictionary<string, MonitorConfig> Monitors { get; set; }

        public Profile()
        {
            Name = "";
            HotKey = 0;
            Monitors = new Dictionary<string, MonitorConfig>();
        }
    }

    public class AppSettings
    {
        public Dictionary<string, MonitorConfig> Current { get; set; }
        public List<Profile> Profiles { get; set; }
        /// <summary>每台显示器首次见到时捕获的基线 LUT (768 项), 通常是系统 ICC 校准曲线。</summary>
        public Dictionary<string, List<int>> Baselines { get; set; }
        public bool KeepApplied { get; set; }      // 定时重新应用（防止游戏/驱动重置）
        public bool TrayOnClose { get; set; }      // 点关闭按钮时最小化到托盘
        public bool ApplyOnStartup { get; set; }   // 启动时自动恢复上次的设置
        public string LastProfile { get; set; }

        public AppSettings()
        {
            Current = new Dictionary<string, MonitorConfig>();
            Profiles = new List<Profile>();
            Baselines = new Dictionary<string, List<int>>();
            KeepApplied = false;
            TrayOnClose = true;
            ApplyOnStartup = true;
            LastProfile = "";
        }
    }

    public static class SettingsStore
    {
        public static string SettingsDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GammaPanelX");
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(SettingsDir, "settings.json"); }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    AppSettings s = ser.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        if (s.Current == null) s.Current = new Dictionary<string, MonitorConfig>();
                        if (s.Profiles == null) s.Profiles = new List<Profile>();
                        if (s.Baselines == null) s.Baselines = new Dictionary<string, List<int>>();
                        return s;
                    }
                }
            }
            catch (Exception)
            {
                // 配置文件损坏时回退到默认设置
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                if (!Directory.Exists(SettingsDir))
                    Directory.CreateDirectory(SettingsDir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(SettingsPath, ser.Serialize(settings));
            }
            catch (Exception)
            {
                // 保存失败不应让程序崩溃
            }
        }
    }
}
