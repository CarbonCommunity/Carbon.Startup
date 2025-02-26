using System;
using System.IO;
using System.Linq;
using Carbon.Core;
using Mono.Cecil.Cil;
using Doorstop.Utility;
using Facepunch;

namespace Carbon.Utilities.Patches;

public class RustHarmony() : Patch(Defines.GetRustManagedFolder(), "Rust.Harmony.dll")
{
	public override bool IsAlreadyPatched => false;

	public override bool Execute()
	{
		if (!base.Execute()) return false;

		try
		{
			PatchLoadHarmonyMods();
		}
		catch (Exception ex)
		{
			Logger.Error(ex);
			return false;
		}

		return true;
	}

	private void PatchLoadHarmonyMods()
	{
		var harmonyLoader = assembly.MainModule.GetType("HarmonyLoader");
		var method = harmonyLoader.Methods.FirstOrDefault(x => x.Name == "LoadHarmonyMods");

		if (method is null || !method.HasBody)
		{
			return;
		}

		Logger.Debug($" - Patching HarmonyLoader.LoadHarmonyMods");

		var switchReference = assembly.MainModule.ImportReference(typeof(CommandLine).GetMethod("GetSwitch", [typeof(string), typeof(string)]));
		var combineReference = assembly.MainModule.ImportReference(typeof(Path).GetMethod("Combine", [typeof(string), typeof(string)]));

		const int offset = 21;
		method.Body.Instructions.RemoveAt(offset);
		method.Body.Instructions.RemoveAt(offset);
		method.Body.Instructions.RemoveAt(offset);

		method.Body.Instructions.Insert(offset, Instruction.Create(OpCodes.Ldstr, "-harmonydir"));
		method.Body.Instructions.Insert(offset + 1, Instruction.Create(OpCodes.Ldloc_0));
		method.Body.Instructions.Insert(offset + 2, Instruction.Create(OpCodes.Ldstr, "HarmonyMods"));
		method.Body.Instructions.Insert(offset + 3, Instruction.Create(OpCodes.Call, combineReference));
		method.Body.Instructions.Insert(offset + 4, Instruction.Create(OpCodes.Call, switchReference));
	}
}
