﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Carbon.Core;
using Carbon.Utilities.Patches;
using Doorstop.Utility;
using HarmonyLib;
using Logger = Doorstop.Utility.Logger;
using Patch = Carbon.Utilities.Patch;

namespace Startup;

[SuppressUnmanagedCodeSecurity]
public sealed class Entrypoint
{
	private static readonly string[] PreloadPostUpdate =
	[
		Path.GetFullPath(Path.Combine(Defines.GetManagedFolder(), "Carbon.Compat.dll"))
	];
	private static readonly string[] Delete =
	[
		Path.Combine(Defines.GetExtensionsFolder(), "CCLBootstrap.dll"),
		Path.Combine(Defines.GetExtensionsFolder(), "Carbon.Ext.Discord.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "x64"),
		Path.Combine(Defines.GetRustManagedFolder(), "x86"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.Common.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.Core.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.CSharp.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.MySql.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.References.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.Rust.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.SQLite.dll"),
		Path.Combine(Defines.GetRustManagedFolder(), "Oxide.Unity.dll")
	];

	private static readonly Dictionary<(string directory, string filter), string> WildcardMove = new()
	{
		[(Defines.GetRustManagedFolder(), "Oxide.Ext.")] = Path.Combine(Defines.GetExtensionsFolder())
	};
	private static readonly Dictionary<string, string> CopyTargetEmpty = new()
	{
		[Path.Combine(Defines.GetRustRootFolder(), "oxide", "config")] = Path.Combine(Defines.GetRootFolder(), "configs"),
		[Path.Combine(Defines.GetRustRootFolder(), "oxide", "data")] = Path.Combine(Defines.GetRootFolder(), "data"),
		[Path.Combine(Defines.GetRustRootFolder(), "oxide", "plugins")] = Path.Combine(Defines.GetRootFolder(), "plugins"),
		[Path.Combine(Defines.GetRustRootFolder(), "oxide", "lang")] = Path.Combine(Defines.GetRootFolder(), "lang")
	};
	private static readonly Dictionary<string, string> Move = new()
	{
		[Path.Combine(Defines.GetRootFolder(), "harmony")] = Path.Combine(Defines.GetRustRootFolder(), "HarmonyMods")
	};
	private static readonly Dictionary<string, string> Rename = new()
	{
		[Path.Combine(Defines.GetRootFolder(), "config_client.json")] = Path.Combine(Defines.GetRootFolder(), "config.client.json"),
		[Path.Combine(Defines.GetRootFolder(), "carbonauto.cfg")] = Path.Combine(Defines.GetRootFolder(), "config.auto.json"),
		[Path.Combine(Defines.GetRootFolder(), "config.auto.cfg")] = Path.Combine(Defines.GetRootFolder(), "config.auto.json")
	};

	#region Native MonoProfiler

	[DllImport("CarbonNative")]
	public static unsafe extern void init_profiler(char* ptr, int length);

	[DllImport("__Internal", CharSet = CharSet.Ansi)]
	public static extern void mono_dllmap_insert(ModuleHandle assembly, string dll, string func, string tdll, string tfunc);

	public static unsafe void InitNative()
	{
#if UNIX
        mono_dllmap_insert(ModuleHandle.EmptyHandle, "CarbonNative", null, Path.Combine(Defines.GetRootFolder(), "native", "libCarbonNative.so"), null);
#elif WIN
		mono_dllmap_insert(ModuleHandle.EmptyHandle, "CarbonNative", null, Path.Combine(Defines.GetRootFolder(), "native", "CarbonNative.dll"), null);
#endif

		var path = Path.Combine(Defines.GetRootFolder(), "config.profiler.json");

		fixed (char* ptr = path)
		{
			init_profiler(ptr, path.Length);
		}
	}

	#endregion

	public static Patch[] Patches = [new AssemblyCSharp(), new RustHarmony()];
	public static List<Patch> Publicized = new();
	public static Dictionary<Assembly, string> PatchMapping = new();

	public static void Start()
	{
		Config.Init();

		Logger.Log($" Initialized Carbon.Startup {typeof(Entrypoint).Assembly.GetName().Version}");

		new Harmony("com.carbon.locationpatch").PatchCategory("location");

		foreach (string file in PreloadPostUpdate)
		{
			try
			{
				Assembly harmony = Assembly.LoadFile(file);
				Logger.Log($" Preloaded {harmony.GetName().Name} {harmony.GetName().Version}");
			}
			catch (Exception e)
			{
				Logger.Log($"Unable to preload '{file}' ({e?.Message})");
			}
		}

		PerformStartup();
	}
	public static void PerformStartup()
	{
		try
		{
			InitNative();
		}
		catch (Exception ex)
		{
			Logger.Error("Failed to init native", ex);
		}

		var patchableFiles = Directory.EnumerateFiles(Defines.GetRustManagedFolder());

		Patch.Init();
		foreach (var file in patchableFiles)
		{
			try
			{
				var name = Path.GetFileName(file);
				var patch = Patches.FirstOrDefault(x => x.fileName.Equals(name));

				if (patch != null && patch.Execute())
				{
					patch.Load();
					if (Config.Singleton.DeveloperMode)
					{
						patch.Write(Path.Combine(Defines.GetDeveloperPatchedAssembliesFolder(), name));
					}
					continue;
				}

				if (!Config.Singleton.Publicizer.PublicizedAssemblies.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}

				patch = new Patch(Path.GetDirectoryName(file), name);
				if (patch.Execute())
				{
					patch.Load();
					if (Config.Singleton.DeveloperMode)
					{
						patch.Write(Path.Combine(Defines.GetDeveloperPatchedAssembliesFolder(), name));
					}
					Publicized.Add(patch);
				}
			}
			catch (Exception ex)
			{
				Logger.Error("Failed to patch", ex);
			}
		}
		Patch.Uninit();

		try
		{
			PerformMove();
			PerformWildcardMove();
			PerformRename();
			PerformCleanup();
			PerformCopyTargetEmpty();
		}
		catch (Exception ex)
		{
			Logger.Error("Preloader fatal failure", ex);
		}
	}
	public static void PerformCleanup()
	{
		foreach (var path in Delete)
		{
			try
			{
				if (!File.Exists(path))
				{
					if (Directory.Exists(path))
					{
						Logger.Log($" Removed '{Path.GetFileName(path)}'");
						Directory.Delete(path, true);
					}

					continue;
				}

				Logger.Log($" Removed '{Path.GetFileName(path)}'");
				File.Delete(path);
			}
			catch (Exception ex)
			{
				Logger.Error($" Cleanup process error! Failed removing '{path}'", ex);
			}
		}
	}
	public static void PerformWildcardMove()
	{
		foreach (var fileWildcard in WildcardMove)
		{
			var files = Directory.GetFiles(fileWildcard.Key.directory);

			foreach (var file in files)
			{
				if (!file.Contains(fileWildcard.Key.filter))
				{
					continue;
				}

				var destination = Path.Combine(fileWildcard.Value, Path.GetFileName(file));

				if (File.Exists(destination))
				{
					continue;
				}

				File.Move(file, destination);
				Logger.Log($" Moved {Path.GetFileName(file)} -> carbon/{Path.GetFileName(fileWildcard.Value)}");
			}
		}
	}
	public static void PerformCopyTargetEmpty()
	{
		if (CopyTargetEmpty.Any(x => Directory.Exists(x.Value) && new DirectoryInfo(x.Value).GetFiles().Any()))
		{
			return;
		}

		if (!Directory.Exists(Path.Combine(Defines.GetRustRootFolder(), "oxide")))
		{
			return;
		}

		Logger.Log($" Fresh Carbon installation detected. Migrating Oxide directories.");

		foreach (var folder in CopyTargetEmpty)
		{
			if (!Directory.Exists(folder.Key))
			{
				continue;
			}

			var target = new DirectoryInfo(folder.Value);

			if (target.GetFiles().Any())
			{
				continue;
			}

			try
			{
				Logger.Log($" Copied oxide/{Path.GetFileName(folder.Key)} -> carbon/{Path.GetFileName(folder.Value)}");
				IO.Copy(folder.Key, folder.Value);
			}
			catch (Exception e)
			{
				Logger.Debug($" Unable to copy '{folder.Key}' -> '{folder.Value}' ({e?.Message})");
			}
		}
	}
	public static void PerformMove()
	{
		foreach (var folder in Move)
		{
			if (!Directory.Exists(folder.Key))
			{
				continue;
			}

			if (!Directory.Exists(folder.Value))
			{
				Directory.CreateDirectory(folder.Value);
			}

			try
			{
				IO.Move(folder.Key, folder.Value);
			}
			catch (Exception e)
			{
				Logger.Debug($" Unable to move '{folder.Key}' -> '{folder.Value}' ({e?.Message})");
			}
		}
	}
	public static void PerformRename()
	{
		foreach (var file in Rename)
		{
			try
			{
				if (!File.Exists(file.Key)) continue;

				File.Move(file.Key, file.Value);
			}
			catch (Exception e)
			{
				Logger.Debug($" Unable to rename '{file.Key}' -> '{file.Value}' ({e?.Message})");
			}
		}
	}
}
