#pragma warning disable CS1591 // enum members are self-descriptive; names mirror BOINC constants
namespace Lattice.Boinc.GuiRpc;

// Integer values mirror BOINC lib/common_defs.h. Unknown values from newer
// daemons are preserved by direct cast — never assume these lists are complete.

public enum RunMode { Always = 1, Auto = 2, Never = 3, Restore = 4 }

public enum SuspendReason
{
    NotSuspended = 0, Batteries = 1, UserActive = 2, UserRequest = 4, TimeOfDay = 8,
    Benchmarks = 16, DiskSize = 32, CpuThrottle = 64, NoRecentInput = 128,
    InitialDelay = 256, ExclusiveAppRunning = 512, CpuUsage = 1024,
    NetworkQuotaExceeded = 2048, Os = 4096, WifiState = 4097,
    BatteryCharging = 4098, BatteryOverheated = 4099, NoGuiKeepalive = 4100,
}

public enum MessagePriority { Info = 1, UserAlert = 2, InternalError = 3 }

public enum ResultState
{
    New = 0, FilesDownloading = 1, FilesDownloaded = 2, ComputeError = 3,
    FilesUploading = 4, FilesUploaded = 5, Aborted = 6, UploadFailed = 7,
}
