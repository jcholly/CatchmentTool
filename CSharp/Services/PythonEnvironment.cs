using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Discovers Python installations, locates the delineation script,
    /// and validates that required packages are installed.
    /// </summary>
    public class PythonEnvironment
    {
        /// <summary>
        /// Path to the Python executable, or null if not found.
        /// </summary>
        public string PythonPath { get; private set; }

        /// <summary>
        /// Path to the catchment_delineation.py script, or null if not found.
        /// </summary>
        public string ScriptPath { get; private set; }

        /// <summary>
        /// Human-readable Python version string (e.g. "Python 3.11.5").
        /// </summary>
        public string PythonVersion { get; private set; }

        /// <summary>
        /// List of required Python packages.
        /// </summary>
        public static readonly string[] RequiredPackages = new[]
        {
            "whitebox", "rasterio", "geopandas", "shapely", "numpy", "fiona", "pyproj"
        };

        /// <summary>
        /// Optional callback for status messages.
        /// </summary>
        public Action<string> StatusCallback { get; set; }

        // -------------------------------------------------------------------
        // Discovery
        // -------------------------------------------------------------------

        /// <summary>
        /// Discover Python and the delineation script.
        /// Returns true if both are found.
        /// </summary>
        public bool Discover()
        {
            PythonPath = FindPython();
            if (PythonPath != null)
            {
                PythonVersion = GetVersion(PythonPath);
            }

            ScriptPath = FindScript();

            return PythonPath != null && ScriptPath != null;
        }

        /// <summary>
        /// Search for a working Python 3 executable.
        /// First checks for a bundled Python, then py launcher, then PATH, then common locations.
        /// Skips the Windows Store Python stub.
        /// </summary>
        private string FindPython()
        {
            // Priority 1: Check for bundled Python inside the plugin directory
            string bundledPython = FindBundledPython();
            if (bundledPython != null)
            {
                string ver = GetVersion(bundledPython);
                if (ver != null && ver.Contains("3."))
                {
                    StatusCallback?.Invoke($"Found bundled {ver} at {bundledPython}");
                    return bundledPython;
                }
            }

            // Priority 2: Candidates on PATH (skip Windows Store stub)
            string[] candidates = new[] { "py", "python", "python3" };

            foreach (var candidate in candidates)
            {
                // Skip the Windows Store stub
                if (IsWindowsStoreStub(candidate))
                {
                    StatusCallback?.Invoke($"Skipping Windows Store stub: {candidate}");
                    continue;
                }

                string version = GetVersion(candidate);
                if (version != null && version.Contains("3."))
                {
                    StatusCallback?.Invoke($"Found {version} via '{candidate}'");
                    // For the py launcher, resolve to the actual path
                    if (candidate == "py")
                        return "py";
                    return candidate;
                }
            }

            // Check common Windows install locations
            string[] commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "Python"),
                @"C:\Python311",
                @"C:\Python312",
                @"C:\Python310",
                @"C:\Program Files\Python311",
                @"C:\Program Files\Python312",
                @"C:\Program Files\Python310",
            };

            foreach (var basePath in commonPaths)
            {
                if (!Directory.Exists(basePath)) continue;

                // Check direct python.exe
                string direct = Path.Combine(basePath, "python.exe");
                if (File.Exists(direct))
                {
                    string ver = GetVersion(direct);
                    if (ver != null && ver.Contains("3."))
                    {
                        StatusCallback?.Invoke($"Found {ver} at {direct}");
                        return direct;
                    }
                }

                // Check subdirectories (e.g. Python311\python.exe)
                try
                {
                    foreach (var dir in Directory.GetDirectories(basePath))
                    {
                        string exe = Path.Combine(dir, "python.exe");
                        if (File.Exists(exe))
                        {
                            string ver = GetVersion(exe);
                            if (ver != null && ver.Contains("3."))
                            {
                                StatusCallback?.Invoke($"Found {ver} at {exe}");
                                return exe;
                            }
                        }
                    }
                }
                catch { /* permission denied etc. */ }
            }

            StatusCallback?.Invoke("Python 3 not found");
            return null;
        }

        /// <summary>
        /// Locate the catchment_delineation.py script relative to the plugin DLL,
        /// or in well-known locations.
        /// </summary>
        private string FindScript()
        {
            string dllDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

            string[] searchPaths = new[]
            {
                // Alongside DLL in bundle Contents/Python/
                Path.Combine(dllDir, "Python", "catchment_delineation.py"),
                // One level up (Development layout)
                Path.Combine(dllDir, "..", "Python", "catchment_delineation.py"),
                // User Documents
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                             "CatchmentTool", "Python", "catchment_delineation.py"),
            };

            foreach (var path in searchPaths)
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                {
                    StatusCallback?.Invoke($"Script found: {full}");
                    return full;
                }
            }

            StatusCallback?.Invoke("catchment_delineation.py not found. Searched:");
            foreach (var p in searchPaths)
                StatusCallback?.Invoke($"  {Path.GetFullPath(p)}");

            return null;
        }

        // -------------------------------------------------------------------
        // Package validation
        // -------------------------------------------------------------------

        /// <summary>
        /// Check which required packages are installed.
        /// Returns a list of missing package names (empty = all good).
        /// </summary>
        public List<string> CheckMissingPackages()
        {
            var missing = new List<string>();
            if (PythonPath == null) return new List<string>(RequiredPackages);

            foreach (var pkg in RequiredPackages)
            {
                if (!IsPackageInstalled(pkg))
                    missing.Add(pkg);
            }

            return missing;
        }

        private static readonly System.Text.RegularExpressions.Regex SafePackageName =
            new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9_\-]+$");

        private bool IsPackageInstalled(string packageName)
        {
            if (!SafePackageName.IsMatch(packageName))
                return false;

            try
            {
                var args = PythonPath == "py"
                    ? $"-3 -c \"import {packageName}\""
                    : $"-c \"import {packageName}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = PythonPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Install missing packages via pip.
        /// Uses safe argument passing (no shell, no string concatenation of package names).
        /// Returns true if all installs succeeded.
        /// </summary>
        public bool InstallPackages(IEnumerable<string> packages)
        {
            // Validate package names against a safe pattern to prevent injection
            var validPattern = new System.Text.RegularExpressions.Regex(
                @"^[a-zA-Z0-9_\-]+([<>=!]+[\d.]+)?$");

            var safePackages = new List<string>();
            foreach (var pkg in packages)
            {
                if (!validPattern.IsMatch(pkg))
                {
                    StatusCallback?.Invoke($"Skipping invalid package name: '{pkg}'");
                    continue;
                }
                safePackages.Add(pkg);
            }

            if (safePackages.Count == 0)
            {
                StatusCallback?.Invoke("No valid packages to install");
                return true;
            }

            StatusCallback?.Invoke($"Installing: {string.Join(", ", safePackages)}");

            try
            {
                // Build arguments safely — UseShellExecute=false prevents shell interpretation
                var psi = new ProcessStartInfo
                {
                    FileName = PythonPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Build the argument string safely (no cmd.exe shell)
                var argParts = new List<string>();
                if (PythonPath == "py")
                    argParts.Add("-3");
                argParts.Add("-m");
                argParts.Add("pip");
                argParts.Add("install");
                argParts.Add("--no-input");
                argParts.Add("--quiet");
                argParts.AddRange(safePackages);

                psi.Arguments = string.Join(" ", argParts);

                var output = new StringBuilder();
                var error = new StringBuilder();

                using (var proc = Process.Start(psi))
                {
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit(300000); // 5 minutes

                    if (proc.ExitCode == 0)
                    {
                        StatusCallback?.Invoke("Packages installed successfully");
                        return true;
                    }
                    else
                    {
                        StatusCallback?.Invoke($"pip install failed (exit {proc.ExitCode}): {error}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"pip install error: {ex.Message}");
                return false;
            }
        }

        // -------------------------------------------------------------------
        // Execution
        // -------------------------------------------------------------------

        /// <summary>
        /// Build ProcessStartInfo for running the delineation script.
        /// </summary>
        public ProcessStartInfo BuildProcessStartInfo(string arguments)
        {
            string fullArgs = PythonPath == "py"
                ? $"-3 \"{ScriptPath}\" {arguments}"
                : $"\"{ScriptPath}\" {arguments}";

            return new ProcessStartInfo
            {
                FileName = PythonPath,
                Arguments = fullArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ScriptPath)
            };
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Look for a bundled Python in the plugin's directory structure.
        /// Expected path: {DLL dir}/Python/python-embed/python.exe
        /// </summary>
        private string FindBundledPython()
        {
            string dllDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

            string[] searchPaths = new[]
            {
                Path.Combine(dllDir, "Python", "python-embed", "python.exe"),
                Path.Combine(dllDir, "..", "Python", "python-embed", "python.exe"),
                Path.Combine(dllDir, "python-embed", "python.exe"),
            };

            foreach (var path in searchPaths)
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                    return full;
            }

            return null;
        }

        /// <summary>
        /// Detect the Windows Store Python stub (0-byte exe in WindowsApps).
        /// </summary>
        private bool IsWindowsStoreStub(string executable)
        {
            try
            {
                // Resolve the full path via where.exe
                var psi = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                    // The Store stub lives in Microsoft\WindowsApps
                    if (output.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private string GetVersion(string executable)
        {
            try
            {
                string args = executable == "py" ? "-3 --version" : "--version";

                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd().Trim();
                    string stderr = proc.StandardError.ReadToEnd().Trim();
                    proc.WaitForExit(5000);

                    // Python prints version to stdout or stderr depending on version
                    string version = !string.IsNullOrEmpty(stdout) ? stdout : stderr;
                    return version.StartsWith("Python") ? version : null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
