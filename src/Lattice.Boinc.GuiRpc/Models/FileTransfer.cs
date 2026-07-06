using System.Xml.Linq;

namespace Lattice.Boinc.GuiRpc;

/// <summary>
/// An in-progress file upload or download, from get_file_transfers.
/// The daemon nests retry fields inside &lt;persistent_file_xfer&gt; and live-transfer
/// fields inside &lt;file_xfer&gt;; the BOINC reference parser reads all tags flatly
/// (lib/gui_rpc_client_ops.cpp, FILE_TRANSFER::parse), so parsing here accepts every
/// tag at any depth within &lt;file_transfer&gt;.
/// </summary>
public sealed record FileTransfer(
    string Name,
    string ProjectUrl,
    string ProjectName,
    double Nbytes,
    int Status,
    bool IsUpload,
    int NumRetries,
    DateTimeOffset? FirstRequestTime,
    DateTimeOffset? NextRequestTime,
    double TimeSoFar,
    double BytesXferred,
    double FileOffset,
    double XferSpeed,
    double ProjectBackoff,
    bool PersXferActive,
    bool XferActive)
{
    internal static FileTransfer Parse(XElement e)
    {
        // Direction: modern daemons emit is_upload; very old ones only generated_locally.
        XElement? direction = Find(e, "is_upload") ?? Find(e, "generated_locally");
        // Progress: bytes_xferred is the live value (inside file_xfer); last_bytes_xferred
        // is the persisted value between attempts.
        XElement? bytes = Find(e, "bytes_xferred") ?? Find(e, "last_bytes_xferred");
        return new(
            Str(e, "name"),
            Str(e, "project_url"),
            Str(e, "project_name"),
            Dbl(e, "nbytes"),
            (int)Dbl(e, "status"),
            direction is not null && BoolValue(direction),
            (int)Dbl(e, "num_retries"),
            Timestamp(e, "first_request_time"),
            Timestamp(e, "next_request_time"),
            Dbl(e, "time_so_far"),
            bytes is not null ? DblValue(bytes) : 0.0,
            Dbl(e, "file_offset"),
            Dbl(e, "xfer_speed"),
            Dbl(e, "project_backoff"),
            e.Element("persistent_file_xfer") is not null,
            e.Element("file_xfer") is not null);
    }

    private static XElement? Find(XElement e, string name) => e.Descendants(name).FirstOrDefault();

    private static string Str(XElement e, string name) => (string?)Find(e, name) ?? string.Empty;

    private static double Dbl(XElement e, string name) =>
        Find(e, name) is { } el ? DblValue(el) : 0.0;

    private static double DblValue(XElement el) =>
        double.TryParse((string)el, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0.0;

    private static bool BoolValue(XElement el)
    {
        string v = ((string)el).Trim();
        return v.Length == 0 || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? Timestamp(XElement e, string name)
    {
        double seconds = Dbl(e, name);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
    }
}
