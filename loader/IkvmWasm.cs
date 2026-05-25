using System;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.InteropServices.JavaScript;

static partial class IkvmWasm
{
	internal static void Main()
	{
		Console.WriteLine(":3");
	}

	public static string[][] ConvertJSObjectToStringArray(JSObject jsObject)
	{
		var outerLength = jsObject.GetPropertyAsInt32("length");
		var result = new string[outerLength][];

		for (int i = 0; i < outerLength; i++)
		{
			using var innerArray = jsObject.GetPropertyAsJSObject(i.ToString());
			var innerLength = innerArray!.GetPropertyAsInt32("length");
			result[i] = new string[innerLength];

			for (int j = 0; j < innerLength; j++)
			{
				result[i][j] = innerArray.GetPropertyAsString(j.ToString()) ?? string.Empty;
			}
		}

		return result;
	}

	[JSExport]
	internal static Task PreInit(string fetchbase, JSObject jars, JSObject props)
	{
		try
		{
			Emscripten.MountOpfs();

			Emscripten.MountFetch(0, fetchbase + "/image", "/ikvm");
			Emscripten.MountFetchDir(0, "/ikvm/bin");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libzip.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libnio.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libnet.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libmanagement.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libawt.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libfontmanager.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libmlib_image.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/liblcms.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libjpeg.so");
			Emscripten.MountFetchDir(0, "/ikvm/lib");
			Emscripten.MountFetchFile(0, "/ikvm/lib/currency.data");
			Emscripten.MountFetchFile(0, "/ikvm/lib/tzdb.dat");
			Emscripten.MountFetchFile(0, "/ikvm/lib/content-types.properties");
			Emscripten.MountFetchFile(0, "/ikvm/lib/logging.properties");

			Emscripten.MountFetch(1, fetchbase + "/assets", "/assets");
			var jarPaths = new string[jars.GetPropertyAsInt32("length")];
			for (int i = 0; i < jarPaths.Length; i++)
			{
				jarPaths[i] = jars.GetPropertyAsString(i.ToString()) ?? throw new ArgumentException($"jars[{i}] is null");
				Emscripten.MountFetchFile(1, jarPaths[i]);
			}

			File.WriteAllText("/ikvm.properties", "ikvm.home=/ikvm");

			// -- ikvm will init after this --
			var bundleDlls = IkvmcManifest.LoadEmbedded().AllDlls();
			java.lang.Thread.currentThread().setContextClassLoader(new IkvmClassLoader(jarPaths, bundleDlls, []));

			java.lang.System.setProperty("java.awt.headless", "true");

			foreach (var prop in ConvertJSObjectToStringArray(props))
			{
				Console.WriteLine($"-D{prop[0]}={prop[1]}");
				java.lang.System.setProperty(prop[0], prop[1]);
			}

			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "Error in PreInit()!");
			return Task.FromException(e);
		}
	}

	[JSExport]
	internal static Task RunJar(string jarPath, string mainclass)
	{
		try
		{
			Console.WriteLine($"[IKVM] running jar {jarPath}");
			var mainClassName = mainclass ?? GetMainClassName(jarPath);
			Console.WriteLine($"[IKVM] main class: {mainClassName}");

			var mainClass = java.lang.Class.forName(mainClassName, true, java.lang.Thread.currentThread().getContextClassLoader());
			var stringArrayClass = java.lang.Class.forName("[Ljava.lang.String;");
			var mainMethod = mainClass.getMethod("main", new[] { stringArrayClass });
			mainMethod.invoke(null, new object[] { Array.Empty<string>() });
			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "[IKVM] RunJar failed");
			return Task.FromException(e);
		}
	}

	private static string GetMainClassName(string jarPath)
	{
		var jar = new java.util.jar.JarFile(jarPath);
		var manifest = jar.getManifest();
		if (manifest == null)
		{
			throw new InvalidDataException($"Jar has no manifest: {jarPath}");
		}

		var mainClass = manifest.getMainAttributes()?.getValue(java.util.jar.Attributes.Name.MAIN_CLASS);
		if (string.IsNullOrWhiteSpace(mainClass))
		{
			throw new InvalidDataException($"Jar manifest missing Main-Class: {jarPath}");
		}

		return mainClass.Trim();
	}
}
