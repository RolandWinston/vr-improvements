using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// ALVR 20.14.1 / SteamVR session coordinator. No forced termination.
// Uses vrpathreg to change ONLY the registered ALVR path; never rewrites vrpath JSON.
internal static class VRSession
{
    static readonly string AlvrRoot = Environment.GetEnvironmentVariable("VRSESSION_ALVR_ROOT") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ALVR");
    static readonly string SteamRoot = Environment.GetEnvironmentVariable("VRSESSION_STEAM_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    static readonly string SteamExe = Path.Combine(SteamRoot, "steam.exe");
    static readonly string VrRoot = Path.Combine(SteamRoot, @"steamapps\common\SteamVR");
    static readonly string PathReg = Path.Combine(VrRoot, @"bin\win64\vrpathreg.exe");
    static readonly string Monitor = Path.Combine(VrRoot, @"bin\win64\vrmonitor.exe");
    static readonly string Dashboard = Path.Combine(AlvrRoot, "ALVR Dashboard.exe");
    static readonly string PathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"openvr\openvrpaths.vrpath");
    static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vr-session.log");
    static readonly string[] KnownGames = { "Palworld-Win64-Shipping", "Pal", "Palworld", "Titanfall2", "Titanfall2VRLauncher" };
    // Non-Steam launcher IDs are not game processes. Always check actual games first.
    static readonly HashSet<uint> LauncherIds = new HashSet<uint> { 3957310308u, 3717760503u };
    static string action = "--status"; // Public package: explicit --ensure starts a session.

