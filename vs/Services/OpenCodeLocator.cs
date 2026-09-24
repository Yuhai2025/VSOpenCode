using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VSOpenCode.Services
{
    /// <summary>
    /// Describes the resolved OpenCode binary and how it has to be launched.
    /// </summary>
    /// <remarks>
    /// A globally installed npm package on Windows is exposed through a
    /// <c>.cmd</c> shim, which is a batch script — it cannot be handed to
    /// <c>CreateProcess</c> directly, nor can its output be redirected that way.
    /// Whenever possible the shim is resolved to the real <c>.exe</c> it wraps;
    /// if that fails the binary is launched through <c>cmd.exe /c</c> instead.
    /// </remarks>
    public sealed class OpenCodeExecutable
    {
        public OpenCodeExecutable(string binaryPath, string nodeDirectory)
        {
            BinaryPath = binaryPath;
            NodeDirectory = nodeDirectory;
        }

        /// <summary>Path of the opencode binary (native executable, or a batch shim).</summary>
        public string BinaryPath { get; }

        /// <summary>Directory that contains <c>node.exe</c>, if it could be determined.</summary>
        public string NodeDirectory { get; }

        /// <summary>True when <see cref="BinaryPath"/> is a batch script that needs cmd.exe.</summary>
        public bool IsBatchShim
        {
            get
            {
                if (string.IsNullOrEmpty(BinaryPath)) return false;
                return BinaryPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || BinaryPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Value for <c>ProcessStartInfo.FileName</c>.</summary>
        public string FileName => IsBatchShim ? "cmd.exe" : BinaryPath;

        /// <summary>
        /// Build the argument string that runs <paramref name="arguments"/>
        /// against this binary.
        /// </summary>
        public string BuildArguments(string arguments)
        {
            // cmd.exe /c ""C:\path with space\opencode.cmd" serve"
            return IsBatchShim
                ? string.Format("/c \"\"{0}\" {1}\"", BinaryPath, arguments)
                : arguments;
        }

        public override string ToString()
        {
            return BinaryPath ?? "(not found)";
        }
    }

    /// <summary>
    /// Locates the <c>opencode</c> binary on the user's machine.
    /// </summary>
    /// <remarks>
    /// Visual Studio is a GUI application: it is launched from the Start menu or
    /// Explorer and therefore never evaluates a shell profile. Version managers
    /// such as <b>fnm</b>, <b>nvm</b> and <b>volta</b> only put Node.js on
    /// <c>PATH</c> from inside an interactive shell (via <c>fnm env</c>), so a
    /// plain <c>where opencode</c> inside VS finds nothing. This locator
    /// therefore also probes the on-disk layout of the well known version
    /// managers instead of relying on <c>PATH</c>.
    /// </remarks>
    public static class OpenCodeLocator
    {
        /// <summary>
        /// Environment variable that overrides auto-detection. Set it to the full
        /// path of <c>opencode.exe</c> (or <c>opencode.cmd</c>) and restart Visual Studio.
        /// </summary>
        public const string PathOverrideVariable = "VSOPENCODE_OPENCODE_PATH";

        private const string NpmPackageName = "opencode-ai";

        // Candidate scores — higher wins.
        private const int ScoreOverride = 100;
        private const int ScoreNativeBinary = 90;
        private const int ScoreExe = 70;
        private const int ScoreShim = 40;
        private const int ScoreExtensionless = 30;

        private static readonly bool IsWindows =
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// Resolve the best available opencode binary, or <c>null</c> when none
        /// could be found.
        /// </summary>
        public static OpenCodeExecutable Locate()
        {
            var candidates = new List<Candidate>();

            AddOverride(candidates);
            AddFromKnownRoots(candidates);
            AddFromPath(candidates);

            var best = candidates
                .Where(c => !string.IsNullOrEmpty(c.Path))
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();

            if (best == null)
            {
                Debug.WriteLine("[OpenCode] No opencode binary found.");
                return null;
            }

            var binary = best.Path;
            var nodeDirectory = best.NodeDirectory;

            // A .cmd shim cannot be started through CreateProcess — resolve it
            // to the executable it wraps so we can launch and kill it directly.
            if (IsBatchShim(binary))
            {
                var resolved = ResolveBatchShim(binary);
                if (resolved != null)
                {
                    Debug.WriteLine($"[OpenCode] Resolved batch shim '{binary}' to '{resolved}'");
                    binary = resolved;
                }
            }

            if (nodeDirectory == null)
                nodeDirectory = FindNodeDirectory(binary);

            Debug.WriteLine($"[OpenCode] Using opencode binary '{binary}' (node dir: {nodeDirectory ?? "unknown"})");
            return new OpenCodeExecutable(binary, nodeDirectory);
        }

        /// <summary>
        /// Human readable explanation used when <see cref="Locate"/> fails.
        /// </summary>
        public static string BuildNotFoundDiagnostic()
        {
            var builder = new StringBuilder();
            builder.Append("opencode was not found on PATH or in any known Node.js installation.");

            var searched = GetNodeRoots()
                .Where(Directory.Exists)
                .Take(6)
                .ToList();

            if (searched.Count > 0)
                builder.Append(" Searched: ").Append(string.Join(", ", searched)).Append('.');

            builder.Append(" Set the ")
                .Append(PathOverrideVariable)
                .Append(" environment variable to the full path of opencode.exe and restart Visual Studio.");

            return builder.ToString();
        }

        // -------------------------------------------------------------------
        // Candidate collection
        // -------------------------------------------------------------------

        /// <summary>Explicit override through <see cref="PathOverrideVariable"/>.</summary>
        private static void AddOverride(List<Candidate> candidates)
        {
            var configured = Environment.GetEnvironmentVariable(PathOverrideVariable);
            if (string.IsNullOrEmpty(configured)) return;

            configured = configured.Trim().Trim('"');
            if (configured.Length == 0) return;

            if (!File.Exists(configured))
            {
                Debug.WriteLine($"[OpenCode] {PathOverrideVariable} is set but does not exist: {configured}");
                return;
            }

            candidates.Add(new Candidate(configured, ScoreOverride, null));
        }

        private static void AddFromKnownRoots(List<Candidate> candidates)
        {
            foreach (var root in GetNodeRoots())
                AddRootCandidates(candidates, root);
        }

        /// <summary>Add every opencode binary that lives inside <paramref name="root"/>.</summary>
        private static void AddRootCandidates(List<Candidate> candidates, string root)
        {
            if (string.IsNullOrEmpty(root)) return;

            // The real binary ships inside the globally installed npm package.
            var packageBinary = FindPackageBinary(root);
            if (packageBinary != null)
                candidates.Add(new Candidate(packageBinary, ScoreNativeBinary, root));

            if (IsWindows)
            {
                AddIfExists(candidates, Path.Combine(root, "opencode.exe"), ScoreExe, root);
                AddIfExists(candidates, Path.Combine(root, "bin", "opencode.exe"), ScoreExe, root);
                AddIfExists(candidates, Path.Combine(root, "opencode.cmd"), ScoreShim, root);
                AddIfExists(candidates, Path.Combine(root, "bin", "opencode.cmd"), ScoreShim, root);
                AddIfExists(candidates, Path.Combine(root, "opencode"), ScoreExtensionless, root);
            }
            else
            {
                AddIfExists(candidates, Path.Combine(root, "bin", "opencode"), ScoreExe, root);
                AddIfExists(candidates, Path.Combine(root, "opencode"), ScoreExe, root);
            }
        }

        private static void AddIfExists(List<Candidate> candidates, string path, int score, string root)
        {
            try
            {
                if (File.Exists(path))
                    candidates.Add(new Candidate(path, score, root));
            }
            catch { }
        }

        /// <summary>Add everything the current <c>PATH</c> offers.</summary>
        private static void AddFromPath(List<Candidate> candidates)
        {
            foreach (var found in LookupOnPath())
            {
                var extension = Path.GetExtension(found);
                int score;

                if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                    score = ScoreExe;
                else if (IsBatchShim(found))
                    score = ScoreShim;
                else
                    score = ScoreExtensionless;

                candidates.Add(new Candidate(found, score, null));
            }
        }

        private static IEnumerable<string> LookupOnPath()
        {
            string output = null;

            try
            {
                var psi = IsWindows
                    ? new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c where opencode 2>nul",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                    : new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        Arguments = "-c \"command -v opencode 2>/dev/null\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false
                    };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return Enumerable.Empty<string>();

                    output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                return Enumerable.Empty<string>();
            }

            if (string.IsNullOrEmpty(output)) return Enumerable.Empty<string>();

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        // -------------------------------------------------------------------
        // Node.js installation roots
        // -------------------------------------------------------------------

        /// <summary>
        /// Every directory that may hold a Node.js installation or an npm global
        /// prefix, ordered from most to least specific.
        /// </summary>
        public static IEnumerable<string> GetNodeRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in EnumerateNodeRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                if (seen.Add(root)) yield return root;
            }
        }

        private static IEnumerable<string> EnumerateNodeRoots()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // --- fnm (Fast Node Manager) ---
            foreach (var fnmDirectory in GetFnmDirectories())
            {
                // Stable symlink to the version fnm activates by default.
                yield return Path.Combine(fnmDirectory, "aliases", "default");
                yield return Path.Combine(fnmDirectory, "current");

                foreach (var versionDirectory in SortedVersionDirectories(Path.Combine(fnmDirectory, "node-versions")))
                    yield return Path.Combine(versionDirectory, "installation");
            }

            // --- nvm-windows ---
            var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
            if (!string.IsNullOrEmpty(nvmHome))
            {
                foreach (var versionDirectory in SortedVersionDirectories(nvmHome))
                    yield return versionDirectory;
            }
            if (!string.IsNullOrEmpty(appData))
            {
                foreach (var versionDirectory in SortedVersionDirectories(Path.Combine(appData, "nvm")))
                    yield return versionDirectory;
            }

            // --- nvm (unix layout) ---
            var nvmDir = Environment.GetEnvironmentVariable("NVM_DIR");
            if (!string.IsNullOrEmpty(nvmDir))
            {
                foreach (var versionDirectory in SortedVersionDirectories(Path.Combine(nvmDir, "versions", "node")))
                    yield return versionDirectory;
            }

            // --- volta ---
            var voltaHome = Environment.GetEnvironmentVariable("VOLTA_HOME");
            if (string.IsNullOrEmpty(voltaHome) && !string.IsNullOrEmpty(localAppData))
                voltaHome = Path.Combine(localAppData, "Volta");
            if (!string.IsNullOrEmpty(voltaHome))
            {
                yield return Path.Combine(voltaHome, "bin");
                yield return Path.Combine(voltaHome, "tools", "image", "packages", NpmPackageName);
            }

            // --- pnpm ---
            var pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");
            if (!string.IsNullOrEmpty(pnpmHome))
                yield return pnpmHome;
            if (!string.IsNullOrEmpty(localAppData))
                yield return Path.Combine(localAppData, "pnpm");

            // --- bun ---
            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, ".bun", "bin");

            // --- standalone installers (opencode.ai install script) ---
            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, ".opencode", "bin");
            if (!string.IsNullOrEmpty(localAppData))
                yield return Path.Combine(localAppData, "opencode", "bin");

            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "opencode");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86))
                yield return Path.Combine(programFilesX86, "opencode");

            // --- npm global prefix (the default for the official installer) ---
            foreach (var prefix in GetNpmPrefixes(appData, home))
                yield return prefix;

            // --- Node.js installations themselves ---
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "nodejs");
            if (!string.IsNullOrEmpty(programFilesX86))
                yield return Path.Combine(programFilesX86, "nodejs");
            if (!string.IsNullOrEmpty(localAppData))
                yield return Path.Combine(localAppData, "Programs", "nodejs");

            if (!IsWindows)
            {
                yield return "/usr/local";
                yield return "/usr/lib/node_modules";
            }
        }

        /// <summary>
        /// Candidate fnm data directories. <c>FNM_DIR</c> wins; the rest mirror
        /// fnm's own resolution order.
        /// </summary>
        private static IEnumerable<string> GetFnmDirectories()
        {
            var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR");
            if (!string.IsNullOrEmpty(fnmDir)) yield return fnmDir;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "fnm");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                yield return Path.Combine(localAppData, "fnm");

            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdgDataHome))
                yield return Path.Combine(xdgDataHome, "fnm");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".fnm");
                yield return Path.Combine(home, ".local", "share", "fnm");
            }
        }

        private static IEnumerable<string> GetNpmPrefixes(string appData, string home)
        {
            // Honour a custom prefix configured in the user's .npmrc.
            foreach (var prefix in GetNpmPrefixFromNpmrc(home))
                yield return prefix;

            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "npm");

            var npmConfigPrefix = Environment.GetEnvironmentVariable("npm_config_prefix");
            if (!string.IsNullOrEmpty(npmConfigPrefix))
                yield return npmConfigPrefix;

            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, ".npm-global");
        }

        private static IEnumerable<string> GetNpmPrefixFromNpmrc(string home)
        {
            if (string.IsNullOrEmpty(home)) return Enumerable.Empty<string>();

            var npmrc = Path.Combine(home, ".npmrc");
            if (!File.Exists(npmrc)) return Enumerable.Empty<string>();

            string prefix = null;

            try
            {
                foreach (var rawLine in File.ReadAllLines(npmrc))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                    var separator = line.IndexOf('=');
                    if (separator < 0) continue;

                    if (!line.Substring(0, separator).Trim()
                            .Equals("prefix", StringComparison.OrdinalIgnoreCase)) continue;

                    var value = line.Substring(separator + 1).Trim().Trim('"').Trim('\'');
                    if (value.StartsWith("~/") || value.StartsWith("~\\"))
                        value = Path.Combine(home, value.Substring(2));

                    if (value.Length > 0)
                    {
                        prefix = value;
                        break;
                    }
                }
            }
            catch
            {
                return Enumerable.Empty<string>();
            }

            return prefix == null
                ? Enumerable.Empty<string>()
                : new[] { prefix };
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Find <c>node_modules/opencode-ai/bin/opencode[.exe]</c> inside a node root.
        /// Windows keeps global packages in <c>node_modules</c>, unix in <c>lib/node_modules</c>.
        /// </summary>
        private static string FindPackageBinary(string root)
        {
            try
            {
                var moduleRoots = new[]
                {
                    Path.Combine(root, "node_modules"),
                    Path.Combine(root, "lib", "node_modules")
                };

                var binaryNames = IsWindows
                    ? new[] { "opencode.exe" }
                    : new[] { "opencode", "opencode.exe" };

                foreach (var moduleRoot in moduleRoots)
                {
                    if (!Directory.Exists(moduleRoot)) continue;

                    foreach (var packageName in PackageNames(moduleRoot))
                    {
                        foreach (var binaryName in binaryNames)
                        {
                            var candidate = Path.Combine(moduleRoot, packageName, "bin", binaryName);
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>The canonical package name first, then any other <c>opencode*</c> package.</summary>
        private static IEnumerable<string> PackageNames(string moduleRoot)
        {
            yield return NpmPackageName;

            foreach (var directory in SafeEnumerateDirectories(moduleRoot, "opencode*"))
                yield return Path.GetFileName(directory);
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path, string pattern)
        {
            try
            {
                return Directory.EnumerateDirectories(path, pattern).ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Parse a <c>.cmd</c> npm shim and return the executable it wraps.
        /// </summary>
        /// <remarks>
        /// npm's cmd-shim writes a line such as
        /// <c>"%~dp0\node_modules\opencode-ai\bin\opencode.exe" %*</c>.
        /// Some generators omit the <c>~</c>, so both spellings are accepted.
        /// </remarks>
        private static string ResolveBatchShim(string shimPath)
        {
            try
            {
                var content = ReadHead(shimPath, 4096);
                if (string.IsNullOrEmpty(content)) return null;

                var directory = Path.GetDirectoryName(shimPath);
                if (string.IsNullOrEmpty(directory)) return null;

                // "%~dp0\...\opencode.exe" — also tolerate "%dp0%\..." variants.
                foreach (Match match in Regex.Matches(content, "\"%~?dp0%?([^\"]+)\"", RegexOptions.IgnoreCase))
                {
                    // npm writes Windows separators; normalise so the captured
                    // path composes correctly with Path.Combine. The leading
                    // separator has to go: Path.Combine treats a rooted second
                    // argument as an absolute path and drops the base directory.
                    var relative = match.Groups[1].Value
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);
                    var resolved = Path.GetFullPath(Path.Combine(directory, relative));

                    // Only a real executable helps us — a .js entry point still
                    // has to be started through node and stays a batch shim.
                    if (!File.Exists(resolved)) continue;
                    if (IsWindows && !resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    return resolved;
                }
            }
            catch { }

            return null;
        }

        private static string ReadHead(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[maxBytes];
                var read = stream.Read(buffer, 0, maxBytes);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        /// <summary>Walk upwards from a binary looking for the directory holding node.</summary>
        private static string FindNodeDirectory(string binaryPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(binaryPath);
                for (var i = 0; i < 6 && !string.IsNullOrEmpty(directory); i++)
                {
                    if (File.Exists(Path.Combine(directory, "node.exe"))
                        || File.Exists(Path.Combine(directory, "node")))
                        return directory;

                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch { }

            return null;
        }

        private static IEnumerable<string> SortedVersionDirectories(string parent)
        {
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                yield break;

            List<string> directories;
            try
            {
                directories = Directory.GetDirectories(parent).ToList();
            }
            catch
            {
                yield break;
            }

            foreach (var directory in directories.OrderByDescending(d => ParseVersion(Path.GetFileName(d))))
                yield return directory;
        }

        private static Version ParseVersion(string name)
        {
            try
            {
                var match = Regex.Match(name ?? string.Empty, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
                if (!match.Success) return new Version(0, 0, 0);

                var major = int.Parse(match.Groups[1].Value);
                var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
                var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

                return new Version(major, minor, build);
            }
            catch
            {
                return new Version(0, 0, 0);
            }
        }

        private static bool IsBatchShim(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Candidate
        {
            public Candidate(string path, int score, string nodeDirectory)
            {
                Path = path;
                Score = score;
                NodeDirectory = nodeDirectory;
            }

            public string Path { get; }
            public int Score { get; }
            public string NodeDirectory { get; }
        }
    }
}
