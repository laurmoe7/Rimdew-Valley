using System.IO;
using System.Linq;
using Verse;

namespace RimDewBridge
{
    /// <summary>
    /// Loads a save straight from the main menu so the hidden colony needs no clicks.
    /// Controlled by Mods/RimDewBridge/autoload.txt: "latest" (default), a save name, or delete the file to disable.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AutoLoad
    {
        static AutoLoad()
        {
            LongEventHandler.ExecuteWhenFinished(Run);
        }

        private static void Run()
        {
            try
            {
                var mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => m.PackageId == "laurm.rimdewbridge");
                if (mod == null) return;
                string cfg = Path.Combine(mod.RootDir, "autoload.txt");
                if (!File.Exists(cfg)) return;
                string want = File.ReadAllText(cfg).Trim();
                if (want.Length == 0) return;

                string name = want;
                if (want == "latest")
                {
                    var newest = GenFilePaths.AllSavedGameFiles.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (newest == null) { Log.Warning("[RimDew] autoload: no saves found."); return; }
                    name = Path.GetFileNameWithoutExtension(newest.Name);
                }
                Log.Message("[RimDew] autoload: loading '" + name + "'.");
                GameDataSaveLoader.LoadGame(name);
            }
            catch (System.Exception e)
            {
                Log.Error("[RimDew] autoload failed: " + e);
            }
        }
    }
}
