using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class IkvmcManifest
{
	public IkvmcBundle[] Bundles { get; init; } = Array.Empty<IkvmcBundle>();

	private const string EmbeddedResourceName = "IkvmWasm.ikvmc-manifest.json";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
	};

	public static IkvmcManifest LoadEmbedded()
	{
		var assembly = typeof(IkvmcManifest).Assembly;
		using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
		if (stream is null)
		{
			var available = string.Join(", ", assembly.GetManifestResourceNames());
			throw new InvalidOperationException(
				$"Embedded manifest '{EmbeddedResourceName}' not found in assembly. Available: [{available}]. "
				+ "Did build-ikvmc.py run before dotnet publish?");
		}

		var manifest = JsonSerializer.Deserialize<IkvmcManifest>(stream, JsonOptions)
			?? throw new InvalidDataException("ikvmc manifest deserialized to null.");

		manifest.Validate();
		return manifest;
	}

	private void Validate()
	{
		foreach (var bundle in Bundles)
		{
			if (string.IsNullOrWhiteSpace(bundle.AssemblyName))
			{
				throw new InvalidDataException($"ikvmc bundle '{bundle.Name}' is missing assemblyName.");
			}
			if (bundle.Prefixes is null || bundle.Prefixes.Length == 0)
			{
				throw new InvalidDataException($"ikvmc bundle '{bundle.Name}' has no class prefixes.");
			}
		}
	}

	/// <summary>
	/// Every bundle in the manifest, ready to feed straight into <see cref="IkvmClassLoader"/>.
	/// Used by the generic CLI loader, which doesn't have a version JSON to match against —
	/// every AOT'd bundle is always active.
	/// </summary>
	public IkvmClassLoaderDll[] AllDlls()
	{
		return (from bundle in Bundles
				select (bundle.Prefixes, bundle.AssemblyName)).ToArray();
	}
}

internal sealed class IkvmcBundle
{
	public string Name { get; init; } = string.Empty;
	public string AssemblyName { get; init; } = string.Empty;
	public string[] Prefixes { get; init; } = Array.Empty<string>();
	public bool AlwaysReplace { get; init; }
	public IkvmcBundleJar[] Jars { get; init; } = Array.Empty<IkvmcBundleJar>();
}

internal sealed class IkvmcBundleJar
{
	public string Group { get; init; } = string.Empty;
	public string Artifact { get; init; } = string.Empty;
	public string Version { get; init; } = string.Empty;
	public string RelativePath { get; init; } = string.Empty;
}
