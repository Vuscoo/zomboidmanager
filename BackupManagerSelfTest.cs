using System.Diagnostics;
using System.Text.Json;

namespace ZomboidManager;

/// <summary>
/// Creates a synthetic Zomboid data tree with tens of thousands of small files
/// and times zip backup + cancel cleanup. Run: ZomboidManager.exe --selftest-backup
/// Results are written to %LocalAppData%\ZomboidManager\backup-selftest-result.json
/// (WinExe has no console).
/// </summary>
public static class BackupManagerSelfTest
{
    public static void Run()
    {
        string resultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZomboidManager",
            "backup-selftest-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

        string root = Path.Combine(Path.GetTempPath(), "ZM_BackupSelfTest_" + Guid.NewGuid().ToString("N"));
        string? createdZip = null;
        try
        {
            Directory.CreateDirectory(root);
            string serverDir = Path.Combine(root, "Server");
            string worldDir = Path.Combine(root, "Saves", "Multiplayer", "servertest");
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(worldDir);

            File.WriteAllText(Path.Combine(serverDir, "servertest.ini"), "DefaultPort=16261\n");
            File.WriteAllText(Path.Combine(serverDir, "servertest_SandboxVars.lua"), "SandboxVars = {}\n");

            const int fileCount = 80_000;
            var createSw = Stopwatch.StartNew();
            Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                string path = Path.Combine(worldDir, $"chunk_{i:D6}.bin");
                File.WriteAllBytes(path, BitConverter.GetBytes(i));
            });
            createSw.Stop();

            Directory.CreateDirectory(BackupManager.GetBackupsRoot());

            var cfg = new AppConfig
            {
                ZomboidDataPath = root,
                LastIniFilePath = Path.Combine(serverDir, "servertest.ini")
            };

            int progressEvents = 0;
            var progress = new Progress<BackupProgressUpdate>(_ => Interlocked.Increment(ref progressEvents));

            var sw = Stopwatch.StartNew();
            BackupResult result = BackupManager.CreateBackup(cfg, progress);
            sw.Stop();

            createdZip = result.Path;
            bool zipOk = result.Success
                && !string.IsNullOrWhiteSpace(result.Path)
                && File.Exists(result.Path!)
                && result.Path!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            long zipBytes = zipOk ? new FileInfo(result.Path!).Length : 0;

            using var cts = new CancellationTokenSource();
            var cancelTask = Task.Run(() => BackupManager.CreateBackup(cfg, null, cts.Token));
            Thread.Sleep(200);
            cts.Cancel();
            BackupResult cancelled = cancelTask.GetAwaiter().GetResult();
            bool cancelOk = cancelled.Cancelled;
            bool noPartial = !Directory.EnumerateFiles(BackupManager.GetBackupsRoot(), "*.partial").Any();

            var payload = new
            {
                ok = zipOk && cancelOk && noPartial,
                fixtureCreateSeconds = Math.Round(createSw.Elapsed.TotalSeconds, 2),
                backupElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
                fileCount = result.FileCount,
                progressEvents,
                zipPath = result.Path,
                zipSizeMb = Math.Round(zipBytes / (1024.0 * 1024.0), 2),
                message = result.Message,
                cancelOk,
                noPartialLeft = noPartial,
                cancelMessage = cancelled.Message
            };

            File.WriteAllText(resultPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Debug.WriteLine(File.ReadAllText(resultPath));

            if (!payload.ok)
                Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(createdZip) && File.Exists(createdZip))
            {
                try { File.Delete(createdZip); } catch { /* ignore */ }
            }

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // leave temp for inspection
            }
        }
    }
}
