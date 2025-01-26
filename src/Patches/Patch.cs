using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Carbon.Core;
using Doorstop;
using Doorstop.Utility;
using Mono.Cecil;
using Entrypoint = Startup.Entrypoint;

namespace Carbon.Utilities;

public class Patch : IDisposable
{
	protected static AssemblyDefinition bootstrap;

	public static void Init()
	{
		try
		{
			bootstrap = AssemblyDefinition.ReadAssembly(
				new MemoryStream(File.ReadAllBytes(Path.Combine(Defines.GetManagedFolder(), "Carbon.Bootstrap.dll"))));
		}
		catch { }
	}
	public static void Uninit()
	{
		bootstrap?.Dispose();
		bootstrap = null;
	}

	private ReaderParameters readerParameters;
	protected AssemblyDefinition assembly;

	public string GetFullPath() => Path.Combine(filePath, fileName);

	public byte[] processed;
	public string filePath;
	public string fileName;

	public virtual bool IsAlreadyPatched => assembly.MainModule.Types.FirstOrDefault(x => x.Name == "<Module>").Fields.Any(x => x.Name == "CarbonPatched");
	public bool ShouldPublicize => Doorstop.Config.Singleton.Publicizer.PublicizedAssemblies.Any(x => fileName.StartsWith(x, StringComparison.OrdinalIgnoreCase));

	public Patch(string path, string name)
	{
		filePath = path;
		fileName = name;

		var resolver = new DefaultAssemblyResolver();
		readerParameters = new ReaderParameters { AssemblyResolver = resolver };
		resolver.AddSearchDirectory(Defines.GetRustManagedFolder());
	}

	public virtual bool Execute()
	{
		assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(GetFullPath())), readerParameters);

		if (IsAlreadyPatched || !ShouldPublicize)
		{
			return false;
		}

		Publicize();
		return true;
	}

	public void UpdateBuffer()
	{
		if (processed != null)
			return;

		using var memoryStream = new MemoryStream();
		assembly.Write(memoryStream);
		processed = memoryStream.ToArray();
		API.Assembly.PatchedAssemblies.AssemblyCache[Path.GetFileNameWithoutExtension(fileName)] = memoryStream.ToArray();
	}

	public void Write(string path)
	{
		UpdateBuffer();
		try
		{
			Logger.Debug(" - Writing to disk");

			File.WriteAllBytes(path, processed);

			Dispose();
		}
		catch (Exception ex)
		{
			Logger.Error(ex);
		}
	}

	public void Load()
	{
		UpdateBuffer();
		var assembly = Assembly.Load(processed);
		Entrypoint.PatchMapping[assembly] = Path.Combine(filePath, fileName);
		Logger.Log($" Loading patched assembly {fileName}");
	}

	public void Dispose()
	{
		readerParameters = null;
		assembly?.Dispose();
		assembly = null;
	}

	protected bool IsPublic(string type, string method)
	{
		try
		{
			if (assembly == null)
			{
				throw new Exception($"Loaded assembly is null: {GetFullPath()}");
			}

			var typeDef = assembly.MainModule.Types.First(x => x.Name == type) ?? throw new Exception($"Unable to get type definition for '{type}'");
			var methodDef = typeDef.Methods.First(x => x.Name == method) ?? throw new Exception($"Unable to get method definition for '{method}'");
			return methodDef.IsPublic;
		}
		catch (Exception ex)
		{
			Logger.Error(ex.Message);
			throw ex;
		}
	}

	protected void Publicize()
	{
		if (assembly == null)
		{
			throw new Exception($"Loaded assembly is null: {GetFullPath()}");
		}

		Logger.Debug($" - Publicize assembly");

		foreach (var type in assembly.MainModule.Types)
			Publicize(type);
	}

	protected static void Publicize(TypeDefinition type)
	{
		try
		{
			if (Config.Singleton.Publicizer.IsMemberIgnored(type.Name))
			{
				Logger.Warn($"Excluded '{type.Name}' due to blacklisting");
				return;
			}

			if (type.IsNested)
				type.IsNestedPublic = true;
			else type.IsPublic = true;

			foreach (var method in type.Methods)
			{
				if (Config.Singleton.Publicizer.IsMemberIgnored($"{type.Name}.{method.Name}"))
				{
					Logger.Warn($"Excluded '{type.Name}.{method.Name}' due to blacklisting");
					continue;
				}

				method.IsPublic = true;
			}

			foreach (var field in type.Fields)
			{
				if (Config.Singleton.Publicizer.IsMemberIgnored($"{type.Name}.{field.Name}"))
				{
					Logger.Warn($"Excluded '{type.Name}.{field.Name}' due to blacklisting");
					continue;
				}

				var hasEvent = false;
				foreach (var ev in type.Events)
				{
					if (ev.Name != field.Name) continue;
					hasEvent = true;
					break;
				}

				if (hasEvent) continue;

				var hasSerializeFieldAttribute = false;
				foreach (var attribute in field.CustomAttributes)
				{
					if (attribute.AttributeType.FullName != "UnityEngine.SerializeField") continue;
					hasSerializeFieldAttribute = true;
					break;
				}

				if (!field.IsPublic && !hasSerializeFieldAttribute)
					field.IsNotSerialized = true;

				field.IsPublic = true;
			}

			foreach (var property in type.Properties)
			{
				if (property.GetMethod != null)
					property.GetMethod.IsPublic = true;
				if (property.SetMethod != null)
					property.SetMethod.IsPublic = true;
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex.Message);
			throw ex;
		}

		foreach (var subtype in type.NestedTypes)
			Publicize(subtype);
	}
}
