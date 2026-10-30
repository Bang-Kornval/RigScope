using RigScope.Core.Snapshot;

namespace RigScope.Core.Tests;

/// <summary>
/// Made-up PCs that cover the hardware mixes RigScope has to handle. Every rule is evaluated against all of them,
/// so "dynamic" is tested, not assumed. No real person's data goes in here.
/// </summary>
public static class FakePcs
{
    public static IEnumerable<object[]> All() => Named().Select(p => new object[] { p.Name });

    public static IEnumerable<(string Name, Func<SystemSnapshot> Build)> Named() =>
    [
        ("intel-nvidia-desktop", IntelNvidiaDesktop),
        ("amd-x3d-radeon", AmdX3dRadeon),
        ("intel-hybrid-laptop", IntelHybridLaptopOnBattery),
        ("wifi-no-games", WifiNoGames),
        ("empty", () => new SystemSnapshot()),
    ];

    public static SystemSnapshot ByName(string name) => Named().First(p => p.Name == name).Build();

    /// <summary>A typical mid-range esports desktop: 11th-gen Intel, RTX card, 165 Hz, CS2 installed, a few RGB suites.</summary>
    public static SystemSnapshot IntelNvidiaDesktop() => new()
    {
        Os = new OsInfo { ProductName = "Windows 11 Home", DisplayVersion = "26H2", Build = 26300 },
        Cpu = new CpuInfo { Vendor = HardwareVendor.Intel, Name = "11th Gen Intel(R) Core(TM) i7-11700K @ 3.60GHz", Cores = 8, Threads = 16 },
        Gpus = [new GpuInfo { Vendor = HardwareVendor.Nvidia, Name = "NVIDIA GeForce RTX 3060 Ti", DriverVersion = "32.0.16.2101", VendorDriverVersion = "621.01",
                              DriverDate = DateTime.Today.AddDays(-11), VramBytes = 8L << 30, ResizableBarEnabled = false, PcieGen = 4, PcieWidth = 16 }],
        Displays =
        [
            new DisplayInfo { MonitorName = "Monitor A", IsPrimary = true, Width = 2560, Height = 1440, RefreshHz = 165, MaxRefreshHz = 165 },
            new DisplayInfo { MonitorName = "Monitor B", IsPrimary = false, Width = 1920, Height = 1080, RefreshHz = 120, MaxRefreshHz = 144 },
        ],
        Memory = new MemoryInfo
        {
            TotalBytes = 32L << 30, Generation = "DDR4",
            Modules = [Module(16, 2133, 3000, 3000), Module(16, 2133, 3000, 3000)],
        },
        Firmware = new FirmwareInfo { BiosVersion = "1801", BiosDate = DateTime.Today.AddDays(-1400), BoardProduct = "Z590 BOARD", IsUefi = true, SecureBootEnabled = true },
        Security = new SecurityInfo { VbsRunning = true, MemoryIntegrityRunning = true, HypervisorPresent = true },
        Windows = new WindowsGamingInfo { BackgroundRecording = false, WindowedOptimizations = true, PowerPlanName = "High performance",
                                          PowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", OnBattery = false, HiberfilBytes = 13L << 30 },
        Apps =
        [
            new KnownAppInfo { Id = "ghub", Name = "Logitech G HUB", Category = "rgb", Running = true, StartsWithWindows = true, ProcessCount = 3 },
            new KnownAppInfo { Id = "steelseries", Name = "SteelSeries GG", Category = "rgb", Running = true, StartsWithWindows = true, ProcessCount = 5 },
            new KnownAppInfo { Id = "docker", Name = "Docker Desktop (WSL2 VM)", Category = "vm", Running = false, StartsWithWindows = true },
            new KnownAppInfo { Id = "onedrive", Name = "OneDrive", Category = "sync", Running = true, StartsWithWindows = true },
        ],
        Network =
        [
            new NetworkAdapterInfo { Name = "Ethernet", Description = "Intel(R) Ethernet Controller (3) I225-V", Kind = AdapterKind.Ethernet, IsUp = true,
                                     LinkSpeedBps = 1_000_000_000, IsDefaultRoute = true, HardwareId = @"PCI\VEN_8086&DEV_15F3&SUBSYS_00000000&REV_03", InterruptModeration = true },
            new NetworkAdapterInfo { Name = "vEthernet (WSL)", Description = "Hyper-V Virtual Ethernet Adapter", Kind = AdapterKind.Virtual, IsUp = true },
        ],
        Drives = [new DriveInfoSnapshot { Letter = "C:", TotalBytes = 1000L << 30, FreeBytes = 300L << 30, MediaType = "SSD", IsSystem = true }],
        Games =
        [
            new InstalledGame { Id = "cs2", Name = "Counter-Strike 2", Launcher = "Steam", InstallPath = @"C:\Steam\steamapps\common\Counter-Strike Global Offensive",
                                SizeBytes = 69L << 30, LastPlayed = DateTime.Now.AddDays(-2), IsCompetitive = true },
            new InstalledGame { Id = "apex", Name = "Apex Legends", Launcher = "Steam", InstallPath = @"C:\Steam\steamapps\common\Apex Legends",
                                SizeBytes = 78L << 30, LastPlayed = DateTime.Now.AddDays(-1), IsCompetitive = true },
        ],
        Cs2 = Cs2WithVideo(new()
        {
            ["fullscreen"] = "1", ["mat_vsync"] = "0", ["r_low_latency"] = "1", ["msaa_samples"] = "4",
            ["videocfg_dynamic_shadows"] = "1", ["videocfg_fsr_detail"] = "0", ["defaultres"] = "1280", ["defaultresheight"] = "1024",
            ["refreshrate_numerator"] = "164999", ["refreshrate_denominator"] = "1000",
        }, launchOptions: "+fps_max 0 -nojoy +exec autoexec"),
    };

    /// <summary>AMD dual-CCD X3D with a Radeon card, CS2 on a hard drive, RAM without EXPO, 60 Hz by mistake.</summary>
    public static SystemSnapshot AmdX3dRadeon() => new()
    {
        Os = new OsInfo { ProductName = "Windows 11 Pro", Build = 26100 },
        Cpu = new CpuInfo { Vendor = HardwareVendor.Amd, Name = "AMD Ryzen 9 7950X3D 16-Core Processor", Cores = 16, Threads = 32, IsX3D = true },
        Gpus = [new GpuInfo { Vendor = HardwareVendor.Amd, Name = "AMD Radeon RX 7900 XTX", DriverVersion = "32.0.21001.9024", DriverDate = DateTime.Today.AddDays(-400), VramBytes = 24L << 30 }],
        Displays = [new DisplayInfo { MonitorName = "Monitor C", IsPrimary = true, Width = 2560, Height = 1440, RefreshHz = 60, MaxRefreshHz = 240 }],
        Memory = new MemoryInfo { TotalBytes = 32L << 30, Generation = "DDR5", Modules = [Module(16, 4800, 4800, 6000), Module(16, 4800, 4800, 6000)] },
        Firmware = new FirmwareInfo { BiosVersion = "2.10", BiosDate = DateTime.Today.AddDays(-100), IsUefi = true },
        Security = new SecurityInfo { MemoryIntegrityRunning = false },
        Windows = new WindowsGamingInfo { GameModeEnabled = false, BackgroundRecording = true, PowerPlanGuid = "a1841308-3541-4fab-bc81-f71556f20b4a", PowerPlanName = "Power saver", OnBattery = false },
        Apps =
        [
            new KnownAppInfo { Id = "medal", Name = "Medal.tv", Category = "recorder", Running = true },
            new KnownAppInfo { Id = "rtss", Name = "RivaTuner Statistics Server", Category = "overlay", Running = true },
            new KnownAppInfo { Id = "icue", Name = "Corsair iCUE", Category = "rgb", Running = true, StartsWithWindows = true },
        ],
        Network = [new NetworkAdapterInfo { Name = "Ethernet", Description = "Realtek PCIe 2.5GbE Family Controller", Kind = AdapterKind.Ethernet, IsUp = true, IsDefaultRoute = true, EnergyEfficientEthernet = true }],
        Drives =
        [
            new DriveInfoSnapshot { Letter = "C:", TotalBytes = 500L << 30, FreeBytes = 20L << 30, MediaType = "SSD", IsSystem = true },
            new DriveInfoSnapshot { Letter = "D:", TotalBytes = 2000L << 30, FreeBytes = 15L << 30, MediaType = "HDD" },
        ],
        Games = [new InstalledGame { Id = "cs2", Name = "Counter-Strike 2", Launcher = "Steam", InstallPath = @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", SizeBytes = 69L << 30, IsCompetitive = true }],
        Cs2 = Cs2WithVideo(new()
        {
            ["fullscreen"] = "0", ["mat_vsync"] = "1", ["msaa_samples"] = "8", ["videocfg_dynamic_shadows"] = "0", ["videocfg_fsr_detail"] = "2",
            ["refreshrate_numerator"] = "60000", ["refreshrate_denominator"] = "1000", ["r_player_visibility_mode"] = "0",
        }, launchOptions: "-high -threads 16", convars: new() { ["cl_net_buffer_ticks"] = "2" }),
    };

    /// <summary>Intel hybrid laptop on battery, single RAM stick, Windows 10, NVIDIA laptop GPU, no CS2 config yet.</summary>
    public static SystemSnapshot IntelHybridLaptopOnBattery() => new()
    {
        Os = new OsInfo { ProductName = "Windows 10 Home", Build = 19045 },
        Cpu = new CpuInfo { Vendor = HardwareVendor.Intel, Name = "13th Gen Intel(R) Core(TM) i7-13700H", Cores = 14, Threads = 20, IsHybrid = true, PerformanceCores = 6, EfficiencyCores = 8 },
        Gpus =
        [
            new GpuInfo { Vendor = HardwareVendor.Intel, Name = "Intel(R) Iris(R) Xe Graphics", IsIntegrated = true, DriverDate = DateTime.Today.AddDays(-30) },
            new GpuInfo { Vendor = HardwareVendor.Nvidia, Name = "NVIDIA GeForce RTX 4060 Laptop GPU", DriverDate = DateTime.Today.AddDays(-20), VendorDriverVersion = "620.10", PcieWidth = 2 },
        ],
        Displays = [new DisplayInfo { MonitorName = "Built-in", IsPrimary = true, Width = 1920, Height = 1080, RefreshHz = 144, MaxRefreshHz = 144 }],
        Memory = new MemoryInfo { TotalBytes = 16L << 30, Generation = "DDR5", Modules = [Module(16, 5200, 5200, 5200)] },
        Firmware = new FirmwareInfo { IsLaptop = true, BiosDate = DateTime.Today.AddDays(-200) },
        Windows = new WindowsGamingInfo { OnBattery = true, HiberfilBytes = 6L << 30, PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e" },
        Network = [new NetworkAdapterInfo { Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX211", Kind = AdapterKind.Wifi, IsUp = true, IsDefaultRoute = true }],
        Drives = [new DriveInfoSnapshot { Letter = "C:", TotalBytes = 512L << 30, FreeBytes = 200L << 30, MediaType = "SSD", IsSystem = true }],
        Games = [new InstalledGame { Id = "valorant", Name = "VALORANT", Launcher = "Riot", InstallPath = @"C:\Riot Games\VALORANT\live", IsCompetitive = true }],
    };

    /// <summary>Very little detected: Wi-Fi, one GPU with an unknown driver date, no games.</summary>
    public static SystemSnapshot WifiNoGames() => new()
    {
        Cpu = new CpuInfo { Vendor = HardwareVendor.Intel, Name = "Intel(R) Core(TM) i5-9400F CPU @ 2.90GHz", Cores = 6, Threads = 6 },
        Gpus = [new GpuInfo { Vendor = HardwareVendor.Nvidia, Name = "NVIDIA GeForce GTX 1660" }],
        Network = [new NetworkAdapterInfo { Name = "Wi-Fi", Kind = AdapterKind.Wifi, IsUp = true, IsDefaultRoute = true }],
    };

    private static MemoryModule Module(int gb, int spd, int configured, int rated) =>
        new() { CapacityBytes = (long)gb << 30, SpdMts = spd, ConfiguredMts = configured, RatedMts = rated };

    private static Cs2Info Cs2WithVideo(Dictionary<string, string> video, string launchOptions = "", Dictionary<string, string>? convars = null)
    {
        var account = new Cs2Account { SteamId3 = "1", Video = new(video, StringComparer.OrdinalIgnoreCase), LaunchOptions = launchOptions,
                                       Convars = new(convars ?? [], StringComparer.OrdinalIgnoreCase) };
        return new Cs2Info { Primary = account, Accounts = [account] };
    }
}
