using System.Runtime.CompilerServices;

namespace Lattice.VisualTests;

internal static class VisualPaths
{
    /// <summary>Directory of the calling source file (compile-time path).</summary>
    public static string SourceDir([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;

    /// <summary>
    /// Transient outputs (diff masks, calibration reports). Under the gitignored repo
    /// <c>artifacts/</c> tree by default; CI points its upload step at the same place.
    /// Override with <c>LATTICE_VISUAL_ARTIFACTS</c>.
    /// </summary>
    public static string ArtifactsDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("LATTICE_VISUAL_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            // SourceDir() here = <repo>/tests/Lattice.VisualTests ; up two → repo root.
            return Path.GetFullPath(Path.Combine(SourceDir(), "..", "..", "artifacts", "visual"));
        }
    }
}
