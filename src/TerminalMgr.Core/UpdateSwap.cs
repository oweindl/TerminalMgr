// This file is also compiled into TerminalMgr.Updater (.NET Framework 4.8, which every Windows 10/11 has), so it sticks to
// plain language features and APIs that exist in both worlds.
using System;
using System.IO;
using System.Threading;

namespace TerminalMgr.Core
{
    public sealed class SwapResult
    {
        public SwapResult(bool success, string message) { Success = success; Message = message; }
        public bool Success { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Replaces the installation folder by a staged one: old folder to "&lt;install&gt;.old", staged folder in its place, start the
    /// new app and check it really came up. The backup is deleted only after that; otherwise the old version is put back.
    /// </summary>
    public static class UpdateSwap
    {
        public static SwapResult Apply(string installDir, string stagedDir, Func<bool> startAndVerifyNew, Action startOld, Action<string> log, int lockRetrySeconds = 30)
        {
            installDir = installDir.TrimEnd('\\', '/');
            stagedDir = stagedDir.TrimEnd('\\', '/');
            string backup = installDir + ".old";
            if (!Directory.Exists(stagedDir)) return new SwapResult(false, "Der vorbereitete Ordner fehlt: " + stagedDir);
            if (!Directory.Exists(installDir)) return new SwapResult(false, "Der Installationsordner fehlt: " + installDir);

            if (Directory.Exists(backup) && !TryDelete(backup, 5, log)) backup = installDir + ".old-" + DateTime.UtcNow.Ticks;

            // 1. The old version steps aside. Nothing has changed yet if this fails (files in use, virus scanner).
            if (!Retry(delegate { MoveDirectory(installDir, backup); }, lockRetrySeconds, log))
                return new SwapResult(false, "Der alte Ordner ließ sich nicht umbenennen (Dateien in Benutzung?). Die alte Version bleibt unverändert.");
            log("Alte Version gesichert: " + backup);

            // 2. The new version takes its place.
            try { MoveDirectory(stagedDir, installDir); }
            catch (Exception ex)
            {
                log("Einsetzen der neuen Version fehlgeschlagen: " + ex.Message);
                Restore(installDir, backup, log);
                TryStart(startOld, log);
                return new SwapResult(false, "Die neue Version ließ sich nicht einsetzen: " + ex.Message + " Die alte Version wurde wiederhergestellt.");
            }
            log("Neue Version eingesetzt: " + installDir);

            // 3. It must really come up before the backup goes away.
            bool ok;
            try { ok = startAndVerifyNew(); }
            catch (Exception ex) { log("Start der neuen Version fehlgeschlagen: " + ex.Message); ok = false; }
            if (ok)
            {
                TryDelete(backup, 10, log);
                return new SwapResult(true, "Aktualisiert.");
            }

            log("Die neue Version ist nicht angelaufen; die alte wird wiederhergestellt.");
            Restore(installDir, backup, log);
            TryStart(startOld, log);
            return new SwapResult(false, "Die neue Version ist nicht angelaufen. Die alte Version wurde wiederhergestellt.");
        }

        private static void Restore(string installDir, string backup, Action<string> log)
        {
            if (Directory.Exists(installDir) && !TryDelete(installDir, 10, log))
            {
                // Not deletable (still locked): move it out of the way instead.
                try { MoveDirectory(installDir, installDir + ".failed-" + DateTime.UtcNow.Ticks); }
                catch (Exception ex) { log("Konnte den fehlgeschlagenen Ordner nicht beiseite schieben: " + ex.Message); }
            }
            Retry(delegate { MoveDirectory(backup, installDir); }, 20, log);
        }

        private static void TryStart(Action start, Action<string> log)
        {
            try { start(); } catch (Exception ex) { log("Start der alten Version fehlgeschlagen: " + ex.Message); }
        }

        private static bool Retry(Action action, int seconds, Action<string> log)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                try { action(); return true; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (DateTime.UtcNow >= deadline) { log("Aufgegeben: " + ex.Message); return false; }
                    Thread.Sleep(500);
                }
            }
        }

        private static bool TryDelete(string directory, int seconds, Action<string> log) =>
            Retry(delegate { if (Directory.Exists(directory)) Directory.Delete(directory, true); }, seconds, log);

        /// <summary>Renames within a volume; across volumes (the root differs) it copies and deletes.</summary>
        public static void MoveDirectory(string source, string destination)
        {
            if (Directory.Exists(destination)) throw new IOException("Das Ziel existiert schon: " + destination);
            try { Directory.Move(source, destination); }
            catch (IOException) when (!string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
            {
                CopyDirectory(source, destination);
                Directory.Delete(source, true);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
