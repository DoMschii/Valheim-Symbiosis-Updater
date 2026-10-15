using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Valheim_Symbiosis_Updater
{
    /// <summary>
    /// BepInEx 5 preloader patcher.
    ///
    /// The updater does not patch any Valheim assembly. BepInEx still discovers this
    /// class because it exposes the legacy v5 patcher contract. Initialize() executes
    /// before the normal BepInEx Chainloader scans/loads plugins, which makes it safe
    /// to replace Symbiosis gameplay DLLs during the same Valheim startup.
    /// </summary>
    public static class Patcher
    {
        public const string UpdaterVersion = "2.0.2";
        private const string ConfigFileName = "Valheim_Symbiosis_Updater.json";
        private const string VersionMarkerName = "Valheim_Symbiosis.version";
        private const string UserAgent = "Valheim-Symbiosis-Preloader-Updater/2.0.2";

        // Empty on purpose: this patcher only uses Initialize() as a pre-chainloader
        // bootstrap and does not rewrite any Valheim managed assembly.
        public static IEnumerable<string> TargetDLLs
        {
            get { return Array.Empty<string>(); }
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            // Intentionally empty. Required by the BepInEx 5 patcher contract.
        }

        public static void Initialize()
        {
            try
            {
                RunUpdateCheck();
            }
            catch (Exception ex)
            {
                Warn("Updater failed open; Valheim continues with the installed version. " +
                     ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Finish()
        {
            // No-op.
        }

        private static void RunUpdateCheck()
        {
            string patcherDll = Assembly.GetExecutingAssembly().Location;
            string patcherDir = Path.GetDirectoryName(patcherDll);
            if (string.IsNullOrWhiteSpace(patcherDir))
            {
                Warn("Could not resolve patcher directory; update check skipped.");
                return;
            }

            string bepinExRoot = Directory.GetParent(patcherDir)?.FullName;
            if (string.IsNullOrWhiteSpace(bepinExRoot) ||
                !Directory.Exists(Path.Combine(bepinExRoot, "plugins")))
            {
                Warn("Could not resolve BepInEx root; update check skipped.");
                return;
            }

            string configPath = Path.Combine(patcherDir, ConfigFileName);
            Settings settings = LoadSettings(configPath);
            if (settings == null || !settings.Enabled)
            {
                Info("Updater disabled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
            {
                Warn("manifest_url is empty; update check skipped.");
                return;
            }

            Version localVersion = GetInstalledSuiteVersion(bepinExRoot);
            Info("Preloader updater v" + UpdaterVersion +
                 "; installed suite " + (localVersion != null ? "v" + localVersion : "unknown") + ".");

            string manifestJson = DownloadText(settings.ManifestUrl, settings.TimeoutMilliseconds);
            Manifest manifest = ParseManifest(manifestJson);
            if (manifest == null || manifest.Version == null)
            {
                Warn("Update manifest is invalid; installed version will be used.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(manifest.Channel) &&
                !string.Equals(manifest.Channel, settings.Channel, StringComparison.OrdinalIgnoreCase))
            {
                Warn("Manifest channel '" + manifest.Channel + "' does not match configured channel '" +
                     settings.Channel + "'; update skipped.");
                return;
            }

            if (localVersion != null && manifest.Version <= localVersion)
            {
                Info("Valheim Symbiosis is current (v" + localVersion + ").");
                return;
            }

            if (string.IsNullOrWhiteSpace(manifest.PackageUrl) ||
                string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                Warn("New version found, but package_url or sha256 is missing.");
                return;
            }

            Info("Update found: " +
                 (localVersion != null ? "v" + localVersion : "unknown") +
                 " -> v" + manifest.Version + ". Downloading before Chainloader startup...");

            string cacheRoot = Path.Combine(bepinExRoot, "cache", "Valheim_Symbiosis_Updater");
            Directory.CreateDirectory(cacheRoot);

            string token = manifest.Version.ToString().Replace('.', '_') + "_" +
                           DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string packagePath = Path.Combine(cacheRoot, "package_" + token + ".zip");
            string stageRoot = Path.Combine(cacheRoot, "stage_" + token);
            string backupRoot = Path.Combine(cacheRoot, "backup_" +
                                (localVersion != null ? localVersion.ToString().Replace('.', '_') : "unknown"));

            try
            {
                DownloadFile(manifest.PackageUrl, packagePath, settings.TimeoutMilliseconds);

                string actualHash = ComputeSha256(packagePath);
                string expectedHash = NormalizeHash(manifest.Sha256);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA256 mismatch. Expected " + expectedHash +
                                                   ", got " + actualHash + ".");

                if (Directory.Exists(stageRoot))
                    Directory.Delete(stageRoot, true);
                Directory.CreateDirectory(stageRoot);

                ExtractZipSafely(packagePath, stageRoot);

                string payloadPlugins = Path.Combine(stageRoot, "Payload", "plugins");
                if (!Directory.Exists(payloadPlugins))
                    throw new InvalidDataException("Update package is missing Payload/plugins.");

                ApplyPayloadPlugins(payloadPlugins, Path.Combine(bepinExRoot, "plugins"), backupRoot);

                File.WriteAllText(Path.Combine(bepinExRoot, VersionMarkerName), manifest.Version.ToString());

                Info("Valheim Symbiosis updated to v" + manifest.Version +
                     " before normal plugins were loaded. Valheim continues now.");
            }
            catch
            {
                // ApplyPayloadPlugins performs its own transactional rollback for modified DLLs.
                throw;
            }
            finally
            {
                TryDeleteDirectory(stageRoot);
                TryDeleteFile(packagePath);
            }
        }

        private static Settings LoadSettings(string path)
        {
            if (!File.Exists(path))
            {
                Warn("Updater config not found: " + path);
                return null;
            }

            string json = File.ReadAllText(path);
            Settings result = new Settings();
            result.Enabled = !Regex.IsMatch(json, "\"enabled\"\\s*:\\s*false", RegexOptions.IgnoreCase);
            result.ManifestUrl = MatchString(json, "manifest_url");
            result.Channel = MatchString(json, "channel") ?? "stable";

            int timeoutSeconds = MatchInt(json, "timeout_seconds", 4);
            if (timeoutSeconds < 1) timeoutSeconds = 1;
            if (timeoutSeconds > 15) timeoutSeconds = 15;
            result.TimeoutMilliseconds = timeoutSeconds * 1000;
            return result;
        }

        private static Manifest ParseManifest(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            string versionText = MatchString(json, "version");
            Version version = ParseVersion(versionText);
            if (version == null) return null;

            return new Manifest
            {
                Version = version,
                Channel = MatchString(json, "channel") ?? "stable",
                PackageUrl = UnescapeJsonUrl(MatchString(json, "package_url")),
                Sha256 = MatchString(json, "sha256")
            };
        }

        private static Version GetInstalledSuiteVersion(string bepinExRoot)
        {
            // Suite release version is the greater of:
            //   1) Workers Core assembly version
            //   2) updater-written suite marker
            //
            // This keeps manually installed/newer Core builds authoritative while also
            // allowing a release package to carry a build revision such as 0.6.0.1
            // when Mount/DrakeForm/Ego changes without bumping Workers Core itself.
            string plugins = Path.Combine(bepinExRoot, "plugins");

            Version coreVersion = null;
            string canonicalCore = Path.Combine(plugins, "Valheim_Workers", "Valheim_Workers.dll");
            coreVersion = TryReadAssemblyVersion(canonicalCore);

            if (coreVersion == null)
            {
                try
                {
                    string[] matches = Directory.GetFiles(plugins, "Valheim_Workers.dll", SearchOption.AllDirectories);
                    for (int i = 0; i < matches.Length; i++)
                    {
                        Version candidate = TryReadAssemblyVersion(matches[i]);
                        if (candidate != null && (coreVersion == null || candidate > coreVersion))
                            coreVersion = candidate;
                    }
                }
                catch { }
            }

            Version markerVersion = null;
            string marker = Path.Combine(bepinExRoot, VersionMarkerName);
            try
            {
                if (File.Exists(marker))
                    markerVersion = ParseVersion(File.ReadAllText(marker));
            }
            catch { }

            if (coreVersion == null) return markerVersion;
            if (markerVersion == null) return coreVersion;
            return markerVersion > coreVersion ? markerVersion : coreVersion;
        }

        private static Version TryReadAssemblyVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                AssemblyName name = AssemblyName.GetAssemblyName(path);
                return name != null ? name.Version : null;
            }
            catch
            {
                return null;
            }
        }

        private static string DownloadText(string url, int timeoutMs)
        {
            try
            {
                using (TimeoutWebClient client = CreateClient(timeoutMs))
                    return client.DownloadString(url);
            }
            catch (Exception ex)
            {
                Info("Managed TLS download unavailable (" + ex.GetType().Name +
                     ": " + ex.Message + "); retrying through Windows curl.exe.");

                string temp = Path.Combine(Path.GetTempPath(),
                    "Valheim_Symbiosis_manifest_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    DownloadFileWithCurl(url, temp, timeoutMs);
                    return File.ReadAllText(temp);
                }
                finally
                {
                    TryDeleteFile(temp);
                }
            }
        }

        private static void DownloadFile(string url, string path, int timeoutMs)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            try
            {
                using (TimeoutWebClient client = CreateClient(timeoutMs))
                    client.DownloadFile(url, path);
            }
            catch (Exception ex)
            {
                Info("Managed TLS package download unavailable (" + ex.GetType().Name +
                     ": " + ex.Message + "); retrying through Windows curl.exe.");
                TryDeleteFile(path);
                DownloadFileWithCurl(url, path, timeoutMs);
            }

            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new IOException("Downloaded update package is empty.");
        }

        private static void DownloadFileWithCurl(string url, string path, int timeoutMs)
        {
            string curl = ResolveCurlExecutable();
            if (string.IsNullOrWhiteSpace(curl))
                throw new InvalidOperationException(
                    "Managed TLS is unavailable and curl.exe could not be found.");

            int timeoutSeconds = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));
            string args =
                "--fail --silent --show-error --location " +
                "--connect-timeout " + timeoutSeconds + " " +
                "--max-time " + timeoutSeconds + " " +
                "-A " + QuoteProcessArgument(UserAgent) + " " +
                "-o " + QuoteProcessArgument(path) + " " +
                QuoteProcessArgument(url);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = curl,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (Process process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException("curl.exe could not be started.");

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(timeoutMs + 2500))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("curl.exe update request timed out.");
                }

                if (process.ExitCode != 0)
                {
                    string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    throw new WebException("curl.exe failed with exit code " +
                                           process.ExitCode + ": " + detail.Trim());
                }
            }
        }

        private static string ResolveCurlExecutable()
        {
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrWhiteSpace(windows))
                {
                    string systemCurl = Path.Combine(windows, "System32", "curl.exe");
                    if (File.Exists(systemCurl)) return systemCurl;
                }
            }
            catch { }

            // Allow PATH lookup as fallback.
            return "curl.exe";
        }

        private static string QuoteProcessArgument(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static TimeoutWebClient CreateClient(int timeoutMs)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            }
            catch { }

            TimeoutWebClient client = new TimeoutWebClient(timeoutMs);
            client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            client.Headers[HttpRequestHeader.Accept] = "application/json, application/octet-stream, */*";
            return client;
        }

        private static string ComputeSha256(string filePath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToUpperInvariant();
            }
        }

        private static string NormalizeHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            Match match = Regex.Match(value, "[A-Fa-f0-9]{64}");
            return match.Success ? match.Value.ToUpperInvariant() : value.Trim().ToUpperInvariant();
        }

        private static void ExtractZipSafely(string zipPath, string destinationRoot)
        {
            string fullRoot = Path.GetFullPath(destinationRoot);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullRoot += Path.DirectorySeparatorChar;

            using (FileStream stream = File.OpenRead(zipPath))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                for (int i = 0; i < archive.Entries.Count; i++)
                {
                    ZipArchiveEntry entry = archive.Entries[i];
                    string targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));

                    if (!targetPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Blocked path traversal in update package: " + entry.FullName);

                    bool isDirectory = string.IsNullOrEmpty(entry.Name);
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    string parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
            }
        }

        private static void ApplyPayloadPlugins(string payloadPlugins, string livePlugins, string backupRoot)
        {
            string[] files = Directory.GetFiles(payloadPlugins, "*", SearchOption.AllDirectories);
            List<AppliedFile> applied = new List<AppliedFile>();

            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    string source = files[i];
                    if (!string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileName = Path.GetFileName(source);
                    if (string.Equals(fileName, "Valheim_Symbiosis_Updater.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Info("Skipping updater DLL inside gameplay package; updater has independent versioning.");
                        continue;
                    }

                    string relative = MakeRelativePath(payloadPlugins, source);
                    string destination = Path.Combine(livePlugins, relative);
                    string destinationDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(destinationDir)) Directory.CreateDirectory(destinationDir);

                    string backup = Path.Combine(backupRoot, relative);
                    bool existed = File.Exists(destination);

                    if (existed)
                    {
                        string backupDir = Path.GetDirectoryName(backup);
                        if (!string.IsNullOrWhiteSpace(backupDir)) Directory.CreateDirectory(backupDir);
                        File.Copy(destination, backup, true);
                    }

                    // Stage next to destination, then replace/move in the same filesystem.
                    string incoming = destination + ".symbiosis-update";
                    File.Copy(source, incoming, true);

                    if (File.Exists(destination))
                        File.Delete(destination);
                    File.Move(incoming, destination);

                    applied.Add(new AppliedFile
                    {
                        Destination = destination,
                        Backup = backup,
                        HadOriginal = existed
                    });

                    Info("Updated " + relative.Replace('\\', '/'));
                }
            }
            catch
            {
                Warn("Update apply failed; rolling back changed gameplay DLLs.");
                for (int i = applied.Count - 1; i >= 0; i--)
                {
                    AppliedFile file = applied[i];
                    try
                    {
                        if (file.HadOriginal && File.Exists(file.Backup))
                        {
                            File.Copy(file.Backup, file.Destination, true);
                        }
                        else if (!file.HadOriginal && File.Exists(file.Destination))
                        {
                            File.Delete(file.Destination);
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        Warn("Rollback failed for " + file.Destination + ": " + rollbackEx.Message);
                    }
                }
                throw;
            }
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            string fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullRoot += Path.DirectorySeparatorChar;

            string full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Payload file escaped plugin root.");

            return full.Substring(fullRoot.Length);
        }

        private static string MatchString(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            Match m = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int MatchInt(string json, string key, int fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            Match m = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return fallback;

            int value;
            return int.TryParse(m.Groups[1].Value, out value) ? value : fallback;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            Match m = Regex.Match(value, @"(\d+)\.(\d+)\.(\d+)");
            if (!m.Success) return null;

            int major, minor, patch;
            if (!int.TryParse(m.Groups[1].Value, out major) ||
                !int.TryParse(m.Groups[2].Value, out minor) ||
                !int.TryParse(m.Groups[3].Value, out patch))
                return null;

            return new Version(major, minor, patch);
        }

        private static string UnescapeJsonUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? value : value.Replace("\\/", "/");
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static void Info(string message)
        {
            Write("INFO", message);
        }

        private static void Warn(string message)
        {
            Write("WARN", message);
        }

        private static void Write(string level, string message)
        {
            string line = "[Valheim Symbiosis Updater] [" + level + "] " + message;
            try { Trace.WriteLine(line); } catch { }
            try { Console.WriteLine(line); } catch { }
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;

            internal TimeoutWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = _timeoutMs;
                    HttpWebRequest http = request as HttpWebRequest;
                    if (http != null)
                        http.ReadWriteTimeout = _timeoutMs;
                }
                return request;
            }
        }

        private sealed class Settings
        {
            internal bool Enabled;
            internal string ManifestUrl;
            internal string Channel;
            internal int TimeoutMilliseconds;
        }

        private sealed class Manifest
        {
            internal Version Version;
            internal string Channel;
            internal string PackageUrl;
            internal string Sha256;
        }

        private sealed class AppliedFile
        {
            internal string Destination;
            internal string Backup;
            internal bool HadOriginal;
        }
    }
}