    [STAThread]
    static int Main(string[] args)
    {
        bool showErrors = args.Contains("--show-errors");
        bool waitStream = args.Contains("--wait-stream");
        try {
            var commands = args.Where(a => a != "--show-errors" && a != "--quiet" && a != "--wait-stream").ToArray();
            if (commands.Length > 1 || (commands.Length == 1 && !new[] { "--ensure", "--stop", "--check", "--status", "--self-test-startup", "--self-test-wired", "--self-test-startup-evidence", "--diagnose-startup" }.Contains(commands[0])))
                throw new Exception("参数：--ensure、--stop、--check/--status，可加 --show-errors；--wait-stream 仅用于 --ensure。");
            if (commands.Length == 1) action = commands[0];
            if (waitStream && action != "--ensure") throw new Exception("--wait-stream 仅可与 --ensure 一起使用。");
            if (action == "--self-test-startup-evidence") return StartupEvidenceSelfTest();
            if (action == "--diagnose-startup") return DiagnoseStartup();
            if (action == "--self-test-startup") return StartupParserSelfTest();
            if (action == "--self-test-wired") return WiredRequestSelfTest();
            if (action == "--check" || action == "--status") { Status(); return 0; }
            bool owned = false;
            using (var gate = new Mutex(false, @"Local\VRSession_ALVR_SteamVR")) {
                try {
                    try { owned = gate.WaitOne(TimeSpan.FromSeconds(120)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new Exception("另一个 VR 启动/退出操作尚未完成；请等待后重试。");
                    Note("BEGIN " + action);
                    ValidateFiles();
                    if (action == "--stop") Stop(showErrors); else { Ensure(); if (waitStream) WaitForStream(); }
                    Note("OK " + action);
                    return 0;
                } finally { if (owned) gate.ReleaseMutex(); }
            }
        } catch (Exception ex) {
            Note("ERROR " + action + ": " + ex.Message);
            try { Console.Error.WriteLine(ex.Message); } catch { }
            if (showErrors) MessageBox.Show(ex.Message + "\n\n日志：" + LogFile, "VR 会话", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    static void Note(string message)
    {
        try { File.AppendAllText(LogFile, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
        try { Console.WriteLine(message); } catch { }
    }
    // Windows can enumerate a terminated process object until outstanding handles
    // are released. Ignore only an exact zero-resource snapshot, never access errors.
    // Game guards below deliberately retain their original raw-existence semantics.
    static bool IsZeroResourceSnapshot(int threads, int handles, long virtualBytes)
    {
        return threads == 0 && handles == 0 && virtualBytes == 0;
    }
    static bool IsTerminatedRemnant(Process process)
    {
        try {
            process.Refresh();
            return IsZeroResourceSnapshot(process.Threads.Count, process.HandleCount, process.VirtualMemorySize64);
        } catch { return false; } // Unknown means potentially active, not safe to ignore.
    }
    static Process[] Processes(string name)
    {
        var active = new List<Process>();
        foreach (var process in Process.GetProcessesByName(name)) {
            if (IsTerminatedRemnant(process)) process.Dispose();
            else active.Add(process);
        }
        return active.ToArray();
    }
    static bool AnyProcessNamed(string name)
    {
        var ps = Process.GetProcessesByName(name);
        try { return ps.Length != 0; } finally { foreach (var p in ps) p.Dispose(); }
    }
    static bool Running(string name)
    {
        var ps = Processes(name);
        try { return ps.Length != 0; } finally { foreach (var p in ps) p.Dispose(); }
    }
    static void ValidateFiles()
    {
        foreach (string path in new[] { SteamExe, PathReg, Monitor, Dashboard })
            if (!File.Exists(path)) throw new Exception("未找到：" + path + "\n请先恢复安装路径。");
        if (!File.Exists(PathsFile)) throw new Exception("OpenVR 路径注册文件不存在；请先正常启动一次 SteamVR。");
    }
    static string Canonical(string path) { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
    static bool SamePath(string a, string b) { return string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase); }
    static HashSet<string> Drivers()
    {
        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PathsFile));
        object raw;
        if (!json.TryGetValue("external_drivers", out raw) || raw == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object item in (System.Collections.IEnumerable)raw) result.Add(Canonical(Convert.ToString(item)));
        return result;
    }
    static void Driver(bool register)
    {
        var before = Drivers();
        string alvr = Canonical(AlvrRoot);
        if (before.Contains(alvr) == register) return;
        var cli = new ProcessStartInfo(PathReg, (register ? "adddriver " : "removedriver ") + Quote(AlvrRoot)) {
            WorkingDirectory = Path.GetDirectoryName(PathReg), UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        using (var process = Process.Start(cli)) {
            if (process == null || !process.WaitForExit(15000))
                throw new Exception("vrpathreg 未在 15 秒内完成。未强制终止；请检查 SteamVR。");
            if (process.ExitCode != 0) throw new Exception("vrpathreg 返回错误 " + process.ExitCode + "。");
        }
        var after = Drivers();
        if (after.Contains(alvr) != register) throw new Exception("ALVR 驱动注册状态未按预期改变。");
        before.Remove(alvr); after.Remove(alvr);
        if (!before.SetEquals(after)) throw new Exception("其他 OpenVR 驱动注册同时发生变化；已停止，请检查外部驱动列表。");
        Note("ALVR driver " + (register ? "registered" : "unregistered") + "; other drivers preserved");
    }
    static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    static void StartUserApp(string exe, string arguments)
    {
        Process.Start(new ProcessStartInfo(exe, arguments) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
    }
    static bool WaitUntil(Func<bool> condition, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        do { if (condition()) return true; Thread.Sleep(500); } while (DateTime.UtcNow < end);
        return condition();
    }
    static bool Ping()
    {
        try {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8082/api/ping");
            request.Proxy = null; request.Timeout = 1200; request.ReadWriteTimeout = 1200;
            request.Headers["X-ALVR"] = "true";
            request.KeepAlive = false;
            using (var response = (HttpWebResponse)request.GetResponse()) return response.StatusCode == HttpStatusCode.OK;
        } catch { return false; }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    static HashSet<int> PortOwners()
    {
        var owners = new HashSet<int>();
        foreach (int family in new[] { 2, 23 }) {
            int size = 0;
            uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0); // TCP_TABLE_OWNER_PID_LISTENER
            if (code != 0 && code != 122) throw new Exception("无法检查 ALVR 8082 端口（系统错误 " + code + "）。");
            if (size < 4) continue;
            IntPtr memory = Marshal.AllocHGlobal(size);
            try {
                code = GetExtendedTcpTable(memory, ref size, false, family, 3, 0);
                if (code != 0) throw new Exception("无法读取 ALVR 8082 端口监听状态（系统错误 " + code + "）。");
                int count = Marshal.ReadInt32(memory), stride = family == 2 ? 24 : 56;
                int portOffset = family == 2 ? 8 : 20, pidOffset = family == 2 ? 20 : 52;
                for (int i = 0; i < count; i++) {
                    int offset = 4 + i * stride;
                    if (offset + stride > size) throw new Exception("端口表发生变化，请重试。");
                    uint port = unchecked((uint)Marshal.ReadInt32(memory, offset + portOffset));
                    int hostPort = (int)(((port & 255) << 8) | ((port >> 8) & 255));
                    if (hostPort == 8082) owners.Add(Marshal.ReadInt32(memory, offset + pidOffset));
                }
            } finally { Marshal.FreeHGlobal(memory); }
        }
        return owners;
    }
    static string ProcessName(int id)
    {
        try { using (var p = Process.GetProcessById(id)) return p.ProcessName; } catch { return "exited/unknown"; }
    }
    static string OwnerText()
    {
        return string.Join(", ", PortOwners().Select(id => id + ":" + ProcessName(id)).ToArray());
    }
    static bool BackendReady()
    {
        var owners = PortOwners();
        return owners.Count > 0 && owners.All(id => string.Equals(ProcessName(id), "vrserver", StringComparison.OrdinalIgnoreCase)) && Ping();
    }
    static bool SteamPreloadedAlvr()
    {
        if (PortOwners().Any(id => string.Equals(ProcessName(id), "steam", StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var process in Processes("steam")) {
            using (process) {
                try {
                    foreach (ProcessModule module in process.Modules)
                        if (string.Equals(module.ModuleName, "driver_alvr_server.dll", StringComparison.OrdinalIgnoreCase) &&
                            Canonical(module.FileName).StartsWith(Canonical(AlvrRoot) + "\\", StringComparison.OrdinalIgnoreCase))
                            return true;
                } catch (Exception ex) { throw new Exception("无法确认 Steam 是否提前加载 ALVR 驱动，未重启 Steam：" + ex.Message); }
            }
        }
        return false;
    }
    static uint AsUInt(object value) { return unchecked((uint)Convert.ToInt64(value)); }
    static void RequireNoGames(bool stoppingVr)
    {
        var active = new List<string>();
        foreach (string name in KnownGames) if (AnyProcessNamed(name)) active.Add(name + ".exe");
        using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) {
            if (steam == null) throw new Exception("无法读取 Steam 游戏运行状态，未执行关闭操作。");
            using (var apps = steam.OpenSubKey("Apps")) {
                if (apps == null && AnyProcessNamed("steam")) throw new Exception("Steam 游戏运行标志不可用，未执行关闭操作。");
                if (apps != null) foreach (string id in apps.GetSubKeyNames()) {
                    uint app;
                    if (!uint.TryParse(id, out app)) continue;
                    using (var entry = apps.OpenSubKey(id)) {
                        if (entry == null) throw new Exception("Steam 应用状态读取失败，未执行关闭操作。");
                        object flag = entry.GetValue("Running");
                        if (flag != null && AsUInt(flag) != 0 && app != 250820u && !(stoppingVr && app == 1494460u) && !LauncherIds.Contains(app))
                            active.Add(Convert.ToString(entry.GetValue("Name", id)) + " (Steam " + id + ")");
                    }
                }
            }
            object current = steam.GetValue("RunningAppID");
            if (current != null) {
                uint id = AsUInt(current);
                if (id != 0 && id != 250820u && !(stoppingVr && id == 1494460u) && !LauncherIds.Contains(id)) active.Add("Steam RunningAppID=" + id);
            }
        }
        if (active.Count != 0) throw new Exception("请先正常退出以下游戏/应用，再重试：\n" + string.Join("\n", active.Distinct().ToArray()) + "\n本程序未强制关闭游戏。");
    }
    static IEnumerable<string> CompleteLines(string text)
    {
        // Ignore a last line still being written. A partial completion marker is not evidence.
        int lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return new string[0];
        return text.Substring(0, lastNewline + 1).Split('\n').Select(line => line.TrimEnd('\r'));
    }
    static bool VrLogTime(string line, out DateTime stamp)
    {
        stamp = DateTime.MinValue;
        int end = line.IndexOf(" [", StringComparison.Ordinal);
        return end > 0 && DateTime.TryParseExact(line.Substring(0, end),
            new[] { "ddd MMM d yyyy HH:mm:ss.fff", "ddd MMM dd yyyy HH:mm:ss.fff" },
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out stamp);
    }
    static bool ConsoleLogTime(string line, out DateTime stamp)
    {
        stamp = DateTime.MinValue;
        return line.Length >= 21 && line[0] == '[' && line[20] == ']' &&
            DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out stamp);
    }
    static bool StartupLogsReady(string vrLog, string consoleLog, int pid, DateTime started, out string reason)
    {
        // A process's lifetime (PID + start time), not a stale success from an earlier Steam run.
        DateTime earliestVrHeader = started.AddTicks(-(started.Ticks % TimeSpan.TicksPerSecond));
        bool currentSegment = false, watchdog = false, completed = false, scannedAlvr = false;
        DateTime segmentTime = DateTime.MinValue;
        foreach (string line in CompleteLines(vrLog)) {
            DateTime time;
            if (!VrLogTime(line, out time) || time < earliestVrHeader) continue;
            int header = line.IndexOf("startup with PID=", StringComparison.Ordinal);
            if (header >= 0 && line.IndexOf("[Info] - steam ", StringComparison.Ordinal) >= 0) {
                int numberStart = header + "startup with PID=".Length;
                int comma = line.IndexOf(',', numberStart);
                int headerPid;
                currentSegment = comma > numberStart && int.TryParse(line.Substring(numberStart, comma - numberStart), out headerPid) && headerPid == pid;
                watchdog = false; completed = false; scannedAlvr = false; segmentTime = time;
                continue;
            }
            if (!currentSegment || time < segmentTime) continue;
            if (line.Contains("vrclient type=")) {
                watchdog = line.Contains("vrclient type=VRApplication_SteamWatchdog");
                completed = false;
            }
            if (!watchdog) continue;
            if (line.Contains("driver alvr_server")) scannedAlvr = true;
            if (line.Contains("No drivers were capable of being watchdogs. Failing init.")) completed = true;
        }
        if (!currentSegment || !watchdog || !completed) { reason = "waiting for current Steam PID watchdog completion"; return false; }
        if (scannedAlvr) { reason = "current Steam watchdog already scanned ALVR"; return false; }

        bool currentConsoleBlock = false, consoleReady = false;
        DateTime consoleBlockStart = DateTime.MinValue;
        foreach (string line in CompleteLines(consoleLog)) {
            DateTime time;
            if (!ConsoleLogTime(line, out time) || time <= started) continue;
            if (line.Contains("Console Log Start")) {
                currentConsoleBlock = true; consoleReady = false; consoleBlockStart = time;
            }
            if (currentConsoleBlock && time >= consoleBlockStart && line.Contains("System startup time:"))
                consoleReady = true;
        }
        if (!consoleReady) { reason = "waiting for current Steam console startup completion"; return false; }
        reason = "current Steam watchdog and client startup complete";
        return true;
    }
    // 20261019: Steam can finish ordinary startup without invoking OpenVR watchdog.
    // The missing optional log must not block an already initialized client forever.
    // Runtime callers still require a stable process identity, clean port/modules and 3s observation.
    static bool StartupGateEvidence(string vrLog, string consoleLog, int pid, DateTime started, DateTime now, out string reason)
    {
        if (StartupLogsReady(vrLog, consoleLog, pid, started, out reason)) return true;
        DateTime floor = started.AddTicks(-(started.Ticks % TimeSpan.TicksPerSecond));
        foreach (string line in CompleteLines(vrLog)) {
            DateTime time;
            if (VrLogTime(line, out time) && time >= floor) {
                reason = "current VR enumeration has evidence but is not confirmed safe: " + reason;
                return false; // Includes partial/rotated logs, wrong PID, or ALVR scans.
            }
        }
        if (now < started || (now - started).TotalSeconds < 30) {
            reason = "waiting for initialized Steam quiet interval"; return false;
        }
        bool block = false;
        DateTime complete = DateTime.MinValue;
        foreach (string line in CompleteLines(consoleLog)) {
            DateTime time;
            if (!ConsoleLogTime(line, out time) || time < floor) continue;
            if (time > now) { reason = "future Steam console timestamp rejected"; return false; }
            if (line.Contains("Console Log Start")) {
                block = time >= started && (time - started).TotalSeconds <= 60;
                complete = DateTime.MinValue;
            } else if (block && line.Contains("System startup time:")) {
                complete = time;
            }
        }
        if (complete == DateTime.MinValue || (now - complete).TotalSeconds < 30) {
            reason = "waiting for current Steam console completion and 30s quiet interval"; return false;
        }
        reason = "Steam console initialized; no current OpenVR enumeration; require clean port/modules";
        return true;
    }
    static int DiagnoseStartup()
    {
        var processes = Processes("steam");
        try {
            if (processes.Length != 1) { Console.WriteLine("STARTUP_READY=False reason=expected exactly one Steam"); return 2; }
            var p = processes[0]; string reason;
            bool evidence = StartupGateEvidence(ReadStartupLog("vrclient_Steam.txt"), ReadStartupLog("console_log.txt"), p.Id, p.StartTime, DateTime.Now, out reason);
            bool portFree = PortOwners().Count == 0;
            bool noPreload = !SteamPreloadedAlvr();
            bool noVr = !Running("vrserver") && !Running("vrmonitor") && !Running("vrstartup");
            bool ready = evidence && portFree && noPreload && noVr;
            Console.WriteLine("STARTUP_READY=" + ready + " evidence=" + evidence + " portFree=" + portFree + " noAlvrPreload=" + noPreload + " noVr=" + noVr + " PID=" + p.Id + " start=" + p.StartTime.ToString("o") + " reason=" + reason);
            return ready ? 0 : 2;
        } finally { foreach (var p in processes) p.Dispose(); }
    }
    static int StartupEvidenceSelfTest()
    {
        DateTime start = new DateTime(2026,10,19,10,51,6,DateTimeKind.Local).AddMilliseconds(357);
        DateTime now = start.AddHours(3);
        string console = "[2026-10-19 10:51:09] Console Log Start\n[2026-10-19 10:51:21] System startup time: 14.28 seconds\n";
        string header = "Mon Oct 19 2026 10:51:09.000 [Info] - steam 2.17.10 startup with PID=34192, config=test\n";
        string watchdog = "Mon Oct 19 2026 10:51:09.001 [Info] - vrclient type=VRApplication_SteamWatchdog\n";
        string done = "Mon Oct 19 2026 10:51:10.000 [Info] - No drivers were capable of being watchdogs. Failing init.\n";
        var cases = new[] {
            new {Name="no-optional-watchdog",Vr="",Con=console,Now=now,Expected=true},
            new {Name="normal-complete-watchdog",Vr=header+watchdog+done,Con=console,Now=now,Expected=true},
            new {Name="partial-watchdog",Vr=header+watchdog,Con=console,Now=now,Expected=false},
            new {Name="wrong-pid-current-log",Vr=header.Replace("34192","12345")+watchdog+done,Con=console,Now=now,Expected=false},
            new {Name="scanned-alvr",Vr=header+watchdog+"Mon Oct 19 2026 10:51:09.500 [Info] - driver alvr_server\n"+done,Con=console,Now=now,Expected=false},
            new {Name="rotated-current-tail",Vr=done,Con=console,Now=now,Expected=false},
            new {Name="no-console",Vr="",Con="",Now=now,Expected=false},
            new {Name="previous-console",Vr="",Con=console.Replace("2026-10-19","2026-10-18"),Now=now,Expected=false},
            new {Name="future-console",Vr="",Con=console.Replace("2026-10-19","2026-10-20"),Now=now,Expected=false},
            new {Name="quiet-interval-incomplete",Vr="",Con=console,Now=start.AddSeconds(35),Expected=false},
            new {Name="partial-console-line",Vr="",Con=console.TrimEnd('\n'),Now=now,Expected=false},
            new {Name="console-block-unbound",Vr="",Con=console.Replace("10:51:","11:51:"),Now=now,Expected=false},
            new {Name="new-unfinished-console-block",Vr="",Con=console+"[2026-10-19 10:51:30] Console Log Start\n",Now=now,Expected=false},
            new {Name="clock-before-process",Vr="",Con=console,Now=start.AddSeconds(-1),Expected=false}
        };
        foreach (var c in cases) {
            string reason; bool actual = StartupGateEvidence(c.Vr,c.Con,34192,start,c.Now,out reason);
            if (actual != c.Expected) throw new Exception("FAIL " + c.Name + ": " + reason);
            Console.WriteLine("PASS " + c.Name);
        }
        Console.WriteLine("PASS " + cases.Length + " startup evidence cases; no runtime mutation");return 0;
    }
    static string ReadStartupLog(string name)
    {
        string path = Path.Combine(SteamRoot, "logs", name);
        if (!File.Exists(path)) return "";
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            // Bound reads; if required evidence was rotated away, fail closed instead of guessing.
            const int maxBytes = 4194304;
            bool truncated = file.Length > maxBytes;
            if (truncated) file.Seek(-maxBytes, SeekOrigin.End);
            using (var reader = new StreamReader(file, Encoding.UTF8, true)) {
                string text = reader.ReadToEnd();
                if (truncated) {
                    int firstNewline = text.IndexOf('\n');
                    text = firstNewline < 0 ? "" : text.Substring(firstNewline + 1);
                }
                return text;
            }
        }
    }
    static void WaitForSteamStartup()
    {
        var timer = Stopwatch.StartNew();
        DateTime? readySince = null;
        int stablePid = 0;
        DateTime stableStarted = DateTime.MinValue;
        string lastReason = "", reason = "waiting for Steam process";
        Note("ALVR startup gate v20261019: current watchdog OR initialized Steam without enumeration; 3 seconds clean port/modules; limit 90 seconds");
        while (timer.Elapsed.TotalSeconds < 90) {
            if (Running("vrserver") || Running("vrmonitor") || Running("vrstartup"))
                throw new Exception("SteamVR 在 Steam 初始化验证完成前启动。ALVR 将保持未注册；请正常退出 SteamVR 后重试。");
            var processes = Processes("steam");
            bool ready = false;
            try {
                if (processes.Length == 1) {
                    var steam = processes[0];
                    int pid = steam.Id;
                    DateTime started = steam.StartTime;
                    if (pid != stablePid || started != stableStarted) {
                        stablePid = pid; stableStarted = started; readySince = null;
                        Note("Observing Steam PID=" + pid + " start=" + started.ToString("o"));
                    }
                    ready = StartupGateEvidence(ReadStartupLog("vrclient_Steam.txt"), ReadStartupLog("console_log.txt"), pid, started, DateTime.Now, out reason);
                    if (ready && PortOwners().Count != 0) { ready = false; reason = "waiting for ALVR port8082 to be free"; }
                    if (ready && SteamPreloadedAlvr()) { ready = false; reason = "Steam has loaded ALVR before SteamVR; startup gate rejected"; }
                } else {
                    reason = "waiting for exactly one Steam process"; readySince = null;
                }
            } finally { foreach (var process in processes) process.Dispose(); }
            if (!ready) readySince = null;
            else if (readySince == null) readySince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - readySince.Value).TotalSeconds >= 3) {
                Note("Steam startup gate passed for PID=" + stablePid + "; ALVR not loaded and port8082 free");
                return;
            }
            if (reason != lastReason) { Note("Startup gate: " + reason); lastReason = reason; }
            Thread.Sleep(500);
        }
        throw new Exception("90 秒内未确认本次 Steam 的 VR 驱动枚举及客户端初始化完成。ALVR 保持未注册，避免再次占用 8082；请查看启动日志并在问题处理后重试。\n最后状态：" + reason);
    }
    static void StartSteamBeforeDriver()
    {
        // Legacy ALVR 20.14.1 initializes during Steam's driver enumeration (#3299).
        // On failure, keep ONLY ALVR unregistered; unconditional finally-registration
        // would recreate the original failure after a slow startup or timeout.
        try {
            Driver(false);
            if (PortOwners().Count != 0) throw new Exception("ALVR 8082 端口被占用：" + OwnerText() + "。未启动新的 VR 会话。");
            if (!Running("steam")) StartUserApp(SteamExe, "");
            WaitForSteamStartup();
            Driver(true);
        } catch {
            try {
                Driver(false);
                Note("Startup aborted; ALVR remains unregistered; other OpenVR driver registrations preserved");
            } catch (Exception cleanup) {
                Note("WARNING: could not verify ALVR unregistered after startup failure: " + cleanup.Message);
            }
            throw;
        }
    }
    static int StartupParserSelfTest()
    {
        // Pure parser checks. No applications, processes, files or registry are changed.
        var started = new DateTime(2026, 10, 10, 15, 4, 15, 400, DateTimeKind.Local);
        const int pid = 28036;
        string header = "Sat Oct 10 2026 15:04:25.044 [Info] - steam 2.17.10 startup with PID=28036, config=test\n";
        string watchdog = "Sat Oct 10 2026 15:04:25.045 [Info] - vrclient type=VRApplication_SteamWatchdog\n";
        string done = "Sat Oct 10 2026 15:04:26.421 [Info] - No drivers were capable of being watchdogs. Failing init.\n";
        string console = "[2026-10-10 15:04:19] Console Log Start\n[2026-10-10 15:04:29] System startup time: 13.47 seconds\n";
        string good = header + watchdog + done;
        var tests = new[] {
            new { Name = "complete-current-startup", Vr = good, Console = console, Expected = true },
            new { Name = "old-pid", Vr = good.Replace("PID=28036", "PID=26848"), Console = console, Expected = false },
            new { Name = "old-console", Vr = good, Console = console.Replace("15:04:", "14:35:"), Expected = false },
            new { Name = "incomplete-watchdog", Vr = header + watchdog, Console = console, Expected = false },
            new { Name = "partial-completion-line", Vr = good.TrimEnd('\n'), Console = console, Expected = false },
            new { Name = "partial-console-line", Vr = good, Console = console.TrimEnd('\n'), Expected = false },
            new { Name = "new-watchdog-not-finished", Vr = good + header + watchdog, Console = console, Expected = false },
            new { Name = "watchdog-scanned-alvr", Vr = header + watchdog + "Sat Oct 10 2026 15:04:26.025 [Info] - Could not create interface in driver alvr_server from test\n" + done, Console = console, Expected = false },
            new { Name = "reused-pid-old-lifetime", Vr = good.Replace("15:04:", "14:35:"), Console = console, Expected = false },
            new { Name = "no-current-console-block", Vr = good, Console = "[2026-10-10 15:04:29] System startup time: 13.47 seconds\n", Expected = false },
            new { Name = "unknown-watchdog-success-no-fallback", Vr = header + watchdog + "Sat Oct 10 2026 15:04:26.421 [Info] - Some other driver succeeded\n", Console = console, Expected = false }
        };
        foreach (var test in tests) {
            string reason;
            bool actual = StartupLogsReady(test.Vr, test.Console, pid, started, out reason);
            if (actual != test.Expected) throw new Exception("Startup parser self-test failed: " + test.Name + " (" + reason + ")");
            Console.WriteLine("PASS " + test.Name);
        }
        Console.WriteLine("PASS: " + tests.Length + " pure startup parser cases; no runtime mutation");
        return 0;
    }

    static bool StableDashboardRunning()
    {
        bool found = false;
        var processes = Processes("ALVR Dashboard");
        try {
            foreach (var process in processes) {
                string path;
                try { path = process.MainModule.FileName; }
                catch (Exception ex) { throw new Exception("无法确认 ALVR 控制面板版本，未重复启动：" + ex.Message); }
                if (!SamePath(path, Dashboard))
                    throw new Exception("另一版本 ALVR 控制面板正在运行，请正常关闭后重试：\n" + path);
                found = true;
            }
        } finally { foreach (var process in processes) process.Dispose(); }
        return found;
    }
    static void EnsureStableDashboard()
    {
        if (!StableDashboardRunning()) ExplorerDashboardLaunch.Execute(Dashboard, "");
        if (!WaitUntil(StableDashboardRunning, 15))
            throw new Exception("ALVR 20.14.1 控制面板未在 15 秒内启动。请检查电脑提示；未启动新的 SteamVR 进程。");
        Note("Stable ALVR Dashboard process confirmed before continuing");
    }
    static HttpWebRequest LocalDashboardRequest(string path, string method)
    {
        var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8082" + path);
        request.Method = method;
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.Timeout = 4000;
        request.ReadWriteTimeout = 4000;
        request.Headers["X-ALVR"] = "true";
        request.KeepAlive = false;
        return request;
    }
    static string ReadAlvrVersion()
    {
        var request = LocalDashboardRequest("/api/version", "GET");
        using (var response = (HttpWebResponse)request.GetResponse()) {
            if (response.StatusCode != HttpStatusCode.OK) throw new Exception("无法读取 ALVR 服务版本。");
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                var text = new StringBuilder();
                int character;
                while ((character = reader.Read()) != -1) {
                    if (text.Length >= 128) throw new Exception("ALVR 版本响应格式异常，未发送客户端配置请求。");
                    text.Append((char)character);
                }
                return text.ToString().Trim();
            }
        }
    }
    static string[] WiredRequests(string serverVersion)
    {
        // Official v20.14.1 ServerRequest / ClientListAction schema only.
        // AddIfMissing does not overwrite other fields on an existing USB entry.
        if (!string.Equals((serverVersion ?? "").Trim(), "20.14.1", StringComparison.Ordinal))
            throw new Exception("ALVR 服务版本必须是 20.14.1，当前为：" + serverVersion + "。已停止自动 USB 配置，避免混用版本。");
        return new[] {
            "{\"UpdateClientList\":{\"hostname\":\"client.wired\",\"action\":{\"AddIfMissing\":{\"trusted\":true,\"manual_ips\":[]}}}}",
            "{\"UpdateClientList\":{\"hostname\":\"client.wired\",\"action\":\"Trust\"}}"
        };
    }
    static void EnsureWiredClient()
    {
        if (!BackendReady()) throw new Exception("ALVR 本机服务尚未就绪，未配置 USB 客户端。");
        string version = ReadAlvrVersion();
        foreach (string json in WiredRequests(version)) {
            // Recheck ownership before each mutation; never configure another process.
            if (!BackendReady()) throw new Exception("ALVR 本机服务在配置前中断，已停止 USB 配置。");
            var request = LocalDashboardRequest("/api/dashboard-request", "POST");
            request.ContentType = "application/json";
            byte[] payload = Encoding.UTF8.GetBytes(json);
            request.ContentLength = payload.Length;
            using (var stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
                if (response.StatusCode != HttpStatusCode.OK) throw new Exception("ALVR USB 客户端配置请求未成功。");
        }
        Note("ALVR " + version + " USB client.wired add/trust requests accepted; ALVR handles USB client launch; live stream still requires confirmation");
    }
    static int WiredRequestSelfTest()
    {
        // Pure schema/version checks; no HTTP, USB, application or driver operations.
        string[] versions = { "20.14.1", " 20.14.1\r\n", "20.14.0", "21.0.0-dev14", "20.14.1-dev1", "\"20.14.1\"", "", null };
        for (int i = 0; i < versions.Length; i++) {
            bool accepted = true;
            try { WiredRequests(versions[i]); } catch { accepted = false; }
            if (accepted != (i < 2)) throw new Exception("Wired version guard test failed at index " + i);
            Console.WriteLine("PASS version-guard-" + i);
        }
        var serializer = new JavaScriptSerializer();
        string[] bodies = WiredRequests("20.14.1");
        foreach (var body in bodies) {
            var outer = serializer.Deserialize<Dictionary<string, object>>(body);
            var request = outer["UpdateClientList"] as Dictionary<string, object>;
            if (outer.Count != 1 || request == null || request.Count != 2 || Convert.ToString(request["hostname"]) != "client.wired")
                throw new Exception("Wired request must only target client.wired");
        }
        var add = (Dictionary<string, object>)((Dictionary<string, object>)serializer.Deserialize<Dictionary<string, object>>(bodies[0])["UpdateClientList"])["action"];
        var fields = add["AddIfMissing"] as Dictionary<string, object>;
        if (add.Count != 1 || fields == null || fields.Count != 2 || !(bool)fields["trusted"] || ((System.Collections.IEnumerable)fields["manual_ips"]).Cast<object>().Any())
            throw new Exception("AddIfMissing schema invalid");
        var trust = (Dictionary<string, object>)serializer.Deserialize<Dictionary<string, object>>(bodies[1])["UpdateClientList"];
        if (Convert.ToString(trust["action"]) != "Trust") throw new Exception("Trust schema invalid");
        Console.WriteLine("PASS client.wired-only AddIfMissing and Trust schemas");
        Console.WriteLine("PASS pure wired-client checks; no runtime mutation");
        return 0;
    }

    static void Ensure()
    {
        if (Running("vrserver")) {
            Note("Existing vrserver; waiting for ALVR API readiness without restarting");
            if (!WaitUntil(BackendReady, 45)) throw new Exception("SteamVR 已运行，但 ALVR 服务未在 45 秒内就绪（8082：" + OwnerText() + "）。请保存游戏后正常退出 VR，再重新启动。");
            EnsureStableDashboard();
            EnsureWiredClient();
            Note("Reused healthy SteamVR/ALVR backend"); return;
        }
        if (Running("vrmonitor") || Running("vrstartup") || Running("vrcompositor")) {
            Note("SteamVR startup in progress; waiting without termination");
            if (!WaitUntil(BackendReady, 45)) throw new Exception("SteamVR 正在启动或退出，45 秒后仍未就绪；未重启或终止进程。请检查 SteamVR 窗口后重试。");
            EnsureStableDashboard();
            EnsureWiredClient();
            return;
        }
        if (Running("steam") && SteamPreloadedAlvr()) {
            RequireNoGames(false);
            Note("Steam preloaded ALVR without vrserver; requesting graceful Steam shutdown");
            StartUserApp(SteamExe, "-shutdown");
            if (!WaitUntil(delegate { return !Running("steam") && PortOwners().Count == 0; }, 45))
                throw new Exception("Steam 未在 45 秒内正常退出，或 8082 端口仍占用；未强制终止。请保存并手动退出 Steam 后重试。");
        }
        StartSteamBeforeDriver(); // Gate both fresh and already-starting Steam clients.
        EnsureStableDashboard();
        if (!Running("vrserver") && !Running("vrmonitor")) StartUserApp(Monitor, "");
        if (!WaitUntil(BackendReady, 60)) throw new Exception("SteamVR 已请求启动，但 ALVR 服务 60 秒内未就绪（8082：" + OwnerText() + "）。请查看 ALVR/SteamVR 窗口；程序没有强制终止进程。");
        EnsureWiredClient();
        Note("ALVR API ready under vrserver; USB client prepared; headset connection is a separate check");
    }
    static void Stop(bool showErrors)
    {
        RequireNoGames(true);
        // Close the legacy ALVR watchdog exposure before any shutdown request.
        // Keep ALVR unregistered even if graceful shutdown later fails or times out.
        Driver(false);
        Note("ALVR driver unregistered before normal VR shutdown; other drivers preserved; remains unregistered on failure");
        if (Running("vrserver") || Running("vrmonitor") || Running("vrcompositor") || Running("vrstartup")) {
            Note("Requesting vrmonitor://quit");
            Process.Start(new ProcessStartInfo("vrmonitor://quit") { UseShellExecute = true });
            if (!WaitUntil(delegate { return !Running("vrserver") && !Running("vrmonitor") && !Running("vrcompositor") && !Running("vrstartup"); }, 50))
                throw new Exception("SteamVR 未在 50 秒内正常退出。ALVR 驱动已保持注销，未强制终止任何进程；请检查残余 SteamVR 进程后再启动。");
        }
        if (PortOwners().Count != 0)
            throw new Exception("SteamVR 已退出，但 8082 仍由以下进程占用：" + OwnerText() + "。请正常退出该应用后重试；程序没有强制终止进程。");

        Note("VR streaming stopped; port8082 free; ALVR driver remains unregistered for next Steam startup");
        foreach (var process in Processes("ALVR Dashboard")) {
            using (process) {
                try {
                    if (!process.HasExited && !process.CloseMainWindow())
                        Note("ALVR Dashboard did not accept CloseMainWindow; leaving it running");
                } catch (Exception ex) {
                    Note("ALVR Dashboard normal close request failed: " + ex.Message);
                }
            }
        }
        if (!WaitUntil(delegate { return !Running("ALVR Dashboard"); }, 8)) {
            Note("VR streaming stopped; ALVR Dashboard remains open; Steam left running");
            if (showErrors) MessageBox.Show("VR 串流已停止，ALVR 控制面板仍在运行，可稍后手动关闭。",
                "VR 会话", MessageBoxButtons.OK, MessageBoxIcon.Information);
        } else {
            Note("VR streaming stopped; ALVR Dashboard closed; Steam left running");
        }
    }

    static bool FreshGraphStatistics(string json, DateTimeOffset connectedAfter)
    {
        try {
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object raw;
            if (!root.TryGetValue("event_type", out raw)) return false;
            var eventType = raw as Dictionary<string, object>;
            object id, dataRaw;
            if (eventType == null || !eventType.TryGetValue("id", out id) || !string.Equals(Convert.ToString(id), "GraphStatistics", StringComparison.Ordinal)) return false;
            if (!eventType.TryGetValue("data", out dataRaw)) return false;
            var data = dataRaw as Dictionary<string, object>;
            object clientFps, serverFps;
            if (data == null || !data.TryGetValue("client_fps", out clientFps) || !data.TryGetValue("server_fps", out serverFps) ||
                Convert.ToDouble(clientFps, CultureInfo.InvariantCulture) <= 0 || Convert.ToDouble(serverFps, CultureInfo.InvariantCulture) <= 0) return false;
            if (!root.TryGetValue("timestamp", out raw)) return false;
            DateTimeOffset timestamp;
            // ALVR uses local HH:mm:ss.fff; parsing supplies today's local date.
            if (!DateTimeOffset.TryParse(Convert.ToString(raw), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out timestamp)) return false;
            var now = DateTimeOffset.Now;
            return Math.Abs((now - timestamp).TotalSeconds) < 5 && timestamp >= connectedAfter;
        } catch { return false; } // Malformed/error/history events never imply success.
    }
    static bool ReadFreshStream(int timeoutMs)
    {
        var connectedAfter = DateTimeOffset.Now;
        try {
            using (var socket = new ClientWebSocket())
            using (var cancellation = new CancellationTokenSource(timeoutMs))
            using (var message = new MemoryStream()) {
                socket.Options.SetRequestHeader("X-ALVR", "true");
                socket.Options.Proxy = null;
                socket.ConnectAsync(new Uri("ws://127.0.0.1:8082/api/events"), cancellation.Token).GetAwaiter().GetResult();
                byte[] buffer = new byte[16384];
                while (!cancellation.IsCancellationRequested) {
                    if (!Running("vrserver")) throw new IOException("SteamVR 已退出，无法继续等待头显串流。");
                    var received = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token).GetAwaiter().GetResult();
                    if (received.MessageType == WebSocketMessageType.Close) return false;
                    if (received.MessageType != WebSocketMessageType.Text) { message.SetLength(0); continue; }
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 1048576) return false;
                    if (!received.EndOfMessage) continue;
                    string json = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    if (FreshGraphStatistics(json, connectedAfter)) return true;
                }
            }
        } catch { /* Timeout, socket errors and malformed events are retried within the overall deadline. */ }
        return false;
    }
    static void WaitForStream()
    {
        Note("Waiting for fresh ALVR GraphStatistics, up to 180 seconds");
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline) {
            if (!Running("vrserver")) throw new Exception("SteamVR 已退出，已停止等待。请重新启动 VR 并在 Quest 中连接 ALVR。");
            int remaining = Math.Max(1, (int)Math.Min(4000, (deadline - DateTime.UtcNow).TotalMilliseconds));
            bool fresh = ReadFreshStream(remaining);
            if (!Running("vrserver")) throw new Exception("SteamVR 已退出，已停止等待。请重新启动 VR 并在 Quest 中连接 ALVR。");
            if (fresh && DateTime.UtcNow <= deadline) { Note("Fresh live ALVR stream confirmed"); return; }
            int pause = Math.Max(0, (int)Math.Min(500, (deadline - DateTime.UtcNow).TotalMilliseconds));
            if (pause > 0) Thread.Sleep(pause);
        }
        throw new Exception("180 秒内未检测到头显实时串流。请在 Quest 中打开 ALVR、连接电脑并看到 SteamVR 后，再启动游戏。VR 服务保持运行。");
    }

    static void Status()
    {
        Note("STATUS Steam=" + Running("steam") + " vrserver=" + Running("vrserver") + " Dashboard=" + Running("ALVR Dashboard") +
            " ALVR-driver-registered=" + Drivers().Contains(Canonical(AlvrRoot)) + " port8082=[" + OwnerText() + "] backend-ready=" + BackendReady());
    }
}






// Based on Microsoft's ExecInExplorer sample: use the existing desktop's
// Shell dispatch, not a new Shell.Application object in the calling process.
// https://learn.microsoft.com/en-us/windows/win32/shell/samples-execinexplorer
internal static class ExplorerDashboardLaunch
{
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [System.Runtime.InteropServices.PreserveSig] int QueryService(ref System.Guid service, ref System.Guid iid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out object result);
    }
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("000214E2-0000-0000-C000-000000000046"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        [System.Runtime.InteropServices.PreserveSig] int GetWindow(out System.IntPtr hwnd);
        [System.Runtime.InteropServices.PreserveSig] int ContextSensitiveHelp([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool enter);
        [System.Runtime.InteropServices.PreserveSig] int InsertMenusSB(System.IntPtr shared, System.IntPtr widths);
        [System.Runtime.InteropServices.PreserveSig] int SetMenuSB(System.IntPtr shared, System.IntPtr holemenu, System.IntPtr active);
        [System.Runtime.InteropServices.PreserveSig] int RemoveMenusSB(System.IntPtr shared);
        [System.Runtime.InteropServices.PreserveSig] int SetStatusTextSB([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string text);
        [System.Runtime.InteropServices.PreserveSig] int EnableModelessSB([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool enable);
        [System.Runtime.InteropServices.PreserveSig] int TranslateAcceleratorSB(System.IntPtr message, ushort id);
        [System.Runtime.InteropServices.PreserveSig] int BrowseObject(System.IntPtr pidl, uint flags);
        [System.Runtime.InteropServices.PreserveSig] int GetViewStateStream(uint mode, out System.IntPtr stream);
        [System.Runtime.InteropServices.PreserveSig] int GetControlWindow(uint id, out System.IntPtr hwnd);
        [System.Runtime.InteropServices.PreserveSig] int SendControlMsg(uint id, uint message, System.IntPtr wparam, System.IntPtr lparam, out System.IntPtr result);
        [System.Runtime.InteropServices.PreserveSig] int QueryActiveShellView(out IShellView view);
    }
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("000214E3-0000-0000-C000-000000000046"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        [System.Runtime.InteropServices.PreserveSig] int GetWindow(out System.IntPtr hwnd);
        [System.Runtime.InteropServices.PreserveSig] int ContextSensitiveHelp([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool enter);
        [System.Runtime.InteropServices.PreserveSig] int TranslateAccelerator(System.IntPtr message);
        [System.Runtime.InteropServices.PreserveSig] int EnableModeless([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool enable);
        [System.Runtime.InteropServices.PreserveSig] int UIActivate(uint state);
        [System.Runtime.InteropServices.PreserveSig] int Refresh();
        [System.Runtime.InteropServices.PreserveSig] int CreateViewWindow(IShellView previous, System.IntPtr settings, IShellBrowser browser, System.IntPtr rectangle, out System.IntPtr hwnd);
        [System.Runtime.InteropServices.PreserveSig] int DestroyViewWindow();
        [System.Runtime.InteropServices.PreserveSig] int GetCurrentInfo(System.IntPtr settings);
        [System.Runtime.InteropServices.PreserveSig] int AddPropertySheetPages(uint reserved, System.IntPtr callback, System.IntPtr parameter);
        [System.Runtime.InteropServices.PreserveSig] int SaveViewState();
        [System.Runtime.InteropServices.PreserveSig] int SelectItem(System.IntPtr pidl, uint flags);
        [System.Runtime.InteropServices.PreserveSig] int GetItemObject(uint item, ref System.Guid iid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out object result);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern System.IntPtr GetShellWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true)] static extern System.IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(System.IntPtr process, uint access, out System.IntPtr token);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(System.IntPtr handle);
    static void Release(object value) { if (value != null && System.Runtime.InteropServices.Marshal.IsComObject(value)) try { System.Runtime.InteropServices.Marshal.ReleaseComObject(value); } catch { } }
    internal static int ShellProcessId()
    {
        uint id; System.IntPtr window = GetShellWindow();
        if (window == System.IntPtr.Zero || GetWindowThreadProcessId(window, out id) == 0 || id == 0)
            throw new System.Exception("Windows 桌面 Explorer 不可用；未从游戏启动链创建 ALVR 控制面板。");
        using (var shell = System.Diagnostics.Process.GetProcessById((int)id))
        using (var current = System.Diagnostics.Process.GetCurrentProcess()) {
            if (!string.Equals(shell.ProcessName, "explorer", System.StringComparison.OrdinalIgnoreCase) || shell.SessionId != current.SessionId)
                throw new System.Exception("Windows 桌面进程身份不匹配；未启动 ALVR 控制面板。");
        }
        System.IntPtr process = OpenProcess(0x1000, false, id), token = System.IntPtr.Zero;
        try {
            if (process == System.IntPtr.Zero || !OpenProcessToken(process, 8, out token))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "无法核对 Explorer 用户身份。");
            using (var shellUser = new System.Security.Principal.WindowsIdentity(token))
            using (var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent())
                if (shellUser.User == null || !shellUser.User.Equals(currentUser.User))
                    throw new System.Exception("Explorer 与当前用户不一致；未启动 ALVR 控制面板。");
        } finally { if (token != System.IntPtr.Zero) CloseHandle(token); if (process != System.IntPtr.Zero) CloseHandle(process); }
        return (int)id;
    }
    internal static void Execute(string executable, string arguments)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
            throw new System.Exception("Explorer 启动需要 STA 线程；未创建 ALVR 控制面板。");
        int expectedShell = ShellProcessId();
        object windows = null, desktop = null, browser = null, background = null, application = null;
        IShellView view = null;
        try {
            windows = System.Activator.CreateInstance(System.Type.GetTypeFromCLSID(new System.Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true));
            object[] find = { null, null, 8, 0, 1 }; // SWC_DESKTOP, SWFO_NEEDDISPATCH
            var byref = new System.Reflection.ParameterModifier(5); byref[0] = true; byref[1] = true; byref[3] = true;
            desktop = windows.GetType().InvokeMember("FindWindowSW", System.Reflection.BindingFlags.InvokeMethod, null, windows, find, new[] { byref }, System.Globalization.CultureInfo.InvariantCulture, null);
            if (desktop == null) throw new System.Exception("没有找到桌面 Shell 自动化对象。");
            uint desktopPid;
            System.IntPtr desktopWindow = new System.IntPtr(System.Convert.ToInt32(find[3]));
            if (GetWindowThreadProcessId(desktopWindow, out desktopPid) == 0 || desktopPid != expectedShell)
                throw new System.Exception("桌面 Shell 自动化对象不属于当前 Explorer；未启动 ALVR 控制面板。");
            System.Guid topBrowser = new System.Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"), browserId = typeof(IShellBrowser).GUID;
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(((IServiceProvider)desktop).QueryService(ref topBrowser, ref browserId, out browser));
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(((IShellBrowser)browser).QueryActiveShellView(out view));
            System.Guid dispatch = new System.Guid("00020400-0000-0000-C000-000000000046");
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(view.GetItemObject(0, ref dispatch, out background)); // SVGIO_BACKGROUND
            application = background.GetType().InvokeMember("Application", System.Reflection.BindingFlags.GetProperty, null, background, null);
            application.GetType().InvokeMember("ShellExecute", System.Reflection.BindingFlags.InvokeMethod, null, application,
                new object[] { executable, arguments, System.IO.Path.GetDirectoryName(executable), "open", 1 });
        } catch (System.Exception ex) {
            throw new System.Exception("无法通过现有 Explorer 启动 ALVR 控制面板；未改用游戏子进程：" + ex.Message, ex);
        } finally { Release(application); Release(background); Release(view); Release(browser); Release(desktop); Release(windows); }
    }
}