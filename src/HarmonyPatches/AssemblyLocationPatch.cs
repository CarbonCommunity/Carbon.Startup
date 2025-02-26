using System.Reflection;
using HarmonyLib;
using Startup;

namespace Patches;

[HarmonyPatchCategory("location")]
[HarmonyPatch("System.Reflection.RuntimeAssembly", "Location", MethodType.Getter)]
public class AssemblyLocationPatch
{
	public static void Postfix(Assembly __instance, ref string __result)
	{
		if (Entrypoint.PatchMapping.TryGetValue(__instance, out var path))
		{
			__result = path;
		}
	}
}
