using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Threading.Tasks;
using IFile = GLib.IFile;

namespace Rivened {
	public class LoadedGame {
		public static LoadedGame Instance;

		public static bool Load(IFile path) {
			Instance = null;

			//PC Version
			if((path.ResolveRelativePath("FILE/SCENE00.afs")?.Exists == true ||
					path.ResolveRelativePath("FILE/SCENE00.afs.bak")?.Exists == true) &&
					path.ResolveRelativePath("FILE/FONTS_PC.AFS")?.Exists == true &&
					path.ResolveRelativePath("FILE/BGL/BGL00_PC.AFS")?.Exists == true) {
				Instance = new LoadedGame(path, true);
				return true;
			}

			//PSP Version
			else if(path.ResolveRelativePath("MAC.AFS")?.Exists == true ||
						path.ResolveRelativePath("MAC.AFS.bak")?.Exists == true) {
				Instance = new LoadedGame(path, false);
				return true;
			}
			return false;
		}

		public static bool IsSpecial(string name) {
			return name.StartsWith("DATA");
		}

		public static bool ShouldIgnore(string name) {
			return name.StartsWith("DBG")
				|| name.StartsWith("MAIN")
				|| name.StartsWith("DMENU")
				|| name.StartsWith("SHORTCUT")
				|| name.StartsWith("INIT")
				|| name.StartsWith("CLRFLG")
				|| name.StartsWith("DICT")
				|| name.StartsWith("DATA")

				// PSP Version
				|| name.StartsWith("APPEND")
				|| name.StartsWith("STARTUP");
		}

		public IDecompiler decompiler;
		public ICompiler compiler;
		public IFile Path;
		public string AFSFileName;
		public string AFSFileNameBackup;
		public bool ScriptsPrepared = false;
		public bool ScriptListDirty = false;
		public AFS ScriptAFS;
		public FontSizeData FontSizeData = null;
		bool isPCVersion = true;

		public LoadedGame(IFile path, bool isPC) {
			Path = path;
			isPCVersion = isPC;
			if(isPC) {
				AFSFileName = "FILE/SCENE00.afs";
				AFSFileNameBackup = "FILE/SCENE00.afs.bak";
				decompiler = new ScriptDecompiler();
				compiler = new ScriptCompiler();
			} else {
				AFSFileName = "MAC.afs";
				AFSFileNameBackup = "MAC.afs.bak";
				decompiler = new ScriptDecompilerPSP();
				compiler = new ScriptCompilerPSP();
			}

			
			if(Path.ResolveRelativePath(AFSFileName)?.Exists == true &&
				Path.ResolveRelativePath(AFSFileNameBackup)?.Exists == true) {
				Trace.Assert(LoadScripts());
			}
		}

		public bool PrepareScripts() {
			if(Path.ResolveRelativePath(AFSFileName)?.Exists == true &&
					Path.ResolveRelativePath(AFSFileNameBackup)?.Exists == true) {
				return LoadScripts();
			}
			var sceneBackup = Path.ResolveRelativePath(AFSFileNameBackup);
			var scene = Path.ResolveRelativePath(AFSFileName);
			if(!sceneBackup.Exists) {
				scene.Copy(sceneBackup, GLib.FileCopyFlags.AllMetadata, null, null);
			}
			if(!scene.Exists) {
				sceneBackup.Copy(scene, GLib.FileCopyFlags.AllMetadata, null, null);
			}
			return LoadScripts();
		}

		public bool RevertScripts() {
			var sceneBackup = Path.ResolveRelativePath(AFSFileName);
			var scene = Path.ResolveRelativePath(AFSFileName);
			if(Path.ResolveRelativePath(AFSFileName)?.Exists == true) {
				sceneBackup.Copy(scene, GLib.FileCopyFlags.Overwrite | GLib.FileCopyFlags.AllMetadata, null, null);
				decompiler = new ScriptDecompiler();
				ScriptAFS = new AFS(Path.ResolveRelativePath(AFSFileName));
				FontSizeData ??= new FontSizeData(Path.ResolveRelativePath("FILE/FONTS_PC.AFS"));
				ScriptListDirty = true;
				return true;
			}
			return false;
		}

		public bool LoadScripts() {
			ScriptsPrepared = true;
			ScriptAFS = new AFS(Path.ResolveRelativePath(AFSFileName));
			if (isPCVersion) {
				FontSizeData ??= new FontSizeData(Path.ResolveRelativePath("FILE/FONTS_PC.AFS"));
			}
			ScriptListDirty = true;
			return true;
		}

		public bool SaveScripts() {
			var tasks = new List<Task>();
			string lastErr = null;
			var isFirst = true;
			foreach(var entry in ScriptAFS.Entries) {
				entry.Load(ScriptAFS);
				if((IsSpecial(entry.Name) || !ShouldIgnore(entry.Name)) && decompiler.CheckAndClearModified(entry.Name)) {
					//var fn = () => {
						if(compiler.Compile(entry.Name, decompiler.Decompile(ScriptAFS, entry), out var arr, out var err)) {
							Trace.Assert(arr.Length != 0);
							//Trace.Assert(arr[0] != 0);
							entry.SetData(arr);
						} else {
							err = entry.Name + ':' + err;
							Program.Log(err);
							lastErr = err;
							return false;
						}
					//};
					//if(isFirst) {
					//	fn();
					//	isFirst = false;
					//} else {
					//	tasks.Add(Task.Factory.StartNew(fn));
					//}
				}
			}
			//Task.WaitAll(tasks.ToArray());
			if(lastErr != null) {
				Program.LatestLog = lastErr;
				MainWindow.Instance.UpdateState();
				return false;
			}
			ScriptAFS.Save();
			return true;
		}
	}
}