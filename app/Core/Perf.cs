using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoolPlayer;

/// <summary>
/// Режимы работы:
///   «Классический» — упор на дизайн: анимации, плавный текст, размытая обложка.
///   «Игровой» — минимум ресурсов: без анимаций и лишних перерисовок, режим эффективности
///   Windows (EcoQoS), пониженный приоритет, вкладки засыпают сразу, память отдаётся системе.
/// </summary>
static class Perf
{
    public static bool Game => SettingsStore.Current.PerfMode == "game";
    public static event Action? Changed;

    static GameOptions O => SettingsStore.Current.Game;
    public static bool NoIslandAnim => Game && O.IslandAnimations;
    public static bool NoIslandEq => Game && O.IslandEqualizer;
    public static bool NoLyricsAnim => Game && O.LyricsAnimations;
    public static bool NoBackdrop => Game && O.LyricsBackdrop;
    public static bool FastSleep => Game && O.FastTabSleep;
    public static bool NoAudioEq => Game && O.AudioEqualizer;

    /// <summary>Настройки игрового режима поменялись — применить заново.</summary>
    public static void OptionsChanged()
    {
        SettingsStore.Save();
        Apply();
        Changed?.Invoke();
    }

    public static void Set(bool game)
    {
        SettingsStore.Current.PerfMode = game ? "game" : "classic";
        SettingsStore.Save();
        Apply();
        Changed?.Invoke();
    }

    public static void Toggle() => Set(!Game);

    public static void Apply()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.PriorityClass = Game && O.LowPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            var s = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = Game && O.EfficiencyMode ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };
            SetProcessInformation(p.Handle, ProcessPowerThrottling, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            if (Game && O.TrimMemory) Trim();
        }
        catch (Exception ex) { Program.Log(ex); }
    }

    /// <summary>Отдать системе неиспользуемую память.</summary>
    public static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Optimized, false);
        using var p = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(p.Handle, -1, -1);
    }

    const int ProcessPowerThrottling = 4;
    const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr h, nint min, nint max);
}
